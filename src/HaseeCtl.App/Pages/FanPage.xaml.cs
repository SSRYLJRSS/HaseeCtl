using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HaseeCtl.App.Controls;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class FanPage : UserControl
{
    private UiState? _state;
    private FanDefaults? _defaults;
    private bool _loading;
    private bool _showGpu;

    /// <summary>Editable copy of the saved profiles. Edits land here first, then get committed to
    /// settings + EC by the debounced auto-save, so a reload triggered by the Agent cannot drop them.</summary>
    private List<FanProfile> _draft = [];

    /// <summary>Debounce for the auto-save: dragging fires on every mouse-up, and each commit
    /// writes the settings file and pushes a table to the EC, so coalesce the bursts.</summary>
    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _autoSaveArmed;

    public FanPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _autoSave.Stop();
        Curve.CurveChanged += OnCurveDragged;
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); FlushAutoSave(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_state is not null) return;
        _state = DataContext as UiState;
        if (_state is null) return;
        try { _defaults = _state.Engine.WithHardware(() => _state.Engine.Fan?.ReadDefaults()); }
        catch (Exception ex) { Log.Error("读取风扇默认值失败", ex); }
        ReloadProfiles();
    }

    private void ReloadProfiles()
    {
        if (_state is null) return;
        _loading = true;
        var fan = _state.Settings.Fan;
        _draft = fan.Profiles.Select(CloneProfile).ToList();
        ProfileBox.ItemsSource = _draft.Select(p => p.Name).ToList();
        ProfileBox.SelectedItem = fan.ActiveProfile;
        if (ProfileBox.SelectedItem is null && _draft.Count > 0) ProfileBox.SelectedIndex = 0;
        _loading = false;
        LoadCurveIntoEditor();
    }

    private FanProfile? Selected
    {
        get
        {
            var name = ProfileBox.SelectedItem as string;
            return _draft.FirstOrDefault(p => p.Name == name) ?? _draft.FirstOrDefault();
        }
    }

    private CurveSettings? CurrentCurve => Selected is null ? null : _showGpu ? Selected.Gpu1 : Selected.Cpu;

    private void LoadCurveIntoEditor()
    {
        var p = Selected;
        var c = CurrentCurve;
        if (p is null || c is null) return;

        double t1 = _defaults is null ? c.T1 : (_showGpu ? _defaults.Gpu1.T1 : _defaults.Cpu.T1);
        // D1 参与 R12 斜率计算（FanCurve.cs:21）并写进 app store（FanService.cs:103），
        // 实际生效的是配置里的值，所以要显示配置值而不是固件报告值。
        double d1 = c.D1;

        _loading = true;
        Curve.T1 = t1;
        Curve.D1 = d1;
        Curve.T4 = 100;
        Curve.D4 = 100;
        Curve.SetCurve(t1, d1, c.T2, c.D2, c.T3, c.D3);
        _loading = false;

        UpdateMarker();
        RefreshInfo(p, c);
    }

    private void RefreshInfo(FanProfile p, CurveSettings c)
    {
        CurveInfo.Text = $"节点 2：{c.T2}°C → {c.D2}%    节点 3：{c.T3}°C → {c.D3}%    " +
                         $"起转 {Curve.T1:0}°C（固件固定）/{c.D1}%（参与斜率）    满载 100°C/100%（固件固定）";
        ShowStatus();
    }

    /// <summary>One always-on status line: which profile is live, and whether the EC is on our
    /// custom curve or still on a firmware mode (the mode buttons can leave it there).</summary>
    private void ShowStatus(string? transient = null, bool error = false)
    {
        if (_state is null) return;
        string text;
        if (transient is not null) text = transient;
        else
        {
            var name = Selected?.Name ?? "—";
            var mode = _state.Settings.Fan.Mode;
            text = mode == "custom"
                ? $"已生效：{name} · 自定义曲线（共 {_draft.Count} 个方案）"
                : $"已生效：{name} · 当前为固件「{ModeLabel(mode)}」档，曲线未生效；拖动曲线或换方案即切回自定义";
        }
        ProfileHint.Text = text;
    }

    private static string ModeLabel(string mode) => mode switch
    {
        "auto" => "自动",
        "silent" => "静音",
        "max" => "最大",
        "slow" => "慢速",
        "maxq" => "静音优先",
        _ => mode,
    };

    /// <summary>Set the hint text and paint it red while it carries a validation error.</summary>
    private void ShowError(string msg)
    {
        ProfileHint.Text = msg;
        ProfileHint.Foreground = errorBrush.Value;
    }

    private static readonly Lazy<System.Windows.Media.Brush> errorBrush = new(() =>
    {
        var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x5C, 0x5C));
        b.Freeze();
        return b;
    });

    private void UpdateMarker()
    {
        if (_state is null) return;
        Curve.MarkerTemp = _showGpu ? _state.GpuTemp : _state.CpuTemp;
    }

    private void OnTargetChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _showGpu = GpuTab.IsChecked == true;
        LoadCurveIntoEditor();
    }

    private void OnCurveDragged(object? sender, EventArgs e)
    {
        if (_loading) return;
        var p = Selected;
        var c = CurrentCurve;
        if (p is null || c is null) return;
        c.T2 = (int)Curve.T2; c.D2 = (int)Curve.D2;
        c.T3 = (int)Curve.T3; c.D3 = (int)Curve.D3;
        RefreshInfo(p, c);
        ArmAutoSave();
    }

    /// <summary>Selecting a profile in the combo box applies it immediately — same path as the
    /// old "应用此方案" button, so the dropdown is the only fan switch the user needs.</summary>
    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadCurveIntoEditor();
        var p = Selected;
        if (p is null || _state is null) return;
        if (!CommitEditor(out var err)) { ShowError(err); return; }
        try
        {
            SaveDraft(p, activate: true);
            _state.Engine.SelectFanProfile(p.Name);
            ShowStatus();
        }
        catch (Exception ex) { ShowError("下发失败：" + ex.Message); }
    }

    /// <summary>(Re)start the debounce window; the actual commit happens in <see cref="FlushAutoSave"/>.</summary>
    private void ArmAutoSave()
    {
        if (_state is null) return;
        _autoSaveArmed = true;
        _autoSave.Stop();
        _autoSave.Start();
    }

    /// <summary>Commit the draft curve, push it to the EC and switch to custom mode. Validation
    /// failures only paint the hint line red — a modal dialog on every drag would be unusable.</summary>
    private void FlushAutoSave()
    {
        if (!_autoSaveArmed) return;
        _autoSaveArmed = false;
        var p = Selected;
        if (p is null || _state is null) return;
        if (!CommitEditor(out var err)) { ShowError(err); return; }
        try
        {
            SaveDraft(p, activate: true, mode: "custom");
            _state.Engine.ApplyFan();
            ShowStatus();
        }
        catch (Exception ex) { ShowError("下发失败：" + ex.Message); }
    }

    private bool CommitEditor(out string error)
    {
        error = string.Empty;
        if (_state is null) { error = "界面未初始化"; return false; }
        var p = Selected;
        if (p is null) { error = "没有可用的风扇方案"; return false; }

        var cpu = p.Cpu;
        var gpu = p.Gpu1;
        var table = p.ToTable();
        var msg = table.Cpu.Validate() ?? table.Gpu1.Validate();
        if (msg is not null)
        {
            error = "曲线不合法：" + msg;
            return false;
        }
        if (cpu.T2 >= cpu.T3 || gpu.T2 >= gpu.T3)
        {
            error = "节点 2 的温度必须小于节点 3。";
            return false;
        }
        return true;
    }

    /// <summary>Write the selected draft profile into the shared settings (replace by name or add).</summary>
    private void SaveDraft(FanProfile p, bool activate, string? mode = null)
    {
        var copy = CloneProfile(p);
        _state!.Update(s =>
        {
            var list = s.Fan.Profiles;
            int i = list.FindIndex(x => x.Name == copy.Name);
            if (i >= 0) list[i] = copy; else list.Add(copy);
            if (activate) s.Fan.ActiveProfile = copy.Name;
            if (mode is not null) s.Fan.Mode = mode;
        });
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(out var err)) { ShowError(err); return; }
        var name = InputDialog.Ask(this, "新建方案", "方案名称：", Selected!.Name + " 副本")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_draft.Any(p => p.Name == name) || _state!.Settings.Fan.Profiles.Any(p => p.Name == name)) { ShowError("同名方案已存在。"); return; }
        try
        {
            var copy = CloneProfile(Selected!);
            copy.Name = name;
            SaveDraft(copy, activate: true, mode: "custom");
            ReloadProfiles();
            _state.Engine.ApplyFan();
            ShowStatus();
        }
        catch (Exception ex) { ShowError("新建失败：" + ex.Message); }
    }

    private static CurveSettings Clone(CurveSettings c) => new()
    {
        T1 = c.T1, D1 = c.D1, T2 = c.T2, D2 = c.D2, T3 = c.T3, D3 = c.D3,
    };

    private static FanProfile CloneProfile(FanProfile p) => new()
    {
        Name = p.Name,
        Cpu = Clone(p.Cpu),
        Gpu1 = Clone(p.Gpu1),
        Gpu2 = p.Gpu2 is null ? null : Clone(p.Gpu2),
    };

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        var name = ProfileBox.SelectedItem as string;
        if (name is null) return;
        if (_state!.Settings.Fan.Profiles.Count <= 1) { Warn("至少保留一个方案。"); return; }
        if (MessageBox.Show($"确定删除方案「{name}」？", "HaseeCtl", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool wasActive = false;
        try
        {
            _state.Update(s =>
            {
                var fan = s.Fan;
                if (fan.Profiles.Count <= 1) return;
                fan.Profiles.RemoveAll(p => p.Name == name);
                wasActive = fan.ActiveProfile == name;
                if (wasActive) fan.ActiveProfile = fan.Profiles[0].Name;
            });
            // the deleted curve may be the one running on the EC: push the new active profile
            if (wasActive && _state.Settings.Fan.Mode == "custom") _state.Engine.ApplyFan();
        }
        catch (Exception ex) { Warn(ex.Message); }
        ReloadProfiles();
    }

    private void OnCopyCpuToGpu(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p is null) return;
        p.Gpu1.T2 = p.Cpu.T2; p.Gpu1.D2 = p.Cpu.D2;
        p.Gpu1.T3 = p.Cpu.T3; p.Gpu1.D3 = p.Cpu.D3;
        LoadCurveIntoEditor();
        FlushNow();
    }

    /// <summary>Commit and push immediately (button actions), skipping the debounce window.</summary>
    private void FlushNow()
    {
        _autoSave.Stop();
        _autoSaveArmed = true;
        FlushAutoSave();
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("把当前方案的曲线恢复成固件出厂值并立即生效？其他自定义方案不受影响。", "HaseeCtl",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var p = Selected;
            if (p is null || _state is null) return;
            // read the firmware's own curve, then write it into the profile instead of leaving the EC in auto
            _defaults = _state.Engine.WithHardware(() => _state.Engine.Fan?.ReadDefaults());
            if (_defaults is not null)
            {
                p.Cpu.T2 = _defaults.Cpu.T2; p.Cpu.D2 = _defaults.Cpu.D2;
                p.Cpu.T3 = _defaults.Cpu.T3; p.Cpu.D3 = _defaults.Cpu.D3;
                p.Cpu.D1 = _defaults.Cpu.D1;
                p.Gpu1.T2 = _defaults.Gpu1.T2; p.Gpu1.D2 = _defaults.Gpu1.D2;
                p.Gpu1.T3 = _defaults.Gpu1.T3; p.Gpu1.D3 = _defaults.Gpu1.D3;
                p.Gpu1.D1 = _defaults.Gpu1.D1;
            }
            LoadCurveIntoEditor();
            FlushNow();
        }
        catch (Exception ex) { ShowError("恢复出厂失败：" + ex.Message); }
    }

    private void OnModeAuto(object sender, RoutedEventArgs e) => SetMode("auto", "自动");
    private void OnModeSilent(object sender, RoutedEventArgs e) => SetMode("silent", "静音");
    private void OnModeMax(object sender, RoutedEventArgs e) => SetMode("max", "最大");

    /// <summary>Persisted, so resume and the periodic check keep the chosen mode.</summary>
    private void SetMode(string mode, string label)
    {
        try
        {
            _state!.Engine.SetFanMode(mode);
            ShowStatus($"风扇已切到固件「{label}」档。拖动曲线或重新选择方案即可切回自定义。");
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private async void OnModeTurboBurst(object sender, RoutedEventArgs e)
    {
        try
        {
            var engine = _state!.Engine;
            if (engine.Fan is not { } fan) return;
            var button = sender as UIElement;
            if (button is not null) button.IsEnabled = false;
            try
            {
                // not persisted: a burst is temporary, the saved fan setting is restored afterwards
                engine.WithHardware(() => fan.SetMode(FanMode.Max));
                ProfileHint.Text = "全速运行中… 3 秒后恢复。";
                await Task.Delay(3000);
                engine.ApplyFan();
                ProfileHint.Text = "已恢复到保存的风扇设置。";
            }
            finally
            {
                if (button is not null) button.IsEnabled = true;
            }
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "HaseeCtl", MessageBoxButton.OK, MessageBoxImage.Warning);
}
