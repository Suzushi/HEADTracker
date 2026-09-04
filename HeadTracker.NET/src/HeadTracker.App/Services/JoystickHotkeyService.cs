using HeadTracker.Core.Configuration;
using SharpDX.DirectInput;

namespace HeadTracker.App.Services;

/// <summary>
/// Polls the configured joystick buttons so in-game re-center/pause works
/// (legacy QJoysticks behaviour). Slot 0 = re-center, slot 1 = pause, matching
/// hotkey_joystick_name0/button0 and name1/button1 in config.yaml.
/// Uses DirectInput in Background|NonExclusive mode, which keeps delivering
/// button state while a game has the foreground, and supports all 128 buttons
/// (WINWING bases expose far more than the 32 of the winmm joystick API).
/// </summary>
public sealed class JoystickHotkeyService : IDisposable
{
    /// <summary>Fired on a button press edge; argument is the hotkey slot (0 or 1).</summary>
    public event Action<int>? HotkeyPressed;

    private volatile TrackerSettings _settings;
    private Thread? _thread;
    private volatile bool _running;

    public string Status { get; private set; } = "Idle";

    public JoystickHotkeyService(TrackerSettings settings)
    {
        _settings = settings;
    }

    public void UpdateSettings(TrackerSettings settings) => _settings = settings;

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "JoystickHotkeys" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }

    private sealed class BoundDevice : IDisposable
    {
        public required Joystick Joystick { get; init; }
        public required string Name { get; init; }
        public bool[] Prev { get; } = new bool[128];

        public void Dispose()
        {
            try
            {
                Joystick.Unacquire();
            }
            catch (SharpDX.SharpDXException)
            {
                // Device unplugged; nothing to release.
            }
            Joystick.Dispose();
        }
    }

    private void PollLoop()
    {
        using var di = new DirectInput();
        var devices = new Dictionary<string, BoundDevice>(StringComparer.OrdinalIgnoreCase);
        DateTime lastBindAttempt = DateTime.MinValue;

        while (_running)
        {
            try
            {
                var slots = ReadSlots();

                // Drop devices that are no longer configured.
                foreach (var name in devices.Keys.Where(k => !slots.Any(s => Matches(s.Name, k))).ToList())
                {
                    devices[name].Dispose();
                    devices.Remove(name);
                }

                // Bind configured devices (retry missing ones every 3 s).
                bool bindWindow = DateTime.Now - lastBindAttempt > TimeSpan.FromSeconds(3);
                foreach (var name in slots.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!devices.ContainsKey(name) && bindWindow)
                    {
                        lastBindAttempt = DateTime.Now;
                        TryBind(di, devices, name);
                    }
                }

                if (devices.Count == 0)
                {
                    Status = slots.Length == 0
                        ? "No joystick hotkey configured"
                        : $"Joystick not found: {slots[0].Name}";
                    Thread.Sleep(200);
                    continue;
                }

                foreach (var dev in devices.Values.ToList())
                {
                    if (!PollDevice(dev, slots))
                    {
                        dev.Dispose();
                        devices.Remove(dev.Name);
                    }
                }
                Status = $"Joystick bound: {string.Join(", ", devices.Keys)}";
                Thread.Sleep(15);
            }
            catch (Exception ex)
            {
                // Never let the polling thread die; report and retry.
                Status = $"Joystick error: {ex.Message}";
                foreach (var dev in devices.Values)
                {
                    dev.Dispose();
                }
                devices.Clear();
                Thread.Sleep(2000);
            }
        }

        foreach (var dev in devices.Values)
        {
            dev.Dispose();
        }
    }

    private (string Name, int Button, int Slot)[] ReadSlots()
    {
        var s = _settings;
        var slots = new List<(string, int, int)>(2);
        string n0 = s.HotkeyJoystickName0?.Trim() ?? "";
        string n1 = s.HotkeyJoystickName1?.Trim() ?? "";
        if (n0.Length > 0)
        {
            slots.Add((n0, s.HotkeyJoystickButton0, 0));
        }
        if (n1.Length > 0)
        {
            slots.Add((n1, s.HotkeyJoystickButton1, 1));
        }
        return slots.ToArray();
    }

    private static bool Matches(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private void TryBind(DirectInput di, Dictionary<string, BoundDevice> devices, string name)
    {
        var match = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AllDevices)
            .FirstOrDefault(d => Matches(d.InstanceName ?? "", name));
        if (match == null)
        {
            return;
        }

        var joy = new Joystick(di, match.InstanceGuid);
        // Background: keeps working while the game has the foreground.
        joy.SetCooperativeLevel(IntPtr.Zero, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
        joy.Acquire();
        devices[name] = new BoundDevice { Joystick = joy, Name = name };
    }

    /// <summary>Returns false when the device was lost and must be rebound.</summary>
    private bool PollDevice(BoundDevice dev, (string Name, int Button, int Slot)[] slots)
    {
        try
        {
            dev.Joystick.Poll();
            var state = dev.Joystick.GetCurrentState();
            var buttons = state.Buttons;
            for (int i = 0; i < buttons.Length && i < dev.Prev.Length; i++)
            {
                if (buttons[i] && !dev.Prev[i])
                {
                    foreach (var slot in slots.Where(s => Matches(s.Name, dev.Name) && s.Button == i))
                    {
                        HotkeyPressed?.Invoke(slot.Slot);
                    }
                }
                dev.Prev[i] = buttons[i];
            }
            return true;
        }
        catch (SharpDX.SharpDXException)
        {
            return false; // unplugged or exclusive-access loss
        }
    }

    public void Dispose() => Stop();
}
