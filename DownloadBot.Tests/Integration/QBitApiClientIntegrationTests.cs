using DownloadBot.QBittorrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DownloadBot.Tests.Integration;

// Deliberately read-only: logs in and lists torrents, nothing more. AddTorrentAsync is NOT exercised
// here on purpose — an automated test that adds a real torrent to a real qBittorrent instance every
// time `dotnet test` runs is a side effect nobody asked for. If you want that covered too, say so
// explicitly and we can add an opt-in test gated behind an environment variable, using a small
// well-known public-domain torrent so it's not adding junk to your real library.
public class QBitApiClientIntegrationTests
{
    private static QBittorrentOptions LoadOptions()
    {
        var options = new QBittorrentOptions();
        TestConfiguration.Root.GetSection("QBittorrent").Bind(options);
        return options;
    }

    private static QBitApiClient CreateClient(QBittorrentOptions options)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true
        };
        return new QBitApiClient(new HttpClient(handler), Options.Create(options));
    }

    [SkippableFact]
    public async Task GetAllTorrentsAsync_LogsInAndReturnsTheTorrentList()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrWhiteSpace(options.Username), "QBittorrent:Username is not configured — skipping live qBittorrent test.");

        var client = CreateClient(options);

        IReadOnlyList<TorrentState> torrents;
        try
        {
            torrents = await client.GetAllTorrentsAsync();
        }
        catch (HttpRequestException ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"qBittorrent at {options.BaseUrl} is not reachable — skipping. ({ex.Message})");
        }

        // The list itself can legitimately be empty (no torrents added) — success here just means
        // authentication worked and the response parsed without throwing.
        Assert.NotNull(torrents);
    }

    [SkippableFact]
    public async Task GetTorrentStateAsync_ReturnsNullForAHashThatDoesNotExist()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrWhiteSpace(options.Username), "QBittorrent:Username is not configured — skipping live qBittorrent test.");

        var client = CreateClient(options);

        TorrentState? state;
        try
        {
            state = await client.GetTorrentStateAsync("0000000000000000000000000000000000000000");
        }
        catch (HttpRequestException ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"qBittorrent at {options.BaseUrl} is not reachable — skipping. ({ex.Message})");
        }

        Assert.Null(state);
    }
}
