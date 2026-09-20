using DownloadBot.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DownloadBot.Tests.Integration;

// Read-only — searches only, never adds anything. Skips (does not fail) when Jackett isn't reachable
// or hasn't been configured, so `dotnet test` is quiet on a machine where the server isn't running,
// and meaningful when it is.
public class JackettClientIntegrationTests
{
    private static JackettOptions LoadOptions()
    {
        var options = new JackettOptions();
        TestConfiguration.Root.GetSection("Jackett").Bind(options);
        return options;
    }

    [SkippableFact]
    public async Task SearchAsync_ReturnsResultsForACommonTitle()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrWhiteSpace(options.ApiKey), "Jackett:ApiKey is not configured — skipping live Jackett test.");

        var client = new JackettClient(new HttpClient(), Options.Create(options));

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await client.SearchAsync("The Matrix 1999");
        }
        catch (HttpRequestException ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Jackett at {options.BaseUrl} is not reachable — skipping. ({ex.Message})");
        }

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Title)));
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.MagnetOrTorrentLink)));
    }
}
