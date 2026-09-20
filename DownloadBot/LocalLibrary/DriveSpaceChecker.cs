namespace DownloadBot.LocalLibrary;

public sealed record DriveSpace(string Name, double FreeGb, double TotalGb);

public interface IDriveSpaceChecker
{
    IReadOnlyList<DriveSpace> GetFreeSpace(string? specificDrive = null);
}

// Same drive-selection rule as PlexLibraryScanner: every attached drive except C:.
public sealed class DriveSpaceChecker : IDriveSpaceChecker
{
    private const double BytesPerGb = 1024d * 1024 * 1024;

    public IReadOnlyList<DriveSpace> GetFreeSpace(string? specificDrive = null)
    {
        var normalized = NormalizeDriveName(specificDrive);

        return DriveInfo.GetDrives()
            .Where(d => !d.Name.TrimEnd('\\').Equals("C:", StringComparison.OrdinalIgnoreCase))
            .Where(d => d.IsReady)
            .Where(d => normalized is null || d.Name.TrimEnd('\\').Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(d => new DriveSpace(d.Name.TrimEnd('\\'), d.AvailableFreeSpace / BytesPerGb, d.TotalSize / BytesPerGb))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Accepts "g", "G", "G:", "g:\", "G:\\" — anything a user might reasonably type — and normalizes
    // to the "G:" form DriveInfo.Name uses (minus the trailing backslash, stripped above).
    public static string? NormalizeDriveName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim().TrimEnd('\\', '/');
        if (!trimmed.EndsWith(':'))
            trimmed += ":";

        return trimmed.ToUpperInvariant();
    }
}
