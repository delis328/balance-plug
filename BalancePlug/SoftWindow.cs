using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BalancePlug;

internal static class UiTheme
{
    public static void Apply(string theme)
    {
        var colors = theme == "Dark"
            ? new[] { "#1D2336", "#53658A", "#D3DEF7", "#A5B8DF", "#AFC5FF", "#303A54", "#252D43", "#FFB3C8" }
            : theme == "Light" ? new[] { "#F9FAFC", "#CDD4DE", "#3A4357", "#8491A5", "#687D99", "#EDF0F5", "#FFFFFF", "#C9758C" }
            : new[] { "#FFFFFF", "#BDCCE5", "#293D78", "#7183A2", "#506EB5", "#EDF3FF", "#F8FAFF", "#C76385" };
        var names = new[] { "SurfaceBrush", "EdgeBrush", "TextBrush", "MutedBrush", "AccentBrush", "HoverBrush", "InputBrush", "DangerBrush" };
        for (var i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze();
            Application.Current.Resources[names[i]] = brush;
        }
    }
}

internal class SoftWindow : Window
{
    protected internal StackPanel Body { get; } = new();
    public SoftWindow(string title, double width, double height)
    {
        Title = title; Width = width; Height = height; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var frame = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");
        var dock = new DockPanel(); frame.Child = dock; Content = frame;
        var header = new DockPanel { Margin = new Thickness(20, 12, 12, 0), LastChildFill = true };
        var close = new Button { Content = "×", Padding = new Thickness(10, 3, 10, 5), FontSize = 18, ToolTip = "关闭" };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭窗口");
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        Body.Margin = new Thickness(22, 18, 22, 22); dock.Children.Add(Body);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
}
