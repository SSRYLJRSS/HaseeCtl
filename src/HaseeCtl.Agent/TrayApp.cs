using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HaseeCtl.Core.Apply;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.Agent;

/// <summary>Tray application: owns the hotkeys, the resume hook and the profile menus.</summary>
public sealed class TrayApp : ApplicationContext
{

    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly PowerWindow _power;
    private readonly HotkeyListener? _hotkeys;
    private FileSystemWatcher? _watch;
    private DateTime _lastReload = DateTime.MinValue;
    private readonly Control _ui = new();
    private readonly WakeHook _wakeHook;

    public TrayApp(Engine engine)
    {
        _engine = engine;
        _ui.CreateControl();
        _wakeHook = new WakeHook(() => _engine.Sys?.WakePanel());
        _power = new PowerWindow(OnResume);
        _icon = new NotifyIcon
        {
            Icon = TrayAssets.Create(),
            Text = "HaseeCtl 控制中心",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenApp();

        if (_engine.Dchu is not null)
        {
            _hotkeys = new HotkeyListener(_engine.Dchu);
            _hotkeys.HotkeyPressed += OnHotkey;
            _hotkeys.Start();
            Log.Write("热键监听已启动");
        }

        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnResume();
            else if (e.Mode == Microsoft.Win32.PowerModes.StatusChange && _engine.Sys is { LowRefreshEnabled: true } sys)
                Task.Run(() => { try { sys.ApplyRefreshRate(); } catch (Exception ex) { Log.Write("切换刷新率失败: " + ex.Message); } });
        };

        StartSettingsWatch();

        if (_engine.Settings.ApplyOnStartup)
            Task.Run(() => _engine.ApplyAll());
    }

    /// <summary>When the UI (or a text editor) rewrites settings.json, pick the new values up
    /// immediately so a later resume restores what the user last chose, not a stale snapshot.</summary>
    private void StartSettingsWatch()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsStore.FilePath)!;
            Directory.CreateDirectory(dir);
            _watch = new FileSystemWatcher(dir, Path.GetFileName(SettingsStore.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watch.Changed += (_, _) => ScheduleReload();
            _watch.Created += (_, _) => ScheduleReload();
            _watch.Renamed += (_, _) => ScheduleReload();
        }
        catch (Exception ex)
        {
            Log.Write("监听配置文件失败: " + ex.Message);
        }
    }

    private void ScheduleReload()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastReload).TotalMilliseconds < 400) return;
        _lastReload = now;
        Task.Delay(250).ContinueWith(_ =>
        {
            try
            {
                _engine.ReloadSettings();
                Log.Write("配置文件已变化，已重新加载");
            }
            catch (Exception ex)
            {
                Log.Write("重新加载配置失败: " + ex.Message);
            }
        });
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };

        menu.Items.Add("打开控制中心", null, (_, _) => OpenApp());
        menu.Items.Add(new ToolStripSeparator());

        var fan = new ToolStripMenuItem("风扇模式");
        AddFanModes(fan);
        menu.Items.Add(fan);

        var profiles = new ToolStripMenuItem("风扇配置");
        AddProfiles(profiles);
        menu.Items.Add(profiles);

        var perf = new ToolStripMenuItem("性能模式");
        AddPerformance(perf);
        menu.Items.Add(perf);

        var charge = new ToolStripMenuItem("充电上限 70/80");
        charge.Click += (_, _) =>
        {
            _engine.Settings.Charge.Enabled = !_engine.Settings.Charge.Enabled;
            SettingsStore.Save(_engine.Settings);
            _engine.ApplyCharge();
            Osd.Show(_engine.Settings.Charge.Enabled ? "充电上限已启用" : "充电上限已关闭", "70% / 80%");
        };
        menu.Items.Add(charge);

        menu.Items.Add(new ToolStripSeparator());
        bool? tp = HaseeCtl.Core.Features.SystemService.TouchpadEnabled();
        var touch = new ToolStripMenuItem("触控板") { Checked = tp == true };
        touch.Click += (_, _) => SetTouchpadFromMenu(tp != true);
        menu.Items.Add(touch);
        bool camOn = _engine.Sys?.CameraOn == true;
        var cam = new ToolStripMenuItem("摄像头") { Checked = camOn, Enabled = _engine.Sys is not null };
        cam.Click += (_, _) => { SetCamera(!camOn); _icon.ContextMenuStrip = BuildMenu(); };
        menu.Items.Add(cam);
        menu.Items.Add("关闭屏幕", null, (_, _) => ScreenOffFromMenu());
        menu.Items.Add("重新应用设置", null, (_, _) => { _engine.ReloadSettings(); _engine.ApplyAll(); Osd.Show("设置已重新应用"); });
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        return menu;
    }

    private void AddFanModes(ToolStripMenuItem parent)
    {
        (string Key, string Label)[] modes =
        [
            ("auto", "自动（固件）"), ("custom", "自定义曲线"), ("max", "全速"), ("silent", "静音"), ("slow", "慢速"),
        ];
        foreach (var (key, label) in modes)
        {
            var item = new ToolStripMenuItem(label) { Checked = _engine.Settings.Fan.Mode == key };
            item.Click += (_, _) =>
            {
                _engine.Settings.Fan.Mode = key;
                SettingsStore.Save(_engine.Settings);
                _engine.ApplyFan();
                _icon.ContextMenuStrip = BuildMenu();
                Osd.Show("风扇：" + label);
            };
            parent.DropDownItems.Add(item);
        }
    }

    private void AddProfiles(ToolStripMenuItem parent)
    {
        foreach (var profile in _engine.Settings.Fan.Profiles)
        {
            var item = new ToolStripMenuItem(profile.Name) { Checked = _engine.Settings.Fan.ActiveProfile == profile.Name };
            var name = profile.Name;
            item.Click += (_, _) =>
            {
                _engine.Settings.Fan.ActiveProfile = name;
                _engine.Settings.Fan.Mode = "custom";
                SettingsStore.Save(_engine.Settings);
                _engine.ApplyFan();
                _icon.ContextMenuStrip = BuildMenu();
                Osd.Show("风扇配置：" + name);
            };
            parent.DropDownItems.Add(item);
        }
    }

    private void AddPerformance(ToolStripMenuItem parent)
    {
        if (_engine.Caps is null) return;
        foreach (var mode in PowerModes.Supported(_engine.Caps))
        {
            var item = new ToolStripMenuItem(PowerModes.DisplayName(mode))
            {
                Checked = string.Equals(_engine.Settings.Performance.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) =>
            {
                _engine.Settings.Performance.Mode = mode.ToString().ToLowerInvariant();
                SettingsStore.Save(_engine.Settings);
                _engine.ApplyPerformance();
                _icon.ContextMenuStrip = BuildMenu();
                Osd.Show("性能模式：" + PowerModes.DisplayName(mode));
            };
            parent.DropDownItems.Add(item);
        }
    }

    private void OnHotkey(int code)
    {
        try
        {
            Log.Write($"收到热键 {code}");
            switch (code)
            {
                case 89:
                {
                    var mode = _engine.CyclePowerMode();
                    Osd.Show("性能模式", PowerModes.DisplayName(mode));
                    break;
                }
                case 143:
                    _engine.FanMax();
                    _engine.Settings.Fan.Mode = "max";
                    SettingsStore.Save(_engine.Settings);
                    Osd.Show("风扇全速");
                    break;
                case 112:
                    _engine.FanAuto();
                    _engine.Settings.Fan.Mode = "auto";
                    SettingsStore.Save(_engine.Settings);
                    Osd.Show("风扇自动");
                    break;
                case 149:
                    OpenApp();
                    break;
                case 129:
                case 32:
                    _engine.Light?.BrightnessDown();
                    Osd.Show("键盘灯亮度", (_engine.Light?.Brightness ?? 1).ToString());
                    break;
                case 130:
                case 33:
                    _engine.Light?.BrightnessUp();
                    Osd.Show("键盘灯亮度", (_engine.Light?.Brightness ?? 1).ToString());
                    break;
                case 158:
                case 159:
                    ToggleLight();
                    break;
                case 246:
                case 247:
                    SetCamera(code == 247);
                    break;
                case 134:
                case 133:
                    ToggleAirplane();
                    break;
                case 93:
                case 252:
                    TouchpadKey();
                    break;
                case 199:
                case 200:
                    Osd.Show("数字键盘", code == 200 ? "开" : "关");
                    break;
                case 201:
                case 202:
                    Osd.Show("大写锁定", code == 202 ? "开" : "关");
                    break;
                case 203:
                case 204:
                    Osd.Show("滚动锁定", code == 204 ? "开" : "关");
                    break;
                case 250:
                case 251:
                    // volume is handled by Windows itself; only echo the OSD like the stock service
                    Osd.Show("音量");
                    break;
                case 215:
                    // Fn+F2：EC 已自行关闭背光，这里接管官方 WakePanelUp_Hook 的唤醒职责
                    ArmWakeHook();
                    break;
                default:
                    Log.Write($"热键 {code} 未绑定（如需支持请告知）");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"处理热键 {code} 失败: {ex.Message}");
        }
    }

    private void ToggleLight()
    {
        if (_engine.Light is null) return;
        bool on = !_engine.Light.Enabled;
        _engine.Settings.Light.Enabled = on;
        SettingsStore.Save(_engine.Settings);
        _engine.Light.SetEnabled(on);
        Osd.Show(on ? "键盘灯已开启" : "键盘灯已关闭");
    }

    private void SetCamera(bool on)
    {
        if (_engine.Sys is null) return;
        _engine.Sys.SetCamera(on);
        Osd.Show(on ? "摄像头已开启" : "摄像头已关闭");
    }

    private void ToggleAirplane()
    {
        if (_engine.Sys is null) return;
        if (OfficialFnKeyRunning()) { Log.Write("官方 FnKey 仍在运行，飞行模式交给它处理"); return; }
        bool on = _engine.Sys.ToggleAirplane();
        Osd.Show(on ? "飞行模式已开启" : "飞行模式已关闭");
    }

    /// <summary>Fn+F1：切换由固件完成，这里等 100ms 读回 Windows 状态并同步到 EC（官方 case 93）。</summary>
    private void TouchpadKey()
    {
        Thread.Sleep(150);
        bool? on = HaseeCtl.Core.Features.SystemService.TouchpadEnabled();
        if (on is bool v)
        {
            _engine.Sys?.SyncTouchpadToEc(v);
            Osd.Show(v ? "触控板已开启" : "触控板已关闭");
        }
        else Osd.Show("触控板");
    }

    private void SetTouchpadFromMenu(bool on)
    {
        if (_engine.Sys is null) return;
        bool now = _engine.Sys.SetTouchpad(on);
        Osd.Show(now ? "触控板已开启" : "触控板已关闭");
        _icon.ContextMenuStrip = BuildMenu();
    }

    private void ArmWakeHook()
    {
        if (OfficialFnKeyRunning()) { Log.Write("官方 FnKey 仍在运行，关屏唤醒交给官方钩子"); return; }
        _ui.BeginInvoke(() => _wakeHook.Arm());
    }

    private void ScreenOffFromMenu()
    {
        HaseeCtl.Core.Features.SystemService.ScreenOff();
    }

    private static bool OfficialFnKeyRunning()
    {
        try { return Process.GetProcessesByName("FnKey").Length > 0; }
        catch { return false; }
    }

    private void OnResume()
    {
        Log.Write("系统唤醒，重新应用设置");
        Task.Run(() =>
        {
            try
            {
                if (!_engine.Settings.ApplyOnResume) return;
                Thread.Sleep(1500); // let the EC finish its own resume sequence first
                _engine.ApplyAll();
                Thread.Sleep(4000);
                _engine.ApplyFan(); // the firmware sometimes re-arms its own table a few seconds later
                Log.Write("唤醒后设置已恢复");
            }
            catch (Exception ex) { Log.Write("唤醒恢复失败: " + ex.Message); }
        });
    }

    private void OpenApp()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, "HaseeCtl.App.exe");
            if (!File.Exists(exe))
            {
                Osd.Show("未找到控制中心程序", exe);
                return;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch (Exception ex)
        {
            Log.Write("启动界面失败: " + ex.Message);
        }
    }

    private void ExitApp()
    {
        _hotkeys?.Dispose();
        _wakeHook.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _power.DestroyHandle();
        ExitThread();
    }
}

/// <summary>Hidden window that receives WM_POWERBROADCAST (modern standby resume).</summary>
internal sealed class PowerWindow : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeAutomatic = 0x0012;
    private readonly Action _onResume;

    public PowerWindow(Action onResume)
    {
        _onResume = onResume;
        CreateHandle(new CreateParams { Caption = "HaseeCtlPowerSink", X = 0, Y = 0, Width = 0, Height = 0 });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPowerBroadcast && m.WParam.ToInt64() == PbtApmResumeAutomatic)
            _onResume();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

internal static class TrayAssets
{
    /// <summary>Tray icon: the shipped monochrome .ico, resized to the tray metric.</summary>
    public static Icon Create()
    {
        try
        {
            using var stream = typeof(TrayAssets).Assembly
                .GetManifestResourceStream("HaseeCtl.Agent.app.ico");
            if (stream is not null)
            {
                var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
                using var all = new Icon(stream, new Size(size, size));
                return (Icon)all.Clone();
            }
        }
        catch
        {
            // fall through to the drawn fallback
        }
        return DrawFallback();
    }

    /// <summary>Black rounded square with a white H and a hub hole.</summary>
    private static Icon DrawFallback()
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(10, 10, 10)))
            using (var path = RoundedRect(new Rectangle(0, 0, s - 1, s - 1), 7))
                g.FillPath(bg, path);
            using (var fg = new SolidBrush(Color.White))
            {
                using (var p = RoundedRect(new Rectangle(8, 6, 5, 20), 1)) g.FillPath(fg, p);
                using (var p = RoundedRect(new Rectangle(19, 6, 5, 20), 1)) g.FillPath(fg, p);
                using (var p = RoundedRect(new Rectangle(12, 13, 8, 6), 1)) g.FillPath(fg, p);
            }
            using (var hole = new SolidBrush(Color.FromArgb(10, 10, 10)))
                g.FillEllipse(hole, 14, 14, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
