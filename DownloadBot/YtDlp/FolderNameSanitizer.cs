using System.Text.RegularExpressions;

namespace DownloadBot.YtDlp;

// Shared by YtDlpRunner (addtofolder/newfoldername) and /rename-folder — a folder name only ever
// gets letters, digits, spaces, '-', or '_'. Anything else (commas, colons, periods, quotes, path
// separators, ...) is replaced with a space rather than dropped outright, so e.g. "A: B" becomes
// "A B" instead of accidentally merging into "AB". Pure and I/O-free, directly unit-testable.
public static partial class FolderNameSanitizer
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    public static (string Sanitized, bool WasChanged) Sanitize(string input)
    {
        var replaced = new string(input.Select(c => IsAllowed(c) ? c : ' ').ToArray());
        var collapsed = WhitespaceRun().Replace(replaced, " ").Trim();
        var result = string.IsNullOrEmpty(collapsed) ? "Untitled" : collapsed;

        return (result, !string.Equals(result, input, StringComparison.Ordinal));
    }

    private static bool IsAllowed(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ';
}
