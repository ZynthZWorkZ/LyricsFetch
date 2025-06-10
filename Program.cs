using System;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Web;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Generic;
using System.Linq;

class Program
{
    static string CleanSearchQuery(string query)
    {
        // Remove special characters but keep spaces and alphanumeric
        return Regex.Replace(query, @"[^\w\s]", "");
    }

    static double CalculateMatchScore(string searchQuery, string resultText)
    {
        // Normalize both strings
        string normalizedQuery = searchQuery.ToLower().Trim();
        string normalizedResult = resultText.ToLower().Trim();

        // Split into words
        var queryWords = normalizedQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var resultWords = normalizedResult.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // Calculate word match scores
        double wordMatchScore = 0;
        foreach (var queryWord in queryWords)
        {
            // Check for exact word matches
            if (resultWords.Contains(queryWord))
            {
                wordMatchScore += 1.0;
            }
            else
            {
                // Check for partial matches
                var partialMatches = resultWords.Where(w => w.Contains(queryWord) || queryWord.Contains(w));
                wordMatchScore += partialMatches.Count() * 0.5;
            }
        }

        // Normalize word match score
        wordMatchScore = wordMatchScore / queryWords.Length;

        // Calculate position score (earlier matches are better)
        double positionScore = 0;
        for (int i = 0; i < queryWords.Length; i++)
        {
            int position = Array.IndexOf(resultWords, queryWords[i]);
            if (position != -1)
            {
                positionScore += 1.0 / (position + 1);
            }
        }
        positionScore = positionScore / queryWords.Length;

        // Calculate title relevance
        double titleScore = 0;
        if (normalizedResult.Contains("\""))
        {
            var titleMatch = Regex.Match(normalizedResult, "\"([^\"]+)\"");
            if (titleMatch.Success)
            {
                string title = titleMatch.Groups[1].Value.ToLower();
                if (title.Contains(normalizedQuery))
                {
                    titleScore = 1.0;
                }
                else
                {
                    // Check how many query words are in the title
                    int matchingWords = queryWords.Count(w => title.Contains(w));
                    titleScore = (double)matchingWords / queryWords.Length;
                }
            }
        }

        // Combine scores with weights
        return (wordMatchScore * 0.4) + (positionScore * 0.3) + (titleScore * 0.3);
    }

    static async Task<(string href, string text, double score)?> SearchLyrics(HttpClient client, string searchQuery)
    {
        string formattedQuery = HttpUtility.UrlEncode(searchQuery.Replace(' ', '+'));
        string searchUrl = $"https://search.azlyrics.com/search.php?q={formattedQuery}&x=1aaabdc6f73f84ddda60f8d362182ab3d11f45dbf49385c5d7098eb5bc80e9b4";

        string html = await client.GetStringAsync(searchUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var table = doc.DocumentNode.SelectSingleNode("//table[contains(@class, 'table-condensed')]");
        if (table == null)
        {
            return null;
        }

        var rows = table.SelectNodes(".//tr");
        if (rows == null || rows.Count == 0)
        {
            return null;
        }

        var results = new List<(string href, string text, double score)>();
        foreach (var row in rows)
        {
            var linkNode = row.SelectSingleNode(".//a");
            if (linkNode != null)
            {
                string href = linkNode.GetAttributeValue("href", "");
                string text = linkNode.InnerText.Trim();
                double score = CalculateMatchScore(searchQuery, text);
                results.Add((href, text, score));
            }
        }

        // Sort results by score in descending order
        results.Sort((a, b) => b.score.CompareTo(a.score));

        return results.Count > 0 ? results[0] : null;
    }

    static async Task<(string href, string text, double score)?> AdvancedSearch(HttpClient client, string originalQuery)
    {
        // Try different search combinations
        var searchAttempts = new List<string>();

        // 1. Original query
        searchAttempts.Add(originalQuery);

        // First split by dash to separate artists from song title
        var mainParts = originalQuery.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
        if (mainParts.Length > 1)
        {
            string artistsPart = mainParts[0].Trim();
            string songTitle = mainParts[1].Trim();

            // Split artists by common separators
            var artists = artistsPart.Split(new[] { ",", "&", "ft.", "feat.", "featuring" }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(a => a.Trim())
                                   .Where(a => !string.IsNullOrWhiteSpace(a))
                                   .ToList();

            // Try each artist with the song title
            foreach (var artist in artists)
            {
                searchAttempts.Add($"{artist} {songTitle}");
            }

            // Try just the song title
            searchAttempts.Add(songTitle);

            // Try first and last artist with song title
            if (artists.Count > 1)
            {
                searchAttempts.Add($"{artists[0]} {songTitle}");
                searchAttempts.Add($"{artists[artists.Count - 1]} {songTitle}");
            }
        }
        else
        {
            // If no dash found, try splitting the whole query
            var parts = originalQuery.Split(new[] { ",", "&", "ft.", "feat.", "featuring" }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(p => p.Trim())
                                   .Where(p => !string.IsNullOrWhiteSpace(p))
                                   .ToList();

            if (parts.Count > 1)
            {
                string lastPart = parts.Last();
                foreach (var part in parts.Take(parts.Count - 1))
                {
                    searchAttempts.Add($"{part} {lastPart}");
                }
                searchAttempts.Add(lastPart);
            }
        }

        // Try each search attempt
        foreach (var attempt in searchAttempts)
        {
            Console.WriteLine($"Trying search: {attempt}");
            var result = await SearchLyrics(client, attempt);
            if (result.HasValue && result.Value.score > 0.4)
            {
                return result;
            }
        }

        return null;
    }

    static async Task Main(string[] args)
    {
        bool outputToFile = false;
        string searchQuery = null;

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-S" && i + 1 < args.Length)
            {
                // Collect all arguments until we hit another flag
                var queryParts = new List<string>();
                for (int j = i + 1; j < args.Length; j++)
                {
                    if (args[j].StartsWith("-"))
                        break;
                    queryParts.Add(args[j]);
                }
                searchQuery = string.Join(" ", queryParts);
            }
            else if (args[i] == "-o")
            {
                outputToFile = true;
            }
        }

        if (searchQuery == null)
        {
            Console.WriteLine("Usage: LyricsFetch.exe -S \"song name artist\" [-o]");
            Console.WriteLine("-S: Search for lyrics");
            Console.WriteLine("-o: Output lyrics to lyrics.txt");
            return;
        }

        // First split by dash to separate artists from song title
        var mainParts = searchQuery.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
        if (mainParts.Length > 1)
        {
            string artistsPart = mainParts[0].Trim();
            string songTitle = mainParts[1].Trim();

            // Handle parentheses in song title
            string baseTitle = songTitle;
            string featuredArtists = "";
            if (songTitle.Contains("(") && songTitle.Contains(")"))
            {
                var match = Regex.Match(songTitle, @"(.*?)\s*\((.*?)\)");
                if (match.Success)
                {
                    baseTitle = match.Groups[1].Value.Trim();
                    featuredArtists = match.Groups[2].Value.Trim();
                }
            }

            // Split main artists by common separators
            var mainArtists = artistsPart.Split(new[] { ",", "&" }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(a => a.Trim())
                                       .Where(a => !string.IsNullOrWhiteSpace(a))
                                       .ToList();

            // Split featured artists if any
            var featArtists = new List<string>();
            if (!string.IsNullOrEmpty(featuredArtists))
            {
                featArtists = featuredArtists.Split(new[] { ",", "&", "ft.", "feat.", "featuring" }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(a => a.Trim())
                                           .Where(a => !string.IsNullOrWhiteSpace(a))
                                           .ToList();
            }

            // Create search attempts
            var searchAttempts = new List<string>();
            
            // Try main artist with base title
            foreach (var artist in mainArtists)
            {
                searchAttempts.Add($"{artist} {baseTitle}");
            }

            // Try main artist with full title
            foreach (var artist in mainArtists)
            {
                searchAttempts.Add($"{artist} {songTitle}");
            }

            // Try featured artists with base title
            foreach (var artist in featArtists)
            {
                searchAttempts.Add($"{artist} {baseTitle}");
            }

            // Try just the base title
            searchAttempts.Add(baseTitle);

            // Try first main artist with base title
            if (mainArtists.Count > 0)
            {
                searchAttempts.Add($"{mainArtists[0]} {baseTitle}");
            }

            // Try combinations of main and featured artists
            if (mainArtists.Count > 0 && featArtists.Count > 0)
            {
                searchAttempts.Add($"{mainArtists[0]} {baseTitle} feat {string.Join(" ", featArtists)}");
            }

            // Clean each search attempt
            searchAttempts = searchAttempts.Select(CleanSearchQuery).ToList();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                try
                {
                    Console.WriteLine($"Searching AZLyrics for: {searchQuery}\n");

                    // Try each search attempt
                    foreach (var attempt in searchAttempts)
                    {
                        Console.WriteLine($"Trying search: {attempt}");
                        var result = await SearchLyrics(client, attempt);
                        if (result.HasValue && result.Value.score > 0.4)
                        {
                            Console.WriteLine($"Best match: {result.Value.text}\n{result.Value.href}\n");
                            
                            // Fetch lyrics
                            string lyricsHtml = await client.GetStringAsync(result.Value.href);
                            var lyricsDoc = new HtmlDocument();
                            lyricsDoc.LoadHtml(lyricsHtml);
                            var lyricsDiv = lyricsDoc.DocumentNode.SelectNodes("//div[not(@class) and .//br]")?.FirstOrDefault();
                            if (lyricsDiv == null)
                            {
                                lyricsDiv = lyricsDoc.DocumentNode.SelectNodes("//div[contains(@class, 'ringtone')]/following-sibling::div[1]")?.FirstOrDefault();
                            }

                            if (lyricsDiv != null)
                            {
                                string lyrics = lyricsDiv.InnerText.Trim();
                                
                                if (outputToFile)
                                {
                                    try
                                    {
                                        string filePath = "lyrics.txt";
                                        File.WriteAllText(filePath, lyrics);
                                        Console.WriteLine($"Successfully saved lyrics to lyrics.txt");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Error saving to file: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Lyrics:\n");
                                    Console.WriteLine(lyrics);
                                }
                            }
                            else
                            {
                                Console.WriteLine("Could not find lyrics div on the lyrics page.");
                            }
                            return;
                        }
                    }

                    Console.WriteLine("No suitable match found.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }
        else
        {
            // If no dash found, try the original search
            searchQuery = CleanSearchQuery(searchQuery);
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                try
                {
                    Console.WriteLine($"Searching AZLyrics for: {searchQuery}\n");
                    var result = await SearchLyrics(client, searchQuery);
                    if (result.HasValue)
                    {
                        Console.WriteLine($"Best match: {result.Value.text}\n{result.Value.href}\n");
                        
                        // Fetch lyrics
                        string lyricsHtml = await client.GetStringAsync(result.Value.href);
                        var lyricsDoc = new HtmlDocument();
                        lyricsDoc.LoadHtml(lyricsHtml);
                        var lyricsDiv = lyricsDoc.DocumentNode.SelectNodes("//div[not(@class) and .//br]")?.FirstOrDefault();
                        if (lyricsDiv == null)
                        {
                            lyricsDiv = lyricsDoc.DocumentNode.SelectNodes("//div[contains(@class, 'ringtone')]/following-sibling::div[1]")?.FirstOrDefault();
                        }

                        if (lyricsDiv != null)
                        {
                            string lyrics = lyricsDiv.InnerText.Trim();
                            
                            if (outputToFile)
                            {
                                try
                                {
                                    string filePath = "lyrics.txt";
                                    File.WriteAllText(filePath, lyrics);
                                    Console.WriteLine($"Successfully saved lyrics to lyrics.txt");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error saving to file: {ex.Message}");
                                }
                            }
                            else
                            {
                                Console.WriteLine("Lyrics:\n");
                                Console.WriteLine(lyrics);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Could not find lyrics div on the lyrics page.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("No suitable match found.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }
    }
}
