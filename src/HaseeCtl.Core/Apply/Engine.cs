using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;
using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Apply;

/// <summary>
/// All hardware work happens here so the tray, hotkeys and UI share one implementation.
/// <para>Threading: every multi-step hardware sequence runs inside <see cref="HardwareLock"/>
/// (in-process monitor + session-wide mutex shared with the other HaseeCtl process).
/// Settings changes go through <see cref="UpdateSettings"/>, which re-reads the file first when
/// the other process has rewritten it, so the two processes never overwrite each other's edits.
/// Lock order is always hardware → settings.</para>
/// </summary>
public sealed class Engine
{
    private readonly HardwareLock _hw;
    private readonly object _settingsSync = new();
    private readonly Func<IAcpiBridge> _bridgeFactory;
    private SettingsStore.FileStamp _stamp;
    private Dchu? _dchu;
    private volatile AppSettings _settings = AppSettings.CreateDefault();
    private bool _statusFailing;

    public Engine() : this(HardwareLock.Session, () => new AcpiBridge()) { }

    /// <summary>Test seam: custom lock and bridge.</summary>
    public Engine(HardwareLock hardwareLock, Func<IAcpiBridge> bridgeFactory)
    {
        _hw = hardwareLock;
        _bridgeFactory = bridgeFactory;
    }

    public Capabilities? Caps { get; private set; }
    public FanService? Fan { get; private set; }
    public PowerModeService? Power { get; private set; }
    public ChargeService? Charge { get; private set; }
    public GpuService? Gpu { get; private set; }
    public KeyboardLightService? Light { get; private set; }
    public SystemService? Sys { get; private set; }
    public Dchu? Dchu => _dchu;
    public string? DevicePath { get; private set; }

    /// <summary>Current settings snapshot. Replaced as a whole on reload; mutate only via <see cref="UpdateSettings"/>
    /// (UI editors may keep a draft on the object and commit it through UpdateSettings).</summary>
    public AppSettings Settings => _settings;

    public bool Ready => _dchu is not null && Caps is not null;

    /// <summary>Stamp of the settings file as last loaded or saved by this process.</summary>
    public SettingsStore.FileStamp LoadedStamp
    {
        get { lock (_settingsSync) return _stamp; }
    }

    // ---------------------------------------------------------------- connection

    public bool Connect()
    {
        using var _ = _hw.Enter();
        try
        {
            var bridge = _bridgeFactory();
            if (!bridge.IsAvailable) return false;
            var dchu = new Dchu(bridge);
            var caps = Capabilities.Read(dchu);
            Fan = new FanService(dchu, caps);
            Power = new PowerModeService(dchu, caps);
            Charge = new ChargeService(dchu);
            Gpu = new GpuService(dchu);
            Light = new KeyboardLightService(dchu, caps);
            Sys = new SystemService(dchu, caps);
            Caps = caps;
            _dchu = dchu;
            DevicePath = (bridge as AcpiBridge)?.DevicePath;
            Log.Write("已连接硬件: " + (DevicePath ?? bridge.GetType().Name));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("连接硬件失败", ex);
            return false;
        }
    }

    /// <summary>Connects when not connected yet; true when hardware is usable afterwards.</summary>
    public bool EnsureConnected() => Ready || Connect();

    /// <summary>Run arbitrary hardware code under the shared hardware lock (for UI pages).</summary>
    public void WithHardware(Action action)
    {
        using var _ = _hw.Enter();
        action();
    }

    public T WithHardware<T>(Func<T> func)
    {
        using var _ = _hw.Enter();
        return func();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Reload from disk. Keeps the current settings when the file cannot be read right now.</summary>
    public bool ReloadSettings()
    {
        lock (_settingsSync)
        {
            var r = SettingsStore.Load();
            if (r.Settings is null)
            {
                Log.Write("读取配置失败，保留当前设置: " + r.Error);
                return false;
            }
            _settings = r.Settings;
            _stamp = r.Stamp;
            Log.Write("设置已重新加载");
            return true;
        }
    }

    /// <summary>
    /// Atomic read-modify-write of the settings: picks up the other process's changes first,
    /// applies <paramref name="mutate"/>, and saves.
    /// </summary>
    public void UpdateSettings(Action<AppSettings> mutate)
    {
        lock (_settingsSync)
        {
            if (SettingsStore.Stamp() != _stamp)
            {
                var r = SettingsStore.Load();
                if (r.Settings is not null)
                {
                    _settings = r.Settings;
                    _stamp = r.Stamp;
                }
            }
            mutate(_settings);
            SettingsStore.Normalize(_settings);
            if (SettingsStore.Save(_settings) is { } stamp) _stamp = stamp;
        }
    }

    // ---------------------------------------------------------------- apply

    /// <summary>Apply every persisted setting. Called on startup, resume and on demand.
    /// Each step is isolated so one failing feature does not skip the rest.</summary>
    public bool ApplyAll(bool includePerformance = true)
    {
        using var _ = _hw.Enter();
        if (!EnsureConnected()) return false;
        bool ok = true;
        // performance first: the EC re-arms its own fan table when the power mode changes
        if (includePerformance) ok &= Step("性能模式", () => ApplyPerformance(reapplyFan: false));
        ok &= Step("风扇", ApplyFan);
        ok &= Step("充电上限", ApplyCharge);
        ok &= Step("键盘灯", ApplyLight);
        ok &= Step("系统设置", ApplySystem);
        return ok;

        static bool Step(string name, Action a)
        {
            try { a(); return true; }
            catch (Exception ex) { Log.Error("应用" + name + "失败", ex); return false; }
        }
    }

    /// <summary>EC 接管 I2C 触控板 + 同步触控板状态 + 低刷新率规则。</summary>
    public void ApplySystem()
    {
        using var _ = _hw.Enter();
        if (Sys is null) return;
        Sys.EnableEcTouchpadControl();
        if (SystemService.TouchpadEnabled() is bool tp) Sys.SyncTouchpadToEc(tp);
        if (Sys.LowRefreshEnabled) Sys.ApplyRefreshRate();
    }

    public static FanMode? ParseFanMode(string? mode) => mode switch
    {
        "max" => FanMode.Max,
        "silent" => FanMode.Silent,
        "slow" => FanMode.Slow,
        "maxq" => FanMode.MaxQ,
        "auto" => FanMode.Auto,
        "custom" => FanMode.Custom,
        _ => null,
    };

    public void ApplyFan()
    {
        using var _ = _hw.Enter();
        if (Fan is null) return;
        var f = Settings.Fan;
        Log.Write($"应用风扇: mode={f.Mode} profile={f.ActiveProfile} offset={f.OffsetPercent}");
        var mode = ParseFanMode(f.Mode) ?? FanMode.Custom;
        if (mode == FanMode.Custom)
        {
            if (f.Find(f.ActiveProfile) is { } profile) Fan.ApplyTable(profile.ToTable());
        }
        else Fan.SetMode(mode);
        Fan.SetOffset(f.OffsetPercent);
    }

    public static PowerModeKind ParsePowerMode(string? mode) =>
        Enum.TryParse<PowerModeKind>(mode, true, out var m) && Enum.IsDefined(m) ? m : PowerModeKind.Performance;

    /// <param name="reapplyFan">The EC resets the fan table on a mode switch, so by default the
    /// saved fan setting is pushed again afterwards.</param>
    public void ApplyPerformance(bool reapplyFan = true)
    {
        using var _ = _hw.Enter();
        if (Power is null) return;
        var mode = ParsePowerMode(Settings.Performance.Mode);
        Log.Write("应用性能模式: " + mode);
        Power.Set(mode);
        Power.ApplyEnergyStar(mode);
        Power.SetTurboFan(Settings.Performance.TurboFan);
        if (reapplyFan) ApplyFan();
    }

    /// <summary>Send the saved charge window, including "disabled" (status 0).</summary>
    public void ApplyCharge()
    {
        using var _ = _hw.Enter();
        if (Charge is null) return;
        var c = Settings.Charge;
        Charge.Set(c.Enabled, c.Start, c.Stop);
        Log.Write(c.Enabled ? $"应用充电上限 {c.Start}%/{c.Stop}%" : "已关闭充电上限");
    }

    public void ApplyLight()
    {
        using var _ = _hw.Enter();
        if (Light is null || !Light.IsRgb) return;
        var l = Settings.Light;
        if (!l.Enabled) { Light.SetEnabled(false); return; }
        Light.SetEnabled(true);
        Light.SetBrightness(l.Brightness);
        Light.ApplyZones(Rgb(l.Left), Rgb(l.Mid), Rgb(l.Right));
        Light.SetEffect((LightEffect)l.Effect);
    }

    private static (byte R, byte G, byte B) Rgb(int[]? c) =>
        c is { Length: >= 3 }
            ? ((byte)Math.Clamp(c[0], 0, 255), (byte)Math.Clamp(c[1], 0, 255), (byte)Math.Clamp(c[2], 0, 255))
            : ((byte)0, (byte)0, (byte)200);

    // ---------------------------------------------------------------- user actions (persist + apply)

    public void SetFanMode(string mode)
    {
        if (ParseFanMode(mode) is null) throw new ArgumentException("未知风扇模式: " + mode, nameof(mode));
        using var _ = _hw.Enter();
        UpdateSettings(s => s.Fan.Mode = mode);
        ApplyFan();
    }

    /// <summary>Fn key 143: all fans to full speed.</summary>
    public void FanMax() => SetFanMode("max");

    /// <summary>Fn key 112: back to firmware automatic control.</summary>
    public void FanAuto() => SetFanMode("auto");

    public void SelectFanProfile(string name)
    {
        using var _ = _hw.Enter();
        UpdateSettings(s =>
        {
            if (s.Fan.Profiles.Any(p => p.Name == name)) s.Fan.ActiveProfile = name;
            s.Fan.Mode = "custom";
        });
        ApplyFan();
    }

    public void SetPerformanceMode(PowerModeKind mode)
    {
        using var _ = _hw.Enter();
        UpdateSettings(s => s.Performance.Mode = mode.ToString().ToLowerInvariant());
        ApplyPerformance();
    }

    /// <summary>Fn key 89: cycle the performance mode like the vendor hotkey.</summary>
    public PowerModeKind CyclePowerMode()
    {
        using var _ = _hw.Enter();
        if (Power is null || Caps is null) throw new InvalidOperationException("未连接硬件");
        var next = PowerModes.Next(Power.Current, Caps);
        SetPerformanceMode(next);
        Log.Write("切换性能模式 -> " + next);
        return next;
    }

    public void SetTurboFan(bool on)
    {
        using var _ = _hw.Enter();
        UpdateSettings(s => s.Performance.TurboFan = on);
        ApplyPerformance();
    }

    public void SetChargeLimit(bool enabled, int start, int stop)
    {
        if (enabled && start >= stop) throw new ArgumentException("起始电量必须低于停止电量");
        using var _ = _hw.Enter();
        UpdateSettings(s =>
        {
            s.Charge.Enabled = enabled;
            s.Charge.Start = start;
            s.Charge.Stop = stop;
        });
        ApplyCharge();
    }

    public bool ToggleLight()
    {
        using var _ = _hw.Enter();
        if (Light is null) throw new InvalidOperationException("未连接硬件");
        bool on = !Light.Enabled;
        SetLightEnabled(on);
        return on;
    }

    public void SetLightEnabled(bool on)
    {
        using var _ = _hw.Enter();
        UpdateSettings(s => s.Light.Enabled = on);
        Light?.SetEnabled(on);
    }

    /// <summary>Change keyboard brightness by <paramref name="delta"/> (hotkeys) and persist it so resume keeps it.</summary>
    public int AdjustLightBrightness(int delta)
    {
        using var _ = _hw.Enter();
        if (Light is null) throw new InvalidOperationException("未连接硬件");
        return SetLightBrightness(Light.Brightness + delta);
    }

    public int SetLightBrightness(int level)
    {
        int v = Math.Clamp(level, KeyboardLightService.MinBrightness, KeyboardLightService.MaxBrightness);
        using var _ = _hw.Enter();
        UpdateSettings(s => s.Light.Brightness = v);
        Light?.SetBrightness(v);
        return v;
    }

    public void SetLightColors((byte R, byte G, byte B) left, (byte R, byte G, byte B) mid, (byte R, byte G, byte B) right)
    {
        using var _ = _hw.Enter();
        UpdateSettings(s =>
        {
            s.Light.Left = [left.R, left.G, left.B];
            s.Light.Mid = [mid.R, mid.G, mid.B];
            s.Light.Right = [right.R, right.G, right.B];
        });
        Light?.ApplyZones(left, mid, right);
    }

    public void SetLightEffect(LightEffect effect)
    {
        using var _ = _hw.Enter();
        UpdateSettings(s => s.Light.Effect = (int)effect);
        if (Light is null) return;
        Light.SetEffect(effect);
        if (effect == LightEffect.CustomZones)
        {
            var l = Settings.Light;
            Light.ApplyZones(Rgb(l.Left), Rgb(l.Mid), Rgb(l.Right));
        }
    }

    public void WakePanel()
    {
        using var _ = _hw.Enter();
        Sys?.WakePanel();
    }

    // ---------------------------------------------------------------- monitoring

    /// <summary>
    /// Periodic check: if something else (official FnKey, firmware) changed the fan mode recorded
    /// in App(4,5), push the saved fan setting again. Returns true when it had to re-apply.
    /// </summary>
    public bool VerifyFan()
    {
        using var _ = _hw.Enter();
        if (Fan is null) return false;
        var expected = ParseFanMode(Settings.Fan.Mode) ?? FanMode.Custom;
        var actual = Fan.CurrentMode;
        if (actual == expected) return false;
        Log.Write($"风扇模式被改动 (EC={actual}, 期望={expected})，重新应用");
        ApplyFan();
        return true;
    }

    public FanStatus? ReadStatus()
    {
        try
        {
            var s = Fan?.ReadStatus();
            if (_statusFailing && s is not null) { _statusFailing = false; Log.Write("传感器读取已恢复"); }
            return s;
        }
        catch (Exception ex)
        {
            if (!_statusFailing) Log.Error("读取传感器失败", ex); // once, not every second
            _statusFailing = true;
            return null;
        }
    }
}
