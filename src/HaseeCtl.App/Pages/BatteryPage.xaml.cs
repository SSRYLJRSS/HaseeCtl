using System.Windows;
using System.Windows.Controls;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class BatteryPage : UserControl
{
    private UiState? _state;
    private bool _loading;
    private ChargeLimit? _limit;

    public BatteryPage()
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

        try { _limit = _state.Engine.WithHardware(() => _state.Engine.Charge?.Read()); }
        catch (Exception ex) { Log.Error("读取充电上限失败", ex); }

        var starts = _limit?.StartOptions is { Length: > 0 } so ? so : [40, 50, 60, 70, 80, 95];
        var stops = _limit?.StopOptions is { Length: > 0 } sp ? sp : [60, 70, 80, 90, 100];
        StartBox.ItemsSource = starts.Select(v => v + "%").ToList();
        StopBox.ItemsSource = stops.Select(v => v + "%").ToList();
        StartBox.Tag = starts;
        StopBox.Tag = stops;

        if (_limit is not null)
        {
            EnableCheck.IsChecked = _limit.Enabled;
            SelectValue(StartBox, starts, _limit.StartPercent);
            SelectValue(StopBox, stops, _limit.StopPercent);
        }
        else
        {
            EnableCheck.IsChecked = _state.Settings.Charge.Enabled;
            SelectValue(StartBox, starts, _state.Settings.Charge.Start);
            SelectValue(StopBox, stops, _state.Settings.Charge.Stop);
        }

        RefreshBattery();
        StartBox.IsEnabled = StopBox.IsEnabled = EnableCheck.IsChecked == true;
        _loading = false;
        HintText.Text = _limit is null ? "本机未报告充电上限能力。" : $"固件支持：起始 {string.Join(" / ", starts)}%；停止 {string.Join(" / ", stops)}%。";
    }

    private static void SelectValue(ComboBox box, int[] values, int value)
    {
        int idx = Array.IndexOf(values, value);
        if (idx < 0) idx = values.Length > 0 ? values.Length - 1 : 0;
        box.SelectedIndex = idx;
    }

    private static int SelectedValue(ComboBox box)
    {
        if (box.Tag is int[] values && box.SelectedIndex >= 0 && box.SelectedIndex < values.Length)
            return values[box.SelectedIndex];
        return int.TryParse((box.SelectedItem as string)?.TrimEnd('%'), out var v) ? v : 80;
    }

    private void RefreshBattery()
    {
        var pct = BatteryInfo.Percent();
        PercentText.Text = pct < 0 ? "—" : pct.ToString();
        AcText.Text = BatteryInfo.OnAc() ? "（已接电源）" : "（使用电池中）";
    }

    /// <summary>The checkbox only edits the draft; nothing reaches the EC until 应用 is pressed.</summary>
    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = EnableCheck.IsChecked == true;
        StartBox.IsEnabled = StopBox.IsEnabled = on;
        HintText.Text = on ? "点「应用」后生效。" : "点「应用」后关闭充电上限（充到 100%）。";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox box) return;
        if (box == StartBox && SelectedValue(StartBox) >= SelectedValue(StopBox))
        {
            int idx = StopBox.SelectedIndex;
            if (idx >= 0 && idx < StopBox.Items.Count - 1) StopBox.SelectedIndex = idx + 1;
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply(EnableCheck.IsChecked == true, SelectedValue(StartBox), SelectedValue(StopBox));

    private void OnPreset7080(object sender, RoutedEventArgs e)
    {
        EnableCheck.IsChecked = true;
        SelectValue(StartBox, StartBox.Tag as int[] ?? [40, 50, 60, 70, 80, 95], 70);
        SelectValue(StopBox, StopBox.Tag as int[] ?? [60, 70, 80, 90, 100], 80);
        Apply(true, 70, 80);
    }

    private void OnDisable(object sender, RoutedEventArgs e)
    {
        EnableCheck.IsChecked = false;
        Apply(false, SelectedValue(StartBox), SelectedValue(StopBox));
    }

    private void Apply(bool enabled, int start, int stop)
    {
        if (_state is null) return;
        try
        {
            if (enabled && start >= stop)
            {
                HintText.Text = "起始电量必须低于停止电量。";
                return;
            }
            _state.Engine.SetChargeLimit(enabled, start, stop);
            StartBox.IsEnabled = StopBox.IsEnabled = enabled;
            HintText.Text = enabled
                ? $"已启用：充到 {stop}% 停止，降到 {start}% 再充。"
                : "已关闭充电上限，电池会充到 100%。";
            RefreshBattery();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
