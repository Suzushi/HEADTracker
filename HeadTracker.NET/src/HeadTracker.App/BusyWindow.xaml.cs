using System.ComponentModel;
using System.Windows;
using System.Threading.Tasks;

namespace HeadTracker.App;

/// <summary>
/// Modal "please wait" shield shown while camera negotiation runs on a background thread.
/// Probing the (backend, format, resolution) ladder takes seconds, and letting the user open
/// settings or stop mid-probe only invites trouble, so this dialog is unclosable and, being
/// modal, makes every other control unreachable until negotiation settles.
/// </summary>
public partial class BusyWindow : Window
{
    /// <summary>Set by the owner right before Close(); user-initiated closes stay cancelled.</summary>
    public bool AllowClose { get; set; }

    public BusyWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true; // no X, no Alt+F4: only negotiation completion may close us
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a background thread behind this unclosable modal dialog.
    /// Every path that reopens the camera goes through here, not just the main window's start
    /// button: saving settings restarts the pipeline too, and "refresh cameras" clicked mid-probe
    /// means a device enumeration racing a live capture stream.
    /// </summary>
    public static async Task<bool> RunBlockedAsync(Func<bool> work, Window? owner = null)
    {
        owner ??= Application.Current?.MainWindow;
        var busy = new BusyWindow { Owner = owner is { IsVisible: true } ? owner : null };
        var task = Task.Run(work);
        _ = task.ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            busy.AllowClose = true;
            busy.Close();
        })), TaskScheduler.Default);
        busy.ShowDialog(); // modal: nothing else is clickable until this returns
        return await task;
    }
}
