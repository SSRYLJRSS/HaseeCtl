using System.Buffers.Binary;

namespace HaseeCtl.Core.Hw;

/// <summary>
/// Same helper surface as FnKey's DCHU class (SetWMI / GetWMI / packages / app settings),
/// built on <see cref="IAcpiBridge"/>. All encodings come from the decompiled FnKey sources.
/// </summary>
public sealed class Dchu(IAcpiBridge bridge)
{
    public IAcpiBridge Bridge { get; } = bridge;

    /// <summary>GetDCHU_Data_Integer: last type-0 argument, 0 if none.</summary>
    public int GetWmi(int command)
    {
        int v = 0;
        foreach (var a in Bridge.Invoke(command, new byte[256]))
            if (a.Type == 0) v = a.AsInt32;
        return v;
    }

    /// <summary>GetDCHU_Data_Buffer: bytes of the type-2 argument (256 bytes, zero padded).</summary>
    public byte[] GetWmiPackage(int command) => CollectBuffer(Bridge.Invoke(command, new byte[256]));

    /// <summary>SetDCHU_Data(cmd, LE uint32 data).</summary>
    public int SetWmi(int command, uint data)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, data);
        return SetData(command, b);
    }

    /// <summary>SetWMI(cmd, sub, data): LE uint32 with byte[3] replaced by the sub command.</summary>
    public int SetWmi(int command, int subCommand, uint data)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, data);
        b[3] = checked((byte)subCommand);
        return SetData(command, b);
    }

    public static uint EncodeSub(int subCommand, uint data) => (data & 0x00FFFFFF) | ((uint)(byte)subCommand << 24);

    /// <summary>SetDCHU_Data return semantics: int of a type-0 result, or the command for a type-2 result.</summary>
    public int SetData(int command, ReadOnlySpan<byte> data)
    {
        int r = 0;
        foreach (var a in Bridge.Invoke(command, data))
            r = a.Type == 0 ? a.AsInt32 : a.Type == 2 ? command : r;
        return r;
    }

    public int SetWmiPackage(int command, ReadOnlySpan<byte> data) => SetData(command, data);

    /// <summary>SetDCHU_DataEx: send a 256-byte package, return the type-2 output buffer.</summary>
    public byte[] SetWmiPackageEx(int command, ReadOnlySpan<byte> data) => CollectBuffer(Bridge.Invoke(command, data));

    public byte[] Ex4(params byte[] request)
    {
        var buf = new byte[256];
        request.CopyTo(buf, 0);
        return SetWmiPackageEx(4, buf);
    }

    public byte[] ReadApp(int page, int offset, int length)
    {
        var all = Bridge.ReadAppStore();
        return all.AsSpan(page * 0x100 + offset, length).ToArray();
    }

    public byte ReadAppByte(int page, int offset) => ReadApp(page, offset, 1)[0];

    public void WriteApp(int page, int offset, ReadOnlySpan<byte> data) => Bridge.WriteAppStore(page, offset, data);

    public void WriteAppByte(int page, int offset, byte value) => WriteApp(page, offset, [value]);

    private static byte[] CollectBuffer(IReadOnlyList<AcpiArgument> args)
    {
        var result = new byte[256];
        foreach (var a in args)
            if (a.Type == 2)
                a.Data.AsSpan(0, Math.Min(a.Data.Length, 256)).CopyTo(result);
        return result;
    }
}
