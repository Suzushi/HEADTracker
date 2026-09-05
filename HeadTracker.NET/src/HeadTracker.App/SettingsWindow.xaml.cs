using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeadTracker.App.Services;
using HeadTracker.Core.Configuration;

namespace HeadTracker.App;

/// <summary>
/// Settings editor. Binds directly to a fresh copy of TrackerSettings loaded
/// from disk; "Save &amp; Apply" persists it to config.yaml and restarts the
/// pipeline when tracking is active.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly TrackerService _service;
    private readonly TrackerSettings _edit;
    private readonly string _langOnOpen;

    public SettingsWindow(TrackerService service)
    {
        InitializeComponent();
        _service = service;
        _edit = SettingsStore.Load(service.ConfigPath);
        _langOnOpen = LanguageService.Current;
        DataContext = _edit;
        try
        {
            RefreshCameraList();
        }
        catch (Exception ex)
        {
            // A driver fault here must not take the main window down with it.
            App.LogNote($"settings RefreshCameraList: {ex}");
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Numeric boxes commit on focus loss now, so half-typed states ("0.", "-", a cleared
        // field) stay editable instead of being reverted mid-keystroke. Enter activates this
        // default button without moving focus, though: flush the field the caret is still in,
        // and refuse to save what the user cannot have meant -- a silent fallback to the
        // previous number is worse than an error.
        if (Keyboard.FocusedElement is TextBox focused)
        {
            string typed = focused.Text;
            var binding = focused.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            if (binding is { HasError: true })
            {
                MessageBox.Show(this, string.Format(Loc.Tr("set_invalid_number"), typed),
                    Loc.Tr("settings_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                focused.Focus();
                return;
            }
        }

        _edit.Normalize();
        SaveButton.IsEnabled = false;
        SaveButton.Content = Loc.Tr("btn_saving");
        if (_service.IsRunning)
        {
            // Saving while tracking restarts the pipeline, which re-runs camera negotiation
            // (seconds of probing). Shield this window as well: "refresh cameras" clicked
            // mid-probe would enumerate devices against a live capture stream.
            await BusyWindow.RunBlockedAsync(() =>
            {
                _service.ApplySettings(_edit);
                return true;
            }, this);
        }
        else
        {
            // Plain persist: nothing to reopen, so no dialog flash.
            await Task.Run(() => _service.ApplySettings(_edit));
        }
        DialogResult = true;
        Close();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        // Live preview: switch the UI language immediately; persisted on Save.
        if (sender is ComboBox { SelectedValue: string tag })
        {
            LanguageService.Apply(LanguageService.Resolve(tag));
        }
    }

    private void OnRefreshCameras(object sender, RoutedEventArgs e)
    {
        // Re-enumerating while a capture graph is streaming can crash some drivers; while
        // running we only re-show the cache, and re-probe the hardware when idle.
        if (!_service.IsRunning)
        {
            CameraEnumerator.RefreshCache();
        }
        RefreshCameraList();
    }

    /// <summary>
    /// Fills the camera ComboBox from the cached DirectShow device list (captured at startup,
    /// before any stream existed). If enumeration found nothing we offer plain numeric ids
    /// (0..4) as a manual fallback, and if the saved id isn't connected we keep it selectable
    /// so the user's config is never silently rewritten.
    /// </summary>
    private void RefreshCameraList()
    {
        var list = new List<CameraEnumerator.CameraDevice>(CameraEnumerator.GetCached());

        if (list.Count == 0)
        {
            for (int i = 0; i <= 4; i++)
            {
                list.Add(new CameraEnumerator.CameraDevice(i, string.Format(Loc.Tr("set_camera_fallback"), i)));
            }
        }

        int saved = _edit.CameraId;
        if (!list.Exists(d => d.Index == saved))
        {
            list.Insert(0, new CameraEnumerator.CameraDevice(saved, string.Format(Loc.Tr("set_camera_unavailable"), saved)));
        }

        CameraCombo.ItemsSource = list;
        // Re-assert the selection after (re)assigning ItemsSource so the shown
        // camera is deterministic and survives a manual Refresh.
        CameraCombo.SelectedValue = saved;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // Undo the live language preview, mirroring "discard all edits".
        if (LanguageService.Current != _langOnOpen)
        {
            LanguageService.Apply(_langOnOpen);
        }
        DialogResult = false;
        Close();
    }

    private void OnCalibrationClick(object sender, RoutedEventArgs e)
    {
        // The wizard owns the camera (it stops the pipeline) and writes K/D straight
        // to config.yaml + service.Settings. Mirror those fields back into our edit
        // copy so a later "Save & Apply" here does not clobber the fresh calibration.
        var window = new CalibrationWindow(_service) { Owner = this };
        window.ShowDialog();
        CopyCalibration(_service.Settings, _edit);
    }

    private void OnEditCurves(object sender, RoutedEventArgs e)
    {
        // The editor mutates the six curve_* fields of our edit copy directly on OK
        // (and leaves them untouched on Cancel); "Save & Apply" then persists them.
        var window = new CurveEditorWindow(_edit) { Owner = this };
        window.ShowDialog();
    }

    private static void CopyCalibration(TrackerSettings from, TrackerSettings to)
    {
        to.CameraFx = from.CameraFx;
        to.CameraFy = from.CameraFy;
        to.CameraCx = from.CameraCx;
        to.CameraCy = from.CameraCy;
        to.DistK1 = from.DistK1;
        to.DistK2 = from.DistK2;
        to.DistP1 = from.DistP1;
        to.DistP2 = from.DistP2;
        to.DistK3 = from.DistK3;
        to.CalibratedWidth = from.CalibratedWidth;
        to.CalibratedHeight = from.CalibratedHeight;
        to.CalibrationRms = from.CalibrationRms;
    }
}
