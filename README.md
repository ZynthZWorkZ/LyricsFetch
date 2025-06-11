# LyricsFetch

A command-line application that fetches song lyrics from AZLyrics.

## Requirements

- .NET 9.0 SDK or later
- Internet connection

## Installation

1. Clone this repository
2. Navigate to the project directory
3. Build the project:
```bash
dotnet build
```

## Usage

The application accepts the following command-line arguments:

- `-S`: Search for lyrics (required)
- `-o`: Output lyrics to a file (optional)

### Examples

1. Search for lyrics and display them in the console:
```bash
LyricsFetch.exe -S "Artist - Song Title"
```

2. Search for lyrics and save them to a file:
```bash
LyricsFetch.exe -S "Artist - Song Title" -o
```

### Search Format

You can search for songs in the following formats:
- `"Artist - Song Title"`
- `"Artist - Song Title (feat. Other Artist)"`
- `"Artist1, Artist2 - Song Title"`
- `"Artist1 & Artist2 - Song Title"`
- `"Artist1 ft. Artist2 - Song Title"`
- `"Artist1 featuring Artist2 - Song Title"`
- `"Artist1 / Artist2 - Song Title"`

The application supports advanced search capabilities:
- Multiple artist separators: `,`, `&`, `ft.`, `feat.`, `featuring`, `/`
- Automatic handling of remixes and mashups
- Smart handling of "Part 2" or "Pt 2" variations
- Fallback searches if the initial search fails
- Automatic artist name variations and combinations

The search algorithm considers:
- Exact title matches
- Artist name matches
- Word position and relevance
- Special cases like remixes and mashups
- Multiple search attempts with different combinations

The application will automatically try different combinations of the search query to find the best match, including:
- Original query
- Individual artist searches
- Title-only searches
- Various artist combinations
- Alternative separators

### Output

- If `-o` is not specified, lyrics will be displayed in the console
- If `-o` is specified, lyrics will be saved to `lyrics.txt` in the current directory

## Notes

- The application uses AZLyrics as the source for lyrics
- Search results are ranked based on relevance to your query
- The application will try multiple search combinations to find the best match 
