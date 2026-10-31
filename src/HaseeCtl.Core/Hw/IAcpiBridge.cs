namespace HaseeCtl.Core.Hw;

/// <summary>One value returned by an ACPI WMI method call. Type 0 = integer, type 2 = buffer.</summary>
public readonly record struct AcpiArgument(ushort Type, byte[] Data)
{
    public int AsInt32 => Data.Length >= 4 ? BitConverter.ToInt32(Data, 0) : Data.Length switch
    {
        0 => 0,
        1 => Data[0],
        2 => BitConverter.ToUInt16(Data, 0),
        _ => Data[0] | (Data[1] << 8) | (Data[2] << 16),
    };
}

/// <summary>
/// Low level access to the Clevo AcpiBridge KMDF driver (device ACPI\CLV0001).
/// Mirrors what InsydeDCHU.dll does, without depending on that DLL.
/// </summary>
public interface IAcpiBridge
{
    bool IsAvailable { get; }

    /// <summary>Calls WMI method <paramref name="command"/> with up to 256 bytes of input.</summary>
    IReadOnlyList<AcpiArgument> Invoke(int command, ReadOnlySpan<byte> input);

    /// <summary>Reads the whole 4 KiB application settings store (16 pages x 256 bytes).</summary>
    byte[] ReadAppStore();

    /// <summary>Writes <paramref name="data"/> into the store at page*256+offset.</summary>
    void WriteAppStore(int page, int offset, ReadOnlySpan<byte> data);
}

public sealed class AcpiBridgeException(string message, int win32Error = 0) : IOException(message)
{
    public int Win32Error { get; } = win32Error;
}
