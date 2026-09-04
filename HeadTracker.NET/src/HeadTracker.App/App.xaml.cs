using System.IO;
using System.Windows;
using System.Windows.Threading;
using HeadTracker.App.Services;
using HeadTracker.Core.Configuration;

namespace HeadTracker.App;

/// <summary>
/// Interaction logic for App.xaml. Installs global exception handlers so a
/// fault never terminates the process silently: UI-thread faults are handled
/// and kept alive, every fault is appended to crash.log next to the exe.
/// </summary>
public partial class App : Application
{
    // Single-instance guard. HeadTracker hides to the tray instead of exiting, so it
    // is easy to pile up zombie processes that all fight over the one camera
    // ("cannot open camera 0", flaky/tiled preview). A second launch signals the
    // running instance to bring its window forward, then exits quietly.
    private const string MutexName = "HeadTracker_SingleInstance_Mutex_v1";
    private const string ActivateEventName = "HeadTracker_Activate_Event_v1";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateThread;
    private volatile bool _instanceAlive;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }
        StartActivationListener();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Apply the configured UI language before StartupUri creates MainWindow.
        try
        {
            var settings = SettingsStore.Load(Path.Combine(AppContext.BaseDirectory, "config.yaml"));
            LanguageService.Apply(LanguageService.Resolve(settings.UiLanguage));
        }
        catch
        {
            // Missing/corrupt config: keep the default (English) dictionary from App.xaml.
        }

        // A corrupt config.yaml now falls back to defaults instead of crashing; tell the
        // user so they know why their settings were reset and where the backup is.
        if (SettingsStore.LastLoadError != null)
        {
            MessageBox.Show(
                "config.yaml 解析失败，已改用默认设置启动。\n" +
                "原文件已备份为 config.bad.yaml，详情见 config-error.log。\n\n" +
                "Failed to parse config.yaml — started with default settings.\n" +
                $"Error: {SettingsStore.LastLoadError}",
                "HeadTracker", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        base.OnStartup(e);
    }

    /// <summary>Background wait: when a second launch signals us, bring our window forward.</summary>
    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _instanceAlive = true;
        _activateThread = new Thread(() =>
        {
            while (_instanceAlive)
            {
                if (_activateEvent!.WaitOne(200))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var window = MainWindow;
                        if (window != null)
                        {
                            window.Show();
                            window.WindowState = WindowState.Normal;
                            window.Activate();
                        }
                    });
                }
            }
        })
        { IsBackground = true, Name = "HeadTrackerActivate" };
        _activateThread.Start();
    }

    /// <summary>Second instance: ask the running one to surface (best-effort).</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var evt))
            {
                evt.Set();
                evt.Dispose();
            }
        }
        catch
        {
            // If we cannot signal it we still refuse to start a duplicate.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceAlive = false;
        _activateThread?.Join(300);
        _activateEvent?.Dispose();
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "UI thread (handled, app continues)");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash(e.ExceptionObject as Exception, $"background thread (fatal={e.IsTerminating})");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash(e.Exception, "unobserved task");
        e.SetObserved();
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never become the next crash.
        }
    }
}
