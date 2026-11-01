using System.Windows;
using System.Windows.Controls;
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

    /// <summary>Editable copy of the saved profiles. Edits stay here until a save/apply button commits them
    /// through <see cref="UiState.Update"/>, so a reload triggered by the Agent can never drop them.</summary>
    private List<FanProfile> _draft = [];

    public FanPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Curve.CurveChanged += OnCurveDragged;
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
        ProfileHint.Text = $"当前方案：{p.Name}（共 {_draft.Count} 个）。" +
                           "拖动圆点后点「保存修改」写入配置，点「应用此方案」立即下发到 EC。";
    }

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
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadCurveIntoEditor();
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

    private void OnApplyProfile(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(out var err)) { Warn(err); return; }
        try
        {
            var p = Selected!;
            SaveDraft(p, activate: true, mode: "custom");
            _state!.Engine.ApplyFan();
            ProfileHint.Text = $"已下发并生效：{p.Name}";
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(out var err)) { Warn(err); return; }
        try
        {
            var p = Selected!;
            SaveDraft(p, activate: true);
            ProfileHint.Text = $"已保存方案：{p.Name}（下次开机/唤醒会自动应用）";
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(out var err)) { Warn(err); return; }
        var name = InputDialog.Ask(this, "新建方案", "方案名称：", Selected!.Name + " 副本")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_draft.Any(p => p.Name == name) || _state!.Settings.Fan.Profiles.Any(p => p.Name == name)) { Warn("同名方案已存在。"); return; }
        try
        {
            var copy = CloneProfile(Selected!);
            copy.Name = name;
            SaveDraft(copy, activate: true);
            ReloadProfiles();
            ProfileHint.Text = $"已新建方案：{name}";
        }
        catch (Exception ex) { Warn(ex.Message); }
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

    private void OnApplyCurve(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(out var err)) { Warn(err); return; }
        try
        {
            SaveDraft(Selected!, activate: true, mode: "custom");
            _state!.Engine.ApplyFan();
            ProfileHint.Text = "已应用自定义曲线，EC 当前处于自定义模式。";
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnPushOnly(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(out var err)) { Warn(err); return; }
        try
        {
            var p = Selected!;
            SaveDraft(p, activate: false);
            var table = p.ToTable();
            _state!.Engine.WithHardware(() => _state.Engine.Fan?.PushTableOnly(table));
            ProfileHint.Text = "曲线已写入固件表，风扇模式保持不变。";
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnCopyCpuToGpu(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p is null) return;
        p.Gpu1.T2 = p.Cpu.T2; p.Gpu1.D2 = p.Cpu.D2;
        p.Gpu1.T3 = p.Cpu.T3; p.Gpu1.D3 = p.Cpu.D3;
        LoadCurveIntoEditor();
        ProfileHint.Text = "已把 CPU 曲线复制到 GPU，记得点「保存修改」。";
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("把固件自带的曲线恢复回去？保存的自定义方案不会被删除。", "HaseeCtl",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var engine = _state!.Engine;
            _defaults = engine.WithHardware(() =>
            {
                engine.Fan?.RestoreDefaults();
                return engine.Fan?.ReadDefaults();
            });
            // the firmware is back in automatic mode; record it, or the Agent would re-push the custom curve
            _state.Update(s => s.Fan.Mode = "auto");
            if (_defaults is not null)
            {
                var p = Selected;
                if (p is not null)
                {
                    p.Cpu.T2 = _defaults.Cpu.T2; p.Cpu.D2 = _defaults.Cpu.D2;
                    p.Cpu.T3 = _defaults.Cpu.T3; p.Cpu.D3 = _defaults.Cpu.D3;
                    p.Gpu1.T2 = _defaults.Gpu1.T2; p.Gpu1.D2 = _defaults.Gpu1.D2;
                    p.Gpu1.T3 = _defaults.Gpu1.T3; p.Gpu1.D3 = _defaults.Gpu1.D3;
                }
            }
            LoadCurveIntoEditor();
            ProfileHint.Text = "已恢复固件默认曲线并切回自动模式；默认值已填入编辑器，点「保存修改」可存入当前方案。";
        }
        catch (Exception ex) { Warn(ex.Message); }
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
            ProfileHint.Text = $"风扇已切到「{label}」模式。切回自定义请点「应用曲线」。";
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
