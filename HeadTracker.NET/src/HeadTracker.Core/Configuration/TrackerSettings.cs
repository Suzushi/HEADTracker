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

    /// <summary>Requested capture resolution. Some cameras (notably cheap 1080p models) ship a
    /// badly-implemented low-res mode (640x480 capped near 8fps) while their native mode runs at
    /// full rate; raise these to the native size if [DIAG] cap stays low at 640x480.</summary>
    [YamlMember(Alias = "capture_width")]
    public int CaptureWidth { get; set; } = 640;

    [YamlMember(Alias = "capture_height")]
    public int CaptureHeight { get; set; } = 480;

    /// <summary>At startup, probe a ladder of (backend, pixel-format, resolution) combos and keep
    /// the first whose measured capture rate meets the target fps. Cameras are too mode-
    /// inconsistent to assume one combo works. Disable to force the exact capture_api /
    /// capture_fourcc / capture_width / capture_height above.</summary>
    [YamlMember(Alias = "capture_auto_negotiate")]
    public bool CaptureAutoNegotiate { get; set; } = true;

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
    /// <remarks>Derived, so not persisted. Without <see cref="YamlIgnoreAttribute"/> the serializer
    /// emits it under the CLR name, dropping a PascalCase key into an otherwise snake_case file.</remarks>
    [YamlIgnore]
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
    // Removed: re-center is not worth spending a joystick button on, and shipping a factory
    // config bound to one developer's throttle base hijacked that button for anyone who owned
    // the same hardware. Existing config.yaml files may still carry hotkey_joystick_* keys;
    // SettingsStore deserializes with IgnoreUnmatchedProperties, so they are simply dropped.

    /// <summary>Global keyboard hotkey that re-centers the pose, e.g. "Ctrl+X" (opentrack-style).
    /// Registered via Win32 RegisterHotKey so it fires even when the game has focus; the app layer
    /// owns it on a dedicated thread rather than the UI thread, which a busy sim can starve.
    /// Parsed by HotkeyParser; a bare F-key (F13) needs no modifier. Empty/invalid leaves it
    /// unregistered.</summary>
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

    // Game-side rotation range at full deflection. The legacy values (178.5 yaw / 103.5 pitch
    // against 25.75 / 15.95 input) are a ~6.9x gain: 0.2 deg of residual head jitter became
    // 1.4 deg of view wander, so holding a fixed gaze meant holding perfectly still. ~4.6x keeps
    // the same head sweep usable in DCS-class cockpits while leaving the noise to the filters.
    [YamlMember(Alias = "out_bound_pitch")]
    public double OutBoundPitch { get; set; } = 75;

    [YamlMember(Alias = "out_bound_yaw")]
    public double OutBoundYaw { get; set; } = 120;

    [YamlMember(Alias = "expo_eul_roll")]
    public double ExpoEulRoll { get; set; } = 0.0;

    [YamlMember(Alias = "expo_eul_pitch")]
    public double ExpoEulPitch { get; set; } = 0.0;

    [YamlMember(Alias = "expo_eul_yaw")]
    public double ExpoEulYaw { get; set; } = 0.0;

    // --- Per-axis direction inversion -------------------------------------------
    // Flips the sign of a mapped output axis inside PoseRemapper (after curves/expo, before
    // Accela), on the SAME value the main window shows and the senders publish -- so ticking
    // a box is immediately visible on the x/y/z readout and takes effect in-game at once.
    // For setups whose head/camera orientation makes an axis move opposite to expectation.
    // Off by default: the legacy output is unchanged.
    [YamlMember(Alias = "invert_trans_x")]
    public bool InvertTransX { get; set; } = false;

    [YamlMember(Alias = "invert_trans_y")]
    public bool InvertTransY { get; set; } = false;

    [YamlMember(Alias = "invert_trans_z")]
    public bool InvertTransZ { get; set; } = false;

    [YamlMember(Alias = "invert_eul_yaw")]
    public bool InvertEulYaw { get; set; } = false;

    [YamlMember(Alias = "invert_eul_pitch")]
    public bool InvertEulPitch { get; set; } = false;

    [YamlMember(Alias = "invert_eul_roll")]
    public bool InvertEulRoll { get; set; } = false;

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

    // --- One-Euro filter (adaptive low-pass on the raw head pose) ---------------
    // Off by default so legacy configs are unaffected; config.yaml opts in. Applied
    // per-axis to yaw/pitch/roll and to X/Y/Z at the processing rate, on the raw head
    // pose -- ahead of the bounds/expo gain and of Accela -- so the cutoffs and betas
    // are in physical units (degrees, metres) whatever sensitivity the user maps them
    // onto. Run it after the gain and the derivative term scales with the gain too,
    // opening the cutoff by that factor and letting the at-rest buzz through unfiltered.
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

    /// <summary>Cutoff in Hz at rest for the translation axes (X/Y/Z, metres).</summary>
    [YamlMember(Alias = "one_euro_pos_min_cutoff")]
    public double OneEuroPosMinCutoff { get; set; } = 1.0;

    /// <summary>Speed coefficient for the translation axes. Must not be shared with the rotation
    /// beta: the derivative here is in m/s (a deliberate head slide is ~0.5) rather than deg/s
    /// (a deliberate head turn is ~100), so the rotation value would leave position wide open.</summary>
    [YamlMember(Alias = "one_euro_pos_beta")]
    public double OneEuroPosBeta { get; set; } = 0.5;

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
