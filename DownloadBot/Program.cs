using global::Discord.WebSocket;
using DownloadBot.Discord;
using DownloadBot.LocalLibrary;
using DownloadBot.QBittorrent;
using DownloadBot.Search;
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

    builder.Services.AddSingleton<DownloadTrackingStore>();
    builder.Services.AddSingleton<DiscordSocketClient>();
    builder.Services.AddSingleton<IPlexLibraryScanner, PlexLibraryScanner>();
    builder.Services.AddSingleton<IDriveSpaceChecker, DriveSpaceChecker>();
    builder.Services.AddHttpClient<IJackettClient, JackettClient>();

    // Registered once as a concrete singleton, then exposed both as the ILibraryCache PlexLibraryScanner
    // reads from and as the hosted service that keeps it refreshed — same instance either way.
    builder.Services.AddSingleton<LibraryCacheService>();
    builder.Services.AddSingleton<ILibraryCache>(sp => sp.GetRequiredService<LibraryCacheService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryCacheService>());

    // qBittorrent auth uses a session cookie set by /api/v2/auth/login, so the HttpClient must persist cookies across calls.
    builder.Services.AddHttpClient<IQBitApiClient, QBitApiClient>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true
        });

    builder.Services.AddHostedService<DownloadBotService>();
    builder.Services.AddHostedService<CompletionPollerService>();
    builder.Services.AddHostedService<DashboardService>();

    var app = builder.Build();

    app.MapGet("/", () => "DownloadBot is running.");

    // Bound to localhost only (see launchSettings.json), so this is only reachable from
    // the same machine — used by the deploy workflow to trigger a graceful shutdown
    // (runs the same ApplicationStopping hooks Ctrl+C would, e.g. the "Bot shutting
    // down" Discord message) instead of killing the process directly, which a
    // non-interactive scheduled-task process can't otherwise be asked to do cleanly.
    app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
    {
        lifetime.StopApplication();
        return Results.Ok("Shutting down.");
    });

    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
