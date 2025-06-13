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
                
                // Penalize remixes, mashups, and Pt 2 variations
                double penalty = 0;
                if (title.Contains("remix") || title.Contains("(remix)"))
                {
                    penalty += 0.3; // Penalty for remixes
                }
                if (title.Contains("mashup"))
                {
                    penalty += 0.3; // Penalty for mashups
                }

                // Check for Pt 2 variations
                bool hasPt2 = Regex.IsMatch(title, @"pt\.?\s*2|part\s*2|pt2", RegexOptions.IgnoreCase);
                bool searchHasPt2 = Regex.IsMatch(normalizedQuery, @"pt\.?\s*2|part\s*2|pt2", RegexOptions.IgnoreCase);
                
                // If result has Pt 2 but search doesn't, apply penalty
                if (hasPt2 && !searchHasPt2)
                {
                    penalty += 0.4; // Higher penalty for Pt 2 when not searched for
                }

                if (title.Contains(normalizedQuery))
                {
                    titleScore = 1.0 - penalty;
                }
                else
                {
                    // Check how many query words are in the title
                    int matchingWords = queryWords.Count(w => title.Contains(w));
                    titleScore = ((double)matchingWords / queryWords.Length) - penalty;
                }
            }
        }

        // Calculate artist match score
        double artistScore = 0;
        var searchArtists = normalizedQuery.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries)[0].ToLower();
        var searchArtistList = searchArtists.Split(new[] { ",", "&", "ft.", "feat.", "featuring", "/" }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(a => a.Trim())
                                          .Where(a => !string.IsNullOrWhiteSpace(a))
                                          .ToList();

        // Try different artist patterns
        var patterns = new[] {
            @"- ([^-]+)$",
            @"performed by ([^-]+)$",
            @"by ([^-]+)$"
        };

        foreach (var pattern in patterns)
        {
            var artistMatch = Regex.Match(normalizedResult, pattern, RegexOptions.IgnoreCase);
            if (artistMatch.Success)
            {
                string resultArtists = artistMatch.Groups[1].Value.ToLower().Trim();
                var resultArtistList = resultArtists.Split(new[] { ",", "&", "ft.", "feat.", "featuring", "/" }, StringSplitOptions.RemoveEmptyEntries)
                                                  .Select(a => a.Trim())
                                                  .Where(a => !string.IsNullOrWhiteSpace(a))
                                                  .ToList();

                int matchingArtists = 0;
                foreach (var searchArtist in searchArtistList)
                {
                    if (resultArtistList.Any(r => r.Contains(searchArtist) || searchArtist.Contains(r)))
                    {
                        matchingArtists++;
                    }
                }
                artistScore = Math.Max(artistScore, (double)matchingArtists / searchArtistList.Count);
            }
        }

        // Combine scores with weights
        return (wordMatchScore * 0.3) + (positionScore * 0.2) + (titleScore * 0.3) + (artistScore * 0.2);
    }

    static async Task<(string href, string text, double score)?> SearchLyrics(HttpClient client, string searchQuery)
    {
        string formattedQuery = HttpUtility.UrlEncode(searchQuery.Replace(' ', '+'));
        
        // Try first URL format
        string searchUrl1 = $"https://search.azlyrics.com/search.php?q={formattedQuery}&x=1aaabdc6f73f84ddda60f8d362182ab3d11f45dbf49385c5d7098eb5bc80e9b4";
        var result1 = await TrySearchWithUrl(client, searchUrl1, searchQuery);
        if (result1.HasValue)
        {
            return result1;
        }

        // Try second URL format as fallback
        string searchUrl2 = $"https://search.azlyrics.com/?q={formattedQuery}&x=1c4c543a750e03040717042df81160e8879de9efc44b5691c99cb0a7eaad8b3f";
        return await TrySearchWithUrl(client, searchUrl2, searchQuery);
    }

    static async Task<(string href, string text, double score)?> TrySearchWithUrl(HttpClient client, string searchUrl, string searchQuery)
    {
        try
        {
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
        catch (Exception)
        {
            return null;
        }
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

            // Split main artists by common separators
            var mainArtists = artistsPart.Split(new[] { ",", "&", "ft.", "feat.", "featuring", "/" }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(a => a.Trim())
                                   .Where(a => !string.IsNullOrWhiteSpace(a))
                                   .ToList();

            // Try each artist with the song title
            foreach (var artist in mainArtists)
            {
                searchAttempts.Add($"{artist} {songTitle}");
            }

            // Only try just the song title if it's not a single short word
            if (songTitle.Split(' ').Length > 1 || songTitle.Length > 3)
            {
            searchAttempts.Add(songTitle);
            }

            // Try first and last artist with song title
            if (mainArtists.Count > 1)
            {
                searchAttempts.Add($"{mainArtists[0]} {songTitle}");
                searchAttempts.Add($"{mainArtists[mainArtists.Count - 1]} {songTitle}");
            }
        }
        else
        {
            // If no dash found, try splitting the whole query
            var parts = originalQuery.Split(new[] { ",", "&", "ft.", "feat.", "featuring", "/" }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(p => p.Trim())
                                   .Where(p => !string.IsNullOrWhiteSpace(p))
                                   .ToList();

            if (parts.Count > 1)
            {
                string lastPart = parts.Last();
                // Only try single-word searches if the word is long enough
                if (lastPart.Split(' ').Length > 1 || lastPart.Length > 3)
                {
                foreach (var part in parts.Take(parts.Count - 1))
                {
                    searchAttempts.Add($"{part} {lastPart}");
                }
                searchAttempts.Add(lastPart);
                }
                else
                {
                    // If the last part is too short, only try combinations
                    foreach (var part in parts.Take(parts.Count - 1))
                    {
                        searchAttempts.Add($"{part} {lastPart}");
                    }
                }
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

        using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                try
                {
                    Console.WriteLine($"Searching AZLyrics for: {searchQuery}\n");

                // First try with the original query
                var result = await SearchLyrics(client, searchQuery);
                
                // If no results found, try different variations
                if (!result.HasValue)
                {
                    // Try without the dash if it exists
                    if (searchQuery.Contains(" - "))
                    {
                        string fallbackQuery = searchQuery.Replace(" - ", " ");
                        Console.WriteLine($"\nNo results found. Trying alternative search: {fallbackQuery}\n");
                        result = await SearchLyrics(client, fallbackQuery);
                    }

                    // If still no results and query contains a slash, try different variations
                    if (!result.HasValue && searchQuery.Contains("/"))
                    {
                        // Try with space instead of slash
                        string slashQuery = searchQuery.Replace("/", " ");
                        Console.WriteLine($"\nNo results found. Trying alternative search: {slashQuery}\n");
                        result = await SearchLyrics(client, slashQuery);

                        // If still no results, try each artist individually
                        if (!result.HasValue)
                        {
                            var parts = searchQuery.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                string songTitle = parts[1].Trim();
                                var artists = parts[0].Split(new[] { "/" }, StringSplitOptions.RemoveEmptyEntries)
                                                    .Select(a => a.Trim())
                                                    .Where(a => !string.IsNullOrWhiteSpace(a))
                                                    .ToList();

                                foreach (var artist in artists)
                                {
                                    string individualQuery = $"{artist} - {songTitle}";
                                    Console.WriteLine($"\nTrying individual artist search: {individualQuery}\n");
                                    result = await SearchLyrics(client, individualQuery);
                                    if (result.HasValue)
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

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
