using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HaseeCtl.App.Controls;

/// <summary>
/// Draggable fan-curve editor. Four nodes: the first (start-up) and the last (full load) come
/// straight from firmware and cannot move; the two middle nodes are dragged with the mouse.
/// Everything is drawn in black and white to match the app's look.
/// </summary>
public class FanCurveEditor : FrameworkElement
{
    private const double PadLeft = 46;
    private const double PadRight = 18;
    private const double PadTop = 16;
    private const double PadBottom = 30;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Brush Grid = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly Brush AxisText = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush Curve = Brushes.White;
    private static readonly Brush NodeFill = Brushes.White;
    private static readonly Brush NodeLockedFill = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Brush LabelBg = new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x00, 0x00));

    private static readonly Pen GridPen = MakePen(Grid, 1);
    private static readonly Pen AxisPen = MakePen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1);
    private static readonly Pen CurvePen = MakePen(Curve, 2.4);
    private static readonly Pen CurveGlow = MakePen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 7);
    private static readonly Pen NodePen = MakePen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)), 2.5);
    private static readonly Pen LockedPen = MakePen(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)), 1.6);

    private static Pen MakePen(Brush b, double t)
    {
        var p = new Pen(b, t);
        p.Freeze();
        return p;
    }

    private static readonly Typeface Face = new("Segoe UI");

    private double _t1, _d1, _t2, _d2, _t3, _d3;
    private double _t4 = 100, _d4 = 100;
    private int _drag = -1;
    private bool _hover;

    public FanCurveEditor()
    {
        SnapsToDevicePixels = true;
        Focusable = true;
        MinHeight = 220;
        Cursor = Cursors.Arrow;
    }

    /// <summary>Raised when a draggable node has moved.</summary>
    public event EventHandler? CurveChanged;

    public double T1 { get => _t1; set { _t1 = value; InvalidateVisual(); } }
    public double D1 { get => _d1; set { _d1 = value; InvalidateVisual(); } }
    public double T2 { get => _t2; set { _t2 = Clamp(value, _t1 + 1, _t3 - 1); InvalidateVisual(); } }
    public double D2 { get => _d2; set { _d2 = Clamp(value, 0, 100); InvalidateVisual(); } }
    public double T3 { get => _t3; set { _t3 = Clamp(value, _t2 + 1, _t4 - 1); InvalidateVisual(); } }
    public double D3 { get => _d3; set { _d3 = Clamp(value, 0, 100); InvalidateVisual(); } }
    public double T4 { get => _t4; set { _t4 = value; InvalidateVisual(); } }
    public double D4 { get => _d4; set { _d4 = value; InvalidateVisual(); } }

    /// <summary>Shown next to the locked start-up node so the user knows it is firmware owned.</summary>
    public string LockedHint { get; set; } = "温度固件固定 · 可上下拖";

    /// <summary>Current sensor temperature, drawn as a vertical marker when set.</summary>
    public double? MarkerTemp
    {
        get => _marker;
        set { _marker = value; InvalidateVisual(); }
    }
    private double? _marker;

    public void SetCurve(double t1, double d1, double t2, double d2, double t3, double d3)
    {
        _t1 = t1; _d1 = d1;
        _t2 = Clamp(t2, t1 + 1, t3 - 1);
        _d2 = Clamp(d2, 0, 100);
        _t3 = Clamp(t3, _t2 + 1, 99);
        _d3 = Clamp(d3, 0, 100);
        InvalidateVisual();
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    // ---- geometry helpers -------------------------------------------------

    private Rect Plot => new(PadLeft, PadTop, Math.Max(1, ActualWidth - PadLeft - PadRight), Math.Max(1, ActualHeight - PadTop - PadBottom));

    private Point ToPoint(double temp, double duty)
    {
        var r = Plot;
        // temperatures beyond 100°C are squashed into the right edge
        double x = r.X + r.Width * (Math.Min(temp, 100) - 0) / 100.0;
        double y = r.Y + r.Height * (1 - Math.Min(duty, 100) / 100.0);
        return new Point(x, y);
    }

    private double ToTemp(Point p)
    {
        var r = Plot;
        double t = (p.X - r.X) / r.Width * 100.0;
        return Math.Round(Clamp(t, 0, 100));
    }

    private double ToDuty(Point p)
    {
        var r = Plot;
        double d = (1 - (p.Y - r.Y) / r.Height) * 100.0;
        return Math.Round(Clamp(d, 0, 100));
    }

    private Point NodePoint(int i) => i switch
    {
        0 => ToPoint(_t1, _d1),
        1 => ToPoint(_t2, _d2),
        2 => ToPoint(_t3, _d3),
        _ => ToPoint(_t4, _d4),
    };

    // ---- rendering --------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);

        // grid
        for (int t = 20; t <= 100; t += 20)
        {
            double x = r.X + r.Width * t / 100.0;
            dc.DrawLine(GridPen, new Point(x, r.Y), new Point(x, r.Bottom));
            DrawText(dc, t + "°", new Point(x - 10, r.Bottom + 6), 11, AxisText);
        }
        for (int d = 20; d <= 100; d += 20)
        {
            double y = r.Y + r.Height * (1 - d / 100.0);
            dc.DrawLine(GridPen, new Point(r.X, y), new Point(r.Right, y));
            DrawText(dc, d + "%", new Point(6, y - 8), 11, AxisText);
        }
        dc.DrawLine(AxisPen, new Point(r.X, r.Bottom), new Point(r.Right, r.Bottom));
        dc.DrawLine(AxisPen, new Point(r.X, r.Y), new Point(r.X, r.Bottom));

        // filled area under the curve
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(NodePoint(0).X, r.Bottom), true, true);
            for (int i = 0; i < 4; i++) ctx.LineTo(NodePoint(i), true, false);
            ctx.LineTo(new Point(NodePoint(3).X, r.Bottom), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(
            Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
            new Point(0, 0), new Point(0, 1)), null, geo);

        // cooling-marker for the live sensor value
        if (_marker is { } mt)
        {
            double mx = r.X + r.Width * Math.Min(mt, 100) / 100.0;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1)
            { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
            pen.Freeze();
            dc.DrawLine(pen, new Point(mx, r.Y), new Point(mx, r.Bottom));
        }

        // curve
        var curveGeo = new StreamGeometry();
        using (var ctx = curveGeo.Open())
        {
            ctx.BeginFigure(NodePoint(0), false, false);
            for (int i = 1; i < 4; i++) ctx.LineTo(NodePoint(i), true, false);
        }
        curveGeo.Freeze();
        dc.DrawGeometry(null, CurveGlow, curveGeo);
        dc.DrawGeometry(null, CurvePen, curveGeo);

        // nodes
        for (int i = 0; i < 4; i++)
        {
            var pt = NodePoint(i);
            bool locked = i == 3; // 起转点 0 只锁温度，转速可上下拖
            bool active = (i == _drag) || (!locked && _hover && HitTest(i, Mouse.GetPosition(this)));
            double radius = locked ? 5 : active ? 9 : 7;
            if (!locked && active)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)), null, pt, radius + 7, radius + 7);
            }
            dc.DrawEllipse(locked ? NodeLockedFill : NodeFill, locked ? LockedPen : NodePen, pt, radius, radius);
        }

        // labels for the two draggable nodes
        DrawValueLabel(dc, NodePoint(1), _t2, _d2, false);
        DrawValueLabel(dc, NodePoint(2), _t3, _d3, true);
        DrawValueLabel(dc, NodePoint(0), _t1, _d1, false, LockedHint);
    }

    private void DrawValueLabel(DrawingContext dc, Point at, double temp, double duty, bool above, string? suffix = null)
    {
        string text = $"{temp:0}° / {duty:0}%" + (suffix is null ? string.Empty : "  " + suffix);
        var ft = MakeText(text, 11, Brushes.White);
        double w = ft.Width + 12, h = ft.Height + 6;
        double x = Math.Min(Math.Max(at.X - w / 2, 4), Math.Max(4, ActualWidth - w - 4));
        double y = above ? at.Y - h - 12 : at.Y + 12;
        if (y + h > ActualHeight - 2) y = at.Y - h - 12;
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(x, y, w, h), 4, 4);
        dc.DrawText(ft, new Point(x + 6, y + 3));
    }

    private FormattedText MakeText(string text, double size, Brush brush)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, Point at, double size, Brush brush)
        => dc.DrawText(MakeText(text, size, brush), at);

    // ---- interaction ------------------------------------------------------

    private bool HitTest(int node, Point p)
    {
        var c = NodePoint(node);
        return (p - c).Length <= 14;
    }

    private int NodeAt(Point p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (HitTest(i, p)) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_drag >= 0)
        {
            if (_drag == 1)
            {
                _t2 = ToTemp(p);
                _d2 = ToDuty(p);
            }
            else if (_drag == 0)
            {
                // 起转温度由固件决定，这里只改起转转速，且不能高于节点 2
                _d1 = Clamp(ToDuty(p), 0, _d2);
            }
            else if (_drag == 2)
            {
                _t3 = ToTemp(p);
                _d3 = ToDuty(p);
            }
            InvalidateVisual();
            return;
        }

        bool over = NodeAt(p) is 0 or 1 or 2;
        if (over != _hover)
        {
            _hover = over;
            Cursor = over ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int node = NodeAt(e.GetPosition(this));
        if (node is 0 or 1 or 2)
        {
            _drag = node;
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag < 0) return;
        _drag = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        CurveChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag >= 0) return;
        if (_hover)
        {
            _hover = false;
            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }
    }
}
