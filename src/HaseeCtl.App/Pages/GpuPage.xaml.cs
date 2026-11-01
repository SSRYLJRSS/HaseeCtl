using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class GpuPage : UserControl
{
    private UiState? _state;
    private GpuState? _current;

    public GpuPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_state is not null) return;
        _state = DataContext as UiState;
        if (_state is null) return;

        ModePanel.Children.Clear();
        try { _current = _state.Engine.WithHardware(() => _state.Engine.Gpu?.Read()); }
        catch (Exception ex) { Log.Error("读取显卡模式失败", ex); }

        if (_current is null)
        {
            HintText.Text = "本机未报告显卡切换能力。";
            return;
        }

        CurrentText.Text = GpuService.DisplayName(_current.Current);
        foreach (var mode in _current.Supported)
        {
            var rb = new RadioButton
            {
                Content = GpuService.DisplayName(mode),
                Tag = mode,
                GroupName = "gpu",
                Margin = new Thickness(0, 0, 0, 10),
                IsChecked = mode == _current.Current,
            };
            ModePanel.Children.Add(rb);
        }
        HintText.Text = _current.Supported.Count <= 1 ? "固件只报告了一种可用模式。" : "选择后点应用，重启完成切换。";
    }

    private GpuMode? Selected()
    {
        foreach (RadioButton rb in ModePanel.Children)
            if (rb.IsChecked == true && rb.Tag is GpuMode m) return m;
        return null;
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply(false);

    private void OnApplyReboot(object sender, RoutedEventArgs e) => Apply(true);

    private void Apply(bool reboot)
    {
        var mode = Selected();
        if (mode is null || _state is null) return;
        if (_current is not null && mode == _current.Current)
        {
            HintText.Text = "已经是该模式，无需切换。";
            return;
        }

        var text = $"切换到「{GpuService.DisplayName(mode.Value)}」" + (reboot ? "并立即重启？" : "？重启后生效。");
        if (MessageBox.Show(text, "HaseeCtl", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            var m = mode.Value;
            _state.Engine.WithHardware(() => _state.Engine.Gpu?.Switch(m));
            _state.Update(s => s.Gpu.Mode = (int)m);
            HintText.Text = "已写入切换标志，重启后生效。";
            if (reboot) Process.Start(new ProcessStartInfo("shutdown.exe", "-f -r -t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
