using Microsoft.Extensions.Configuration;

namespace DownloadBot.Tests.Integration;

// Loads the same appsettings.json + user-secrets the running bot uses (shared UserSecretsId), so
// integration tests exercise real Jackett/qBittorrent with whatever credentials are already set up
// — nothing test-specific to configure separately.
public static class TestConfiguration
{
    public static readonly IConfiguration Root = Build();

    private static IConfiguration Build()
    {
        var downloadBotAppSettings = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "DownloadBot", "appsettings.json");

        var builder = new ConfigurationBuilder();

        if (File.Exists(downloadBotAppSettings))
            builder.AddJsonFile(downloadBotAppSettings, optional: true);

        builder.AddUserSecrets(typeof(TestConfiguration).Assembly, optional: true);

        return builder.Build();
    }
}
