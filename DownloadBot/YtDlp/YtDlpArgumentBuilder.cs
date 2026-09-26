namespace DownloadBot.YtDlp;

// Builds the full yt-dlp argument list for one invocation. Pure and I/O-free (the only exception is
// the caller's responsibility to ensure DownloadArchivePath's directory exists before starting the
// process) — directly unit-testable, and keeps YtDlpRunner focused on actually running the process
// rather than assembling its arguments.
public static class YtDlpArgumentBuilder
{
    public static IReadOnlyList<string> Build(
        string url, string destinationDirectory, string? folderName, string? quality, YtDlpOptions options)
    {
        var args = new List<string>
        {
            "-f", YtDlpFormatSelector.Build(quality, options.DefaultMaxHeight),
            "--merge-output-format", options.MergeOutputFormat,
            "--max-downloads", options.MaxDownloadsPerInvocation.ToString(),
            // A private/deleted/age-restricted video partway through a playlist shouldn't abort the
            // whole thing — skip it and keep going.
            "--ignore-errors",
            "--retries", options.Retries.ToString(),
            "--fragment-retries", options.Retries.ToString()
        };

        // Cleans the title metadata itself (used below for both the default per-video folder name and
        // the file name) down to the same allowed set FolderNameSanitizer enforces for user-typed
        // folder names — letters, digits, spaces, '-', '_'. Order: strip disallowed chars to a space,
        // collapse runs, trim ends.
        args.AddRange(["--replace-in-metadata", "title", @"[^\w\s-]", " "]);
        args.AddRange(["--replace-in-metadata", "title", @"\s+", " "]);
        args.AddRange(["--replace-in-metadata", "title", @"^\s+|\s+$", ""]);

        // Plex's scanners generally expect a video to sit in its own folder rather than a flat pile of
        // files in one directory, or it may not show up in the library at all. Default (no folderName)
        // gives each video its own folder named after its (now-sanitized) title; passing folderName
        // instead groups several related videos together (e.g. as one Plex "show"/season).
        var folderComponent = folderName is null ? "%(title)s" : FolderNameSanitizer.Sanitize(folderName).Sanitized;
        args.AddRange(["-o", Path.Combine(destinationDirectory, folderComponent, "%(title)s.%(ext)s")]);

        args.AddRange(["--print", "after_move:filepath"]);
        args.Add("--newline");

        if (!string.IsNullOrWhiteSpace(options.FfmpegLocation))
            args.AddRange(["--ffmpeg-location", options.FfmpegLocation]);

        if (!string.IsNullOrWhiteSpace(options.CookiesFilePath))
            args.AddRange(["--cookies", options.CookiesFilePath]);

        if (!string.IsNullOrWhiteSpace(options.DownloadArchivePath))
            args.AddRange(["--download-archive", options.DownloadArchivePath]);

        if (!string.IsNullOrWhiteSpace(options.SponsorBlockRemoveCategories))
            args.AddRange(["--sponsorblock-remove", options.SponsorBlockRemoveCategories]);

        // Literal separator: without it, a URL string that happened to start with "-" could be parsed
        // by yt-dlp as an option instead of a positional argument. Must stay last.
        args.AddRange(["--", url]);

        return args;
    }
}
