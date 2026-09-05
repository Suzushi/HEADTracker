using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;
using Xunit;

namespace HeadTracker.Core.Tests;

/// <summary>
/// The negotiation ladder is pure configuration logic (no camera needed), so its ordering and
/// dedup rules are unit-tested directly. The probing itself needs real hardware.
/// </summary>
public class CameraNegotiatorTests
{
    [Fact]
    public void BuildLadder_DefaultSettings_ProbesUserConfigThenMsmfThenMsmfMjpg()
    {
        var s = new TrackerSettings(); // dshow + auto format + 640x480
        var ladder = CameraNegotiator.BuildLadder(s);

        // User config first; the trailing dshow+auto rung duplicates it and is deduped away.
        Assert.Equal(3, ladder.Count);
        Assert.Equal(new CameraNegotiator.Combo("dshow", "", 640, 480), ladder[0]);
        Assert.Equal(new CameraNegotiator.Combo("msmf", "", 640, 480), ladder[1]);
        Assert.Equal(new CameraNegotiator.Combo("msmf", "MJPG", 640, 480), ladder[2]);
    }

    [Fact]
    public void BuildLadder_CustomResolution_AppendsFallback640x480AfterPreferred()
    {
        var s = new TrackerSettings { CaptureWidth = 1920, CaptureHeight = 1080, CaptureApi = "msmf" };
        var ladder = CameraNegotiator.BuildLadder(s);

        int first1080 = ladder.FindIndex(c => c.Height == 1080);
        int first480 = ladder.FindIndex(c => c.Height == 480);
        Assert.True(first1080 >= 0);
        Assert.True(first480 > first1080); // preferred resolution probed before the cheap fallback
    }

    [Fact]
    public void BuildLadder_RespectsUserBackendAndFormat()
    {
        var s = new TrackerSettings { CaptureApi = "msmf", CaptureFourcc = "YUY2" };
        var ladder = CameraNegotiator.BuildLadder(s);
        Assert.Equal(new CameraNegotiator.Combo("msmf", "YUY2", 640, 480), ladder[0]);
    }
}
