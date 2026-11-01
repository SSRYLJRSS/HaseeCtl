using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class SettingsPage : UserControl
{
    private UiState? _state;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _state = DataContext as UiState;
        if (_state is null) return;
        _loading = true;

        ResumeCheck.IsChecked = _state.Settings.ApplyOnResume;
        OsdCheck.IsChecked = _state.Settings.ShowOsd;
        StartupCheck.IsChecked = _state.Settings.ApplyOnStartup;

        ConfigPathText.Text = "配置文件：" + SettingsStore.FilePath;
        LogPathText.Text = "日志目录：" + SettingsStore.LogDirectory;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        AboutText.Text = $"HaseeCtl {version} · 神舟 CV15S / 蓝天 Clevo 控制中心替代实现。\n"
                       + "直接与 EC 通信，不依赖官方 Control Center；官方软件可以安全卸载。";
        RepoText.Text = "项目主页 / 更新地址：" + UpdateChecker.HomeUrl;

        LoadSystem();

        var caps = _state.Engine.Caps;
        CapsText.Text = caps is null
            ? "未能读取能力位。"
            : $"风扇调速：{(caps.FanSpeedSetting ? "支持" : "不支持")}　自定义曲线：{(caps.CustomFanSupport ? "支持" : "不支持")}\n"
              + $"性能模式：{(caps.PowerModeSupport ? "支持" : "不支持")}　强冷：{(caps.TurboFan ? "支持" : "不支持")}\n"
              + $"键盘灯：{(caps.Lightbar ? "灯带" : "无灯带")}　显卡切换：{(_state.GpuSupported ? "支持" : "不支持")}";

        _loading = false;
    }

    private void OnResumeChanged(object sender, RoutedEventArgs e) => Save(s => s.ApplyOnResume = ResumeCheck.IsChecked == true);
    private void OnOsdChanged(object sender, RoutedEventArgs e) => Save(s => s.ShowOsd = OsdCheck.IsChecked == true);
    private void OnStartupChanged(object sender, RoutedEventArgs e) => Save(s => s.ApplyOnStartup = StartupCheck.IsChecked == true);

    private void LoadSystem()
    {
        var sys = _state?.Engine.Sys;
        TouchpadCheck.IsChecked = SystemService.TouchpadEnabled() == true;
        if (sys is null)
        {
            foreach (var c in new[] { CameraCheck, FnLockCheck, WinLockCheck, SwapCheck, LowRefreshCheck }) c.IsEnabled = false;
            SystemStatus.Text = "未连接驱动，键盘设置不可用。";
            return;
        }
        try
        {
            var (camera, k, lowRefresh) = _state!.Engine.WithHardware(() => (sys.CameraOn, sys.ReadKeyboard(), sys.LowRefreshEnabled));
            CameraCheck.IsChecked = camera;
            FnLockCheck.IsChecked = k.FnLock;
            WinLockCheck.IsChecked = k.WinKeyLock;
            SwapCheck.IsChecked = k.FnWinSwap;
            LowRefreshCheck.IsChecked = lowRefresh;
            SystemStatus.Text = $"当前刷新率 {Display.CurrentHz()} Hz · 飞行模式{(SystemService.AirplaneOn() ? "开" : "关")}";
        }
        catch (Exception ex) { SystemStatus.Text = "读取失败：" + ex.Message; }
    }

    private void OnTouchpadChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state?.Engine.Sys is not { } sys) return;
        bool want = TouchpadCheck.IsChecked == true;
        bool now = _state.Engine.WithHardware(() => sys.SetTouchpad(want));
        _loading = true;
        TouchpadCheck.IsChecked = now;
        _loading = false;
    }

    private void OnCameraChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state?.Engine.Sys is not { } sys) return;
        bool want = CameraCheck.IsChecked == true;
        try
        {
            bool on = _state.Engine.WithHardware(() => { sys.SetCamera(want); return sys.CameraOn; });
            _loading = true;
            CameraCheck.IsChecked = on;
            _loading = false;
            SystemStatus.Text = "摄像头已" + (on ? "开启" : "关闭");
        }
        catch (Exception ex) { SystemStatus.Text = "写入失败：" + ex.Message; }
    }

    private void OnKeyboardChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state?.Engine.Sys is not { } sys) return;
        try
        {
            var opts = new SystemService.KeyboardOptions(FnLockCheck.IsChecked == true, WinLockCheck.IsChecked == true, SwapCheck.IsChecked == true);
            var k = _state.Engine.WithHardware(() => { sys.WriteKeyboard(opts); return sys.ReadKeyboard(); });
            _loading = true;
            FnLockCheck.IsChecked = k.FnLock; WinLockCheck.IsChecked = k.WinKeyLock; SwapCheck.IsChecked = k.FnWinSwap;
            _loading = false;
        }
        catch (Exception ex) { SystemStatus.Text = "写入失败：" + ex.Message; }
    }

    private void OnLowRefreshChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state?.Engine.Sys is not { } sys) return;
        bool on = LowRefreshCheck.IsChecked == true;
        try
        {
            _state.Engine.WithHardware(() =>
            {
                sys.LowRefreshEnabled = on;
                sys.ApplyRefreshRate();
            });
            SystemStatus.Text = $"当前刷新率 {Display.CurrentHz()} Hz";
        }
        catch (Exception ex) { SystemStatus.Text = "写入失败：" + ex.Message; }
    }

    private void OnScreenOff(object sender, RoutedEventArgs e) => SystemService.ScreenOff();

    private void OnAirplane(object sender, RoutedEventArgs e)
    {
        if (_state?.Engine.Sys is not { } sys) return;
        bool on = _state.Engine.WithHardware(sys.ToggleAirplane);
        SystemStatus.Text = "飞行模式已" + (on ? "开启" : "关闭");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("打开链接失败: " + ex.Message); }
    }

    private void OnOpenRepo(object sender, RoutedEventArgs e) => OpenUrl(UpdateChecker.HomeUrl);

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "正在检查…";
        var r = await UpdateChecker.CheckAsync();
        UpdateStatus.Text = r.Message;
        UpdateButton.IsEnabled = true;
        if (r.HasUpdate &&
            MessageBox.Show($"{r.Message}，是否打开下载页？", "HaseeCtl 更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            OpenUrl(r.PageUrl);
    }

    private void Save(Action<AppSettings> mutate)
    {
        if (_loading || _state is null) return;
        try { _state.Update(mutate); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e) => Open(SettingsStore.Directory);
    private void OnOpenLogs(object sender, RoutedEventArgs e) => Open(SettingsStore.LogDirectory);

    private static void Open(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_state is null) return;
            if (!_state.Engine.ReloadSettings()) throw new InvalidOperationException("配置文件暂时无法读取，已保留当前设置。");
            OnLoaded(this, e); // show the reloaded values
            MessageBox.Show("已从磁盘重新加载配置。", "HaseeCtl", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "重载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReapply(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_state is null) return;
            if (!_state.Engine.ApplyAll()) throw new InvalidOperationException("部分设置应用失败，详见日志。");
            MessageBox.Show("已重新应用全部设置。", "HaseeCtl", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "应用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
