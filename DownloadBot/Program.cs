using global::Discord.WebSocket;
using DownloadBot.Discord;
using DownloadBot.Feed;
using DownloadBot.QBittorrent;
using DownloadBot.Search;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.SingleLine = true;
});

builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.Configure<JackettOptions>(builder.Configuration.GetSection("Jackett"));
builder.Services.Configure<QBittorrentOptions>(builder.Configuration.GetSection("QBittorrent"));

builder.Services.AddSingleton<PendingItemQueue>();
builder.Services.AddSingleton<DownloadTrackingStore>();
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddHttpClient<IJackettClient, JackettClient>();

// qBittorrent auth uses a session cookie set by /api/v2/auth/login, so the HttpClient must persist cookies across calls.
builder.Services.AddHttpClient<IQBitApiClient, QBitApiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        CookieContainer = new System.Net.CookieContainer(),
        UseCookies = true
    });

builder.Services.AddHostedService<DownloadBotService>();
builder.Services.AddHostedService<CompletionPollerService>();

var app = builder.Build();

app.MapGet("/", () => "DownloadBot is running.");
app.MapFeedEndpoint();

// Drop feed items qBittorrent hasn't polled within 10 minutes so stale entries don't re-match forever.
var queue = app.Services.GetRequiredService<PendingItemQueue>();
var expiryTimer = new Timer(_ => queue.RemoveExpired(TimeSpan.FromMinutes(10)), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

app.Run();
