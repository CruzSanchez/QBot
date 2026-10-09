namespace DownloadBot.Discord;

// Pure "when does the next scheduled drive report fire" logic — separate from the service so the
// time-zone/daylight-saving handling can be tested without waiting on a real clock.
public static class DriveCheckSchedule
{
    // Wall-clock times in the given zone (Central, so it follows daylight saving like the rest of the
    // bot's timestamps): 8:00 AM, 12:00 PM, 10:00 PM.
    private static readonly TimeSpan[] RunTimes = [TimeSpan.FromHours(8), TimeSpan.FromHours(12), TimeSpan.FromHours(22)];

    // First run strictly after nowUtc ("strictly" so a run that just fired never schedules itself again).
    public static DateTimeOffset NextRunUtc(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;

        for (var dayOffset = 0; dayOffset <= 1; dayOffset++)
        {
            foreach (var time in RunTimes)
            {
                var candidate = localNow.Date.AddDays(dayOffset) + time;
                if (candidate > localNow)
                    return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, zone), TimeSpan.Zero);
            }
        }

        throw new InvalidOperationException("Unreachable: tomorrow's first run is always after now.");
    }
}
