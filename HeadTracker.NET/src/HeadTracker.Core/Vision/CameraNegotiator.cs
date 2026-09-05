using HeadTracker.Core.Configuration;

namespace HeadTracker.Core.Vision;

/// <summary>
/// Picks a working (backend, pixel-format, resolution) combination for cameras whose modes
/// behave inconsistently. Cheap webcams are wildly mode-dependent: one backend may read a
/// given mode at 8fps while another saturates 30fps, and some resolutions are slow internal
/// downsample paths. Rather than hardcoding assumptions -- the bug class this project hit,
/// where DSHOW read a perfectly good camera at 8fps while MSMF saturated it at 30 -- we probe
/// a short ladder of combos, measure each one's real capture rate, and keep the first that
/// meets the target. This automates the trial-and-error a human would otherwise perform.
/// </summary>
public sealed class CameraNegotiator
{
    /// <summary>How long each candidate combo is probed before judging its frame rate.</summary>
    private const double ProbeSeconds = 0.8;

    /// <summary>A candidate capture configuration to probe.</summary>
    public readonly record struct Combo(string Api, string Fourcc, int Width, int Height);

    /// <summary>
    /// Negotiation outcome. When a combo met the target its capture is handed back still open
    /// so the caller can pass it straight to the pipeline without a second open. When nothing
    /// met the target, <see cref="Camera"/> is null and <see cref="Combo"/> is the best-effort
    /// (highest measured rate) candidate for the caller to open anyway.
    /// </summary>
    public sealed record Outcome(CameraCapture? Camera, Combo Combo, double MeasuredFps, bool MetTarget);

    /// <summary>
    /// Builds the probe ladder. The user's explicit config is probed first (respect it), then
    /// MSMF across pixel formats (the backend that fixed slow DSHOW reads), then a DSHOW
    /// fallback -- all at the preferred resolution. If nothing there meets the target the same
    /// ladder is retried at a cheap 640x480 mode, which is accurate enough for head tracking
    /// and keeps per-frame processing cost low.
    /// </summary>
    public static List<Combo> BuildLadder(TrackerSettings s)
    {
        var combos = new List<Combo>();
        AddResolution(combos, s, s.CaptureWidth, s.CaptureHeight);
        if (s.CaptureWidth != 640 || s.CaptureHeight != 480)
        {
            AddResolution(combos, s, 640, 480);
        }
        // The user's config can coincide with a later rung (e.g. dshow + auto format); probing
        // the identical combo twice would just waste a probe window.
        return combos.Distinct().ToList();
    }

    private static void AddResolution(List<Combo> combos, TrackerSettings s, int w, int h)
    {
        string cfgApi = string.IsNullOrWhiteSpace(s.CaptureApi) ? "msmf" : s.CaptureApi;
        string cfgFourcc = s.CaptureFourcc ?? "";
        combos.Add(new Combo(cfgApi, cfgFourcc, w, h)); // exactly what the user asked for
        combos.Add(new Combo("msmf", "", w, h));
        combos.Add(new Combo("msmf", "MJPG", w, h));
        combos.Add(new Combo("dshow", "", w, h));
    }

    /// <summary>
    /// Probes the ladder in order and returns the first combo whose measured capture rate meets
    /// <paramref name="targetFps"/> within a 15% tolerance, with that capture left open. When no
    /// combo meets the target, returns the highest-rate combo with <c>Camera=null</c> and
    /// <c>MetTarget=false</c> so tracking can still start best-effort.
    /// </summary>
    public Outcome Negotiate(int cameraId, TrackerSettings s, double targetFps, Action<string>? log = null)
    {
        // Never request more than 30fps while probing: asking a 30fps camera for 60 puts it in
        // an unsupported mode whose auto-exposure oscillates and tanks the rate we measure.
        double probeFps = Math.Min(targetFps, 30.0);
        double need = probeFps * 0.85;
        Combo bestCombo = default;
        double bestFps = -1;

        foreach (var combo in BuildLadder(s))
        {
            var probe = new CameraCapture();
            if (!probe.Open(cameraId, combo.Width, combo.Height, probeFps,
                    s.EnableAutoExpo, s.CameraGain, s.CameraExpo, combo.Api, combo.Fourcc))
            {
                log?.Invoke($"negotiate {Describe(combo)}: open failed");
                probe.Dispose();
                continue;
            }

            Thread.Sleep((int)(ProbeSeconds * 1000));
            double fps = probe.CaptureFps;
            log?.Invoke($"negotiate {Describe(combo)}: {fps:F1} fps");

            if (fps > bestFps)
            {
                bestFps = fps;
                bestCombo = combo;
            }

            if (fps >= need)
            {
                return new Outcome(probe, combo, fps, true); // keep this capture open
            }

            probe.Dispose();
        }

        return new Outcome(null, bestCombo, bestFps, false);
    }

    private static string Describe(Combo c) =>
        $"{c.Api}/{(c.Fourcc.Length == 0 ? "auto" : c.Fourcc)}/{c.Width}x{c.Height}";
}
