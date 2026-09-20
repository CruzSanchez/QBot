using global::Discord.WebSocket;
using DownloadBot.Discord;
using DownloadBot.Feed;
using DownloadBot.QBittorrent;
using DownloadBot.Search;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        "logs/downloadbot-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

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
    var expiryLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var expiryTimer = new Timer(_ => queue.RemoveExpiredAsync(TimeSpan.FromMinutes(10))
        .ContinueWith(t => expiryLogger.LogError(t.Exception, "Feed expiry check failed"), TaskContinuationOptions.OnlyOnFaulted),
        null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
