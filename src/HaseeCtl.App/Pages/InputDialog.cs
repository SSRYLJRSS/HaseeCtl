using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HaseeCtl.App.Pages;

/// <summary>Minimal modal text prompt (WPF has no built-in input box).</summary>
internal static class InputDialog
{
    public static string? Ask(DependencyObject owner, string title, string label, string initial = "")
    {
        var window = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (owner is Window w) window.Owner = w;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = initial, Padding = new Thickness(6, 4, 6, 4) };
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        string? result = null;
        var ok = new Button { Content = "确定", Width = 84, Padding = new Thickness(0, 5, 0, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 84, Padding = new Thickness(0, 5, 0, 5), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text.Trim(); window.DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
