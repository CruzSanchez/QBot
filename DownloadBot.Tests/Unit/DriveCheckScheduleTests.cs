using DownloadBot.Discord;

namespace DownloadBot.Tests.Unit;

public class DriveCheckScheduleTests
{
    private static readonly TimeZoneInfo Zone = CentralTime.Zone;

    private static DateTimeOffset Utc(DateTime local) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);

    private static DateTime LocalOf(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;

    [Theory]
    [InlineData("2026-10-09 07:59:00", "2026-10-09 08:00:00")]
    [InlineData("2026-10-09 08:00:00", "2026-10-09 12:00:00")] // exactly on a run time -> the next one, never itself
    [InlineData("2026-10-09 12:30:00", "2026-10-09 16:00:00")]
    [InlineData("2026-10-09 15:59:00", "2026-10-09 16:00:00")]
    [InlineData("2026-10-09 16:00:00", "2026-10-09 22:00:00")] // exactly on 4 PM -> the next slot
    [InlineData("2026-10-09 17:00:00", "2026-10-09 22:00:00")]
    [InlineData("2026-10-09 22:00:00", "2026-10-10 08:00:00")]
    [InlineData("2026-10-09 23:30:00", "2026-10-10 08:00:00")]
    [InlineData("2026-10-09 00:05:00", "2026-10-09 08:00:00")]
    public void NextRunUtc_PicksTheNextEightTwelveFourOrTenPmCentral(string nowLocal, string expectedLocal)
    {
        var next = DriveCheckSchedule.NextRunUtc(Utc(DateTime.Parse(nowLocal)), Zone);

        Assert.Equal(DateTime.Parse(expectedLocal), LocalOf(next));
    }

    [Fact]
    public void NextRunUtc_FollowsDaylightSavingTime()
    {
        // October is CDT (UTC-5): 8:00 AM Central = 13:00 UTC. December is CST (UTC-6): 8:00 AM = 14:00 UTC.
        var summer = DriveCheckSchedule.NextRunUtc(new DateTimeOffset(2026, 10, 9, 5, 0, 0, TimeSpan.Zero), Zone);
        var winter = DriveCheckSchedule.NextRunUtc(new DateTimeOffset(2026, 12, 9, 5, 0, 0, TimeSpan.Zero), Zone);

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 13, 0, 0, TimeSpan.Zero), summer);
        Assert.Equal(new DateTimeOffset(2026, 12, 9, 14, 0, 0, TimeSpan.Zero), winter);
    }
}
