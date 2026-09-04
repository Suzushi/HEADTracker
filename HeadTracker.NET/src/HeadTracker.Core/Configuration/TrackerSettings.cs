using YamlDotNet.Serialization;

namespace HeadTracker.Core.Configuration;

/// <summary>
/// Tracker configuration. YAML key names are kept identical to the legacy
/// C++ config.yaml so existing user configs load unchanged.
/// Defaults mirror the legacy FlightAgxSettings member initializers.
/// </summary>
public sealed class TrackerSettings
{
    // --- capture / pipeline -------------------------------------------------
    [YamlMember(Alias = "detect_duration")]
    public int DetectDuration { get; set; } = 10;

    [YamlMember(Alias = "camera_id")]
    public int CameraId { get; set; } = 0;

    [YamlMember(Alias = "enable_multithread_detect")]
    public bool EnableMultithreadDetect { get; set; } = true;

    [YamlMember(Alias = "retrack_queue_size")]
    public int RetrackQueueSize { get; set; } = 10;

    [YamlMember(Alias = "roi_filter_rate")]
    public double RoiFilterRate { get; set; } = 0.7;

    /// <summary>Horizontally mirror every frame; cancels the selfie-mirror of phone front cameras.</summary>
    [YamlMember(Alias = "mirror_camera")]
    public bool MirrorCamera { get; set; } = false;

    [YamlMember(Alias = "fps")]
    public double Fps { get; set; } = 30.0;

    [YamlMember(Alias = "enable_gpu")]
    public bool EnableGpu { get; set; } = false;

    [YamlMember(Alias = "enable_auto_expo")]
    public bool EnableAutoExpo { get; set; } = true;

    [YamlMember(Alias = "camera_expo")]
    public double CameraExpo { get; set; } = 0.5;

    [YamlMember(Alias = "camera_gain")]
    public double CameraGain { get; set; } = 0.5;

    /// <summary>OpenCV capture backend: "dshow" (default), "msmf", or "any". MSMF often
    /// fixes broken/tiled/green frames from virtual cameras (Iriun, OBS, phone-as-webcam)
    /// that the DSHOW backend mis-converts. Device index order can differ per backend.</summary>
    [YamlMember(Alias = "capture_api")]
    public string CaptureApi { get; set; } = "dshow";

    /// <summary>Optional pixel format to request, e.g. "mjpg" or "yuy2". Empty = backend default.</summary>
    [YamlMember(Alias = "capture_fourcc")]
    public string CaptureFourcc { get; set; } = "";

    // --- camera calibration (written by the charuco wizard) -----------------
    // All zero / absent means "not calibrated": the pipeline falls back to the
    // legacy PS3Eye intrinsics scaled to the live resolution.

    /// <summary>Custom focal length fx in pixels, valid at CalibratedWidth x CalibratedHeight.</summary>
    [YamlMember(Alias = "camera_fx")]
    public double CameraFx { get; set; }

    /// <summary>Custom focal length fy in pixels.</summary>
    [YamlMember(Alias = "camera_fy")]
    public double CameraFy { get; set; }

    /// <summary>Custom principal point cx in pixels.</summary>
    [YamlMember(Alias = "camera_cx")]
    public double CameraCx { get; set; }

    /// <summary>Custom principal point cy in pixels.</summary>
    [YamlMember(Alias = "camera_cy")]
    public double CameraCy { get; set; }

    /// <summary>Distortion coefficient k1 (OpenCV order k1, k2, p1, p2, k3).</summary>
    [YamlMember(Alias = "dist_k1")]
    public double DistK1 { get; set; }

    /// <summary>Distortion coefficient k2.</summary>
    [YamlMember(Alias = "dist_k2")]
    public double DistK2 { get; set; }

    /// <summary>Distortion coefficient p1 (tangential).</summary>
    [YamlMember(Alias = "dist_p1")]
    public double DistP1 { get; set; }

    /// <summary>Distortion coefficient p2 (tangential).</summary>
    [YamlMember(Alias = "dist_p2")]
    public double DistP2 { get; set; }

    /// <summary>Distortion coefficient k3.</summary>
    [YamlMember(Alias = "dist_k3")]
    public double DistK3 { get; set; }

    /// <summary>Frame width the calibration was captured at; K scales proportionally to the live frame.</summary>
    [YamlMember(Alias = "calibrated_width")]
    public int CalibratedWidth { get; set; }

    /// <summary>Frame height the calibration was captured at.</summary>
    [YamlMember(Alias = "calibrated_height")]
    public int CalibratedHeight { get; set; }

    /// <summary>Reprojection RMS of the accepted calibration (diagnostic; not used at runtime).</summary>
    [YamlMember(Alias = "calibration_rms")]
    public double CalibrationRms { get; set; }

    /// <summary>True once a plausible custom calibration has been written by the wizard.</summary>
    public bool HasCustomCalibration =>
        CameraFx > 1 && CameraFy > 1 && CalibratedWidth > 0 && CalibratedHeight > 0;

    /// <summary>Clear a custom calibration so the legacy defaults are used again.</summary>
    public void ClearCalibration()
    {
        CameraFx = CameraFy = CameraCx = CameraCy = 0;
        DistK1 = DistK2 = DistP1 = DistP2 = DistK3 = 0;
        CalibratedWidth = CalibratedHeight = 0;
        CalibrationRms = 0;
    }

    // --- output protocols ---------------------------------------------------
    [YamlMember(Alias = "send_posedata_udp")]
    public bool SendPosedataUdp { get; set; } = true;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 4242;

    [YamlMember(Alias = "udp_host")]
    public string UdpHost { get; set; } = "127.0.0.1";

    [YamlMember(Alias = "use_ft")]
    public bool UseFt { get; set; } = false;

    [YamlMember(Alias = "use_npclient")]
    public bool UseNpclient { get; set; } = false;

    // --- EKF ----------------------------------------------------------------
    [YamlMember(Alias = "cov_Q_fsa")]
    public double CovQFsa { get; set; } = 0.006;

    [YamlMember(Alias = "cov_Q_lm")]
    public double CovQLm { get; set; } = 0.006;

    [YamlMember(Alias = "cov_T")]
    public double CovT { get; set; } = 0.01;

    [YamlMember(Alias = "cov_V")]
    public double CovV { get; set; } = 10.0;

    [YamlMember(Alias = "cov_W")]
    public double CovW { get; set; } = 2.0;

    [YamlMember(Alias = "ekf_predict_dt")]
    public double EkfPredictDt { get; set; } = 0.01;

    [YamlMember(Alias = "use_ekf")]
    public bool UseEkf { get; set; } = false;

    // --- pose sources / mixing ----------------------------------------------
    [YamlMember(Alias = "use_fsa")]
    public bool UseFsa { get; set; } = true;

    [YamlMember(Alias = "fsa_pnp_mixture_rate")]
    public double FsaPnpMixtureRate { get; set; } = 0.5;

    [YamlMember(Alias = "pitch_offset_fsa_pnp")]
    public double PitchOffsetFsaPnp { get; set; } = 11.0 * Math.PI / 180.0;

    [YamlMember(Alias = "cervical_face_model")]
    public double CervicalFaceModel { get; set; } = -0.088;

    [YamlMember(Alias = "cervical_face_model_x")]
    public double CervicalFaceModelX { get; set; } = 0.12;

    [YamlMember(Alias = "cervical_face_model_y")]
    public double CervicalFaceModelY { get; set; } = 0.16;

    [YamlMember(Alias = "enable_face_spd_est")]
    public bool EnableFaceSpdEst { get; set; } = true;

    // --- landmark model selection -------------------------------------------
    /// <summary>0..4 selects the OpenSeeFace ONNX models (fast/noisy -> slow/accurate).</summary>
    [YamlMember(Alias = "landmark_detect_method")]
    public int LandmarkDetectMethod { get; set; } = 4;

    // --- joystick hotkeys -----------------------------------------------------
    [YamlMember(Alias = "hotkey_joystick_name0")]
    public string HotkeyJoystickName0 { get; set; } = "";

    [YamlMember(Alias = "hotkey_joystick_button0")]
    public int HotkeyJoystickButton0 { get; set; } = 0;

    [YamlMember(Alias = "hotkey_joystick_name1")]
    public string HotkeyJoystickName1 { get; set; } = "";

    [YamlMember(Alias = "hotkey_joystick_button1")]
    public int HotkeyJoystickButton1 { get; set; } = 0;

    /// <summary>Global keyboard hotkey that re-centers the pose, e.g. "Ctrl+X" (opentrack-style).
    /// Registered via Win32 RegisterHotKey so it fires even when the game has focus. Parsed by
    /// HotkeyParser; a bare F-key (F13) needs no modifier. Empty/invalid leaves it unregistered.</summary>
    [YamlMember(Alias = "recenter_hotkey")]
    public string RecenterHotkey { get; set; } = "Ctrl+X";

    // --- UI -------------------------------------------------------------------
    /// <summary>Interface language: "auto" (follow system), "en" or "zh".</summary>
    [YamlMember(Alias = "ui_language")]
    public string UiLanguage { get; set; } = "auto";

    // --- chart display (legacy debug windows) ---------------------------------
    [YamlMember(Alias = "disp_duration")]
    public double DispDuration { get; set; } = 30.0;

    [YamlMember(Alias = "disp_max_series_size")]
    public int DispMaxSeriesSize { get; set; } = 1000;

    // --- remapper: input/output bounds + expo ---------------------------------
    [YamlMember(Alias = "inp_bound_x")]
    public double InpBoundX { get; set; } = 0.3;

    [YamlMember(Alias = "inp_bound_y")]
    public double InpBoundY { get; set; } = 0.12;

    [YamlMember(Alias = "inp_bound_z")]
    public double InpBoundZ { get; set; } = 0.3;

    [YamlMember(Alias = "out_bound_x")]
    public double OutBoundX { get; set; } = 0.77;

    [YamlMember(Alias = "out_bound_y")]
    public double OutBoundY { get; set; } = 0.73;

    [YamlMember(Alias = "out_bound_z")]
    public double OutBoundZ { get; set; } = 0.75;

    [YamlMember(Alias = "expo_trans_x")]
    public double ExpoTransX { get; set; } = 0.0;

    [YamlMember(Alias = "expo_trans_y")]
    public double ExpoTransY { get; set; } = 0.0;

    [YamlMember(Alias = "expo_trans_z")]
    public double ExpoTransZ { get; set; } = 0.0;

    [YamlMember(Alias = "inp_bound_roll")]
    public double InpBoundRoll { get; set; } = 45.0;

    [YamlMember(Alias = "inp_bound_pitch")]
    public double InpBoundPitch { get; set; } = 16.0;

    [YamlMember(Alias = "inp_bound_yaw")]
    public double InpBoundYaw { get; set; } = 26.0;

    [YamlMember(Alias = "out_bound_roll")]
    public double OutBoundRoll { get; set; } = 43.5;

    [YamlMember(Alias = "out_bound_pitch")]
    public double OutBoundPitch { get; set; } = 103.5;

    [YamlMember(Alias = "out_bound_yaw")]
    public double OutBoundYaw { get; set; } = 178.5;

    [YamlMember(Alias = "expo_eul_roll")]
    public double ExpoEulRoll { get; set; } = 0.0;

    [YamlMember(Alias = "expo_eul_pitch")]
    public double ExpoEulPitch { get; set; } = 0.0;

    [YamlMember(Alias = "expo_eul_yaw")]
    public double ExpoEulYaw { get; set; } = 0.0;

    // --- Optional response curves (replace expo when non-empty) -----------------
    // Serialized as "-1,-1;x,y;...;1,1" (see ResponseCurve). Empty = fall back to expo.
    [YamlMember(Alias = "curve_trans_x")]
    public string CurveTransX { get; set; } = "";

    [YamlMember(Alias = "curve_trans_y")]
    public string CurveTransY { get; set; } = "";

    [YamlMember(Alias = "curve_trans_z")]
    public string CurveTransZ { get; set; } = "";

    [YamlMember(Alias = "curve_eul_yaw")]
    public string CurveEulYaw { get; set; } = "";

    [YamlMember(Alias = "curve_eul_pitch")]
    public string CurveEulPitch { get; set; } = "";

    [YamlMember(Alias = "curve_eul_roll")]
    public string CurveEulRoll { get; set; } = "";

    // --- Accela filter ----------------------------------------------------------
    [YamlMember(Alias = "use_accela")]
    public bool UseAccela { get; set; } = false;

    [YamlMember(Alias = "double_accela")]
    public bool DoubleAccela { get; set; } = false;

    [YamlMember(Alias = "accela_rot_smoothing")]
    public double AccelaRotSmoothing { get; set; } = 0.08;

    [YamlMember(Alias = "accela_rot_deadzone")]
    public double AccelaRotDeadzone { get; set; } = 3.0;

    [YamlMember(Alias = "accela_pos_smoothing")]
    public double AccelaPosSmoothing { get; set; } = 0.03;

    [YamlMember(Alias = "accela_pos_deadzone")]
    public double AccelaPosDeadzone { get; set; } = 0.03;

    // --- One-Euro filter (adaptive low-pass on the Euler output) ----------------
    // Off by default so legacy configs are unaffected; config.yaml opts in. Applied
    // per-axis to yaw/pitch/roll at the processing rate, before Accela, to kill the
    // at-rest "buzz" without adding lag to real head movement.
    [YamlMember(Alias = "use_one_euro")]
    public bool UseOneEuro { get; set; } = false;

    /// <summary>Cutoff in Hz at rest; lower smooths more but adds lag when still.</summary>
    [YamlMember(Alias = "one_euro_min_cutoff")]
    public double OneEuroMinCutoff { get; set; } = 1.2;

    /// <summary>Speed coefficient; higher reduces lag when moving but passes more jitter.</summary>
    [YamlMember(Alias = "one_euro_beta")]
    public double OneEuroBeta { get; set; } = 0.25;

    /// <summary>Cutoff in Hz for the derivative low-pass.</summary>
    [YamlMember(Alias = "one_euro_deriv_cutoff")]
    public double OneEuroDerivCutoff { get; set; } = 1.0;

    // --- derived (not persisted) ---------------------------------------------
    /// <summary>UI convenience: pitch_offset_fsa_pnp in degrees (stored in radians).</summary>
    [YamlIgnore]
    public double PitchOffsetFsaPnpDegrees
    {
        get => PitchOffsetFsaPnp * 180.0 / Math.PI;
        set => PitchOffsetFsaPnp = value * Math.PI / 180.0;
    }

    /// <summary>Landmark network input size: 112 for levels 0-1, 224 for 2-4.</summary>
    [YamlIgnore]
    public int LandmarkNetSize => LandmarkDetectMethod <= 1 ? 112 : 224;

    /// <summary>Landmark heatmap side: 14 for levels 0-1, 28 for 2-4.</summary>
    [YamlIgnore]
    public int LandmarkNetOutputSize => LandmarkDetectMethod <= 1 ? 14 : 28;

    /// <summary>Clamp the landmark level into the valid 0..4 range (legacy configs may carry -1).</summary>
    public void Normalize()
    {
        if (LandmarkDetectMethod is < 0 or > 4)
        {
            LandmarkDetectMethod = 4;
        }
    }
}
