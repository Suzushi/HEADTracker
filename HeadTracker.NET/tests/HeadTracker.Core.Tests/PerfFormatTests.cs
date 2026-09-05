using HeadTracker.Core.Vision;

namespace HeadTracker.Core.Tests;

public class PerfFormatTests
{
    [Fact]
    public void FormatPerfLine_KeysAndInvariantNumbers_AreStable()
    {
        var line = TrackingPipeline.FormatPerfLine(
            scrfd: 1.25, track: 2, landmark: 3.5, pnp: 0.5, fsa: 0, preview: 1.75,
            proc: 9.5, wakesPerSec: 612, publishPerSec: 250, capFps: 59.7, readMs: 16.2);

        // The keys and their order are a contract: docs/perf_measurement.md and the periodic
        // crash.log perf note are parsed by eye and by script against exactly this shape.
        Assert.StartsWith(
            "scrfd=1.25 track=2.00 lm=3.50 pnp=0.50 fsa=0.00 preview=1.75 proc=9.50 ", line);
        Assert.EndsWith("out_wakes=612/s out_pub=250/s cap=59.7fps read=16.2ms", line);
    }

    [Fact]
    public void FormatPerfLine_PreWindowSentinels_Survive()
    {
        // -1 means "no window completed yet" (or no CameraCapture behind the source); the line
        // must still parse so a too-early perf note never looks like a crash.
        var line = TrackingPipeline.FormatPerfLine(-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1);
        Assert.StartsWith("scrfd=-1.00", line);
        Assert.EndsWith("cap=-1.0fps read=-1.0ms", line);
    }
}
