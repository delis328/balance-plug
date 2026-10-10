using System.Windows;
using System.Windows.Controls;
using BalancePlug.Core;

namespace BalancePlug;

internal sealed class MascotNameWindow : SoftWindow
{
    public string? ResultName { get; private set; }
    public MascotNameWindow(string name) : base("吉祥物名称", 410, 260)
    {
        Body.Children.Add(new TextBlock { Text = "名称显示在吉祥物切换菜单中", Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = name, MaxLength = 40 };
        System.Windows.Automation.AutomationProperties.SetName(input, "吉祥物名称"); Body.Children.Add(input);
        var error = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); Body.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "确定", IsDefault = true };
        save.Click += (_, _) =>
        {
            try { ResultName = LocalStore.ValidateMascotName(input.Text); DialogResult = true; }
            catch (BalanceException ex) { error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); Body.Children.Add(buttons);
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
