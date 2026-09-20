namespace DownloadBot.QBittorrent;

public sealed record StallTrackingState(double LastProgress, DateTimeOffset LastProgressAt, bool Alerted);

// Pure decision logic for "has this download stopped making progress for too long" — no I/O, so it's
// directly testable with fabricated timestamps instead of waiting on a real stalled torrent.
public static class StallDetector
{
    // Returns the state to store going forward, and whether this call should trigger an alert.
    public static (StallTrackingState NewState, bool ShouldAlert) Evaluate(
        StallTrackingState? existing, double currentProgress, DateTimeOffset now, TimeSpan stallThreshold)
    {
        if (existing is null)
            return (new StallTrackingState(currentProgress, now, false), false);

        if (currentProgress > existing.LastProgress)
            return (new StallTrackingState(currentProgress, now, false), false); // progress resumed — reset the clock

        if (existing.Alerted || now - existing.LastProgressAt < stallThreshold)
            return (existing, false);

        return (existing with { Alerted = true }, true); // alert once per stall episode
    }
}
