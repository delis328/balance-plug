using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BalancePlug.Core;

namespace BalancePlug;
internal sealed class SpendAlertWindow : SoftWindow
{
    public SpendAlertWindow(SpendAlertSettings current, Action<SpendAlertSettings> save) : base("今日花费提醒",480,630)
    {
        Body.Children.Add(new TextBlock { Text="北京时间 00:00—24:00 · 美元实际计费\n超过阈值首次提醒，之后每增加 $5 提醒一次，显示 10 秒。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12) });
        var enabled=new CheckBox { Content="开启今日花费提醒",IsChecked=current.Enabled,Margin=new Thickness(0,0,0,12) };Body.Children.Add(enabled);
        Body.Children.Add(new TextBlock {Text="每日阈值（美元）"});
        var threshold=new TextBox {Text=current.Threshold.ToString(CultureInfo.InvariantCulture),Margin=new Thickness(0,5,0,12)};Body.Children.Add(threshold);
        Body.Children.Add(new TextBlock {Text="提示文本（勾选多条后随机选取，可直接编辑）",FontSize=12});
        var rows=new StackPanel(); var scroll=new ScrollViewer {Content=rows,Height=190,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,8,0,8)};Body.Children.Add(scroll);
        void Add(SpendMessage entry)
        {
            var row=new DockPanel {Margin=new Thickness(0,0,0,6)};
            var check=new CheckBox {IsChecked=entry.Selected,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
            var remove=new Button {Content="×",Padding=new Thickness(7,4,7,4),Margin=new Thickness(6,0,0,0)};DockPanel.SetDock(remove,Dock.Right);remove.Click+=(_,_)=>rows.Children.Remove(row);row.Children.Add(remove);
            row.Children.Add(new TextBox {Text=entry.Text,MaxLength=100});rows.Children.Add(row);
        }
        foreach(var entry in current.Messages)Add(entry);
        var add=new Button {Content="添加提示文本"};add.Click+=(_,_)=>Add(new SpendMessage("",true));Body.Children.Add(add);
        var error=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};error.SetResourceReference(TextBlock.ForegroundProperty,"DangerBrush");Body.Children.Add(error);
        var confirm=new Button {Content="保存设置"};Body.Children.Add(confirm);
        confirm.Click+=(_,_)=>
        {
            if(!decimal.TryParse(threshold.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var value)||value<0||value>1000000){error.Text="阈值须为 0—1000000 的美元金额";return;}
            var entries=rows.Children.Cast<DockPanel>().Select(row=>new SpendMessage(((TextBox)row.Children[2]).Text.Trim(),((CheckBox)row.Children[0]).IsChecked==true)).Where(x=>x.Text.Length>0).ToList();
            if(enabled.IsChecked==true&&!entries.Any(x=>x.Selected)){error.Text="启用提醒时至少勾选一条非空提示文本";return;}
            try {save(new SpendAlertSettings {Enabled=enabled.IsChecked==true,Threshold=value,Messages=entries});Close();}
            catch {error.Text="无法保存，请检查本地目录权限";}
        };
    }
}
