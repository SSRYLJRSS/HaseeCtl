using System.Text;
using HaseeCtl.Core.Features;
using HaseeCtl.Core.Hw;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 1 && args[0] == "--hotkeyxml")
{
    var xml = System.IO.File.ReadAllText(args[1]);
    int hits = 0;
    foreach (var piece in xml.Split("<?xml", StringSplitOptions.RemoveEmptyEntries))
    {
        if (HaseeCtl.Core.Features.HotkeyListener.TryParseCode(piece, out int code))
        {
            Console.WriteLine($"解析到键码 {code}");
            hits++;
        }
    }
    Console.WriteLine($"共解析 {hits} 条");
    return;
}

if (args.Length > 0 && args[0] == "--driverkeys")
{
    int seconds = args.Length > 1 ? int.Parse(args[1]) : 15;
    int got = HaseeCtl.Core.Features.HotkeyListener.DriverProbe(seconds, Console.WriteLine);
    Console.WriteLine($"共收到 {got} 个键码");
    return;
}

if (args.Length > 0 && args[0] == "--camtest")
{
    var engC = new HaseeCtl.Core.Apply.Engine();
    engC.Connect();
    var sysC = engC.Sys!;
    bool orig = sysC.CameraOn;
    Console.WriteLine($"摄像头原状态 App(1,6)={(orig ? 1 : 0)}");
    sysC.SetCamera(!orig);
    System.Threading.Thread.Sleep(4000);
    Console.WriteLine($"切换后 App(1,6)={(sysC.CameraOn ? 1 : 0)}");
    if (args.Contains("--keep")) return;
    sysC.SetCamera(orig);
    System.Threading.Thread.Sleep(3000);
    Console.WriteLine($"已恢复 App(1,6)={(sysC.CameraOn ? 1 : 0)}");
    return;
}
if (args.Length > 0 && args[0] == "--tpoff")
{
    var sv0 = new HaseeCtl.Core.Features.SystemService(null!, null!);
    try { sv0.SetTouchpad(args.Contains("--on")); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name); }
    return;
}
if (args.Length > 0 && args[0] == "--tponly")
{
    var eng0 = new HaseeCtl.Core.Apply.Engine();
    if (!args.Contains("--nodrv")) eng0.Connect();
    if (eng0.Sys is null) { Console.WriteLine("无驱动模式"); }
    if (args.Contains("--wait")) Thread.Sleep(2000);
    Console.WriteLine("前=" + HaseeCtl.Core.Features.SystemService.TouchpadEnabled());
    var sv = eng0.Sys ?? new HaseeCtl.Core.Features.SystemService(null!, null!);
    try { Console.WriteLine("关 -> " + sv.SetTouchpad(false)); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + " " + HaseeCtl.Core.Features.SystemService.TouchpadEnabled()); }
    Thread.Sleep(1000);
    try { Console.WriteLine("开 -> " + sv.SetTouchpad(true)); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + " " + HaseeCtl.Core.Features.SystemService.TouchpadEnabled()); }
    return;
}
if (args.Length > 0 && args[0] == "--systest")
{
    var eng = new HaseeCtl.Core.Apply.Engine();
    if (!eng.Connect() || eng.Sys is null) { Console.WriteLine("未连接"); return; }
    var sys = eng.Sys;
    Console.WriteLine($"触控板={HaseeCtl.Core.Features.SystemService.TouchpadEnabled()} 飞行={HaseeCtl.Core.Features.SystemService.AirplaneOn()} 刷新率={HaseeCtl.Core.Features.Display.CurrentHz()} 低刷={sys.LowRefreshEnabled}");
    var k = sys.ReadKeyboard();
    Console.WriteLine($"键盘 {k}");
    sys.WriteKeyboard(k);
    Console.WriteLine($"原值回写后 {sys.ReadKeyboard()}");
    if (!args.Contains("--noec")) sys.EnableEcTouchpadControl();
    if (args.Contains("--tp"))
    {
        Console.WriteLine("关闭触控板 -> " + sys.SetTouchpad(false));
        for (int i = 0; i < 6; i++) { Thread.Sleep(300); Console.WriteLine(" 注册表=" + HaseeCtl.Core.Features.SystemService.TouchpadEnabled()); }
        Thread.Sleep(800);
        Console.WriteLine("打开触控板 -> " + sys.SetTouchpad(true));
    }
    return;
}
if (args.Length > 0 && args[0] == "--iotest")
{
    try
    {
        var dir = HaseeCtl.Core.Config.SettingsStore.Directory;
        Console.WriteLine("AppData=" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Console.WriteLine("配置文件目录=" + dir);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "iotest.txt"), DateTime.Now.ToString());
        Console.WriteLine("写入成功");
        var loaded = HaseeCtl.Core.Config.SettingsStore.Load();
        if (loaded.Settings is not { } ok) { Console.WriteLine("Load 失败: " + loaded.Error); return; }
        Console.WriteLine("Load 成功, 方案数=" + ok.Fan.Profiles.Count);
        HaseeCtl.Core.Config.SettingsStore.Save(ok);
        Console.WriteLine("Save 成功, 文件存在=" + System.IO.File.Exists(HaseeCtl.Core.Config.SettingsStore.FilePath));
        HaseeCtl.Core.Config.Log.Write("iotest ok");
        Console.WriteLine("Log 成功, 日志目录存在=" + System.IO.Directory.Exists(HaseeCtl.Core.Config.SettingsStore.LogDirectory));
    }
    catch (Exception ex)
    {
        Console.WriteLine("失败: " + ex);
    }
    return;
}
var bridge = new AcpiBridge();
var dchu = new Dchu(bridge);
Console.WriteLine($"设备路径: {bridge.DevicePath}");
Console.WriteLine($"可用: {bridge.IsAvailable}");

try
{
    var caps = Capabilities.Read(dchu);
    Console.WriteLine($"[能力] {caps}");
    Console.WriteLine($"  WMI70=0x{caps.Wmi70:X} WMI122=0x{caps.Wmi122:X} WMI96=0x{caps.Wmi96:X} WMI16=0x{caps.Wmi16:X}");
    Console.WriteLine($"  HW_P_state={caps.HardwarePState} DTT={caps.Dtt} 慢速={caps.SlowFan} 涡轮={caps.TurboFan} 自定义曲线={caps.CustomFanSupport}");
    Console.WriteLine($"  支持性能模式: {string.Join(",", PowerModes.Supported(caps))}");
    Console.WriteLine($"  键盘灯表[15]={caps.FeatureTable[15]} 特性[16]=0x{caps.FeatureTable[16]:X} [17]=0x{caps.FeatureTable[17]:X} [18]=0x{caps.FeatureTable[18]:X} [20]=0x{caps.FeatureTable[20]:X}");

    var fan = new FanService(dchu, caps);
    var st = fan.ReadStatus();
    Console.WriteLine($"[传感器] CPU {st.CpuTempC}°C {st.CpuRpm}rpm {st.CpuDutyPercent}% | GPU {st.GpuTempC}°C {st.GpuRpm}rpm {st.GpuDutyPercent}%");

    var d = fan.ReadDefaults();
    Console.WriteLine($"[默认曲线] 风扇数={d.FanCount} InitMode={d.InitFanMode} 自定义支持={d.CustomSupported}");
    Console.WriteLine($"  CPU  T={d.Cpu.T1}/{d.Cpu.T2}/{d.Cpu.T3}/{FanCurve.T4}  D={d.Cpu.D1}/{d.Cpu.D2}/{d.Cpu.D3}/100  R={d.Cpu.R12}/{d.Cpu.R23}/{d.Cpu.R34}");
    Console.WriteLine($"  GPU1 T={d.Gpu1.T1}/{d.Gpu1.T2}/{d.Gpu1.T3}/{FanCurve.T4}  D={d.Gpu1.D1}/{d.Gpu1.D2}/{d.Gpu1.D3}/100");
    if (d.Gpu2 is not null)
        Console.WriteLine($"  GPU2 T={d.Gpu2.T1}/{d.Gpu2.T2}/{d.Gpu2.T3}/{FanCurve.T4}  D={d.Gpu2.D1}/{d.Gpu2.D2}/{d.Gpu2.D3}/100");

    Console.WriteLine($"[APP 页4] InitMode={dchu.ReadAppByte(4,4)} Mode={dchu.ReadAppByte(4,5)} FanCount={dchu.ReadAppByte(4,6)} Offset={dchu.ReadAppByte(4,7)} Turbo={dchu.ReadAppByte(4,8)} Slow={dchu.ReadAppByte(4,9)}");
    Console.WriteLine($"[APP 页1] PowerMode={dchu.ReadAppByte(1,1)} CCD={dchu.ReadAppByte(1,6)} TP={dchu.ReadAppByte(1,10)} 服务发送={dchu.ReadAppByte(1,12)} DTT={dchu.ReadAppByte(1,32)}");

    Console.WriteLine($"[键盘设置] FnLock(1开2关)={dchu.ReadAppByte(1,36)} WinKey(2开1关)={dchu.ReadAppByte(1,37)} WinFnSwap(1开2关)={dchu.ReadAppByte(1,38)} 低刷新率={dchu.ReadAppByte(1,28)} TP-EC={(caps.Wmi96 >> 4) & 1}");

    var pm = new PowerModeService(dchu, caps);
    Console.WriteLine($"[性能模式] 当前={PowerModes.DisplayName(pm.Current)} 涡轮风扇={pm.TurboFanEnabled}");

    var charge = new ChargeService(dchu);
    var cl = charge.Read();
    Console.WriteLine(cl is null ? "[充电] 不支持" : $"[充电] 启用={cl.Enabled} 起={cl.StartPercent}% 停={cl.StopPercent}% 可选起=[{string.Join(",", cl.StartOptions)}] 停=[{string.Join(",", cl.StopOptions)}]");

    var gpu = new GpuService(dchu);
    var gs = gpu.Read();
    Console.WriteLine($"[显卡] 当前={GpuService.DisplayName(gs.Current)} 支持=[{string.Join(",", gs.Supported.Select(GpuService.DisplayName))}]");

    var kb = new KeyboardLightService(dchu, caps);
    Console.WriteLine($"[键盘灯] 类型={kb.KeyboardType} RGB={kb.IsRgb} 支持={kb.Supported} 开={kb.Enabled} 亮度={kb.Brightness} 效果={kb.Effect} Left={kb.Left} Mid={kb.Mid} Right={kb.Right}");
    Console.WriteLine($"[热键] 服务接管事件={new HotkeyListener(dchu).ServiceOwnsEvents}");

    if (args.Contains("--write"))
    {
        Console.WriteLine("[写入测试] 设置风扇偏置 0 ...");
        fan.SetOffset(0);
        Console.WriteLine("  完成，读回 APP(4,7)=" + dchu.ReadAppByte(4, 7));
    }
}
catch (Exception ex)
{
    Console.WriteLine("错误: " + ex);
}
