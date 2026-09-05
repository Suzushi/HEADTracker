using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace HeadTracker.Core.Vision;

/// <summary>
/// Thin wrapper around OpenCV's CSRT tracker (the legacy MedianFlow was
/// removed from contrib; CSRT is the drop-in the legacy code switched to).
///
/// CSRT runs on a downscaled working copy: it was the pipeline's single hottest
/// stage (~47 ms/frame at 640x480, 58% of processing time) and its cost scales
/// with frame area, while ROI tracking only needs enough resolution to keep the
/// box on a face. Callers keep working in full-frame pixels; Init/Update do the
/// coordinate conversion internally, so the pipeline is unaware of the scaling.
/// </summary>
public sealed class RoiTracker : IDisposable
{
    // Longest side of the image CSRT sees. 320 keeps a 640x480 frame's typical
    // face ROI at ~75 px -- ample for CSRT -- while cutting the tracked area 4x.
    private const int MaxWorkingSide = 320;

    private TrackerCSRT? _tracker;
    private Mat? _work;
    private double _scale = 1.0;

    public void Init(Mat frame, Rect roi)
    {
        _tracker?.Dispose();
        _tracker = null;

        var work = Prepare(frame);
        var scaled = ScaleRect(roi, _scale, work.Width, work.Height);
        if (scaled.Width < 1 || scaled.Height < 1)
        {
            // Degenerate ROI: stay empty. Update() then reports failure and the
            // pipeline's re-detection path re-anchors us on the next detect tick.
            return;
        }

        var tracker = TrackerCSRT.Create();
        tracker.Init(work, scaled);
        _tracker = tracker;
    }

    /// <summary>Advance the tracker one frame; on success <paramref name="roi"/> holds the new box.</summary>
    public bool Update(Mat frame, ref Rect roi)
    {
        var tracker = _tracker;
        if (tracker == null)
        {
            return false;
        }

        var work = Prepare(frame);
        var scaled = ScaleRect(roi, _scale, work.Width, work.Height);
        if (!tracker.Update(work, ref scaled) || scaled.Width < 1 || scaled.Height < 1)
        {
            return false;
        }

        // Back to full-frame pixels; the downstream landmark crop has ~40% padding,
        // so the sub-pixel rounding here is far below anything it can notice.
        roi = new Rect(
            (int)Math.Round(scaled.X / _scale),
            (int)Math.Round(scaled.Y / _scale),
            (int)Math.Round(scaled.Width / _scale),
            (int)Math.Round(scaled.Height / _scale));
        return true;
    }

    public void Reset()
    {
        _tracker?.Dispose();
        _tracker = null;
    }

    /// <summary>
    /// Returns the downscaled working image for <paramref name="frame"/>, recomputing the
    /// scale from the frame's longest side. Frames already within budget are returned as-is
    /// (no copy); the working Mat is reused across calls to keep allocation out of the loop.
    /// </summary>
    private Mat Prepare(Mat frame)
    {
        int longest = Math.Max(frame.Width, frame.Height);
        _scale = longest > MaxWorkingSide ? (double)MaxWorkingSide / longest : 1.0;
        if (_scale >= 1.0)
        {
            return frame;
        }

        var work = _work ??= new Mat();
        Cv2.Resize(frame, work, new Size(
            Math.Max(1, (int)Math.Round(frame.Width * _scale)),
            Math.Max(1, (int)Math.Round(frame.Height * _scale))),
            0, 0, InterpolationFlags.Area);
        return work;
    }

    /// <summary>Scale a full-frame rect into working space, clamped to stay inside the image
    /// (CSRT refuses out-of-bounds boxes) and never degenerating below 1x1.</summary>
    private static Rect ScaleRect(Rect r, double scale, int maxW, int maxH)
    {
        int x = Math.Clamp((int)Math.Round(r.X * scale), 0, maxW - 1);
        int y = Math.Clamp((int)Math.Round(r.Y * scale), 0, maxH - 1);
        int w = Math.Clamp((int)Math.Round(r.Width * scale), 1, maxW - x);
        int h = Math.Clamp((int)Math.Round(r.Height * scale), 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    public void Dispose()
    {
        _tracker?.Dispose();
        _work?.Dispose();
    }
}
