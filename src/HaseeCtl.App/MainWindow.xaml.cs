using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HaseeCtl.App.Pages;
using Wpf.Ui.Controls;

namespace HaseeCtl.App;

public partial class MainWindow : FluentWindow
{
    private readonly UiState _state = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _state;
        NavList.SelectedIndex = 0;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;
        FrameworkElement page = tag switch
        {
            "fan" => new FanPage(),
            "performance" => new PerformancePage(),
            "battery" => new BatteryPage(),
            "gpu" => new GpuPage(),
            "light" => new LightPage(),
            "settings" => new SettingsPage(),
            _ => new DashboardPage(),
        };
        page.DataContext = _state;
        PageHost.Content = page;
    }

    public int PageCount => NavList.Items.Count;

    public string PageKey(int index) =>
        (NavList.Items[index] as ListBoxItem)?.Tag as string ?? index.ToString();

    public void SelectPage(int index) => NavList.SelectedIndex = index;

    /// <summary>Self-test helper: render the current visual tree to a PNG without screen capture.</summary>
    public bool SaveVisual(string path)
    {
        var visual = (FrameworkElement)Content;
        double w = visual.ActualWidth, h = visual.ActualHeight;
        var parent = VisualTreeHelper.GetParent(visual) as FrameworkElement;
        if (parent is not null) { w = parent.ActualWidth; h = parent.ActualHeight; }
        if (w <= 1 || h <= 1) return false;

        const double scale = 1.5;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // the live window paints its backdrop with Mica, which RenderTargetBitmap cannot see,
        // so composite the tree over an explicit dark backdrop first
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, w, h));
        }
        rtb.Render(backdrop);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return true;
    }
}
