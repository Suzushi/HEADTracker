using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using HeadTracker.App.Services;
using HeadTracker.App.ViewModels;

namespace HeadTracker.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TaskbarIcon _trayIcon;
    private readonly GlobalHotkey _hotkey;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        var service = new TrackerService(Path.Combine(AppContext.BaseDirectory, "config.yaml"));
        _viewModel = new MainViewModel(service, OpenSettings, RequestExit);
        DataContext = _viewModel;

        // No HWND is involved any more (see GlobalHotkey), so the hotkey can be bound here rather
        // than waiting for SourceInitialized — it works before the window is ever shown.
        _hotkey = new GlobalHotkey(OnGlobalHotkey);
        RegisterRecenterHotkey();

        // Enumerate cameras now, before any capture graph exists: re-enumerating while a
        // stream is running has been seen to take the process down silently on some drivers.
        CameraEnumerator.WarmCache();

        // Force instantiation of the lazily-created tray icon resource.
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnWindowClosing;
        // The hotkey warning is built in code, so it must be re-translated with everything else.
        LanguageService.LanguageChanged += UpdateHotkeyWarning;
        // Silent deaths (window destroyed without an exception) leave no crash.log entry;
        // these notes make the next one attributable.
        Closed += (_, _) => App.LogNote("main window closed");
        // Preview only matters while the window is on screen; hiding to tray or minimizing
        // stops both the UI bitmap copy and the pipeline's per-frame DrawPreview.
        IsVisibleChanged += (_, _) => UpdatePreviewVisible();
        StateChanged += (_, _) => UpdatePreviewVisible();
    }

    private void UpdatePreviewVisible() =>
        _viewModel.SetPreviewVisible(IsVisible && WindowState != WindowState.Minimized);

    /// <summary>
    /// (Re)bind the global re-center hotkey from settings. "Global" is the point: it fires even
    /// when the game has focus, unlike the in-window C/F9 handlers. Delivery must not depend on the
    /// WPF UI thread getting scheduled — a sim saturating the machine starves it, which is what
    /// GlobalHotkey's own message-loop thread is for. Integrity level is a second condition (UIPI
    /// drops WM_HOTKEY for a process lower than the foreground window) but is not requested up
    /// front; the elevated= field logged below is what says after the fact whether it mattered.
    /// </summary>
    private void RegisterRecenterHotkey()
    {
        _hotkey.Rebind(_viewModel.Service.Settings.RecenterHotkey);

        // Registration still says nothing about delivery, so log every state that decides it:
        // a registration line with no matching "fired" line while the game is in front narrows
        // the cause down to the key never reaching us at all.
        App.LogNote($"hotkey '{_hotkey.Spec}': parsed={_hotkey.SpecParsed}, registered={_hotkey.Registered}, " +
                    $"error={_hotkey.LastError}, elevated={IsElevated()}");
        UpdateHotkeyWarning();
    }

    /// <summary>
    /// Runs on the hotkey thread, not the UI thread. The re-center itself is one lock plus a few
    /// field writes in the remapper, so it happens here; only the status text is handed over, and
    /// asynchronously. Routing the action through the dispatcher is what made it unreliable in DCS.
    /// </summary>
    private void OnGlobalHotkey()
    {
        // The dispatch-latency probe is what settles the argument in crash.log. Posted before the
        // work, stamped when the UI thread finally runs it:
        //   - seconds late  -> a sim saturating the machine was starving the UI thread, and binding
        //                      the hotkey to it (the old code) would have lost the key press;
        //   - milliseconds   -> starvation is not the mechanism, so a press that logs no "fired"
        //                      line at all means something upstream is swallowing the key.
        var probe = Stopwatch.StartNew();
        Application.Current?.Dispatcher.BeginInvoke(
            () => App.LogNote($"ui thread latency at hotkey: {probe.ElapsedMilliseconds} ms"));

        App.LogNote($"hotkey fired -> recenter (tracking={_viewModel.IsRunning})");
        _viewModel.Service.Recenter();
        _viewModel.NotifyRecentered();
    }

    /// <summary>Rebuilds the localized in-window warning about an unusable re-center hotkey. Empty
    /// — and therefore collapsed in XAML — unless the user asked for a hotkey and Windows refused
    /// it. Running un-elevated is not warned about: the app ships without a manifest on purpose,
    /// and UIPI only matters for the subset of users whose game is itself elevated, which is what
    /// the <c>elevated=</c> field in crash.log is for.</summary>
    private void UpdateHotkeyWarning()
    {
        _viewModel.HotkeyWarning = _hotkey.SpecParsed && !_hotkey.Registered
            ? string.Format(Loc.Tr("hotkey_reg_failed"), _hotkey.Spec, _hotkey.LastError)
            : "";
    }

    /// <summary>True when this process holds an elevated (high integrity) token. The Administrators
    /// SID is deny-only in a filtered token, so this is false for a non-elevated admin user. Logged
    /// rather than acted on: if a user's game runs elevated, UIPI drops our hotkey, and this field
    /// is the only thing in crash.log that distinguishes that from every other failure mode.</summary>
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F9:
                _viewModel.ToggleCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C:
                _viewModel.RecenterCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_viewModel.Service) { Owner = this };
        window.ShowDialog();
        // The re-center hotkey may have changed in the dialog; re-register from applied settings.
        RegisterRecenterHotkey();
    }

    private void RequestExit()
    {
        _reallyExit = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        App.LogNote($"main window closing (reallyExit={_reallyExit})");
        if (!_reallyExit)
        {
            // Keep tracking in the background; exit only via the tray menu.
            e.Cancel = true;
            Hide();
            return;
        }

        // Posts WM_QUIT to the hotkey thread, which unregisters on the thread that registered,
        // then joins it — leaving the process without a stale global hotkey behind.
        _hotkey.Dispose();
        LanguageService.LanguageChanged -= UpdateHotkeyWarning;
        _trayIcon.Dispose();
        _viewModel.Dispose();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayShowClick(object sender, RoutedEventArgs e) => ShowFromTray();
    private void OnTrayToggleClick(object sender, RoutedEventArgs e) => _viewModel.ToggleCommand.Execute(null);
    private void OnTrayRecenterClick(object sender, RoutedEventArgs e) => _viewModel.RecenterCommand.Execute(null);
    private void OnTraySettingsClick(object sender, RoutedEventArgs e) => OpenSettings();
    private void OnTrayExitClick(object sender, RoutedEventArgs e) => RequestExit();
}
