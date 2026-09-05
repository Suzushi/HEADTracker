using System.ComponentModel;
using System.Windows;

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
}
