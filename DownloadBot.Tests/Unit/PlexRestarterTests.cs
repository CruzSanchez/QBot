using DownloadBot.Plex;

namespace DownloadBot.Tests.Unit;

public class PlexRestarterTests
{
    [Fact]
    public void BuildArguments_PassesScriptExeAndWaitAsSeparateArguments()
    {
        // ArgumentList entries, not one string: paths with spaces ("Program Files", "Plex Media Server")
        // must reach PowerShell intact without any manual quoting.
        var args = PlexRestarter.BuildArguments(@"C:\repo\restart-plex.ps1", @"C:\Program Files\Plex\Plex Media Server\Plex Media Server.exe", 90);

        Assert.Equal(
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\repo\restart-plex.ps1",
             "-PlexExe", @"C:\Program Files\Plex\Plex Media Server\Plex Media Server.exe", "-WaitSeconds", "90"],
            args);
    }
}
