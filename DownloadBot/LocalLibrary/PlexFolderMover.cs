using System.Diagnostics;

namespace DownloadBot.LocalLibrary;

public static class PlexFolderMover
{
    // Same drive: an instant rename. Different drive: Directory.Move can't cross volumes, so robocopy
    // /MOVE (built into Windows) copies and then removes each file — a partial failure leaves a
    // consistent split between source and destination rather than a half-deleted original.
    public static async Task<(bool Ok, string? Error)> MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(destination))
            return (false, "A folder with that name already exists at the destination.");

        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Move(source, destination);
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        return await MoveWithRobocopyAsync(source, destination, cancellationToken);
    }

    public static async Task<(bool Ok, string? Error)> MoveWithRobocopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "robocopy",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in new[] { source, destination, "/E", "/MOVE", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP" })
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo)!;
            // Output is discarded, but has to be drained or a full pipe buffer would stall robocopy.
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(cancellationToken), process.StandardError.ReadToEndAsync(cancellationToken));
            await process.WaitForExitAsync(cancellationToken);
            await drain;

            // robocopy exit codes below 8 mean success (bit flags: files copied, extras, mismatches...).
            if (process.ExitCode >= 8)
                return (false, $"robocopy failed with exit code {process.ExitCode}; some files may have been moved already.");

            return Directory.Exists(source)
                ? (false, "Files were copied but the original folder couldn't be fully removed.")
                : (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
