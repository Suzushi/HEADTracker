using System.Diagnostics;
using HeadTracker.Core;
using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;

namespace HeadTracker.Bench;

/// <summary>
/// Console benchmark for the tracking pipeline. Runs the full pipeline against a
/// camera or a video file and prints FPS / pose stats; optionally publishes the
/// pose via freetrack shared memory / UDP so games (e.g. DCS World) can consume
/// it directly without opentrack.
///
/// Usage:
///   HeadTracker.Bench --camera [id] [--width 640 --height 480]
///   HeadTracker.Bench --video path\to\file.mp4
///   HeadTracker.Bench --list           (probe cameras 0..5, save snapshots)
///   HeadTracker.Bench --image path.jpg (single-frame pipeline diagnostic)
///   Options: --config path\config.yaml --duration 60 --no-publish --preview
/// Hotkeys: C = re-center, R = force re-detection, Q/Esc = quit.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("HeadTracker requires Windows.");
            return 1;
        }

        if (!TryParseArgs(args, out var opt))
        {
            return 1;
        }

        if (opt.ListCameras)
        {
            return ListCameras();
        }

        string baseDir = AppContext.BaseDirectory;

        if (opt.Image != null)
        {
            return DiagnoseImage(opt.Image, Path.Combine(baseDir, "assets"), SettingsStore.Load(
                opt.Config ?? Path.Combine(baseDir, "config.yaml")));
        }

        string configPath = opt.Config ?? Path.Combine(baseDir, "config.yaml");
        var settings = SettingsStore.Load(configPath);
        Console.WriteLine($"Config: {(File.Exists(configPath) ? configPath : "(defaults)")}");

        IFrameSource source;
        if (opt.Video != null)
        {
            var video = new VideoFileSource(opt.Video);
            if (!video.IsOpen)
            {
                Console.Error.WriteLine($"Cannot open video: {opt.Video}");
                return 1;
            }
            source = video;
            Console.WriteLine($"Video source: {opt.Video} ({video.FrameWidth}x{video.FrameHeight})");
        }
        else
        {
            var camera = new CameraCapture();
            if (!camera.Open(opt.CameraId, opt.Width, opt.Height, settings.Fps,
                    settings.EnableAutoExpo, settings.CameraGain, settings.CameraExpo))
            {
                Console.Error.WriteLine(camera.LastError ?? "Cannot open camera");
                return 1;
            }
            source = camera;
            Console.WriteLine($"Camera {opt.CameraId}: {camera.FrameWidth}x{camera.FrameHeight} @ {camera.ActualFps:F1} fps");
        }

        string assetRoot = Path.Combine(baseDir, "assets");
        Pose6D lastOutput = Pose6D.Zero;
        Vec3 lastYpr = Vec3.Zero;
        Vec3 lastT = Vec3.Zero;
        long outputCount = 0;

        using var publisher = opt.NoPublish ? null
            : new PosePublisher(settings, FindGameDatabase(baseDir));
        using var pipeline = new TrackingPipeline(settings, source, assetRoot);

        pipeline.OutputPose += pose =>
        {
            lastOutput = pose;
            Interlocked.Increment(ref outputCount);
            publisher?.Publish(in pose);
        };
        pipeline.RawPose += (ypr, t) =>
        {
            lastYpr = ypr;
            lastT = t;
        };

        ReportOutputs(publisher, settings);
        pipeline.Start();
        Console.WriteLine("Running. Keys: C=re-center  R=re-detect  Q/Esc=quit");

        var clock = Stopwatch.StartNew();
        double lastStatusMs = 0;
        bool quit = false;
        while (!quit && (opt.DurationSec <= 0 || clock.Elapsed.TotalSeconds < opt.DurationSec))
        {
            if (Console.KeyAvailable)
            {
                switch (Console.ReadKey(intercept: true).Key)
                {
                    case ConsoleKey.C:
                        pipeline.ResetCenter();
                        Console.WriteLine("[re-centered]");
                        break;
                    case ConsoleKey.R:
                        pipeline.ResetDetection();
                        Console.WriteLine("[re-detection forced]");
                        break;
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        quit = true;
                        break;
                }
            }

            if (opt.Preview)
            {
                // Live annotated preview: green dots = landmarks, box = tracked face ROI.
                using var preview = pipeline.TryGetPreview();
                if (preview != null)
                {
                    OpenCvSharp.Cv2.ImShow("HeadTracker preview", preview);
                }
                int key = OpenCvSharp.Cv2.WaitKey(30);
                if (key == 27)
                {
                    quit = true;
                }
            }
            else
            {
                Thread.Sleep(50);
            }

            double nowMs = clock.Elapsed.TotalMilliseconds;
            if (nowMs - lastStatusMs < 500)
            {
                continue;
            }
            lastStatusMs = nowMs;
            Console.WriteLine(
                $"fps={pipeline.FpsEstimate,5:F1} tracked={(pipeline.FaceTracked ? "yes" : "no ")} " +
                $"raw yaw={lastYpr.X,6:F1} pitch={lastYpr.Y,6:F1} roll={lastYpr.Z,6:F1} " +
                $"t=({lastT.X,5:F2},{lastT.Y,5:F2},{lastT.Z,5:F2}) " +
                $"out yaw={lastOutput.Yaw,7:F2} pitch={lastOutput.Pitch,7:F2} roll={lastOutput.Roll,7:F2} " +
                $"x={lastOutput.Tx,6:F2} y={lastOutput.Ty,6:F2} z={lastOutput.Tz,6:F2} " +
                $"rms={pipeline.LastReprojectionRmsPx,4:F2}px outputs={Interlocked.Read(ref outputCount)} " +
                $"errors={pipeline.ErrorCount}");
        }

        pipeline.Stop();
        if (pipeline.ErrorCount > 0)
        {
            Console.WriteLine($"Last pipeline error: {pipeline.LastError}");
        }
        if (opt.Preview)
        {
            OpenCvSharp.Cv2.DestroyAllWindows();
        }
        double seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"Done: {outputCount} poses over {seconds:F1}s " +
                          $"({(seconds > 0 ? outputCount / seconds : 0):F1} Hz output).");
        source.Dispose();
        return 0;
    }

    private static void ReportOutputs(PosePublisher? publisher, TrackerSettings settings)
    {
        if (publisher == null)
        {
            Console.WriteLine("Outputs: disabled (--no-publish)");
            return;
        }

        var parts = new List<string>();
        if (publisher.FreeTrackActive)
        {
            parts.Add("freetrack shared memory (TrackIR protocol)");
        }
        else if (settings.UseFt || settings.UseNpclient)
        {
            parts.Add("freetrack FAILED to initialize");
        }

        if (publisher.UdpActive)
        {
            parts.Add($"UDP {settings.UdpHost}:{settings.Port}");
        }

        Console.WriteLine(parts.Count == 0 ? "Outputs: none enabled in config" : "Outputs: " + string.Join("; ", parts));
    }

    private static string? FindGameDatabase(string baseDir)
    {
        var path = Path.Combine(baseDir, "assets", "facetracknoir supported games.csv");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Probe DirectShow camera ids 0..5, report capabilities and save a snapshot of each.</summary>
    private static int ListCameras()
    {
        Console.WriteLine("Probing cameras 0..5 (DSHOW)...");
        int found = 0;
        for (int id = 0; id <= 5; id++)
        {
            using var cap = new OpenCvSharp.VideoCapture(id, OpenCvSharp.VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
            {
                continue;
            }

            int w = (int)cap.Get(OpenCvSharp.VideoCaptureProperties.FrameWidth);
            int h = (int)cap.Get(OpenCvSharp.VideoCaptureProperties.FrameHeight);
            double fps = cap.Get(OpenCvSharp.VideoCaptureProperties.Fps);

            // Read a few frames so auto-exposure settles before the snapshot.
            using var frame = new OpenCvSharp.Mat();
            for (int i = 0; i < 10; i++)
            {
                cap.Read(frame);
            }
            string snapshot = $"camera_{id}_snapshot.jpg";
            string info = $"Camera {id}: {w}x{h} @ {fps:F1} fps";
            if (!frame.Empty())
            {
                OpenCvSharp.Cv2.ImWrite(snapshot, frame);
                double mean = OpenCvSharp.Cv2.Mean(frame).Val0;
                Console.WriteLine($"{info}  brightness={mean:F0}/255  snapshot -> {Path.GetFullPath(snapshot)}");
            }
            else
            {
                Console.WriteLine($"{info}  (could not read a frame)");
            }
            found++;
        }

        Console.WriteLine(found == 0
            ? "No cameras found."
            : "Open the snapshots to identify your regular webcam (IR cameras look black/gray), " +
              "then run with --camera <id>.");
        return found == 0 ? 1 : 0;
    }

    /// <summary>
    /// Run one image through every pipeline stage and print the intermediate
    /// results, so failures inside the tracking loop (which are swallowed to
    /// keep tracking alive) become visible.
    /// </summary>
    private static int DiagnoseImage(string path, string assetRoot, TrackerSettings settings)
    {
        using var frame = OpenCvSharp.Cv2.ImRead(path);
        if (frame.Empty())
        {
            Console.Error.WriteLine($"Cannot read image: {path}");
            return 1;
        }
        Console.WriteLine($"Image {path}: {frame.Cols}x{frame.Rows}");

        // 1) face detection
        using var scrfd = new ScrfdDetector(Path.Combine(assetRoot, "scrfd_500m_bnkps_shape640x640.onnx"));
        var dets = scrfd.Detect(frame);
        Console.WriteLine($"SCRFD: {dets.Count} detection(s)");
        if (dets.Count == 0)
        {
            Console.WriteLine("No face detected -- nothing downstream can run.");
            return 1;
        }
        var best = dets.OrderByDescending(d => d.Score).First();
        Console.WriteLine($"  best box = ({best.Box.X:F0},{best.Box.Y:F0},{best.Box.Width:F0},{best.Box.Height:F0}) score={best.Score:F3}");

        // 2) landmarks inside a 20% expanded ROI (same as the pipeline)
        var lmRect = Expand(best.Box, frame, 0.2);
        string landmarkDir = Path.Combine(assetRoot, "landmark_models");
        using var landmark = new LandmarkDetector(landmarkDir, Path.Combine(landmarkDir, "model_66.txt"),
            settings.LandmarkDetectMethod, settings.CervicalFaceModel);
        var lm = landmark.Detect(frame, lmRect);
        if (lm == null)
        {
            Console.WriteLine($"Landmark: ROI too small ({lmRect.Width}x{lmRect.Height})");
            return 1;
        }
        double meanConf = lm.Confidences.Average();
        Console.WriteLine($"Landmark: 66 points, mean confidence={meanConf:F3}, " +
                          $"nose=({lm.Points2D[30].X:F1},{lm.Points2D[30].Y:F1})");

        // 3) PnP pose
        var pnp = new PoseEstimator(CameraIntrinsics.ForResolution(frame.Cols, frame.Rows));
        var result = pnp.Solve(lm.Points2D, lm.ModelPoints3D);
        Console.WriteLine($"PnP: success={result.Success} rms={result.ReprojectionRmsPx:F2}px (gate {pnp.MaxRmsPx}px)");
        if (result.Success)
        {
            var rWorld = result.R.Multiply(Mat3.RFace);
            var ypr = QuatD.FromRotationMatrix(rWorld).ToYprDegrees();
            Console.WriteLine($"  raw yaw={ypr.X:F1} pitch={ypr.Y:F1} roll={ypr.Z:F1}");
            Console.WriteLine($"  t=({result.T.X:F3},{result.T.Y:F3},{result.T.Z:F3}) m");
        }

        // annotated result for visual check
        var show = frame.Clone();
        OpenCvSharp.Cv2.Rectangle(show, new OpenCvSharp.Rect((int)best.Box.X, (int)best.Box.Y,
            (int)best.Box.Width, (int)best.Box.Height), new OpenCvSharp.Scalar(0, 200, 255), 1);
        foreach (var p in lm.Points2D)
        {
            OpenCvSharp.Cv2.Circle(show, new OpenCvSharp.Point((int)p.X, (int)p.Y), 1,
                new OpenCvSharp.Scalar(0, 255, 0), -1);
        }
        string outPath = "diagnostic_out.jpg";
        OpenCvSharp.Cv2.ImWrite(outPath, show);
        Console.WriteLine($"Annotated image -> {Path.GetFullPath(outPath)}");
        show.Dispose();
        return 0;
    }

    private static OpenCvSharp.Rect Expand(OpenCvSharp.Rect2d roi, OpenCvSharp.Mat frame, double rate)
    {
        double exX = roi.Width * rate, exY = roi.Height * rate;
        int x = (int)Math.Max(0, roi.X - exX);
        int y = (int)Math.Max(0, roi.Y - exY);
        int w = (int)Math.Min(frame.Cols - x, roi.Width + exX * 2);
        int h = (int)Math.Min(frame.Rows - y, roi.Height + exY * 2);
        return new OpenCvSharp.Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private sealed class Options
    {
        public int CameraId = 0;
        public int Width = 640;
        public int Height = 480;
        public string? Video;
        public string? Config;
        public double DurationSec;
        public bool NoPublish;
        public bool Preview;
        public bool ListCameras;
        public string? Image;
    }

    private static bool TryParseArgs(string[] args, out Options opt)
    {
        opt = new Options();
        bool mode = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--camera":
                    mode = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int id) && !args[i + 1].StartsWith('-'))
                    {
                        opt.CameraId = id;
                        i++;
                    }
                    break;
                case "--video":
                    mode = true;
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--video requires a file path");
                        return false;
                    }
                    opt.Video = args[++i];
                    break;
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--config requires a path");
                        return false;
                    }
                    opt.Config = args[++i];
                    break;
                case "--width":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int w))
                    {
                        opt.Width = w;
                    }
                    break;
                case "--height":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int h))
                    {
                        opt.Height = h;
                    }
                    break;
                case "--duration":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out double d))
                    {
                        opt.DurationSec = d;
                    }
                    break;
                case "--no-publish":
                    opt.NoPublish = true;
                    break;
                case "--preview":
                    mode = true;
                    opt.Preview = true;
                    break;
                case "--list":
                    mode = true;
                    opt.ListCameras = true;
                    break;
                case "--image":
                    mode = true;
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--image requires a file path");
                        return false;
                    }
                    opt.Image = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return false;
            }
        }

        if (!mode)
        {
            Console.Error.WriteLine("Specify a source: --camera [id] or --video <path>");
            return false;
        }
        return true;
    }
}
