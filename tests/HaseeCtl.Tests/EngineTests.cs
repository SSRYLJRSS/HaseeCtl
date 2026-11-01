using HaseeCtl.Core.Apply;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.Tests;

public class SettingsStoreTests
{
    public SettingsStoreTests()
    {
        foreach (var f in Directory.GetFiles(SettingsStore.Directory, "settings.json*")) File.Delete(f);
    }

    [Fact]
    public void Uses_the_test_directory() =>
        Assert.StartsWith(TestSetup.DataDir, SettingsStore.FilePath);

    [Fact]
    public void Missing_file_gives_defaults_and_creates_it()
    {
        var r = SettingsStore.Load();
        Assert.NotNull(r.Settings);
        Assert.True(File.Exists(SettingsStore.FilePath));
        Assert.Equal(SettingsStore.Stamp(), r.Stamp);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_before_defaults_are_written()
    {
        File.WriteAllText(SettingsStore.FilePath, "{ \"fan\": { broken");
        var r = SettingsStore.Load();
        Assert.NotNull(r.Settings);
        Assert.NotNull(r.Error);
        var backup = Assert.Single(Directory.GetFiles(SettingsStore.Directory, "settings.json.corrupt-*.bak"));
        Assert.Equal("{ \"fan\": { broken", File.ReadAllText(backup));
    }

    [Fact]
    public void Save_round_trips_and_leaves_no_temp_file()
    {
        var s = AppSettings.CreateDefault();
        s.Charge.Enabled = true; s.Charge.Start = 55; s.Charge.Stop = 65;
        Assert.NotNull(SettingsStore.Save(s));
        Assert.Empty(Directory.GetFiles(SettingsStore.Directory, "*.tmp"));
        var back = SettingsStore.Load().Settings!;
        Assert.True(back.Charge.Enabled);
        Assert.Equal(55, back.Charge.Start);
        Assert.Equal(65, back.Charge.Stop);
    }

    [Fact]
    public void Normalize_repairs_out_of_range_values()
    {
        var s = AppSettings.CreateDefault();
        s.Fan.Mode = "bogus";
        s.Fan.ActiveProfile = "does not exist";
        s.Fan.OffsetPercent = 300;
        s.Performance.Mode = "turbo???";
        s.Charge.Start = 90; s.Charge.Stop = 80;
        s.Light.Brightness = 9;
        s.Light.Left = [999];
        s.Fan.Profiles[0].Cpu = new CurveSettings { T1 = 40, D1 = 0, T2 = 60, D2 = 0, T3 = 80, D3 = 0 };
        SettingsStore.Normalize(s);
        Assert.Equal("custom", s.Fan.Mode);
        Assert.Contains(s.Fan.Profiles, p => p.Name == s.Fan.ActiveProfile);
        Assert.Equal(100, s.Fan.OffsetPercent);
        Assert.Equal("performance", s.Performance.Mode);
        Assert.True(s.Charge.Start < s.Charge.Stop);
        Assert.Equal(KeyboardLightService.MaxBrightness, s.Light.Brightness);
        Assert.Equal([0, 0, 200], s.Light.Left);
        Assert.Null(s.Fan.Profiles[0].Cpu.ToCurve().Validate());
    }
}

public class EngineTests
{
    private readonly FakeBridge _bridge = new();
    private readonly Engine _engine;

    public EngineTests()
    {
        foreach (var f in Directory.GetFiles(SettingsStore.Directory, "settings.json*")) File.Delete(f);
        _engine = new Engine(new HardwareLock(null), () => _bridge);
        Assert.True(_engine.ReloadSettings());
        Assert.True(_engine.Connect());
        _bridge.Calls.Clear();
    }

    [Fact]
    public void Disabling_the_charge_limit_is_sent_to_the_ec()
    {
        _engine.SetChargeLimit(true, 70, 80);
        Assert.Equal(new byte[] { 31, 1, 70, 80 }, _bridge.Ex4Requests(31).Last()[..4]);
        _engine.SetChargeLimit(false, 70, 80);
        Assert.Equal(new byte[] { 31, 0, 70, 80 }, _bridge.Ex4Requests(31).Last()[..4]);
        Assert.False(SettingsStore.Load().Settings!.Charge.Enabled);
    }

    [Fact]
    public void Invalid_charge_window_is_rejected() =>
        Assert.Throws<ArgumentException>(() => _engine.SetChargeLimit(true, 80, 70));

    [Fact]
    public void Custom_fan_mode_pushes_the_curve_and_records_the_mode()
    {
        _engine.SetFanMode("custom");
        Assert.Contains(_bridge.Calls, c => c.Command == 14);
        Assert.Equal((byte)FanMode.Custom, _bridge.Store[4 * 256 + 5]);
    }

    [Fact]
    public void Performance_mode_switch_reapplies_the_fan_curve()
    {
        _engine.SetFanMode("custom");
        _bridge.Calls.Clear();
        _engine.SetPerformanceMode(PowerModeKind.Quiet);
        Assert.Contains(_bridge.Calls, c => c.Command == 14);
        Assert.Equal("quiet", _engine.Settings.Performance.Mode);
    }

    [Fact]
    public void VerifyFan_restores_a_mode_changed_behind_our_back()
    {
        _engine.SetFanMode("custom");
        Assert.False(_engine.VerifyFan());
        _bridge.Store[4 * 256 + 5] = (byte)FanMode.Auto; // e.g. firmware reset on mode switch
        Assert.True(_engine.VerifyFan());
        Assert.Equal((byte)FanMode.Custom, _bridge.Store[4 * 256 + 5]);
    }

    [Fact]
    public void UpdateSettings_keeps_changes_written_by_the_other_process()
    {
        // simulate the App saving a new profile while the Agent holds an older snapshot
        var other = SettingsStore.Load().Settings!;
        other.Fan.Profiles.Add(new FanProfile { Name = "外部方案" });
        Thread.Sleep(20);
        SettingsStore.Save(other);

        _engine.SetChargeLimit(true, 60, 80);

        var disk = SettingsStore.Load().Settings!;
        Assert.Contains(disk.Fan.Profiles, p => p.Name == "外部方案");
        Assert.True(disk.Charge.Enabled);
        Assert.Contains(_engine.Settings.Fan.Profiles, p => p.Name == "外部方案");
    }

    [Fact]
    public void Light_brightness_is_clamped_and_persisted()
    {
        Assert.Equal(KeyboardLightService.MaxBrightness, _engine.SetLightBrightness(99));
        Assert.Equal(KeyboardLightService.MinBrightness, _engine.SetLightBrightness(-5));
        Assert.Equal(KeyboardLightService.MinBrightness, SettingsStore.Load().Settings!.Light.Brightness);
    }

    [Fact]
    public void Unknown_fan_mode_is_rejected() =>
        Assert.Throws<ArgumentException>(() => _engine.SetFanMode("warp"));

    [Fact]
    public void ApplyAll_survives_and_reports_ok_on_the_fake_hardware() =>
        Assert.True(_engine.ApplyAll());

    [Fact]
    public void Concurrent_actions_do_not_corrupt_settings()
    {
        Parallel.For(0, 40, i =>
        {
            if (i % 2 == 0) _engine.SetLightBrightness(1 + i % 4);
            else _engine.SetChargeLimit(true, 50 + i % 10, 90);
        });
        var r = SettingsStore.Load();
        Assert.Null(r.Error);
        Assert.NotNull(r.Settings);
    }
}
