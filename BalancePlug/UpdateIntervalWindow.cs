using System;
using System.Windows;
using System.Windows.Controls;

namespace BalancePlug;

internal sealed class UpdateIntervalWindow : SoftWindow
{
    public UpdateIntervalWindow(int seconds, Action<int> preview, Action commit) : base("自动更新间隔", 410, 260)
    {
        Body.Children.Add(new TextBlock { Text = "余额查询不会频繁请求，最短 15 秒，最长 60 分钟。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var row = new DockPanel(); var value = new TextBlock { Text = Format(seconds), HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(value, Dock.Right); row.Children.Add(value); row.Children.Add(new TextBlock { Text = "查询间隔" }); Body.Children.Add(row);
        var slider = new Slider { Minimum = 15, Maximum = 3600, Value = Math.Clamp(seconds, 15, 3600), TickFrequency = 15, IsSnapToTickEnabled = false, Margin = new Thickness(0, 8, 0, 14) };
        System.Windows.Automation.AutomationProperties.SetName(slider, "自动更新间隔");
        slider.ValueChanged += (_, _) => { var current = (int)Math.Round(slider.Value / 15) * 15; value.Text = Format(current); preview(current); };
        Body.Children.Add(slider);
        var preset = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var item in new[] { ("15 秒", 15), ("1 分钟", 60), ("5 分钟", 300) }) { var button = new Button { Content = item.Item1, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(9, 6, 9, 6) }; button.Click += (_, _) => slider.Value = item.Item2; preset.Children.Add(button); }
        Body.Children.Add(preset); var note = new TextBlock { Text = "关闭窗口后保留设置", FontSize = 11, Margin = new Thickness(0, 12, 0, 0) }; note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(note); Closed += (_, _) => commit();
    }
    private static string Format(int seconds) => seconds % 60 == 0 ? (seconds / 60 >= 60 ? $"{seconds / 3600} 小时" : $"{seconds / 60} 分钟") : $"{seconds} 秒";
}
