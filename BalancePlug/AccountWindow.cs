using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BalancePlug.Core;

namespace BalancePlug;

internal sealed class AccountWindow : SoftWindow
{
    public AccountWindow(AppSettings settings, Func<string, string, string, Task> login) : base("API / 账户配置", 450, 480)
    {
        var panel = Body;
        panel.Children.Add(new TextBlock { Text = "Sub2API 账户总余额", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "密码仅用于本次登录；仅加密保存会话，不保存密码。", Margin = new Thickness(0, 8, 0, 12), FontSize = 12 });
        var site = Input(panel, "站点根地址（HTTPS）", settings.SiteUrl);
        var email = Input(panel, "邮箱", settings.Email);
        panel.Children.Add(new TextBlock { Text = "密码", Margin = new Thickness(0, 8, 0, 4) });
        var password = new PasswordBox { Padding = new Thickness(8), FontSize = 14 };
        panel.Children.Add(password);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var button = new Button { Content = "登录 / 测试连接", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(button); panel.Children.Add(status);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false; site.IsEnabled = email.IsEnabled = password.IsEnabled = false;
            status.Text = "正在登录并验证账户余额…";
            try
            {
                await login(site.Text, email.Text, password.Password);
                password.Clear(); status.Text = "连接成功，已加密保存本机会话。";
                Close();
            }
            catch (OperationCanceledException) { status.Text = "操作已取消"; }
            catch (Exception ex) when (ex is BalanceException or SessionExpiredException)
            { status.Text = ex.Message; }
            catch { status.Text = "无法完成登录或保存会话，请检查本地文件权限"; }
            finally
            {
                password.Clear(); button.IsEnabled = true; site.IsEnabled = email.IsEnabled = password.IsEnabled = true;
            }
        };
        Closed += (_, _) => password.Clear();
    }

    private static TextBox Input(StackPanel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var input = new TextBox { Text = value, Padding = new Thickness(8), FontSize = 14 };
        panel.Children.Add(input); return input;
    }
}

