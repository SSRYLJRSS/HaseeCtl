using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Features;

public enum LightEffect
{
    Random = 0,
    CustomZones = 1,
    Breath = 2,
    Cycle = 3,
    Wave = 4,
    Dance = 5,
    Tempo = 6,
    Flash = 7,
    AllSame = 8,
}

/// <summary>RGB keyboard backlight, ported from FnKey RGBKB.cs (keyboard type 6 / 22).</summary>
public sealed class KeyboardLightService(Dchu dchu, Capabilities caps)
{
    public const int Command = 103;
    private const int AppPage = 2;

    public const int MinBrightness = 1;
    public const int MaxBrightness = 4;

    private int? _keyboardType;

    /// <summary>Keyboard type from WMI 13; fixed hardware, read once.</summary>
    public int KeyboardType => _keyboardType ??= dchu.GetWmiPackage(13)[15];
    public bool IsRgb => KeyboardType is 6 or 22;
    public bool Supported => caps.Lightbar || IsRgb;

    public int Brightness => dchu.ReadAppByte(AppPage, 35);
    public LightEffect Effect => (LightEffect)dchu.ReadAppByte(AppPage, 32);
    public bool Enabled => dchu.ReadAppByte(AppPage, 80) != 0;

    public (byte R, byte G, byte B) Left => ReadColor(85);
    public (byte R, byte G, byte B) Mid => ReadColor(89);
    public (byte R, byte G, byte B) Right => ReadColor(93);
    public (byte R, byte G, byte B) All => ReadColor(81);

    private (byte, byte, byte) ReadColor(int offset)
    {
        var b = dchu.ReadApp(AppPage, offset, 3);
        return (b[0], b[1], b[2]);
    }

    public void SetBrightness(int level)
    {
        int v = Math.Clamp(level, MinBrightness, MaxBrightness);
        Send(0xF4000000u + (uint)(47 * v));
        dchu.WriteAppByte(AppPage, 35, (byte)v);
    }

    /// <summary>SetColour(part, rgb) - parts 1=left 2=mid 3=right 8=all 7=tp.</summary>
    public void SetColor(int part, byte r, byte g, byte b)
    {
        uint sub = part switch { 1 => 240, 2 => 241, 3 => 242, 8 => 243, 7 => 246, _ => 240 };
        uint val = ((uint)b << 16) | ((uint)r << 8) | g;
        if (r == 0 && g == 255 && b == 127) val = 0x460000u | ((uint)r << 8) | g;
        Send((sub << 24) | val);
    }

    public void SetAllColor(byte r, byte g, byte b) => SetColor(8, r, g, b);

    public void ApplyZones((byte R, byte G, byte B) left, (byte R, byte G, byte B) mid, (byte R, byte G, byte B) right)
    {
        SetColor(1, left.R, left.G, left.B);
        SetColor(2, mid.R, mid.G, mid.B);
        SetColor(3, right.R, right.G, right.B);
        dchu.WriteApp(AppPage, 84, [1, left.R, left.G, left.B]);
        dchu.WriteApp(AppPage, 88, [1, mid.R, mid.G, mid.B]);
        dchu.WriteApp(AppPage, 92, [1, right.R, right.G, right.B]);
    }

    public void SetEffect(LightEffect effect)
    {
        uint cmd = effect switch
        {
            LightEffect.Random => 0x70000000u,
            LightEffect.CustomZones => 0x15000000u,
            LightEffect.Breath => 0x10000000u | (2u << 16) | 0xA000u,
            LightEffect.Cycle => 0x33010000u,
            LightEffect.Wave => 0xB0000000u,
            LightEffect.Dance => 0x80000000u,
            LightEffect.Tempo => 0x90000000u,
            LightEffect.Flash => 0xA0000000u,
            LightEffect.AllSame => 0x15000000u | (2u << 16) | 0xA000u,
            _ => 0x15000000u,
        };
        Send(cmd);
        dchu.WriteAppByte(AppPage, 32, (byte)effect);
    }

    public void SetEnabled(bool on)
    {
        uint val = on ? BuildOnValue() : 7u;
        Send((0xE0u << 24) | val);
        dchu.WriteAppByte(AppPage, 80, (byte)(on ? 1 : 0));
    }

    private uint BuildOnValue()
    {
        var p = dchu.ReadAppPage(AppPage); // one driver read instead of five
        uint v = 7;
        v |= 1u << 12;
        v |= (uint)(p[100] == 1 ? 1 : 0) << 13;
        v |= (uint)(p[96] == 1 ? 1 : 0) << 15;
        v |= (uint)(p[84] == 1 ? 1 : 0) << 16;
        v |= (uint)(p[88] == 1 ? 1 : 0) << 17;
        v |= (uint)(p[92] == 1 ? 1 : 0) << 18;
        return v;
    }

    public void SetOverrideBootEffect(bool on) =>
        dchu.SetWmi(121, 0x1E000000u + (uint)(on ? 1 : 0));

    private void Send(uint command) => dchu.SetWmi(Command, command);

    /// <summary>Colour presets shipped with the vendor tool (FnKey Init_RGB15Color_Table).</summary>
    public static readonly (byte R, byte G, byte B)[] Presets =
    [
        (0, 0, 255), (0, 0, 200), (0, 255, 0), (255, 0, 0), (0, 255, 200), (0, 127, 200),
        (0, 255, 127), (255, 0, 200), (127, 0, 200), (255, 0, 127), (255, 255, 0),
        (127, 255, 200), (255, 127, 0), (63, 255, 0), (255, 127, 63), (63, 127, 127), (223, 240, 124),
    ];
}
