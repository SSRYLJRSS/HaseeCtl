using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HaseeCtl.Core.Apply;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.Agent;

/// <summary>
/// Tray application: owns the hotkeys, the resume/AC hooks, the periodic fan check and the tray menu.
/// <para>Threading: this object lives on the WinForms UI thread. Hotkeys arrive on the listener's
/// background thread, power/settings events on system threads; all of them do their hardware work
/// through <see cref="Engine"/> (which serialises it) and marshal anything visual through <see cref="_ui"/>.</para>
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    /// <summary>Periodic maintenance: reconnect the driver if needed, otherwise verify the fan mode.</summary>
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);
    /// <summary>Resume notifications from WM_POWERBROADCAST and SystemEvents within this window are one resume.</summary>
    private static readonly TimeSpan ResumeDedupWindow = TimeSpan.FromSeconds(15);

    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly PowerWindow _power;
    private readonly Control _ui = new();
    private readonly WakeHook _wakeHook;
    private readonly System.Windows.Forms.Timer _maintenance;
    private readonly System.Threading.Timer _reloadDebounce;
    private HotkeyListener? _hotkeys;
    private FileSystemWatcher? _watch;
    private long _lastResumeTicks;
    private int _maintenanceBusy;
    private bool? _lastOnAc;
    private bool _exiting;

    public TrayApp(Engine engine)
    {
        _engine = engine;
        _ui.CreateControl();
        _wakeHook = new WakeHook(() => _engine.WakePanel());
        _power = new PowerWindow(() => OnResume("WM_POWERBROADCAST"));

        _menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };
        _menu.Opening += (_, _) => RebuildMenu(); // always shows current state, items are rebuilt in place
        RebuildMenu();
        _icon = new NotifyIcon
        {
            Icon = TrayAssets.Create(),
            Text = "HaseeCtl 控制中心",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => OpenApp();

        TryStartHotkeys();

        try { _lastOnAc = BatteryInfo.OnAc(); } catch { _lastOnAc = null; }
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _reloadDebounce = new System.Threading.Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
        StartSettingsWatch();

        _maintenance = new System.Windows.Forms.Timer { Interval = (int)MaintenanceInterval.TotalMilliseconds };
        _maintenance.Tick += (_, _) => RunMaintenance();
        _maintenance.Start();

        if (_engine.Settings.ApplyOnStartup)
            RunBackground("启动时应用设置", () => _engine.ApplyAll());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Show the OSD on the UI thread (safe to call from any thread).</summary>
    private void ShowOsd(string title, string subtitle = "")
    {
        if (_exiting || !_engine.Settings.ShowOsd || !_ui.IsHandleCreated) return;
        try { _ui.BeginInvoke(() => Osd.Show(title, subtitle)); }
        catch (InvalidOperationException) { /* handle destroyed during exit */ }
    }

    /// <summary>Hardware work off the UI thread so the menu never freezes; failures are logged with stack.</summary>
    private static void RunBackground(string what, Action action) =>
        Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error(what + "失败", ex); }
        });

    private void TryStartHotkeys()
    {
        if (_hotkeys is not null || _engine.Dchu is not { } dchu) return;
        _hotkeys = new HotkeyListener(dchu);
        _hotkeys.HotkeyPressed += OnHotkey;
        _hotkeys.Start();
        Log.Write("热键监听已启动");
    }

    /// <summary>Every 30 s: reconnect when the driver was missing at start, otherwise check the fan mode.</summary>
    private void RunMaintenance()
    {
        if (Interlocked.Exchange(ref _maintenanceBusy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                if (!_engine.Ready)
                {
                    if (!_engine.Connect()) return;
                    Log.Write("驱动已可用，启动热键并应用设置");
                    _ui.BeginInvoke(TryStartHotkeys);
                    _engine.ApplyAll();
                    return;
                }
                _engine.VerifyFan();
            }
            catch (Exception ex) { Log.Error("定时校验失败", ex); }
            finally { Volatile.Write(ref _maintenanceBusy, 0); }
        });
    }

    // ---------------------------------------------------------------- settings file

    /// <summary>When the UI (or a text editor) rewrites settings.json, pick the new values up so a
    /// later resume restores what the user last chose, not a stale snapshot.</summary>
    private void StartSettingsWatch()
    {
        try
        {
            var dir = SettingsStore.Directory;
            Directory.CreateDirectory(dir);
            _watch = new FileSystemWatcher(dir, Path.GetFileName(SettingsStore.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watch.Changed += (_, _) => ScheduleReload();
            _watch.Created += (_, _) => ScheduleReload();
            _watch.Renamed += (_, _) => ScheduleReload();
            _watch.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("监听配置文件失败", ex);
        }
    }

    /// <summary>Debounce: every change restarts the 300 ms timer, so the last write always wins.</summary>
    private void ScheduleReload()
    {
        try { _reloadDebounce.Change(300, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void ReloadFromDisk()
    {
        if (_exiting) return;
        try
        {
            // our own saves also land here; skip when the file is what we already hold
            if (SettingsStore.Stamp() == _engine.LoadedStamp) return;
            if (_engine.ReloadSettings()) Log.Write("配置文件已变化，已重新加载");
        }
        catch (Exception ex) { Log.Error("重新加载配置失败", ex); }
    }

    // ---------------------------------------------------------------- tray menu

    private void RebuildMenu()
    {
        // dispose the previous items (and their handles) before building the new ones
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var item in old) item.Dispose();

        var s = _engine.Settings;
        _menu.Items.Add("打开控制中心", null, (_, _) => OpenApp());
        _menu.Items.Add(new ToolStripSeparator());

        var fan = new ToolStripMenuItem("风扇模式");
        (string Key, string Label)[] modes =
        [
            ("auto", "自动（固件）"), ("custom", "自定义曲线"), ("max", "全速"), ("silent", "静音"), ("slow", "慢速"),
        ];
        foreach (var (key, label) in modes)
        {
            var item = new ToolStripMenuItem(label) { Checked = s.Fan.Mode == key, Enabled = _engine.Ready };
            item.Click += (_, _) => RunBackground("切换风扇模式", () =>
            {
                _engine.SetFanMode(key);
                ShowOsd("风扇：" + label);
            });
            fan.DropDownItems.Add(item);
        }
        _menu.Items.Add(fan);

        var profiles = new ToolStripMenuItem("风扇配置");
        foreach (var profile in s.Fan.Profiles)
        {
            var name = profile.Name;
            var item = new ToolStripMenuItem(name)
            {
                Checked = s.Fan.Mode == "custom" && s.Fan.ActiveProfile == name,
                Enabled = _engine.Ready,
            };
            item.Click += (_, _) => RunBackground("切换风扇配置", () =>
            {
                _engine.SelectFanProfile(name);
                ShowOsd("风扇配置：" + name);
            });
            profiles.DropDownItems.Add(item);
        }
        _menu.Items.Add(profiles);

        var perf = new ToolStripMenuItem("性能模式");
        if (_engine.Caps is { } caps)
        {
            foreach (var mode in PowerModes.Supported(caps))
            {
                var item = new ToolStripMenuItem(PowerModes.DisplayName(mode))
                {
                    Checked = Engine.ParsePowerMode(s.Performance.Mode) == mode,
                };
                item.Click += (_, _) => RunBackground("切换性能模式", () =>
                {
                    _engine.SetPerformanceMode(mode);
                    ShowOsd("性能模式", PowerModes.DisplayName(mode));
                });
                perf.DropDownItems.Add(item);
            }
        }
        perf.Enabled = perf.DropDownItems.Count > 0;
        _menu.Items.Add(perf);

        var c = s.Charge;
        var charge = new ToolStripMenuItem($"充电上限 {c.Start}%/{c.Stop}%") { Checked = c.Enabled, Enabled = _engine.Charge is not null };
        charge.Click += (_, _) => RunBackground("切换充电上限", () =>
        {
            var cur = _engine.Settings.Charge;
            bool on = !cur.Enabled;
            _engine.SetChargeLimit(on, cur.Start, cur.Stop);
            ShowOsd(on ? "充电上限已启用" : "充电上限已关闭", $"{cur.Start}% / {cur.Stop}%");
        });
        _menu.Items.Add(charge);

        _menu.Items.Add(new ToolStripSeparator());
        bool? tp = SystemService.TouchpadEnabled();
        var touch = new ToolStripMenuItem("触控板") { Checked = tp == true, Enabled = _engine.Sys is not null };
        touch.Click += (_, _) => RunBackground("切换触控板", () => SetTouchpad(tp != true));
        _menu.Items.Add(touch);

        bool camOn = false;
        try { camOn = _engine.Sys?.CameraOn == true; } catch (Exception ex) { Log.Error("读取摄像头状态失败", ex); }
        var cam = new ToolStripMenuItem("摄像头") { Checked = camOn, Enabled = _engine.Sys is not null };
        cam.Click += (_, _) => RunBackground("切换摄像头", () => SetCamera(!camOn));
        _menu.Items.Add(cam);

        _menu.Items.Add("关闭屏幕", null, (_, _) => SystemService.ScreenOff());
        _menu.Items.Add("重新应用设置", null, (_, _) => RunBackground("重新应用设置", () =>
        {
            _engine.ReloadSettings();
            ShowOsd(_engine.ApplyAll() ? "设置已重新应用" : "部分设置应用失败，见日志");
        }));
        _menu.Items.Add("退出", null, (_, _) => ExitApp());
    }

    // ---------------------------------------------------------------- hotkeys (background thread)

    private void OnHotkey(int code)
    {
        try
        {
            Log.Write($"收到热键 {code}");
            switch (code)
            {
                case 89:
                    ShowOsd("性能模式", PowerModes.DisplayName(_engine.CyclePowerMode()));
                    break;
                case 143:
                    _engine.FanMax();
                    ShowOsd("风扇全速");
                    break;
                case 112:
                    _engine.FanAuto();
                    ShowOsd("风扇自动");
                    break;
                case 149:
                    OpenApp();
                    break;
                case 129:
                case 32:
                    ShowOsd("键盘灯亮度", _engine.AdjustLightBrightness(-1).ToString());
                    break;
                case 130:
                case 33:
                    ShowOsd("键盘灯亮度", _engine.AdjustLightBrightness(+1).ToString());
                    break;
                case 158:
                case 159:
                    ShowOsd(_engine.ToggleLight() ? "键盘灯已开启" : "键盘灯已关闭");
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
                    ShowOsd("数字键盘", code == 200 ? "开" : "关");
                    break;
                case 201:
                case 202:
                    ShowOsd("大写锁定", code == 202 ? "开" : "关");
                    break;
                case 203:
                case 204:
                    ShowOsd("滚动锁定", code == 204 ? "开" : "关");
                    break;
                case 250:
                case 251:
                    // volume is handled by Windows itself; only echo the OSD like the stock service
                    ShowOsd("音量");
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
            Log.Error($"处理热键 {code} 失败", ex);
        }
    }

    private void SetCamera(bool on)
    {
        if (_engine.Sys is not { } sys) return;
        _engine.WithHardware(() => sys.SetCamera(on));
        ShowOsd(sys.CameraOn ? "摄像头已开启" : "摄像头已关闭");
    }

    private void ToggleAirplane()
    {
        if (_engine.Sys is not { } sys) return;
        if (OfficialFnKeyRunning()) { Log.Write("官方 FnKey 仍在运行，飞行模式交给它处理"); return; }
        bool on = _engine.WithHardware(sys.ToggleAirplane);
        ShowOsd(on ? "飞行模式已开启" : "飞行模式已关闭");
    }

    /// <summary>Fn+F1：切换由固件完成，这里稍等后读回 Windows 状态并同步到 EC（官方 case 93）。</summary>
    private void TouchpadKey()
    {
        Thread.Sleep(150); // hotkey thread only; the UI thread is never blocked
        bool? on = SystemService.TouchpadEnabled();
        if (on is bool v)
        {
            if (_engine.Sys is { } sys) _engine.WithHardware(() => sys.SyncTouchpadToEc(v));
            ShowOsd(v ? "触控板已开启" : "触控板已关闭");
        }
        else ShowOsd("触控板");
    }

    private void SetTouchpad(bool on)
    {
        if (_engine.Sys is not { } sys) return;
        bool now = _engine.WithHardware(() => sys.SetTouchpad(on));
        ShowOsd(now ? "触控板已开启" : "触控板已关闭");
    }

    private void ArmWakeHook()
    {
        if (OfficialFnKeyRunning()) { Log.Write("官方 FnKey 仍在运行，关屏唤醒交给官方钩子"); return; }
        _ui.BeginInvoke(_wakeHook.Arm); // low-level hooks need the thread with the message loop
    }

    private static bool OfficialFnKeyRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("FnKey");
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- power events

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnResume("SystemEvents");
        else if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) OnPowerStatusChange();
    }

    /// <summary>AC plugged/unplugged: the EC re-arms its own fan table, so push ours again.</summary>
    private void OnPowerStatusChange()
    {
        bool? onAc;
        try { onAc = BatteryInfo.OnAc(); } catch { onAc = null; }
        if (onAc is null || onAc == _lastOnAc) return; // StatusChange also fires for battery % changes
        _lastOnAc = onAc;
        Log.Write(onAc.Value ? "已接通电源，重新应用风扇与刷新率" : "已断开电源，重新应用风扇与刷新率");
        RunBackground("电源切换后应用设置", () =>
        {
            Thread.Sleep(1000); // let the EC finish its own switch first
            _engine.ApplyFan();
            if (_engine.Sys is { LowRefreshEnabled: true } sys) _engine.WithHardware(sys.ApplyRefreshRate);
        });
    }

    /// <summary>Both WM_POWERBROADCAST and SystemEvents report the same resume; handle it once.</summary>
    private void OnResume(string source)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastResumeTicks);
        if (last != 0 && now - last < ResumeDedupWindow.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastResumeTicks, now, last) != last) return;

        Log.Write($"系统唤醒 ({source})，重新应用设置");
        if (!_engine.Settings.ApplyOnResume) return;
        RunBackground("唤醒恢复", () =>
        {
            Thread.Sleep(1500); // let the EC finish its own resume sequence first
            _engine.ApplyAll();
            Thread.Sleep(4000);
            _engine.ApplyFan(); // the firmware sometimes re-arms its own table a few seconds later
            Log.Write("唤醒后设置已恢复");
        });
    }

    // ---------------------------------------------------------------- app / exit

    private void OpenApp()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, "HaseeCtl.App.exe");
            if (!File.Exists(exe))
            {
                ShowOsd("未找到控制中心程序", exe);
                return;
            }
            // a second App instance brings the running window to the front itself
            using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch (Exception ex)
        {
            Log.Error("启动界面失败", ex);
        }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Write("HaseeCtl Agent 退出");
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _maintenance.Stop();
        _maintenance.Dispose();
        _watch?.Dispose();
        _reloadDebounce.Dispose();
        _hotkeys?.Dispose();
        _wakeHook.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _power.Dispose();
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
