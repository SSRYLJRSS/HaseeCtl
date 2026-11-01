using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HaseeCtl.Agent;

/// <summary>Minimal on-screen display: a rounded dark card in the upper middle of the primary screen.
/// Must be shown on the tray's UI thread (the one running the message loop); see TrayApp.ShowOsd.</summary>
public sealed class Osd : Form
{
    private static Osd? _current;
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 15f, FontStyle.Bold);
    private static readonly Font SubFont = new("Microsoft YaHei UI", 10f, FontStyle.Regular);
    private static readonly StringFormat Center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private System.Windows.Forms.Timer? _fade;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _title;
    private readonly string _subtitle;

    private Osd(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        DoubleBuffered = true;
        Opacity = 1.0;
        Size = new Size(string.IsNullOrEmpty(subtitle) ? 260 : 300, string.IsNullOrEmpty(subtitle) ? 76 : 96);

        var screen = Screen.PrimaryScreen;
        if (screen is not null)
            Location = new Point(screen.WorkingArea.Left + (screen.WorkingArea.Width - Width) / 2,
                screen.WorkingArea.Top + 60);

        _timer = new System.Windows.Forms.Timer { Interval = 1400 };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            FadeOut();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(ClientRectangle, 14))
        using (var brush = new SolidBrush(BackColor))
            g.FillPath(brush, path);

        using var textBrush = new SolidBrush(Color.White);
        using var dimBrush = new SolidBrush(Color.FromArgb(190, 190, 200));

        var titleRect = string.IsNullOrEmpty(_subtitle)
            ? new RectangleF(0, 0, Width, Height)
            : new RectangleF(0, 10, Width, 44);
        g.DrawString(_title, TitleFont, textBrush, titleRect, Center);
        if (!string.IsNullOrEmpty(_subtitle))
            g.DrawString(_subtitle, SubFont, dimBrush, new RectangleF(0, 52, Width, 30), Center);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void FadeOut()
    {
        _fade = new System.Windows.Forms.Timer { Interval = 30 };
        _fade.Tick += (_, _) =>
        {
            Opacity -= 0.12;
            if (Opacity > 0.1) return;
            _fade?.Stop();
            Close();
        };
        _fade.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (ReferenceEquals(_current, this)) _current = null; // Close() of a modeless form disposes it
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _fade?.Dispose();
            _fade = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    public static void Show(string title, string subtitle = "")
    {
        try
        {
            if (_current is { IsDisposed: false }) _current.Close();
            _current = new Osd(title, subtitle);
            _current.Show();
        }
        catch
        {
            // OSD is cosmetic only
        }
    }
}
