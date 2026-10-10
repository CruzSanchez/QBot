using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.LocalLibrary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Discord;

// Posts the same drive-space report /drive-check shows to Discord:DriveCheckChannelId at 8 AM, 12 PM, 4 PM
// and 10 PM Central, so free space is visible without anyone having to ask.
public sealed class DriveCheckScheduleService(
    DiscordSocketClient discord,
    IDriveSpaceChecker driveSpaceChecker,
    IOptions<DiscordOptions> discordOptions,
    ILogger<DriveCheckScheduleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channelId = discordOptions.Value.DriveCheckChannelId;
        if (channelId is null)
        {
            logger.LogInformation("Discord:DriveCheckChannelId not configured — scheduled drive report disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = DriveCheckSchedule.NextRunUtc(DateTimeOffset.UtcNow, CentralTime.Zone);
            logger.LogInformation("Next scheduled drive report at {Next}", CentralTime.Format(next));

            // Loop on the remaining time rather than one Delay: if the timer ever wakes slightly early,
            // posting then would make NextRunUtc return the same instant and post a second time.
            try
            {
                while (DateTimeOffset.UtcNow < next)
                    await Task.Delay(next - DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await PostReportAsync(channelId.Value);
        }
    }

    private async Task PostReportAsync(ulong channelId)
    {
        try
        {
            if (discord.GetChannel(channelId) is not IMessageChannel channel)
            {
                logger.LogWarning("Could not resolve drive-report channel {ChannelId}; skipping this run", channelId);
                return;
            }

            var drives = driveSpaceChecker.GetFreeSpace();
            if (drives.Count == 0)
            {
                logger.LogWarning("No attached drives found for the scheduled drive report; skipping this run");
                return;
            }

            await channel.SendMessageAsync(embed: DashboardFormatter.BuildDriveSpaceEmbed(drives, DateTimeOffset.UtcNow));
            logger.LogInformation("Posted scheduled drive report ({Count} drive(s)) to channel {ChannelId}", drives.Count, channelId);
        }
        catch (Exception ex)
        {
            // Warning, not Error: a missed report self-corrects at the next slot, and an Error would also
            // trigger the error-log upload for something that isn't worth it.
            logger.LogWarning(ex, "Scheduled drive report failed");
        }
    }
}
