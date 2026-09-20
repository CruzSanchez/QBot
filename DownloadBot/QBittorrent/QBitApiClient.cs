using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DownloadBot.QBittorrent;

public interface IQBitApiClient
{
    Task<TorrentState?> GetTorrentStateAsync(string infoHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TorrentState>> GetAllTorrentsAsync(CancellationToken cancellationToken = default);
    Task AddTorrentAsync(string urlOrMagnet, string savePath, CancellationToken cancellationToken = default);
    Task StopTorrentAsync(string infoHash, CancellationToken cancellationToken = default);
    Task RemoveTorrentAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default);
}

public sealed record TorrentState(string Hash, string Name, string State, double Progress, long DownloadSpeedBytesPerSec = 0, long EtaSeconds = 0)
{
    // qBittorrent states meaning the download itself has finished (seeding/uploading states, paused-after-completion, etc).
    private static readonly HashSet<string> CompletedStates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "uploading", "stalledUP", "pausedUP", "queuedUP", "forcedUP", "checkingUP"
        };

    public bool IsComplete => Progress >= 1.0 || CompletedStates.Contains(State);

    // qBittorrent states meaning the download itself failed — a bad tracker, missing/removed source
    // files, etc. This is distinct from IsComplete: something the user should be alerted about instead
    // of silently waiting on forever.
    private static readonly HashSet<string> ErrorStates =
        new(StringComparer.OrdinalIgnoreCase) { "error", "missingFiles" };

    public bool IsError => ErrorStates.Contains(State);

    // States where a torrent is still actively in the download pipeline (progressing, waiting on a
    // slot, verifying, etc.) — distinct from paused, which the user didn't ask to be told about.
    private static readonly HashSet<string> ActiveDownloadStates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "downloading", "metaDL", "forcedDL", "allocating", "checkingDL", "stalledDL", "queuedDL"
        };

    public bool IsActiveDownload => ActiveDownloadStates.Contains(State);
}

public sealed class QBitApiClient(HttpClient httpClient, IOptions<QBittorrentOptions> options) : IQBitApiClient
{
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public async Task<TorrentState?> GetTorrentStateAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        await EnsureLoggedInAsync(cancellationToken);

        var opts = options.Value;
        var url = $"{opts.BaseUrl.TrimEnd('/')}/api/v2/torrents/info?hashes={infoHash.ToLowerInvariant()}";
        var response = await httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Session cookie expired; force a re-login on the next call.
            _loggedIn = false;
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var torrent = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (torrent.ValueKind != JsonValueKind.Object)
            return null;

        return ParseTorrent(torrent, infoHash);
    }

    public async Task<IReadOnlyList<TorrentState>> GetAllTorrentsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoggedInAsync(cancellationToken);

        var opts = options.Value;
        var url = $"{opts.BaseUrl.TrimEnd('/')}/api/v2/torrents/info";
        var response = await httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _loggedIn = false;
            return [];
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var results = new List<TorrentState>();
        foreach (var torrent in doc.RootElement.EnumerateArray())
        {
            results.Add(ParseTorrent(torrent, torrent.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : ""));
        }

        return results;
    }

    private static TorrentState ParseTorrent(JsonElement torrent, string fallbackHash) =>
        new(
            torrent.TryGetProperty("hash", out var hash) ? hash.GetString() ?? fallbackHash : fallbackHash,
            torrent.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            torrent.TryGetProperty("state", out var state) ? state.GetString() ?? "" : "",
            torrent.TryGetProperty("progress", out var progress) ? progress.GetDouble() : 0,
            torrent.TryGetProperty("dlspeed", out var dlspeed) ? dlspeed.GetInt64() : 0,
            torrent.TryGetProperty("eta", out var eta) ? eta.GetInt64() : 0);

    // qBittorrent hands the URL/magnet to its own HTTP client (not ours), sidestepping the redirect
    // and encoding quirks we previously had to work around ourselves just to download a .torrent file.
    // Its "Ok." response is not a reliable success signal on its own (older versions return it even
    // when the add silently failed server-side) — callers must independently confirm the torrent
    // actually appears via GetTorrentStateAsync/GetAllTorrentsAsync afterward.
    public async Task AddTorrentAsync(string urlOrMagnet, string savePath, CancellationToken cancellationToken = default)
    {
        await EnsureLoggedInAsync(cancellationToken);

        var opts = options.Value;
        var url = $"{opts.BaseUrl.TrimEnd('/')}/api/v2/torrents/add";

        using var content = new MultipartFormDataContent
        {
            { new StringContent(urlOrMagnet), "urls" },
            { new StringContent(savePath), "savepath" },
            { new StringContent("false"), "autoTMM" } // explicit: honor our savepath regardless of qBittorrent's global default
        };

        var response = await httpClient.PostAsync(url, content, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _loggedIn = false;
            throw new InvalidOperationException("qBittorrent session expired while adding a torrent");
        }

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (!body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"qBittorrent rejected the add request: {body}");
    }

    // Stops seeding a completed torrent immediately once the bot has observed and reported completion —
    // rather than relying on qBittorrent's own global/per-torrent seeding-limit settings (whose "then"
    // action didn't clearly apply given this account's current config), the bot controls exactly how
    // long its own downloads seed for: only as long as it took to detect completion. Files on disk are
    // untouched — this pauses transfer, it does not remove the torrent or delete anything.
    public async Task StopTorrentAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        await EnsureLoggedInAsync(cancellationToken);

        var hash = infoHash.ToLowerInvariant();

        // qBittorrent 5.0 renamed "pause" to "stop"; older installs only have the /pause endpoint.
        // Try the current name first and fall back so this works across versions.
        var response = await PostHashAsync("stop", hash, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            response = await PostHashAsync("pause", hash, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _loggedIn = false;
            throw new InvalidOperationException("qBittorrent session expired while stopping a torrent");
        }

        response.EnsureSuccessStatusCode();
    }

    // Used by /cancel. deleteFiles=false just removes the torrent from qBittorrent's list, keeping
    // whatever was already downloaded on disk; deleteFiles=true removes the partial/complete data too.
    public async Task RemoveTorrentAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        await EnsureLoggedInAsync(cancellationToken);

        var opts = options.Value;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = infoHash.ToLowerInvariant(),
            ["deleteFiles"] = deleteFiles ? "true" : "false"
        });

        var response = await httpClient.PostAsync($"{opts.BaseUrl.TrimEnd('/')}/api/v2/torrents/delete", content, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _loggedIn = false;
            throw new InvalidOperationException("qBittorrent session expired while removing a torrent");
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> PostHashAsync(string action, string hash, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash });
        return await httpClient.PostAsync($"{opts.BaseUrl.TrimEnd('/')}/api/v2/torrents/{action}", content, cancellationToken);
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
            return;

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn)
                return;

            var opts = options.Value;
            var loginUrl = $"{opts.BaseUrl.TrimEnd('/')}/api/v2/auth/login";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = opts.Username,
                ["password"] = opts.Password
            });

            var response = await httpClient.PostAsync(loginUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Trim() != "Ok.")
                throw new InvalidOperationException($"qBittorrent login failed: {body}");

            _loggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }
}
