using DownloadBot.Diagnostics;

namespace DownloadBot.Tests.Unit;

public class ErrorLogFileLocatorTests
{
    [Fact]
    public void GetTodaysLogFilePath_MatchesSerilogsRollingFileNamingConvention()
    {
        var date = new DateTime(2026, 3, 5);

        var path = ErrorLogFileLocator.GetTodaysLogFilePath(date);

        Assert.Equal(Path.Combine("logs", "downloadbot-20260305.log"), path);
    }

    [Fact]
    public void GetTodaysLogFilePath_HonorsCustomDirectoryAndPrefix()
    {
        var date = new DateTime(2026, 12, 25);

        var path = ErrorLogFileLocator.GetTodaysLogFilePath(date, logsDirectory: "custom-logs", filePrefix: "app-");

        Assert.Equal(Path.Combine("custom-logs", "app-20261225.log"), path);
    }

    [Fact]
    public void GetTodaysLogFilePath_PadsSingleDigitMonthAndDay()
    {
        var date = new DateTime(2026, 1, 9);

        var path = ErrorLogFileLocator.GetTodaysLogFilePath(date);

        Assert.Equal(Path.Combine("logs", "downloadbot-20260109.log"), path);
    }
}
