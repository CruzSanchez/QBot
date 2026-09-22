namespace DownloadBot.Discord;

public static class CentralTime
{
    private static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
    }

    public static string Format(DateTimeOffset utc) =>
        $"{TimeZoneInfo.ConvertTime(utc, Zone):yyyy-MM-dd HH:mm:ss} CST";
}
