using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HeadTracker.App.Services;
using HeadTracker.Core.Configuration;
using HeadTracker.Core.Vision;

namespace HeadTracker.App;

/// <summary>
/// Visual editor for the per-axis input→output response curves that replace the single
/// legacy "expo" number. It edits the six <c>curve_*</c> fields of a
/// <see cref="TrackerSettings"/> copy in place: the user picks an axis, drags control
/// points on a normalized [-1,1] plot (endpoints pinned), and toggles whether the curve
/// overrides expo. On OK each enabled axis is serialized back; disabled axes are cleared
/// so PoseRemapper falls back to expo. Rendering uses the same <see cref="ResponseCurve"/>
/// (monotone cubic) the runtime uses, so the preview matches in-game behaviour.
/// </summary>
public partial class CurveEditorWindow : Window
{
    private sealed class Axis
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public List<(double X, double Y)> Points { get; set; } = new();
        public double Expo { get; set; }
        public Action<TrackerSettings, string> Set { get; set; } = (_, _) => { };
    }

    private const double Pad = 20;
    private const int Samples = 161;

    private static readonly Color GridMinor = Color.FromRgb(0xDD, 0xDD, 0xDD);
    private static readonly Color GridAxis = Color.FromRgb(0xAA, 0xAA, 0xAA);
    private static readonly Color IdentityColor = Color.FromRgb(0xCC, 0xCC, 0xCC);
    private static readonly Color CurveOn = Color.FromRgb(0x1E, 0x7B, 0xD6);
    private static readonly Color CurveOff = Color.FromRgb(0xAA, 0xAA, 0xAA);
    private static readonly Color EndPointColor = Color.FromRgb(0x88, 0x88, 0x88);
    private static readonly Color PointColor = Color.FromRgb(0x1E, 0x7B, 0xD6);
    private static readonly Color PointSelColor = Color.FromRgb(0xF5, 0xA6, 0x23);

    private readonly TrackerSettings _settings;
    private readonly List<Axis> _axes = new();
    private Axis? _current;

    private int _dragIndex = -1;
    private int _selectedIndex = -1;

    public CurveEditorWindow(TrackerSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        BuildAxes();
        AxisList.ItemsSource = _axes;
        PlotCanvas.SizeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
        PreviewKeyDown += OnWindowKeyDown;
        AxisList.SelectedIndex = 0;
    }

    private void BuildAxes()
    {
        _axes.Add(MakeAxis("curve_axis_trans_x", s => s.CurveTransX, (s, v) => s.CurveTransX = v, s => s.ExpoTransX));
        _axes.Add(MakeAxis("curve_axis_trans_y", s => s.CurveTransY, (s, v) => s.CurveTransY = v, s => s.ExpoTransY));
        _axes.Add(MakeAxis("curve_axis_trans_z", s => s.CurveTransZ, (s, v) => s.CurveTransZ = v, s => s.ExpoTransZ));
        _axes.Add(MakeAxis("curve_axis_yaw", s => s.CurveEulYaw, (s, v) => s.CurveEulYaw = v, s => s.ExpoEulYaw));
        _axes.Add(MakeAxis("curve_axis_pitch", s => s.CurveEulPitch, (s, v) => s.CurveEulPitch = v, s => s.ExpoEulPitch));
        _axes.Add(MakeAxis("curve_axis_roll", s => s.CurveEulRoll, (s, v) => s.CurveEulRoll = v, s => s.ExpoEulRoll));
    }

    private Axis MakeAxis(string nameKey, Func<TrackerSettings, string> get,
        Action<TrackerSettings, string> set, Func<TrackerSettings, double> expo)
    {
        var parsed = ResponseCurve.TryParse(get(_settings));
        double ex = expo(_settings);
        return new Axis
        {
            Name = Loc.Tr(nameKey),
            Enabled = parsed != null,
            Points = (parsed ?? ResponseCurve.FromExpo(ex)).Points,
            Expo = ex,
            Set = set,
        };
    }

    // ------------------------------------------------------------------ selection

    private void OnAxisChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AxisList.SelectedItem is not Axis a)
        {
            return;
        }
        _current = a;
        _selectedIndex = -1;
        _dragIndex = -1;
        EnableCheck.IsChecked = a.Enabled;
        Render();
    }

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        _current.Enabled = EnableCheck.IsChecked == true;
        Render();
    }

    // ------------------------------------------------------------------ editing

    private void OnResetFromExpo(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        EnsureEnabledForEdit();
        _current.Points = ResponseCurve.FromExpo(_current.Expo).Points;
        _selectedIndex = -1;
        Render();
    }

    private void OnLinear(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        EnsureEnabledForEdit();
        _current.Points = new ResponseCurve(Array.Empty<(double X, double Y)>()).Points;
        _selectedIndex = -1;
        Render();
    }

    private void OnDeletePoint(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DeleteSelected())
        {
            e.Handled = true;
        }
    }

    private bool DeleteSelected()
    {
        if (_current == null || !IsInterior(_selectedIndex))
        {
            return false;
        }
        EnsureEnabledForEdit();
        _current.Points.RemoveAt(_selectedIndex);
        _selectedIndex = -1;
        SortPoints();
        Render();
        return true;
    }

    // ------------------------------------------------------------------ mouse

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        var pos = e.GetPosition(PlotCanvas);
        if (e.ClickCount == 2)
        {
            var (x, y) = ToDomain(pos);
            if (Math.Abs(x) <= 0.98)
            {
                EnsureEnabledForEdit();
                _current.Points.Add((x, y));
                SortPoints();
                _selectedIndex = _current.Points.FindIndex(p => Math.Abs(p.X - x) < 1e-9 && Math.Abs(p.Y - y) < 1e-9);
                Render();
            }
            e.Handled = true;
            return;
        }
        _selectedIndex = -1;
        Render();
    }

    private void OnPointMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse { Tag: int idx } || _current == null)
        {
            return;
        }
        _selectedIndex = idx;
        if (IsInterior(idx))
        {
            _dragIndex = idx;
            EnsureEnabledForEdit();
            PlotCanvas.CaptureMouse();
        }
        Render();
        e.Handled = true;
    }

    private void OnPointRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse { Tag: int idx } && IsInterior(idx))
        {
            _selectedIndex = idx;
            DeleteSelected();
        }
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || _current == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var (x, y) = ToDomain(e.GetPosition(PlotCanvas));
        _current.Points[_dragIndex] = (Math.Clamp(x, -0.98, 0.98), Math.Clamp(y, -1, 1));
        Render();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragIndex < 0 || _current == null)
        {
            return;
        }
        var dragged = _current.Points[_dragIndex];
        _dragIndex = -1;
        PlotCanvas.ReleaseMouseCapture();
        SortPoints();
        _selectedIndex = _current.Points.IndexOf(dragged);
        Render();
    }

    // ------------------------------------------------------------------ commit

    private void OnOk(object sender, RoutedEventArgs e)
    {
        foreach (var a in _axes)
        {
            a.Set(_settings, a.Enabled ? new ResponseCurve(a.Points).Serialize() : "");
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ------------------------------------------------------------------ helpers

    private void EnsureEnabledForEdit()
    {
        if (_current != null && !_current.Enabled)
        {
            EnableCheck.IsChecked = true; // fires OnEnableChanged → sets Enabled
        }
    }

    private bool IsInterior(int i) => _current != null && i > 0 && i < _current.Points.Count - 1;

    private bool IsEndpoint(int i) => _current != null && (i == 0 || i == _current.Points.Count - 1);

    private void SortPoints() => _current?.Points.Sort((p, q) => p.X.CompareTo(q.X));

    private Point ToCanvas(double x, double y)
    {
        double w = PlotCanvas.ActualWidth, h = PlotCanvas.ActualHeight;
        return new Point(
            Pad + (x + 1) / 2 * (w - 2 * Pad),
            Pad + (1 - (y + 1) / 2) * (h - 2 * Pad));
    }

    private (double X, double Y) ToDomain(Point p)
    {
        double w = PlotCanvas.ActualWidth, h = PlotCanvas.ActualHeight;
        double x = (p.X - Pad) / (w - 2 * Pad) * 2 - 1;
        double y = (1 - (p.Y - Pad) / (h - 2 * Pad)) * 2 - 1;
        return (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));
    }

    private void AddLine(double x1, double y1, double x2, double y2, Color color, double thick, bool dash = false)
    {
        var line = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thick,
        };
        if (dash)
        {
            line.StrokeDashArray = new DoubleCollection { 4, 4 };
        }
        PlotCanvas.Children.Add(line);
    }

    private void Render()
    {
        if (_current == null)
        {
            return;
        }
        var canvas = PlotCanvas;
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 20 || h < 20)
        {
            return; // not laid out yet; SizeChanged/Loaded will re-render
        }

        foreach (double t in new[] { -1, -0.5, 0, 0.5, 1 })
        {
            var vt = ToCanvas(t, 1); var vb = ToCanvas(t, -1);
            AddLine(vt.X, vt.Y, vb.X, vb.Y, t == 0 ? GridAxis : GridMinor, 1);
            var hl = ToCanvas(-1, t); var hr = ToCanvas(1, t);
            AddLine(hl.X, hl.Y, hr.X, hr.Y, t == 0 ? GridAxis : GridMinor, 1);
        }

        var d0 = ToCanvas(-1, -1); var d1 = ToCanvas(1, 1);
        AddLine(d0.X, d0.Y, d1.X, d1.Y, IdentityColor, 1, dash: true);

        var curve = new ResponseCurve(_current.Points);
        var poly = new Polyline
        {
            Stroke = new SolidColorBrush(_current.Enabled ? CurveOn : CurveOff),
            StrokeThickness = 2.5,
        };
        for (int i = 0; i < Samples; i++)
        {
            double x = -1 + 2.0 * i / (Samples - 1);
            poly.Points.Add(ToCanvas(x, curve.Evaluate(x)));
        }
        canvas.Children.Add(poly);

        for (int i = 0; i < _current.Points.Count; i++)
        {
            var (px, py) = _current.Points[i];
            var cp = ToCanvas(px, py);
            bool endpoint = IsEndpoint(i);
            double r = endpoint ? 5 : 6;
            Color fill = endpoint ? EndPointColor : (i == _selectedIndex ? PointSelColor : PointColor);
            var el = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = new SolidColorBrush(fill),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                Tag = i,
                Cursor = endpoint ? Cursors.Arrow : Cursors.SizeAll,
            };
            Canvas.SetLeft(el, cp.X - r);
            Canvas.SetTop(el, cp.Y - r);
            if (!endpoint)
            {
                el.MouseLeftButtonDown += OnPointMouseDown;
                el.MouseRightButtonUp += OnPointRightClick;
            }
            canvas.Children.Add(el);
        }

        AxisTitle.Text = _current.Name;
        StatusReadout.Text = _current.Enabled
            ? string.Format(Loc.Tr("curve_status_enabled"), _current.Points.Count)
            : string.Format(Loc.Tr("curve_status_disabled"), _current.Expo.ToString("0.##"));
        PointReadout.Text = _selectedIndex >= 0 && _selectedIndex < _current.Points.Count
            ? string.Format(Loc.Tr("curve_selected"), _current.Points[_selectedIndex].X, _current.Points[_selectedIndex].Y)
            : "";
    }
}
