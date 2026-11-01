using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Features;

namespace HaseeCtl.App.Pages;

public partial class LightPage : UserControl
{
    private UiState? _state;
    private bool _loading;
    private (byte R, byte G, byte B) _left = (0, 0, 200);
    private (byte R, byte G, byte B) _mid = (0, 0, 200);
    private (byte R, byte G, byte B) _right = (0, 0, 200);

    /// <summary>Dragging the slider fires ValueChanged for every step; write the EC and the file once it settles.</summary>
    private readonly DispatcherTimer _brightnessCommit = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private static readonly (LightEffect Effect, string Name)[] Effects =
    [
        (LightEffect.CustomZones, "三区自定义"),
        (LightEffect.AllSame, "全键盘同色"),
        (LightEffect.Breath, "呼吸"),
        (LightEffect.Random, "随机"),
        (LightEffect.Flash, "闪烁"),
        (LightEffect.Tempo, "节奏"),
        (LightEffect.Dance, "舞动"),
        (LightEffect.Wave, "波浪"),
        (LightEffect.Cycle, "循环"),
    ];

    public LightPage()
    {
        InitializeComponent();
        BrightnessSlider.Minimum = KeyboardLightService.MinBrightness;
        BrightnessSlider.Maximum = KeyboardLightService.MaxBrightness;
        _brightnessCommit.Tick += (_, _) => CommitBrightness();
        Loaded += OnLoaded;
        Unloaded += (_, _) => { if (_brightnessCommit.IsEnabled) CommitBrightness(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_state is not null) return;
        _state = DataContext as UiState;
        if (_state is null) return;
        _loading = true;

        var light = _state.Engine.Light;
        EffectBox.ItemsSource = Effects.Select(x => x.Name).ToList();

        if (light is not null)
        {
            try
            {
                var (enabled, brightness, left, mid, right, effect) = _state.Engine.WithHardware(() =>
                    (light.Enabled, light.Brightness, light.Left, light.Mid, light.Right, light.Effect));
                EnableCheck.IsChecked = enabled;
                BrightnessSlider.Value = Math.Clamp(brightness, KeyboardLightService.MinBrightness, KeyboardLightService.MaxBrightness);
                _left = left; _mid = mid; _right = right;
                int idx = Array.FindIndex(Effects, x => x.Effect == effect);
                EffectBox.SelectedIndex = idx < 0 ? 0 : idx;
            }
            catch (Exception ex) { Log.Error("读取键盘灯失败", ex); }
        }
        else
        {
            EffectBox.SelectedIndex = 0;
            EnableCheck.IsChecked = _state.Settings.Light.Enabled;
            BrightnessSlider.Value = _state.Settings.Light.Brightness;
        }

        BrightnessText.Text = ((int)BrightnessSlider.Value).ToString();
        BuildPresets();
        UpdateSwatches();
        _loading = false;
    }

    private void BuildPresets()
    {
        PresetPanel.Children.Clear();
        foreach (var (r, g, b) in KeyboardLightService.Presets)
        {
            var btn = new Button
            {
                Width = 30,
                Height = 24,
                Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderThickness = new Thickness(1),
                ToolTip = $"#{r:X2}{g:X2}{b:X2}",
                Tag = (r, g, b),
            };
            btn.Click += OnPresetClick;
            PresetPanel.Children.Add(btn);
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<byte, byte, byte> rgb }) return;
        if (ZoneMid.IsChecked == true) _mid = rgb;
        else if (ZoneRight.IsChecked == true) _right = rgb;
        else if (ZoneAll.IsChecked == true) { _left = rgb; _mid = rgb; _right = rgb; }
        else _left = rgb;
        UpdateSwatches();
        ApplyColorOnly();
    }

    private void UpdateSwatches()
    {
        LeftSwatch.Background = new SolidColorBrush(Color.FromRgb(_left.R, _left.G, _left.B));
        MidSwatch.Background = new SolidColorBrush(Color.FromRgb(_mid.R, _mid.G, _mid.B));
        RightSwatch.Background = new SolidColorBrush(Color.FromRgb(_right.R, _right.G, _right.B));
    }

    private void ApplyColorOnly()
    {
        if (_state is null || _loading) return;
        try
        {
            _state.Engine.SetLightColors(_left, _mid, _right);
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnApplyColors(object sender, RoutedEventArgs e) => ApplyColorOnly();

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _state is null) return;
        try
        {
            _state.Engine.SetLightEnabled(EnableCheck.IsChecked == true);
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is not null) BrightnessText.Text = ((int)e.NewValue).ToString();
        if (_loading || _state is null) return;
        _brightnessCommit.Stop();
        _brightnessCommit.Start();
    }

    private void CommitBrightness()
    {
        _brightnessCommit.Stop();
        if (_state is null) return;
        try { _state.Engine.SetLightBrightness((int)Math.Round(BrightnessSlider.Value)); }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _state is null || EffectBox.SelectedIndex < 0) return;
        var effect = Effects[EffectBox.SelectedIndex].Effect;
        try
        {
            if (effect == LightEffect.CustomZones) _state.Engine.SetLightColors(_left, _mid, _right);
            _state.Engine.SetLightEffect(effect);
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "HaseeCtl", MessageBoxButton.OK, MessageBoxImage.Warning);
}
