using System.Windows;
using System.Windows.Controls;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class PerformancePage : UserControl
{
    private UiState? _state;
    private bool _loading;

    public PerformancePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_state is not null) return;
        _state = DataContext as UiState;
        if (_state is null) return;

        _loading = true;
        var engine = _state.Engine;
        ModePanel.Children.Clear();
        if (engine.Caps is null || engine.Power is null)
        {
            CurrentText.Text = "未连接驱动";
            TurboCheck.IsEnabled = false;
            BootApplyCheck.IsChecked = _state.Settings.ApplyOnStartup;
            _loading = false;
            return;
        }
        var supported = PowerModes.Supported(engine.Caps);
        foreach (var mode in supported)
        {
            var rb = new RadioButton
            {
                Content = PowerModes.DisplayName(mode),
                Tag = mode,
                Margin = new Thickness(0, 0, 0, 10),
                GroupName = "power",
            };
            rb.Checked += OnModeChecked;
            ModePanel.Children.Add(rb);
        }

        try
        {
            var (current, turbo) = engine.WithHardware(() => (engine.Power.Current, engine.Power.TurboFanEnabled));
            CurrentText.Text = PowerModes.DisplayName(current);
            foreach (RadioButton rb in ModePanel.Children)
                if (rb.Tag is PowerModeKind k && k == current) rb.IsChecked = true;
            TurboCheck.IsChecked = turbo;
        }
        catch (Exception ex) { Log.Error("读取性能模式失败", ex); }

        BootApplyCheck.IsChecked = _state.Settings.ApplyOnStartup;
        _loading = false;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || _state is null) return;
        if (sender is not RadioButton { Tag: PowerModeKind mode }) return;
        try
        {
            _state.Engine.SetPerformanceMode(mode); // persists, applies and re-pushes the fan curve
            CurrentText.Text = PowerModes.DisplayName(mode);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnTurboChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state is null) return;
        try
        {
            _state.Engine.SetTurboFan(TurboCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBootApplyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state is null) return;
        bool on = BootApplyCheck.IsChecked == true;
        _state.Update(s => s.ApplyOnStartup = on);
    }
}
