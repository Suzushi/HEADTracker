using OpenCvSharp;
using OpenCvSharp.Aruco;
using ArucoDict = OpenCvSharp.Aruco.Dictionary;

namespace HeadTracker.Core.Vision;

/// <summary>Outcome of a charuco calibration solve.</summary>
public sealed class CalibrationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    /// <summary>Reprojection RMS error in pixels (lower is better; &lt;1 is excellent).</summary>
    public double Rms { get; init; }
    public double Fx { get; init; }
    public double Fy { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }

    /// <summary>Distortion (k1, k2, p1, p2, k3).</summary>
    public double[] Distortion { get; init; } = new double[5];

    public static CalibrationResult Failed(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// Charuco-board camera calibration via the OpenCV 4.7+ aruco API that OpenCvSharp
/// 4.13 binds (<see cref="CharucoDetector.DetectBoard"/>). OpenCvSharp does not
/// expose Board::getChessboardCorners, so the charuco corner grid is rebuilt here.
/// Its absolute origin does not matter for K/D: a shared rigid offset is absorbed by
/// each view's extrinsics; only the square spacing and row-major id order matter.
/// </summary>
public sealed class CharucoCalibrator
{
    // A 5x7 grid gives 4x6 = 24 interior charuco corners per view: enough for a
    // stable solve from a handful of frames while still fitting a printed page.
    public const int SquaresX = 5;
    public const int SquaresY = 7;
    public const float SquareLength = 0.030f;
    public const float MarkerLength = 0.022f;

    /// <summary>Minimum charuco corners in a frame for it to be usable.</summary>
    public const int MinCornersPerFrame = 6;

    /// <summary>Minimum accepted frames before Calibrate() will run.</summary>
    public const int MinSamples = 5;

    private readonly ArucoDict _dictionary;
    private readonly CharucoBoard _board;
    private readonly CharucoDetector _charucoDetector;
    private readonly Point3f[] _boardCorners3D;

    private readonly List<Point3f[]> _objectPoints = new();
    private readonly List<Point2f[]> _imagePoints = new();

    // Peek caches the last detection so the UI can accumulate the exact frame the
    // user saw when they pressed "capture", without re-detecting.
    private Point3f[]? _lastObj;
    private Point2f[]? _lastImg;
    private int _lastCorners;

    public int SampleCount => _imagePoints.Count;

    /// <summary>Corners seen by the most recent Peek (0 when the board is not in view).</summary>
    public int LastCornerCount => _lastCorners;

    public CharucoCalibrator()
    {
        _dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict5X5_100);
        _board = new CharucoBoard(SquaresX, SquaresY, SquareLength, MarkerLength, _dictionary);
        _charucoDetector = new CharucoDetector(_board);

        // Row-major interior-corner grid: id = y * (SquaresX-1) + x.
        int nx = SquaresX - 1;
        int ny = SquaresY - 1;
        _boardCorners3D = new Point3f[nx * ny];
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                _boardCorners3D[y * nx + x] = new Point3f(x * SquareLength, y * SquareLength, 0f);
            }
        }
    }

    /// <summary>Render the board into a printable image of exactly widthPx x heightPx
    /// (OpenCV keeps the squares square and centers them inside a white margin).</summary>
    public Mat GenerateBoardImage(int widthPx, int heightPx, int marginPx = 40)
    {
        var img = new Mat();
        _board.GenerateImage(new Size(Math.Max(64, widthPx), Math.Max(64, heightPx)), img, marginPx, 1);
        return img;
    }

    /// <summary>
    /// Detect charuco corners in one frame and draw them onto <paramref name="annotated"/>
    /// (a BGR clone, for live preview). Caches the result for <see cref="CaptureLast"/>;
    /// does NOT add a sample by itself.
    /// </summary>
    /// <returns>The number of charuco corners seen (may be below MinCornersPerFrame).</returns>
    public int Peek(Mat frame, out Mat annotated)
    {
        _charucoDetector.DetectBoard(frame, out Point2f[] charucoCorners, out int[] charucoIds,
            out Point2f[][] markerCorners, out int[] markerIds);

        annotated = frame.Clone();
        if (markerIds is { Length: > 0 })
        {
            CvAruco.DrawDetectedMarkers(annotated, markerCorners, markerIds);
        }

        if (charucoIds == null || charucoCorners == null || charucoIds.Length < MinCornersPerFrame)
        {
            _lastObj = null;
            _lastImg = null;
            _lastCorners = charucoIds?.Length ?? 0;
            return _lastCorners;
        }

        CvAruco.DrawDetectedCornersCharuco(annotated, charucoCorners, charucoIds, new Scalar(255, 0, 0));

        var obj = new Point3f[charucoIds.Length];
        for (int i = 0; i < charucoIds.Length; i++)
        {
            obj[i] = _boardCorners3D[charucoIds[i]];
        }
        _lastObj = obj;
        _lastImg = charucoCorners;
        _lastCorners = obj.Length;
        return _lastCorners;
    }

    /// <summary>Add the frame seen by the last <see cref="Peek"/> to the calibration set.</summary>
    /// <returns>True if a sample was added (enough corners were present).</returns>
    public bool CaptureLast()
    {
        if (_lastObj == null || _lastImg == null || _lastCorners < MinCornersPerFrame)
        {
            return false;
        }
        _objectPoints.Add(_lastObj);
        _imagePoints.Add(_lastImg);
        return true;
    }

    /// <summary>Convenience: detect one frame and immediately add it as a sample.</summary>
    /// <returns>The number of charuco corners used (0 = board not detected).</returns>
    public int TryCapture(Mat frame, out Mat annotated)
    {
        int corners = Peek(frame, out annotated);
        CaptureLast();
        return corners;
    }

    /// <summary>Solve K/D from every captured frame.</summary>
    public CalibrationResult Calibrate(int imageWidth, int imageHeight)
    {
        if (_imagePoints.Count < MinSamples)
        {
            return CalibrationResult.Failed($"Need at least {MinSamples} samples (have {_imagePoints.Count}).");
        }

        try
        {
            // CalibrateCamera overload #2: enumerable point sets in, K as a
            // double[3,3] and D as a double[5] (k1,k2,p1,p2,k3) filled in place;
            // the per-view extrinsics are discarded.
            var cameraMatrix = new double[3, 3];
            var distCoeffs = new double[5];
            var objEnum = _objectPoints.Select(p => (IEnumerable<Point3f>)p);
            var imgEnum = _imagePoints.Select(p => (IEnumerable<Point2f>)p);

            double rms = Cv2.CalibrateCamera(objEnum, imgEnum, new Size(imageWidth, imageHeight),
                cameraMatrix, distCoeffs, out _, out _, CalibrationFlags.None,
                new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 1e-6));

            double fx = cameraMatrix[0, 0];
            double fy = cameraMatrix[1, 1];
            double cx = cameraMatrix[0, 2];
            double cy = cameraMatrix[1, 2];
            double[] dist = distCoeffs;

            // A focal length wildly off the frame size, or a huge RMS, signals a
            // diverged solve (too few / too coplanar samples).
            bool plausible = fx > imageWidth * 0.2 && fx < imageWidth * 8
                          && fy > imageHeight * 0.2 && fy < imageHeight * 8
                          && !double.IsNaN(rms) && rms > 0 && rms < 5.0;
            if (!plausible)
            {
                return CalibrationResult.Failed($"Solve diverged (RMS {rms:F2}px); recapture with more varied angles.");
            }

            return new CalibrationResult
            {
                Success = true,
                Rms = rms,
                Fx = fx,
                Fy = fy,
                Cx = cx,
                Cy = cy,
                Distortion = dist,
                Message = $"RMS {rms:F3}px from {_imagePoints.Count} samples.",
            };
        }
        catch (Exception ex)
        {
            return CalibrationResult.Failed(ex.Message);
        }
    }

    /// <summary>Drop all captured samples (start a new calibration session).</summary>
    public void Clear()
    {
        _objectPoints.Clear();
        _imagePoints.Clear();
    }
}
