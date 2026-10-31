using System.Text;
using System.Text.RegularExpressions;
using DownloadBot.LocalLibrary;

namespace DownloadBot.Discord;

public sealed record BulkUploadItem(string Query, string Type);

public sealed record BulkUploadParseResult(IReadOnlyList<BulkUploadItem> Items, IReadOnlyList<string> Skipped);

// Reads a BulkUpload file in the same shape as a Plex library listing: CSV rows of "Library,Path,..." (a
// header row and extra columns like size/date are fine). The library column picks the download type, and the
// title is taken from the first folder under the library ("Movies\Jason X (1990) [1080p]\file.mp4" ->
// "Jason X 1990"), so many files from one show/movie collapse into a single search.
public static partial class BulkUploadParser
{
    private static readonly Dictionary<string, string> TypesByLibrary = new()
    {
        ["movies"] = "movie",
        ["tvshows"] = "tv",
        ["kidsmovies"] = "kids-movie",
        ["kidstvshows"] = "kids-tv",
        ["music"] = "music"
    };

    public static BulkUploadParseResult Parse(string text)
    {
        var items = new List<BulkUploadItem>();
        var skipped = new List<string>();
        var seen = new HashSet<(string, string)>();

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            var fields = SplitCsvLine(line);
            if (index == 0 && fields[0].Equals("Library", StringComparison.OrdinalIgnoreCase))
                continue;

            var library = fields[0];
            var path = fields.Count > 1 ? fields[1] : "";

            if (!TypesByLibrary.TryGetValue(NormalizeLibrary(library), out var type))
            {
                skipped.Add($"{line} (unknown library \"{library}\")");
                continue;
            }

            var query = BuildQuery(path);
            if (query.Length == 0)
            {
                skipped.Add($"{line} (couldn't work out a title)");
                continue;
            }

            if (seen.Add((type, query.ToLowerInvariant())))
                items.Add(new BulkUploadItem(query, type));
        }

        return new BulkUploadParseResult(items, skipped);
    }

    private static string NormalizeLibrary(string library) =>
        library.Replace(" ", "").ToLowerInvariant();

    // First folder under the library, or the file name itself when the file sits directly in the library.
    private static string BuildQuery(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return "";

        var name = parts.Length > 2 ? parts[1] : Path.GetFileNameWithoutExtension(parts[1]);
        name = SiteTagRegex().Replace(name, "");
        name = BracketsRegex().Replace(name, " ");

        var (title, year) = TitleYear.Parse(name);
        title = title.Trim(' ', '-', '–', ':');
        if (title.Length == 0)
            return "";

        return year is null ? title : $"{title} {year}";
    }

    // Minimal CSV: fields may be wrapped in double quotes (which may hold commas and "" for a literal quote).
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    [GeneratedRegex(@"^\s*www\.\S+\s*[-–]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SiteTagRegex();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketsRegex();
}
