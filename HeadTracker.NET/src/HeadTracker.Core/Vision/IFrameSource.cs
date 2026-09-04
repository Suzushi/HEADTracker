using OpenCvSharp;

namespace HeadTracker.Core.Vision;

/// <summary>Common frame provider interface for cameras and video-file replay.</summary>
public interface IFrameSource : IDisposable
{
    bool IsOpen { get; }
    int FrameWidth { get; }
    int FrameHeight { get; }

    /// <summary>Fetch the next frame to process, or null when none is ready/available.</summary>
    Mat? GrabLatest();
}

/// <summary>Video-file replay source for benchmarking without a camera.</summary>
public sealed class VideoFileSource : IFrameSource
{
    private readonly VideoCapture _capture;

    public bool IsOpen { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public bool Loop { get; set; }

    public VideoFileSource(string path, bool loop = true)
    {
        _capture = new VideoCapture(path);
        IsOpen = _capture.IsOpened();
        Loop = loop;
        FrameWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
        FrameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
    }

    public Mat? GrabLatest()
    {
        var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty())
        {
            frame.Dispose();
            if (Loop)
            {
                _capture.Set(VideoCaptureProperties.PosFrames, 0);
                frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    return null;
                }
                return frame;
            }
            return null;
        }
        return frame;
    }

    public void Dispose() => _capture.Dispose();
}
