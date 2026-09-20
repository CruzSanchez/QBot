using DownloadBot.QBittorrent;

namespace DownloadBot.Tests.Unit;

public class StallDetectorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(20);

    [Fact]
    public void Evaluate_FirstObservation_NeverAlerts()
    {
        var now = DateTimeOffset.UtcNow;

        var (state, alert) = StallDetector.Evaluate(existing: null, currentProgress: 0.1, now, Threshold);

        Assert.False(alert);
        Assert.Equal(0.1, state.LastProgress);
        Assert.Equal(now, state.LastProgressAt);
        Assert.False(state.Alerted);
    }

    [Fact]
    public void Evaluate_ProgressAdvancing_NeverAlerts()
    {
        var start = DateTimeOffset.UtcNow;
        var existing = new StallTrackingState(0.1, start, false);

        var (state, alert) = StallDetector.Evaluate(existing, currentProgress: 0.2, start + Threshold + Threshold, Threshold);

        Assert.False(alert);
        Assert.Equal(0.2, state.LastProgress);
    }

    [Fact]
    public void Evaluate_NoProgressBeforeThreshold_DoesNotAlertYet()
    {
        var start = DateTimeOffset.UtcNow;
        var existing = new StallTrackingState(0.5, start, false);

        var (state, alert) = StallDetector.Evaluate(existing, currentProgress: 0.5, start + TimeSpan.FromMinutes(19), Threshold);

        Assert.False(alert);
        Assert.False(state.Alerted);
        Assert.Equal(start, state.LastProgressAt); // clock hasn't reset — progress never advanced
    }

    [Fact]
    public void Evaluate_NoProgressAtOrPastThreshold_AlertsOnce()
    {
        var start = DateTimeOffset.UtcNow;
        var existing = new StallTrackingState(0.5, start, false);

        var (state, alert) = StallDetector.Evaluate(existing, currentProgress: 0.5, start + Threshold, Threshold);

        Assert.True(alert);
        Assert.True(state.Alerted);
    }

    [Fact]
    public void Evaluate_AlreadyAlerted_DoesNotAlertAgainWhileStillStalled()
    {
        var start = DateTimeOffset.UtcNow;
        var alreadyAlerted = new StallTrackingState(0.5, start, true);

        var (state, alert) = StallDetector.Evaluate(alreadyAlerted, currentProgress: 0.5, start + Threshold + Threshold, Threshold);

        Assert.False(alert);
        Assert.True(state.Alerted);
    }

    [Fact]
    public void Evaluate_ProgressResumesAfterAlert_ResetsAndCanAlertAgainLater()
    {
        var start = DateTimeOffset.UtcNow;
        var alreadyAlerted = new StallTrackingState(0.5, start, true);

        // Progress resumes — clock and alert flag both reset.
        var (resumed, alertOnResume) = StallDetector.Evaluate(alreadyAlerted, currentProgress: 0.6, start + Threshold, Threshold);
        Assert.False(alertOnResume);
        Assert.False(resumed.Alerted);
        Assert.Equal(0.6, resumed.LastProgress);

        // Stalls again for a full threshold from the new baseline — alerts again.
        var (state2, alert2) = StallDetector.Evaluate(resumed, currentProgress: 0.6, start + Threshold + Threshold, Threshold);
        Assert.True(alert2);
        Assert.True(state2.Alerted);
    }
}
