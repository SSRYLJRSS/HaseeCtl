using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static HaseeCtl.Core.Hw.NativeMethods;

namespace HaseeCtl.Core.Hw;

/// <summary>
/// Direct DeviceIoControl implementation of the AcpiBridge protocol, recovered from InsydeDCHU.dll
/// (Ghidra): IOCTL 0x322400 = evaluate WMI method, 0x32240C = app settings store.
/// A handle is opened per call, as the original DLL does, so a driver reload or a sleep cycle never
/// leaves us holding a stale handle.
/// </summary>
public sealed class AcpiBridge : IAcpiBridge
{
    public static readonly Guid InterfaceGuid = new("86994c74-ad43-4812-b7e7-0c420b5c5fd7");
    public const string FallbackPath = @"\?\ACPI#CLV0001#1#{86994c74-ad43-4812-b7e7-0c420b5c5fd7}";

    internal const uint IoctlEvaluate = 0x322400;
    internal const uint IoctlWaitEvent = 0x322404;
    internal const uint IoctlGetNotifyValue = 0x322408;
    internal const uint IoctlAppStore = 0x32240C;

    internal const int EvalInputSize = 0x420;
    internal const int EvalOutputSize = 0x40C;
    internal const int AppStoreSize = 0x1000;

    // ACPI WMI method GUID 93F224E4-FBDC-4BBF-ADD6-DB71BDC0AFAD in the dword order the DLL writes.
    private static readonly uint[] MethodGuidDwords = [0x93f224e4, 0x4bbffbdc, 0x71dbd6ad, 0xadafc0bd];

    private readonly object _sync = new();
    private string? _path;

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var h = Open();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public string DevicePath
    {
        get
        {
            lock (_sync)
            {
                return _path ??= FindDevicePath() ?? FallbackPath;
            }
        }
    }

    public static string? FindDevicePath()
    {
        var guid = InterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref guid, IntPtr.Zero, 0) != 0 || len <= 1)
            return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, IntPtr.Zero, buf, len, 0) != 0)
            return null;
        var paths = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return paths.FirstOrDefault(p => p.Contains("CLV0001", StringComparison.OrdinalIgnoreCase))
               ?? paths.FirstOrDefault();
    }

    internal SafeFileHandle Open(bool overlapped = false)
    {
        var h = CreateFileW(DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | (overlapped ? FILE_FLAG_OVERLAPPED : 0), IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            lock (_sync) _path = null; // re-enumerate next time (device may have been re-created)
            throw new AcpiBridgeException($"无法打开 AcpiBridge 设备 (Win32 错误 {err})", err);
        }
        return h;
    }

    internal static byte[] BuildEvalInput(int command, ReadOnlySpan<byte> input)
    {
        if (input.Length > 256) throw new ArgumentException("WMI 输入最多 256 字节", nameof(input));
        var b = new byte[EvalInputSize];
        var s = b.AsSpan();
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(s[(i * 4)..], MethodGuidDwords[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], 0x4d53445f); // "_DSM"
        BinaryPrimitives.WriteInt32LittleEndian(s[0x18..], command);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1c..], 0x104);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1e..], 0x01000002);
        input.CopyTo(s[0x22..]);
        return b;
    }

    internal static List<AcpiArgument> ParseEvalOutput(ReadOnlySpan<byte> o, int bytesReturned)
    {
        var list = new List<AcpiArgument>();
        if (bytesReturned < 0xC) return list;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(o[8..]);
        int pos = 0xC;
        for (uint i = 0; i < count && pos + 4 <= o.Length; i++)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(o[pos..]);
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(o[(pos + 2)..]);
            int dataLen = Math.Min(len, o.Length - pos - 4);
            list.Add(new AcpiArgument(type, o.Slice(pos + 4, Math.Max(0, dataLen)).ToArray()));
            pos += Math.Max((int)len, 4) + 4;
        }
        return list;
    }

    public unsafe IReadOnlyList<AcpiArgument> Invoke(int command, ReadOnlySpan<byte> input)
    {
        var inBuf = BuildEvalInput(command, input);
        var outBuf = new byte[EvalOutputSize];
        lock (_sync)
        {
            using var h = Open();
            fixed (byte* pi = inBuf)
            fixed (byte* po = outBuf)
            {
                if (!DeviceIoControl(h, IoctlEvaluate, pi, inBuf.Length, po, outBuf.Length, out int ret, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new AcpiBridgeException($"WMI 调用 {command} 失败 (Win32 错误 {err})", err);
                }
                return ParseEvalOutput(outBuf, ret);
            }
        }
    }

    public unsafe byte[] ReadAppStore()
    {
        var inBuf = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(inBuf.AsSpan(4), 0x10000000);
        var outBuf = new byte[AppStoreSize];
        lock (_sync)
        {
            using var h = Open();
            fixed (byte* pi = inBuf)
            fixed (byte* po = outBuf)
            {
                if (!DeviceIoControl(h, IoctlAppStore, pi, inBuf.Length, po, outBuf.Length, out _, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new AcpiBridgeException($"读取设置存储失败 (Win32 错误 {err})", err);
                }
            }
        }
        return outBuf;
    }

    internal static byte[] BuildAppWrite(int page, int offset, ReadOnlySpan<byte> data)
    {
        if (page < 0 || offset < 0 || page * 0x100 + offset + data.Length > AppStoreSize)
            throw new ArgumentOutOfRangeException(nameof(offset), "超出 4 KiB 设置存储范围");
        var b = new byte[8 + AppStoreSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)(page * 0x100 + offset));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)data.Length);
        data.CopyTo(b.AsSpan(8));
        return b;
    }

    public unsafe void WriteAppStore(int page, int offset, ReadOnlySpan<byte> data)
    {
        var inBuf = BuildAppWrite(page, offset, data);
        lock (_sync)
        {
            using var h = Open();
            fixed (byte* pi = inBuf)
            {
                if (!DeviceIoControl(h, IoctlAppStore, pi, inBuf.Length, null, 0, out _, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new AcpiBridgeException($"写入设置存储失败 (Win32 错误 {err})", err);
                }
            }
        }
    }

    /// <summary>Handle for the driver's wait-event (hotkey) protocol.</summary>
    public SafeFileHandle OpenEventHandle() => Open();

    /// <summary>Arm the driver so it signals <paramref name="hsaEvent"/> when a Fn key is pressed.</summary>
    public unsafe void RegisterWaitEvent(SafeFileHandle handle, IntPtr hsaEvent)
    {
        IntPtr hsa = hsaEvent;
        var outBuf = new byte[256];
        fixed (byte* po = outBuf)
        {
            if (!DeviceIoControl(handle, IoctlWaitEvent, &hsa, IntPtr.Size, po, outBuf.Length, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new AcpiBridgeException($"注册热键等待事件失败 (Win32 错误 {err})", err);
            }
        }
    }

    /// <summary>Read the pending notification value (the Fn key code) after the event fires.</summary>
    public unsafe int ReadNotifyValue(SafeFileHandle handle)
    {
        long value = 0;
        if (!DeviceIoControl(handle, IoctlGetNotifyValue, null, 0, &value, sizeof(long), out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new AcpiBridgeException($"读取热键通知值失败 (Win32 错误 {err})", err);
        }
        return (int)value;
    }
}
