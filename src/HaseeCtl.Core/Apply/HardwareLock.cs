using HaseeCtl.Core.Config;

namespace HaseeCtl.Core.Apply;

/// <summary>
/// Serialises multi-step hardware sequences (read-modify-write of the EC store, mode + curve, …)
/// across threads of this process and across the App/Agent processes of the same session.
/// Monitor gives in-process mutual exclusion; the named mutex covers the other process.
/// Both are re-entrant for the owning thread, so nested Engine calls are safe.
/// </summary>
public sealed class HardwareLock
{
    private const int WaitMs = 5000;
    private readonly object _sync = new();
    private readonly Mutex? _mutex;

    private static readonly Lazy<HardwareLock> SessionLock =
        new(() => new HardwareLock(@"Local\HaseeCtl.Hardware"));

    /// <summary>The lock shared by every HaseeCtl process in the current logon session.</summary>
    public static HardwareLock Session => SessionLock.Value;

    /// <param name="mutexName">null = in-process lock only (used by tests).</param>
    public HardwareLock(string? mutexName)
    {
        if (mutexName is null) return;
        try { _mutex = new Mutex(false, mutexName); }
        catch (Exception ex) { Log.Write("创建跨进程硬件锁失败，仅使用进程内锁: " + ex.Message); }
    }

    public Scope Enter()
    {
        Monitor.Enter(_sync);
        bool owned = false;
        if (_mutex is not null)
        {
            try
            {
                owned = _mutex.WaitOne(WaitMs);
                if (!owned) Log.Write("等待跨进程硬件锁超时，继续执行");
            }
            catch (AbandonedMutexException)
            {
                owned = true; // the other process died while holding it; we own it now
            }
            catch (Exception ex)
            {
                Log.Write("获取跨进程硬件锁失败: " + ex.Message);
            }
        }
        return new Scope(this, owned);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly HardwareLock? _owner;
        private readonly bool _mutexOwned;

        internal Scope(HardwareLock owner, bool mutexOwned)
        {
            _owner = owner;
            _mutexOwned = mutexOwned;
        }

        public void Dispose()
        {
            if (_owner is null) return;
            if (_mutexOwned)
            {
                try { _owner._mutex!.ReleaseMutex(); }
                catch (Exception ex) { Log.Write("释放跨进程硬件锁失败: " + ex.Message); }
            }
            Monitor.Exit(_owner._sync);
        }
    }
}
