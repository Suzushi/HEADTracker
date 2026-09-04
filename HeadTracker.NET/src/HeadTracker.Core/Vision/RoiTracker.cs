using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace HeadTracker.Core.Vision;

/// <summary>
/// Thin wrapper around OpenCV's CSRT tracker (the legacy MedianFlow was
/// removed from contrib; CSRT is the drop-in the legacy code switched to).
/// </summary>
public sealed class RoiTracker : IDisposable
{
    private TrackerCSRT? _tracker;

    public void Init(Mat frame, Rect roi)
    {
        _tracker?.Dispose();
        _tracker = TrackerCSRT.Create();
        _tracker.Init(frame, roi);
    }

    /// <summary>Advance the tracker one frame; on success <paramref name="roi"/> holds the new box.</summary>
    public bool Update(Mat frame, ref Rect roi)
    {
        var tracker = _tracker;
        if (tracker == null)
        {
            return false;
        }
        return tracker.Update(frame, ref roi);
    }

    public void Reset()
    {
        _tracker?.Dispose();
        _tracker = null;
    }

    public void Dispose() => _tracker?.Dispose();
}
