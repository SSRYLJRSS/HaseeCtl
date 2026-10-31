using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;
using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Apply;

/// <summary>All hardware work happens here so the tray, hotkeys and UI share one implementation.</summary>
public sealed class Engine
{
    private readonly object _sync = new();
    private AcpiBridge? _bridge;
    private Dchu? _dchu;

    public Capabilities? Caps { get; private set; }
    public FanService? Fan { get; private set; }
    public PowerModeService? Power { get; private set; }
    public ChargeService? Charge { get; private set; }
    public GpuService? Gpu { get; private set; }
    public KeyboardLightService? Light { get; private set; }
    public SystemService? Sys { get; private set; }
    public AppSettings Settings { get; private set; } = AppSettings.CreateDefault();

    public bool Ready => _dchu is not null && Caps is not null;

    public bool Connect()
    {
        try
        {
            _bridge = new AcpiBridge();
            if (!_bridge.IsAvailable) return false;
            _dchu = new Dchu(_bridge);
            Caps = Capabilities.Read(_dchu);
            Fan = new FanService(_dchu, Caps);
            Power = new PowerModeService(_dchu, Caps);
            Charge = new ChargeService(_dchu);
            Gpu = new GpuService(_dchu);
            Light = new KeyboardLightService(_dchu, Caps);
            Sys = new SystemService(_dchu, Caps);
            Log.Write("已连接硬件: " + _bridge.DevicePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("连接硬件失败: " + ex.Message);
            return false;
        }
    }

    public void ReloadSettings()
    {
        Settings = SettingsStore.Load();
        Log.Write("设置已重新加载");
    }

    /// <summary>Apply every persisted setting. Called on startup, resume and after edits.</summary>
    public void ApplyAll(bool includePerformance = true)
    {
        lock (_sync)
        {
            if (!Ready) { if (!Connect()) return; }
            try
            {
                ApplyFan();
                if (includePerformance) ApplyPerformance();
                ApplyCharge();
                ApplyLight();
                ApplySystem();
            }
            catch (Exception ex)
            {
                Log.Write("应用设置失败: " + ex.Message);
            }
        }
    }

    /// <summary>EC 接管 I2C 触控板 + 同步触控板状态 + 低刷新率规则。</summary>
    public void ApplySystem()
    {
        if (Sys is null) return;
        try
        {
            Sys.EnableEcTouchpadControl();
            if (SystemService.TouchpadEnabled() is bool tp) Sys.SyncTouchpadToEc(tp);
            if (Sys.LowRefreshEnabled) Sys.ApplyRefreshRate();
        }
        catch (Exception ex) { Log.Write("应用系统设置失败: " + ex.Message); }
    }

    public void ApplyFan()
    {
        if (Fan is null) return;
        var f = Settings.Fan;
        Log.Write($"应用风扇: mode={f.Mode} profile={f.ActiveProfile} offset={f.OffsetPercent}");
        switch (f.Mode)
        {
            case "max":
                Fan.SetMode(FanMode.Max);
                break;
            case "silent":
                Fan.SetMode(FanMode.Silent);
                break;
            case "slow":
                Fan.SetMode(FanMode.Slow);
                break;
            case "maxq":
                Fan.SetMode(FanMode.MaxQ);
                break;
            case "auto":
                Fan.SetMode(FanMode.Auto);
                break;
            default:
                if (f.Find(f.ActiveProfile) is { } profile) Fan.ApplyTable(profile.ToTable());
                break;
        }
        Fan.SetOffset(f.OffsetPercent);
    }

    public void ApplyPerformance()
    {
        if (Power is null) return;
        var mode = Enum.TryParse<PowerModeKind>(Settings.Performance.Mode, true, out var m) ? m : PowerModeKind.Performance;
        Log.Write("应用性能模式: " + mode);
        Power.Set(mode);
        Power.ApplyEnergyStar(mode);
        Power.SetTurboFan(Settings.Performance.TurboFan);
    }

    public void ApplyCharge()
    {
        if (Charge is null || !Settings.Charge.Enabled) return;
        try
        {
            Charge.Set(true, Settings.Charge.Start, Settings.Charge.Stop);
            Log.Write($"应用充电上限 {Settings.Charge.Start}%/{Settings.Charge.Stop}%");
        }
        catch (Exception ex) { Log.Write("设置充电上限失败: " + ex.Message); }
    }

    public void ApplyLight()
    {
        if (Light is null || !Light.IsRgb) return;
        try
        {
            var l = Settings.Light;
            if (!l.Enabled) { Light.SetEnabled(false); return; }
            Light.SetEnabled(true);
            Light.SetBrightness(l.Brightness);
            Light.ApplyZones(
                ((byte)l.Left[0], (byte)l.Left[1], (byte)l.Left[2]),
                ((byte)l.Mid[0], (byte)l.Mid[1], (byte)l.Mid[2]),
                ((byte)l.Right[0], (byte)l.Right[1], (byte)l.Right[2]));
            Light.SetEffect((LightEffect)l.Effect);
        }
        catch (Exception ex) { Log.Write("设置键盘灯失败: " + ex.Message); }
    }

    /// <summary>Fn key 143: all fans to full speed.</summary>
    public void FanMax() => Fan?.SetMode(FanMode.Max);

    /// <summary>Fn key 112: back to firmware automatic control.</summary>
    public void FanAuto() => Fan?.SetMode(FanMode.Auto);

    /// <summary>Fn key 89: cycle the performance mode like the vendor hotkey.</summary>
    public PowerModeKind CyclePowerMode()
    {
        if (Power is null || Caps is null) return PowerModeKind.Entertainment;
        var next = PowerModes.Next(Power.Current, Caps);
        Power.Set(next);
        Power.ApplyEnergyStar(next);
        Settings.Performance.Mode = next.ToString().ToLowerInvariant();
        SettingsStore.TrySave(Settings);
        Log.Write("热键切换性能模式 -> " + next);
        return next;
    }

    public FanStatus? ReadStatus()
    {
        try { return Fan?.ReadStatus(); }
        catch (Exception ex) { Log.Write("读取传感器失败: " + ex.Message); return null; }
    }

    public Dchu? Dchu => _dchu;
}
