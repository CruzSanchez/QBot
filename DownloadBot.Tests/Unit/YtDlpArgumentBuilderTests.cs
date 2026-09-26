using DownloadBot.YtDlp;

namespace DownloadBot.Tests.Unit;

public class YtDlpArgumentBuilderTests
{
    private static YtDlpOptions Options(Action<YtDlpOptions>? configure = null)
    {
        var options = new YtDlpOptions
        {
            CookiesFilePath = "",
            DownloadArchivePath = "",
            SponsorBlockRemoveCategories = "",
            FfmpegLocation = ""
        };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void Build_AlwaysEndsWithSeparatorThenUrl()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com/watch?v=x", "C:\\dest", null, null, Options());

        Assert.Equal("--", args[^2]);
        Assert.Equal("https://example.com/watch?v=x", args[^1]);
    }

    [Fact]
    public void Build_AlwaysIncludesIgnoreErrorsAndRetries()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null, Options(o => o.Retries = 3));

        Assert.Contains("--ignore-errors", args);
        AssertFollowedBy(args, "--retries", "3");
        AssertFollowedBy(args, "--fragment-retries", "3");
    }

    [Fact]
    public void Build_OmitsCookiesWhenNotConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null, Options());

        Assert.DoesNotContain("--cookies", args);
    }

    [Fact]
    public void Build_IncludesCookiesWhenConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null,
            Options(o => o.CookiesFilePath = "C:\\cookies.txt"));

        AssertFollowedBy(args, "--cookies", "C:\\cookies.txt");
    }

    [Fact]
    public void Build_OmitsDownloadArchiveWhenNotConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null, Options());

        Assert.DoesNotContain("--download-archive", args);
    }

    [Fact]
    public void Build_IncludesDownloadArchiveWhenConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null,
            Options(o => o.DownloadArchivePath = "data/archive.txt"));

        AssertFollowedBy(args, "--download-archive", "data/archive.txt");
    }

    [Fact]
    public void Build_OmitsSponsorBlockWhenNotConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null, Options());

        Assert.DoesNotContain("--sponsorblock-remove", args);
    }

    [Fact]
    public void Build_IncludesSponsorBlockWhenConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null,
            Options(o => o.SponsorBlockRemoveCategories = "sponsor,selfpromo"));

        AssertFollowedBy(args, "--sponsorblock-remove", "sponsor,selfpromo");
    }

    [Fact]
    public void Build_OmitsFfmpegLocationWhenNotConfigured()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null, Options());

        Assert.DoesNotContain("--ffmpeg-location", args);
    }

    [Fact]
    public void Build_UsesTitleTokenForDefaultFolder()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", null, null, Options());

        var outputIndex = args.ToList().IndexOf("-o");
        Assert.Equal(Path.Combine("C:\\dest", "%(title)s", "%(title)s.%(ext)s"), args[outputIndex + 1]);
    }

    [Fact]
    public void Build_SanitizesUserSuppliedFolderName()
    {
        var args = YtDlpArgumentBuilder.Build("https://example.com", "C:\\dest", "Rocket: League", null, Options());

        var outputIndex = args.ToList().IndexOf("-o");
        Assert.Equal(Path.Combine("C:\\dest", "Rocket League", "%(title)s.%(ext)s"), args[outputIndex + 1]);
    }

    private static void AssertFollowedBy(IReadOnlyList<string> args, string flag, string value)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"Expected to find '{flag}' in the argument list.");
        Assert.Equal(value, args[index + 1]);
    }
}
