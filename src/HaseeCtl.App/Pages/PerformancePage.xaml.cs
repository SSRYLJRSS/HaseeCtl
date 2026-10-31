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
        var supported = engine.Power is null
            ? PowerModes.Supported(engine.Caps!)
            : PowerModes.Supported(engine.Caps!);
        ModePanel.Children.Clear();
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
            if (engine.Power is not null)
            {
                var current = engine.Power.Current;
                CurrentText.Text = PowerModes.DisplayName(current);
                foreach (RadioButton rb in ModePanel.Children)
                    if (rb.Tag is PowerModeKind k && k == current) rb.IsChecked = true;
                TurboCheck.IsChecked = engine.Power.TurboFanEnabled;
            }
        }
        catch (Exception ex) { Log.Write("读取性能模式失败: " + ex.Message); }

        BootApplyCheck.IsChecked = _state.Settings.ApplyOnStartup;
        _loading = false;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || _state is null) return;
        if (sender is not RadioButton { Tag: PowerModeKind mode }) return;
        try
        {
            _state.Settings.Performance.Mode = mode.ToString().ToLowerInvariant();
            _state.Save();
            _state.Engine.ApplyPerformance();
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
            var on = TurboCheck.IsChecked == true;
            _state.Settings.Performance.TurboFan = on;
            _state.Save();
            _state.Engine.Power?.SetTurboFan(on);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBootApplyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state is null) return;
        _state.Settings.ApplyOnStartup = BootApplyCheck.IsChecked == true;
        _state.Save();
    }
}
