using System;
using System.Windows;
using System.Windows.Controls;
using BalancePlug.Core;

namespace BalancePlug;

internal sealed class SizeWindow : SoftWindow
{
    public SizeWindow(AppSettings settings, Action<double, double> preview, Action commit) : base("气泡 / 吉祥物大小", 410, 310)
    {
        var bubble = AddScale("气泡大小", settings.BubbleScale);
        var mascot = AddScale("吉祥物大小", settings.MascotScale);
        bubble.ValueChanged += (_, _) => preview(bubble.Value / 100, mascot.Value / 100);
        mascot.ValueChanged += (_, _) => preview(bubble.Value / 100, mascot.Value / 100);
        var reset = new Button { Content = "恢复默认（各 100%）", Margin = new Thickness(0, 16, 0, 0) };
        reset.Click += (_, _) => { bubble.Value = 100; mascot.Value = 100; };
        Body.Children.Add(reset);
        var note = new TextBlock { Text = "实时预览 · 自动保存；屏幕空间不足时等比适配", FontSize = 11, Margin = new Thickness(0, 12, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(note);
        Closed += (_, _) => commit();
    }
    private Slider AddScale(string label, double scale)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var value = new TextBlock { Text = Math.Round(scale * 100) + "%", HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(value, Dock.Right); row.Children.Add(value); row.Children.Add(new TextBlock { Text = label }); Body.Children.Add(row);
        var slider = new Slider { Minimum = 50, Maximum = 200, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 10, Value = scale * 100, Margin = new Thickness(0, 6, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, _) => value.Text = Math.Round(slider.Value) + "%";
        Body.Children.Add(slider); return slider;
    }
}
