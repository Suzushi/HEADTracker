using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HeadTracker.App.Services;

/// <summary>
/// The global re-center hotkey, registered against a dedicated message-loop thread.
///
/// Binding RegisterHotKey to the main window's HWND is not enough on its own: WM_HOTKEY is then
/// queued to the WPF UI thread, and that thread has to get around to pumping it. Both pipeline
/// threads run at AboveNormal and the processing one is CPU-bound (landmark inference at camera
/// rate), so a full-screen sim that already saturates the machine — DCS — can starve the Normal
/// priority UI thread for long stretches. The hotkey is registered and delivered, then sits in
/// the queue unnoticed; alt-tabbing out frees the CPU and it suddenly "works". That is the
/// failure this class exists to remove: a thread whose only job is GetMessage cannot be queued
/// behind rendering, and it runs the re-center itself instead of handing it to the UI thread.
///
/// Integrity level is a second, independent condition, and is deliberately not forced: UIPI drops
/// WM_HOTKEY outright for a process of lower integrity than the foreground window, but the sims
/// this ships against launch from Steam at medium integrity just like we do, so requesting
/// elevation would cost every user a UAC prompt per launch to fix nothing. Whoever's game does
/// run elevated is still diagnosable — the caller logs elevated= next to the registration line.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const int HotkeyId = 0xA17C;

    // SetLastError matters: without it Marshal.GetLastWin32Error() after a failed registration
    // returns whatever the marshaller last happened to set, and a dead hotkey stays undiagnosable.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);

    // PostThreadMessage wants a Win32 thread id, which is a different number space from
    // Thread.ManagedThreadId; posting the managed id silently goes nowhere (see Shutdown).
    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    private readonly Action _onHotkey;
    private readonly object _gate = new();
    private Thread? _thread;
    private int _win32ThreadId;
    private bool _registered;
    private int _lastError;

    /// <summary>Invoked on the hotkey thread; it must not assume UI-thread affinity.</summary>
    public GlobalHotkey(Action onHotkey) => _onHotkey = onHotkey;

    /// <summary>The spec currently bound, for logging and for the UI warning.</summary>
    public string Spec { get; private set; } = "";

    /// <summary>False when <see cref="Spec"/> is empty or unparseable, i.e. the user chose to run
    /// without a global hotkey. That is a preference, not a fault, so the UI stays quiet.</summary>
    public bool SpecParsed { get; private set; }

    /// <summary>True once RegisterHotKey succeeded on the worker thread.</summary>
    public bool Registered
    {
        get { lock (_gate) { return _registered; } }
    }

    /// <summary>Win32 error from the last failed registration (1409 = already taken).</summary>
    public int LastError
    {
        get { lock (_gate) { return _lastError; } }
    }

    /// <summary>
    /// (Re)bind to a spec such as "Ctrl+X". An empty or invalid spec leaves it unregistered, which
    /// is a deliberate choice by the user rather than a fault. Blocks until the worker thread has
    /// attempted the registration, so the caller can read <see cref="Registered"/> immediately.
    /// </summary>
    public void Rebind(string? spec)
    {
        Shutdown();

        Spec = spec?.Trim() ?? "";
        lock (_gate)
        {
            _registered = false;
            _lastError = 0;
        }
        if (!HotkeyParser.TryParse(Spec, out uint mods, out int vk))
        {
            SpecParsed = false;
            return;
        }
        SpecParsed = true;

        // Deliberately not disposed: if Wait ever timed out, a using block would free the event
        // while the worker is still about to call Set(), and an unhandled exception on a
        // background thread takes the whole process down. Without WaitHandle access this object
        // holds no unmanaged resources, so leaving it to the GC costs nothing.
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => Loop(mods | HotkeyParser.ModNoRepeat, (uint)vk, ready))
        {
            IsBackground = true,
            Name = "GlobalHotkey",
            // Above the UI thread on purpose: noticing one key press must not have to wait behind
            // preview rendering, which is exactly the starvation this class is working around.
            Priority = ThreadPriority.AboveNormal,
        };
        lock (_gate)
        {
            _thread = thread;
        }
        thread.Start();
        ready.Wait(2000);
    }

    private void Loop(uint mods, uint vk, ManualResetEventSlim ready)
    {
        // Stamp the Win32 thread id first thing: Shutdown needs it to post WM_QUIT here, and
        // the ManagedThreadId the Thread object exposes is not a substitute for it.
        lock (_gate)
        {
            _win32ThreadId = GetCurrentThreadId();
        }
        try
        {
            // hWnd = NULL binds the hotkey to this thread's queue rather than to any window, so
            // it survives the main window being hidden to the tray or destroyed and re-created.
            bool ok = RegisterHotKey(IntPtr.Zero, HotkeyId, mods, vk);
            lock (_gate)
            {
                _registered = ok;
                _lastError = ok ? 0 : Marshal.GetLastWin32Error();
            }
            ready.Set();
            if (!ok)
            {
                return;
            }

            // GetMessage returns 0 on WM_QUIT and -1 on error; either ends the loop.
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmHotkey && msg.wParam.ToInt32() == HotkeyId)
                {
                    _onHotkey();
                }
            }

            // The thread that registered must be the one that unregisters.
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
            lock (_gate)
            {
                _registered = false;
            }
        }
        finally
        {
            // Only clear our own id: a replacement worker may already have stamped its own.
            lock (_gate)
            {
                if (_win32ThreadId == GetCurrentThreadId())
                {
                    _win32ThreadId = 0;
                }
            }
        }
    }

    private void Shutdown()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread == null)
        {
            return;
        }

        int win32Id;
        lock (_gate)
        {
            win32Id = _win32ThreadId;
        }
        // The worker stamps its id before registering and Rebind waits for the registration
        // attempt, so the id is normally already here. If that wait timed out, give the id a
        // moment to appear instead of posting WM_QUIT to thread 0: a quit that goes nowhere
        // leaves the worker pumping forever, still holding the old registration, so every
        // later bind of the same combination fails with 1409 while the orphan answers the key.
        for (int i = 0; win32Id == 0 && i < 50 && !thread.Join(10); i++)
        {
            lock (_gate)
            {
                win32Id = _win32ThreadId;
            }
        }

        // Fails harmlessly if the worker already exited (a rejected registration never pumps, so
        // it has no message queue); Join is what actually guarantees the thread is gone.
        if (win32Id != 0)
        {
            PostThreadMessage(win32Id, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        thread.Join(1000);
    }

    public void Dispose() => Shutdown();
}
