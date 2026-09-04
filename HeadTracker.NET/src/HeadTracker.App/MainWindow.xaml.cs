using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Hardcodet.Wpf.TaskbarNotification;
using HeadTracker.App.Services;
using HeadTracker.App.ViewModels;

namespace HeadTracker.App;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyRecenterId = 0xA17C;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly MainViewModel _viewModel;
    private readonly TaskbarIcon _trayIcon;
    private bool _reallyExit;
    private bool _hotkeyRegistered;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        var service = new TrackerService(Path.Combine(AppContext.BaseDirectory, "config.yaml"));
        _viewModel = new MainViewModel(service, OpenSettings, RequestExit);
        DataContext = _viewModel;

        // Force instantiation of the lazily-created tray icon resource.
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnWindowClosing;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterRecenterHotkey();
    }

    /// <summary>
    /// (Re)register the global re-center hotkey from settings. "Global" is the point: it
    /// fires even when the game has focus, unlike the in-window C/F9 handlers. The VK must
    /// come from KeyInterop.VirtualKeyFromKey — casting the WPF Key enum directly is wrong
    /// (Key.C == 46 == VK_DELETE), which is why the old hardcoded Alt+C never worked in-game.
    /// </summary>
    private void RegisterRecenterHotkey()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HotkeyRecenterId);
            _hotkeyRegistered = false;
        }

        if (!HotkeyParser.TryParse(_viewModel.Service.Settings.RecenterHotkey, out uint mods, out int vk))
        {
            return; // empty/invalid spec: leave unregistered (in-window C still works)
        }
        // MOD_NOREPEAT keeps key auto-repeat from flooding re-center while the combo is held.
        _hotkeyRegistered = RegisterHotKey(_hwnd, HotkeyRecenterId, mods | HotkeyParser.ModNoRepeat, (uint)vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyRecenterId)
        {
            _viewModel.RecenterCommand.Execute(null);
            handled = true;
        }
        return IntPtr.Zero;
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
        if (!_reallyExit)
        {
            // Keep tracking in the background; exit only via the tray menu.
            e.Cancel = true;
            Hide();
            return;
        }

        if (_hwnd != IntPtr.Zero && _hotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HotkeyRecenterId);
            _hotkeyRegistered = false;
        }
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
