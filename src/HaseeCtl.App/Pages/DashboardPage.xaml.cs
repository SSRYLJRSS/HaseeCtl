using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class DashboardPage : UserControl
{
    private readonly DispatcherTimer _timer;
    private UiState? _state;
    private bool _refreshFailing;

    public DashboardPage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => RefreshTexts();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _state = DataContext as UiState;
        RefreshTexts();
        _timer.Start();
    }

    private void RefreshTexts()
    {
        if (_state?.Engine is not { Ready: true } engine) return;
        try
        {
            engine.WithHardware(() =>
            {
                if (engine.Power is not null) PowerModeText.Text = PowerModes.DisplayName(engine.Power.Current);
                if (engine.Gpu is not null) GpuText.Text = GpuService.DisplayName(engine.Gpu.Read().Current);
                if (engine.Charge is not null)
                {
                    var c = engine.Charge.Read();
                    ChargeText.Text = c is null ? "不支持" : c.Enabled ? $"{c.StartPercent}% – {c.StopPercent}%" : "已禁用（充到 100%）";
                }
                if (engine.Fan is not null) FanModeText.Text = FanNames(engine.Fan.CurrentMode);
            });
            _refreshFailing = false;
        }
        catch (Exception ex)
        {
            if (!_refreshFailing) Log.Error("Dashboard 刷新失败", ex); // once, not every 3 s
            _refreshFailing = true;
        }
    }

    private static string FanNames(FanMode m) => m switch
    {
        FanMode.Auto => "自动",
        FanMode.Max => "最大",
        FanMode.Silent => "静音",
        FanMode.MaxQ => "MaxQ",
        FanMode.Custom => "自定义",
        FanMode.Slow => "慢速",
        _ => m.ToString(),
    };

    private void OnFanMax(object sender, RoutedEventArgs e) => Run(() => _state?.Engine.FanMax(), "已切到全速风扇");
    private void OnFanAuto(object sender, RoutedEventArgs e) => Run(() => _state?.Engine.FanAuto(), "已切回自动风扇");
    private void OnReapply(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_state is not null && !_state.Engine.ApplyAll()) throw new InvalidOperationException("部分设置应用失败，详见日志。");
    }, "已重新应用全部设置");

    private void OnCyclePower(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        try
        {
            var mode = _state.Engine.CyclePowerMode(); // persists, applies and re-pushes the fan curve
            MessageBox.Show("已切换到：" + PowerModes.DisplayName(mode), "HaseeCtl", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshTexts();
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void Run(Action? action, string ok)
    {
        if (action is null) return;
        try
        {
            action();
            MessageBox.Show(ok, "HaseeCtl", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshTexts();
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
}
