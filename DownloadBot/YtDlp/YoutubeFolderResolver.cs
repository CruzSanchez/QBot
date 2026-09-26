namespace DownloadBot.YtDlp;

// Resolves a Discord-user-supplied folder name (from /rename-folder's autocompleted "folder" option —
// autocomplete only *suggests* existing names, it doesn't stop someone typing something else) against
// a real, direct child of root. Pure filesystem-path logic, no I/O beyond the final Directory.Exists
// check, so the traversal guard itself is directly unit-testable.
public static class YoutubeFolderResolver
{
    public static string? ResolveExisting(string root, string requested)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, requested));

        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        return Directory.Exists(candidate) ? candidate : null;
    }
}
