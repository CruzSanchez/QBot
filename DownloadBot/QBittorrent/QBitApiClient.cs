using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DownloadBot.QBittorrent;

public interface IQBitApiClient
{
    Task<TorrentState?> GetTorrentStateAsync(string infoHash, CancellationToken cancellationToken = default);
}

public sealed record TorrentState(string Hash, string Name, string State, double Progress)
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

        return new TorrentState(
            torrent.GetProperty("hash").GetString() ?? infoHash,
            torrent.GetProperty("name").GetString() ?? "",
            torrent.GetProperty("state").GetString() ?? "",
            torrent.GetProperty("progress").GetDouble());
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
