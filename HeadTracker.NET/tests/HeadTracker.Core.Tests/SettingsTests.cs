using HeadTracker.Core.Configuration;

namespace HeadTracker.Core.Tests;

public class SettingsTests
{
    /// <summary>Snapshot of the legacy C++ config.yaml shipped at the repo root.</summary>
    private const string LegacyYaml = """
        detect_duration: 10
        camera_id: 0
        enable_multithread_detect: true
        retrack_queue_size: 10
        fps: 60
        send_posedata_udp: true
        port: 4242
        udp_host: 127.0.0.1
        use_ft: true
        use_npclient: true
        cov_Q_lm: 0.001
        cov_Q_fsa: 0.083176377110267125
        cov_T: 0.010471285480508999
        cov_V: 1.74805944648536
        cov_W: 7.6315394501615401
        ekf_predict_dt: 0.01
        use_ekf: false
        disp_duration: 30
        disp_max_series_size: 10000
        fsa_pnp_mixture_rate: 0.51000000000000001
        hotkey_joystick_name0: WINWING THROTTLE BASE2 + F18 HANDLE
        hotkey_joystick_button0: 104
        hotkey_joystick_name1: ""
        hotkey_joystick_button1: 0
        landmark_detect_method: 4
        pitch_offset_fsa_pnp: 0.16406095
        cervical_face_model_x: 0.16
        cervical_face_model_y: 0.16
        cervical_face_model: -0.1
        enable_gpu: false
        enable_auto_expo: true
        camera_expo: 1
        camera_gain: 0.22
        enable_face_spd_est: true
        inp_bound_x: 0.30700000000000005
        inp_bound_y: 0.11800000000000001
        inp_bound_z: 0.30700000000000005
        out_bound_x: 0.76899999999999991
        out_bound_y: 0.73399999999999999
        out_bound_z: 0.75499999999999989
        expo_trans_x: 0
        expo_trans_y: 0
        expo_trans_z: 0
        inp_bound_roll: 44.649999999999999
        inp_bound_pitch: 15.949999999999999
        inp_bound_yaw: 25.75
        out_bound_roll: 43.5
        out_bound_pitch: 103.5
        out_bound_yaw: 178.5
        expo_eul_roll: 0
        expo_eul_pitch: 0
        expo_eul_yaw: 0.20999999999999999
        use_accela: true
        accela_rot_smoothing: 0.08539999999999999
        accela_rot_deadzone: 2.9701
        accela_pos_smoothing: 0.029000000000000005
        accela_pos_deadzone: 0.029999999999999999
        double_accela: false
        """;

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"headtracker_test_{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_LegacyConfig_MapsAllKeys()
    {
        var path = WriteTemp(LegacyYaml);
        try
        {
            var s = SettingsStore.Load(path);

            Assert.Equal(60.0, s.Fps);
            Assert.Equal(4242, s.Port);
            Assert.Equal("127.0.0.1", s.UdpHost);
            Assert.True(s.UseFt);
            Assert.True(s.UseNpclient);
            Assert.True(s.SendPosedataUdp);
            Assert.Equal(0.001, s.CovQLm, 12);
            Assert.Equal(0.083176377110267125, s.CovQFsa, 12);
            Assert.Equal(0.51, s.FsaPnpMixtureRate, 10);
            Assert.Equal("WINWING THROTTLE BASE2 + F18 HANDLE", s.HotkeyJoystickName0);
            Assert.Equal(104, s.HotkeyJoystickButton0);
            Assert.Equal("", s.HotkeyJoystickName1);
            Assert.Equal(4, s.LandmarkDetectMethod);
            Assert.Equal(0.16406095, s.PitchOffsetFsaPnp, 8);
            Assert.Equal(0.22, s.CameraGain, 12);
            Assert.True(s.UseAccela);
            Assert.False(s.DoubleAccela);
            Assert.Equal(2.9701, s.AccelaRotDeadzone, 10);
            Assert.Equal(25.75, s.InpBoundYaw, 10);
            Assert.Equal(0.21, s.ExpoEulYaw, 10);
            Assert.Equal(10000, s.DispMaxSeriesSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var s = SettingsStore.Load(Path.Combine(Path.GetTempPath(), "does_not_exist.yaml"));

        Assert.Equal(4242, s.Port);
        Assert.Equal("127.0.0.1", s.UdpHost);
        Assert.Equal(4, s.LandmarkDetectMethod);
        Assert.Equal(224, s.LandmarkNetSize);
    }

    [Theory]
    [InlineData(-1, 4, 224, 28)] // legacy removed dlib path falls back to most accurate
    [InlineData(0, 0, 112, 14)]
    [InlineData(1, 1, 112, 14)]
    [InlineData(2, 2, 224, 28)]
    [InlineData(9, 4, 224, 28)] // out of range clamps to default
    public void Load_LandmarkLevel_DerivesNetShape(int input, int expectedLevel, int expectedSize, int expectedOutput)
    {
        var path = WriteTemp($"landmark_detect_method: {input}");
        try
        {
            var s = SettingsStore.Load(path);
            Assert.Equal(expectedLevel, s.LandmarkDetectMethod);
            Assert.Equal(expectedSize, s.LandmarkNetSize);
            Assert.Equal(expectedOutput, s.LandmarkNetOutputSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"headtracker_test_{Guid.NewGuid():N}.yaml");
        try
        {
            var original = new TrackerSettings
            {
                CameraId = 2,
                Port = 5555,
                UdpHost = "192.168.1.10",
                UseFt = true,
                LandmarkDetectMethod = 1,
                HotkeyJoystickName0 = "TEST JOY",
                AccelaRotSmoothing = 0.123,
                OutBoundYaw = 123.5,
            };

            SettingsStore.Save(path, original);
            var loaded = SettingsStore.Load(path);

            Assert.Equal(2, loaded.CameraId);
            Assert.Equal(5555, loaded.Port);
            Assert.Equal("192.168.1.10", loaded.UdpHost);
            Assert.True(loaded.UseFt);
            Assert.Equal(1, loaded.LandmarkDetectMethod);
            Assert.Equal(112, loaded.LandmarkNetSize);
            Assert.Equal("TEST JOY", loaded.HotkeyJoystickName0);
            Assert.Equal(0.123, loaded.AccelaRotSmoothing, 10);
            Assert.Equal(123.5, loaded.OutBoundYaw, 10);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
