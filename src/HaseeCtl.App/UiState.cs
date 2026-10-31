using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using HaseeCtl.Core.Apply;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App;

/// <summary>Shared view state: one hardware engine plus live sensor values for the whole UI.</summary>
public sealed class UiState : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private int _cpuTemp, _cpuRpm, _cpuDuty, _gpuTemp, _gpuRpm, _gpuDuty;
    private string _status = "正在连接硬件…";
    private bool _connected;

    public Engine Engine { get; }
    public AppSettings Settings { get; }
    public ObservableCollection<FanProfile> Profiles => new(Settings.Fan.Profiles);
    public Capabilities? Caps => Engine.Caps;

    /// <summary>Graphics switching is reported by the EC's GPU interface (WMI 4 sub 21), not by a flag bit.</summary>
    public bool GpuSupported
    {
        get
        {
            try { return Engine.Gpu?.Read().Supported.Count > 0; }
            catch { return false; }
        }
    }

    public UiState()
    {
        Engine = new Engine();
        Engine.ReloadSettings();
        _connected = Engine.Connect();
        Settings = Engine.Settings;
        _status = _connected ? "已连接 AcpiBridge 驱动" : "未找到 AcpiBridge 驱动";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Refresh()
    {
        var s = Engine.ReadStatus();
        if (s is null) return;
        CpuTemp = s.CpuTempC; CpuRpm = s.CpuRpm; CpuDuty = s.CpuDutyPercent;
        GpuTemp = s.GpuTempC; GpuRpm = s.GpuRpm; GpuDuty = s.GpuDutyPercent;
    }

    public bool Connected { get => _connected; set => Set(ref _connected, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int CpuTemp { get => _cpuTemp; set => Set(ref _cpuTemp, value); }
    public int CpuRpm { get => _cpuRpm; set => Set(ref _cpuRpm, value); }
    public int CpuDuty { get => _cpuDuty; set => Set(ref _cpuDuty, value); }
    public int GpuTemp { get => _gpuTemp; set => Set(ref _gpuTemp, value); }
    public int GpuRpm { get => _gpuRpm; set => Set(ref _gpuRpm, value); }
    public int GpuDuty { get => _gpuDuty; set => Set(ref _gpuDuty, value); }

    public void Save() => SettingsStore.Save(Settings);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
