namespace HaseeCtl.Core.Features;

/// <summary>
/// Capability flags read once at startup, mirroring FnKey's DoWMIFlag + BIOSSpecialFeature_v1_0.
/// Every bit name and offset is taken from the decompiled sources.
/// </summary>
public sealed class Capabilities
{
    public int Wmi70 { get; init; }
    public int Wmi122 { get; init; }
    public int Wmi96 { get; init; }
    public int Wmi16 { get; init; }
    public int Wmi82 { get; init; }
    public int Wmi60 { get; init; }
    public byte Toasty { get; init; }

    /// <summary>Raw BIOS special-feature table (Ex(4,{8}) output).</summary>
    public byte[] FeatureTable { get; init; } = new byte[256];

    public static Capabilities Read(Hw.Dchu dchu)
    {
        var table = dchu.Ex4(8);
        return new Capabilities
        {
            Wmi70 = dchu.GetWmi(70),
            Wmi122 = dchu.GetWmi(122),
            Wmi96 = dchu.GetWmi(96),
            Wmi16 = dchu.GetWmi(16),
            Wmi82 = dchu.GetWmi(82),
            Wmi60 = dchu.GetWmi(60),
            Toasty = table.Length > 15 ? table[15] : (byte)0,
            FeatureTable = table,
        };
    }

    public static bool Has(int flags, int bit) => (flags & (1 << bit)) != 0;
    private byte Table(int index) => index < FeatureTable.Length ? FeatureTable[index] : (byte)0;

    public bool LoadDefault => Has(Wmi70, 12);
    public bool OCLoadDefault => Has(Wmi70, 17);
    public bool FanSpeedSupport => Has(Wmi122, 0);
    public bool CustomFanSupport => Has(Wmi122, 4);
    public bool WakeUpLan => Has(Wmi122, 5);
    public bool CheckNotAirplaneOsd => Has(Wmi122, 6);
    public bool Lightbar => Has(Wmi122, 12);
    public bool FanLess => Has(Wmi122, 15);
    public bool MSHybridGpuSwitch => Has(Wmi122, 20);
    public bool CpuOverclock => Has(Wmi122, 23);
    public bool Xmp => Has(Wmi122, 24);
    public bool HardwarePState => Has(Wmi122, 30);

    public bool MultiPowerMode => Has(Wmi96, 2);
    public bool AntiDustFan => Has(Wmi96, 7);
    public bool FanOffset => Has(Wmi96, 10);
    public bool Dtt => Has(Wmi96, 12);
    public bool AmdManual => Has(Wmi96, 13);

    public bool PowerModeSupport => Has(Wmi16, 0);
    public bool Flexikey => Has(Wmi16, 1);
    public bool GpuOverclock => Has(Wmi16, 5);
    public bool FanSpeedSetting => Has(Wmi16, 7);
    public bool EnergySave => Has(Wmi16, 8);

    public bool TurboFan => (Table(16) & 0x10) != 0;
    public bool QuietMode => (Table(17) & 0x01) != 0;
    public bool PowerSavingMode => (Table(17) & 0x02) != 0;
    public bool PerformanceMode => (Table(17) & 0x04) != 0;
    public bool EntertainmentMode => (Table(17) & 0x08) != 0;
    public int UiStyleId => Table(17) >> 4;
    public bool MsHybridSwitch => (Table(18) & 0x01) != 0;
    public bool NvidiaPowerOff => (Table(19) & 0x40) != 0;
    public bool SlowFan => (Table(20) & 0x01) != 0;

    public override string ToString() =>
        $"风控={FanSpeedSupport} 自定义={CustomFanSupport} 慢速={SlowFan} 涡轮={TurboFan} " +
        $"性能模式={PowerModeSupport} 充电={EnergySave} 灯带={Lightbar} UI样式={UiStyleId}";
}
