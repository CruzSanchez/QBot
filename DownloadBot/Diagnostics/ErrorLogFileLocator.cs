namespace DownloadBot.Diagnostics;

// Mirrors Program.cs's Serilog file-sink naming ("logs/downloadbot-.log" with daily rolling — Serilog
// inserts the date before the extension). Pure and I/O-free, directly unit-testable.
public static class ErrorLogFileLocator
{
    public static string GetTodaysLogFilePath(DateTime now, string logsDirectory = "logs", string filePrefix = "downloadbot-") =>
        Path.Combine(logsDirectory, $"{filePrefix}{now:yyyyMMdd}.log");
}
