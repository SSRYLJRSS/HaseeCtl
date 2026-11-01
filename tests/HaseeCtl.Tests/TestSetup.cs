using System.Runtime.CompilerServices;
using HaseeCtl.Core.Hw;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HaseeCtl.Tests;

internal static class TestSetup
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "HaseeCtl.Tests." + Environment.ProcessId);

    /// <summary>SettingsStore picks its directory once; point it at a private temp folder before anything touches it.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("HASEECTL_DATA_DIR", DataDir);
    }
}

/// <summary>In-memory AcpiBridge: records every WMI call and keeps a 4 KiB app store.</summary>
internal sealed class FakeBridge : IAcpiBridge
{
    public readonly List<(int Command, byte[] Input)> Calls = [];
    public readonly byte[] Store = new byte[16 * 256];
    public int StoreReads;

    public bool IsAvailable => true;

    public IReadOnlyList<AcpiArgument> Invoke(int command, ReadOnlySpan<byte> input)
    {
        lock (Calls) Calls.Add((command, input.ToArray()));
        return [];
    }

    public byte[] ReadAppStore()
    {
        Interlocked.Increment(ref StoreReads);
        lock (Store) return (byte[])Store.Clone();
    }

    public void WriteAppStore(int page, int offset, ReadOnlySpan<byte> data)
    {
        lock (Store) data.CopyTo(Store.AsSpan(page * 256 + offset));
    }

    public IEnumerable<byte[]> Ex4Requests(byte sub) =>
        Calls.Where(c => c.Command == 4 && c.Input.Length > 0 && c.Input[0] == sub).Select(c => c.Input);
}
