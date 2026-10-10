using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BalancePlug;
using BalancePlug.Core;
using Forms = System.Windows.Forms;

internal static class UiSmoke
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    internal static object Field(object window,string name) => window.GetType().GetField(name,BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    internal static object? Invoke(object window,string name,params object[] args) => window.GetType().GetMethod(name,BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,args);
    private static void Check(bool ok,string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS UI " + name); }
    private static void Snapshot(string name,FrameworkElement visual)
    {
        visual.UpdateLayout();
        var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"outputs","ui-v1.1")); Directory.CreateDirectory(path);
        var bmp = new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(visual.ActualWidth)),Math.Max(1,(int)Math.Ceiling(visual.ActualHeight)),96,96,PixelFormats.Pbgra32);
        bmp.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(Path.Combine(path,name + ".png")); encoder.Save(file);
    }
    private static async Task ActivateItem(MenuItem item)
    {
        // WPF queues Click at render priority; wait for the real control pipeline, not just the toggle.
        typeof(MenuItem).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(item,null);
        await item.Dispatcher.InvokeAsync(() => { },DispatcherPriority.ContextIdle);
    }
    private static bool DecreaseOpen(object bubble) => (bool)bubble.GetType().GetProperty("IsOpen")!.GetValue(bubble)!;
    public static int Run(bool interactive = false)
    {
        var result = 1; var original = Environment.GetEnvironmentVariable("BALANCEPLUG_DATA_DIR");
        var directory = Path.Combine(Path.GetTempPath(),"BalancePlug-ui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BALANCEPLUG_DATA_DIR",directory);
        var thread = new Thread(() =>
        {
            var app = TestApplication.Create(); MainWindow? window = null;
            try
            {
                var handler = new UiHandler(); var api = new BalanceApi(handler);
                api.RestoreSession(new AccountSession("https://balance.test","test-access","test-refresh",DateTimeOffset.UtcNow.AddDays(1)));
                window = new MainWindow(api, () => true); app.MainWindow = window;
                if (interactive)
                {
                    result = 0; var host = InteractiveHost(window,handler); host.Show(); window.Owner = host; window.Show();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() =>
                    {
                        var settings = (AppSettings)Field(window,"settings");
                        var store = (LocalStore)Field(window,"store");
                        var a = store.ImportMascot(settings,TestAssets.Png(120,240,1),"形象 A.png");
                        store.ImportMascot(settings,TestAssets.Png(240,120,2),"形象 B.png");
                        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BalancePlug","mascot.png");
                        if (File.Exists(legacy)) store.ImportMascot(settings,File.ReadAllBytes(legacy),"已有自定义形象.png");
                        Invoke(window,"SelectMascot",settings.ActiveMascotId!);
                        PositionWithinHost(window);
                        Console.WriteLine("INTERACTIVE READY isolated=" + directory);
                    }));
                }
                else
                {
                    window.Show(); window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async () =>
                    {
                        try
                        {
                            Check(app.Windows.OfType<MainWindow>().Count()==1,"test host contains exactly one production floating window");
                            var display = (BalanceDisplay)Field(window,"display"); var delta = Field(window,"decrease");
                            Check(display.Amount == 12.5m && !DecreaseOpen(delta),"first query does not show decrease");
                            var time = display.UpdatedAt; await (Task)Invoke(window,"RefreshAsync")!;
                            Check(display.Amount == 12.5m && display.UpdatedAt == time && !DecreaseOpen(delta),"failure preserves amount and baseline");
                            await (Task)Invoke(window,"RefreshAsync")!;
                            Check(display.Amount == 10m && DecreaseOpen(delta),"recovery shows decrease from last success");
                            await Task.Delay(80);
                            var popup = (System.Windows.Controls.Primitives.Popup)Field(delta,"popup");
                            popup.Child.UpdateLayout();
                            var popupSource = (HwndSource)PresentationSource.FromVisual((Visual)popup.Child)!;
                            var styles = GetWindowLongPtr(popupSource.Handle,-20).ToInt64();
                            Check((styles & 0x20) != 0 && (styles & 0x08000000) != 0,"decrease popup does not intercept or activate, flags=" + styles.ToString("X"));
                            Snapshot("Decrease",(FrameworkElement)popup.Child);
                            var compact=(Grid)Field(delta,"card"); var label=(TextBlock)Field(delta,"text");
                            Check(compact.Children[0] is System.Windows.Shapes.Ellipse,"decrease background is a true ellipse");
                            Check(compact.ActualWidth<label.ActualWidth+45 && compact.ActualWidth<180,"decrease width fits amount rather than container");
                            var trajectory=(Canvas)popup.Child; var transform=(TranslateTransform)Field(delta,"rise");
                            var firstY=transform.Y; await Task.Delay(250); var secondY=transform.Y; await Task.Delay(250);
                            Check(transform.Y<secondY && secondY<firstY,"decrease moves continuously upward across frames");
                            Check(trajectory.Height>compact.Height && trajectory.Width>=compact.Width,"popup canvas reserves entire motion trajectory");
                            await Task.Delay(3400); Check(!DecreaseOpen(delta),"animation and popup clean up after two seconds");
                            var overlapping = (Task)Invoke(window,"RefreshAsync")!; await (Task)Invoke(window,"RefreshAsync")!; await overlapping;
                            Check(handler.Calls == 4,"overlapping refresh skipped");
                            Check(!DecreaseOpen(delta),"unchanged amount shows no decrease");
                            var handle = new WindowInteropHelper(window).Handle;
                            Check(window.IsVisible && window.Topmost && !window.ShowInTaskbar && window.AllowsTransparency,"transparent topmost window");
                            styles = GetWindowLongPtr(handle,-20).ToInt64();
                            Check((styles & 0x80000) != 0 && (styles & 0x08000000) != 0 && (styles & 0x80) != 0,"main native flags retained");
                            Invoke(window,"SetClickThrough",true); Check((GetWindowLongPtr(handle,-20).ToInt64() & 0x20) != 0,"main click-through enabled");
                            Invoke(window,"SetClickThrough",false); Check((GetWindowLongPtr(handle,-20).ToInt64() & 0x20) == 0,"main click-through disabled");
                            var settings = (AppSettings)Field(window,"settings");
                            display.Success(17.4m); Invoke(window,"RenderDisplay");
                            foreach (var theme in new[] {"Blue"})
                            {
                                settings.Theme = theme;
                                foreach (var b in new[] {.5,.8,1d,2d}) foreach (var m in new[] {.5,.8,1d,2d})
                                {
                                    settings.BubbleScale = b; settings.MascotScale = m; Invoke(window,"ApplyAppearance"); window.UpdateLayout();
                                    var scene = (System.Windows.Controls.Canvas)Field(window,"scene");
                                    var bubble = (Viewbox)Field(window,"bubbleGroup"); var mascot = (Viewbox)Field(window,"mascotGroup");
                                    Check(Math.Abs(bubble.Width-300*b)<.001 && Math.Abs(mascot.Width-225*m)<.001,theme + " independent B" + b + " M" + m);
                                    Check(Canvas.GetLeft(mascot)>=0 && Canvas.GetTop(mascot)+mascot.Height <= scene.Height+.001,"groups fit scene");
                                    Snapshot(theme + "-B" + (int)(b*100) + "-M" + (int)(m*100),(FrameworkElement)window.Content);
                                }
                                settings.BubbleScale = settings.MascotScale = 1; Invoke(window,"ApplyAppearance"); window.UpdateLayout();
                                Invoke(window,"OpenAccount"); var account = (Window)Field(window,"accountWindow"); account.UpdateLayout(); Snapshot(theme + "-Account",account); account.Close();
                                Invoke(window,"OpenSizes"); var sizes = (Window)Field(window,"sizeWindow"); sizes.UpdateLayout(); Snapshot(theme + "-Sizes",sizes); sizes.Close();
                                Invoke(window,"OpenInterval"); var interval=(Window)Field(window,"intervalWindow"); interval.UpdateLayout(); Snapshot(theme + "-Interval",interval); interval.Close();
                                Invoke(window,"OpenBubbleMotion"); var motion=(Window)Field(window,"bubbleMotionWindow"); motion.UpdateLayout(); Snapshot(theme + "-Motion",motion);
                                var editorType=motion.GetType().GetNestedType("PointEditor",BindingFlags.NonPublic)!;
                                Invoke(motion,"SetPathPoint",Enum.Parse(editorType,"Start"),new Point(42,108));
                                Invoke(motion,"SetPathPoint",Enum.Parse(editorType,"Target"),new Point(110,30));
                                var draft=(BubbleMotionSettings)Field(motion,"draft");
                                Check(draft.TargetY<draft.StartY && draft.StartX<draft.TargetX,"visible two-point editor writes ordered upward path");
                                var line=(System.Windows.Shapes.Line)Field(motion,"pathLine");
                                Check(line.Y2<line.Y1,"visible path line matches upward trajectory");
                                var modalBody=(StackPanel)motion.GetType().BaseType!.GetProperty("Body",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(motion)!;
                                Check(modalBody.ActualHeight>=modalBody.DesiredSize.Height-modalBody.Margin.Top-modalBody.Margin.Bottom,"motion controls fit dialog client area");
                                motion.Close();
                                Invoke(window,"OpenMenu"); await Task.Delay(75); var menu = (ContextMenu)Field(window,"openMenu");
                                Check(menu.IsOpen && menu.Items.Count==12,"shared menu opens"); Snapshot(theme + "-Menu",menu);
                                Invoke(window,"CloseMenu");
                            }
                            settings.Theme = "Blue"; Invoke(window,"ApplyAppearance");
                            var store = (LocalStore)Field(window,"store");
                            var first = store.ImportMascot(settings,TestAssets.Png(240,80,1),"Landscape.png");
                            Invoke(window,"SelectMascot",first.Id); Check(((Image)Field(window,"mascot")).Source is BitmapImage,"library selection loads static image");
                            var second = store.ImportMascot(settings,TestAssets.Png(80,240,2),"Portrait.png");
                            Invoke(window,"SelectMascot",second.Id); Snapshot("Portrait",(FrameworkElement)window.Content);
                            Invoke(window,"SelectMascot",first.Id); Snapshot("Landscape",(FrameworkElement)window.Content);
                            Invoke(window,"SelectMascot",new object[] { null! }); Check(settings.Mascots.Count==2 && settings.ActiveMascotId is null,"default selection retains library");
                            Invoke(window,"CloseMenu"); await Task.Delay(120); Invoke(window,"OpenMenu"); await Task.Delay(120); var imagesMenu = (MenuItem)((ContextMenu)Field(window,"openMenu")).Items[5];
                            Check(imagesMenu.Items.Count==4,"all imported images are reusable menu options");
                            imagesMenu.IsSubmenuOpen = true; await Task.Delay(30);
                            await ActivateItem((MenuItem)imagesMenu.Items[1]);
                            var currentMenu = (ContextMenu)Field(window,"openMenu");
                            Check(currentMenu.IsOpen && settings.ActiveMascotId==first.Id,"image control activation stays open and switches image");
                            imagesMenu.IsSubmenuOpen = false;
                            var through = (MenuItem)currentMenu.Items[7];
                            await ActivateItem(through);
                            Check(currentMenu.IsOpen && through.IsChecked && settings.ClickThrough,"through toggle stays open and reflects native state");
                            await ActivateItem(through);
                            Check(currentMenu.IsOpen && !through.IsChecked && !settings.ClickThrough,"through can be switched back without reopening menu");
                            var topmost=(MenuItem)currentMenu.Items[9];
                            await ActivateItem(topmost);
                            Check(currentMenu.IsOpen && !topmost.IsChecked && !settings.Topmost && !window.Topmost,"topmost off stays open and reflects saved and window state");
                            Check((GetWindowLongPtr(handle,-20).ToInt64() & 0x8)==0,"native topmost flag cleared");
                            Check(!store.LoadSettings(out _).Topmost,"topmost off persists");
                            await ActivateItem(topmost);
                            Check(currentMenu.IsOpen && topmost.IsChecked && settings.Topmost && window.Topmost,"topmost on stays open and can be restored");
                            Check((GetWindowLongPtr(handle,-20).ToInt64() & 0x8)!=0,"native topmost flag restored");
                            store.RenameMascot(settings,first.Id,"测试鲸鱼娘");
                            Invoke(window,"CloseMenu"); Invoke(window,"OpenMenu"); await Task.Delay(75);
                            currentMenu=(ContextMenu)Field(window,"openMenu"); imagesMenu=(MenuItem)currentMenu.Items[5];
                            Check(((TextBlock)((MenuItem)imagesMenu.Items[1]).Header).Text=="测试鲸鱼娘","renamed image appears in shared menu");
                            Check(imagesMenu.Items.Count==5 && ((TextBlock)((MenuItem)imagesMenu.Items[3]).Header).Text=="重命名当前形象…","current local image exposes rename action");
                            await ActivateItem((MenuItem)currentMenu.Items[3]);
                            Check(!currentMenu.IsOpen && Field(window,"sizeWindow") is Window,"opening settings closes menu");
                            ((Window)Field(window,"sizeWindow")).Close();
                            Invoke(window,"CloseMenu");
                            var nameType=typeof(MainWindow).Assembly.GetType("BalancePlug.MascotNameWindow")!;
                            var naming=(Window)Activator.CreateInstance(nameType,new object[] {"默认文件名"})!; naming.Owner=window;
                            var body=(StackPanel)typeof(MainWindow).Assembly.GetType("BalancePlug.SoftWindow")!.GetProperty("Body",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(naming)!;
                            var input=(TextBox)body.Children[1]; var buttons=(StackPanel)body.Children[3];
                            Exception? modalFailure=null;
                            _ = naming.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() =>
                            {
                                try
                                {
                                    Snapshot("Mascot-Name",naming);
                                    input.Text="   "; ((Button)buttons.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                    Check(naming.IsVisible && ((TextBlock)body.Children[2]).Text.Length>0,"blank naming stays open with validation error");
                                    ((Button)buttons.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                }
                                catch(Exception ex){modalFailure=ex;naming.Close();}
                            }));
                            naming.ShowDialog(); if(modalFailure is not null)throw modalFailure;
                            Check(nameType.GetProperty("ResultName")!.GetValue(naming) is null,"cancel naming does not return a selection name");
                            naming=(Window)Activator.CreateInstance(nameType,new object[] {"旧名称"})!; naming.Owner=window;
                            body=(StackPanel)typeof(MainWindow).Assembly.GetType("BalancePlug.SoftWindow")!.GetProperty("Body",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(naming)!;
                            input=(TextBox)body.Children[1]; buttons=(StackPanel)body.Children[3];
                            _ = naming.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() =>
                            {
                                input.Text="  鲸鱼娘.v3  "; ((Button)buttons.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }));
                            Check(naming.ShowDialog()==true && (string?)nameType.GetProperty("ResultName")!.GetValue(naming)=="鲸鱼娘.v3","confirmed name is trimmed and preserves punctuation");
                            var bubbleTarget = (Viewbox)Field(window,"bubbleGroup");
                            var staticBubble = delta.GetType().GetConstructor(new[] {typeof(UIElement),typeof(Func<bool>)})!.Invoke(new object[] {bubbleTarget,new Func<bool>(() => false)});
                            staticBubble.GetType().GetMethod("Show")!.Invoke(staticBubble,new object[] {.0000001m, null!});
                            Check(DecreaseOpen(staticBubble) && !((System.Windows.Media.TranslateTransform)Field(staticBubble,"rise")).HasAnimatedProperties,"reduced-motion mode shows static bubble without clocks");
                            await Task.Delay(3400); Check(!DecreaseOpen(staticBubble),"reduced-motion bubble still expires");
                            ((IDisposable)staticBubble).Dispose();
                            delta.GetType().GetMethod("Show")!.Invoke(delta,new object[] {.01m, null!}); await Task.Delay(250);
                            delta.GetType().GetMethod("Show")!.Invoke(delta,new object[] {.02m, null!}); await Task.Delay(250);
                            Check(DecreaseOpen(delta) && (string)delta.GetType().GetProperty("Text")!.GetValue(delta)! == "-$0.02","new decrease replaces old and restarts expiry");
                            await Task.Delay(3400); Check(!DecreaseOpen(delta),"replacement bubble cleans up its timer");
                            var work = Forms.Screen.PrimaryScreen!.WorkingArea; var dpi = VisualTreeHelper.GetDpi(window);
                            var rightX = work.Right-(int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX);
                            DesktopPosition(handle,rightX,work.Top+60);
                            delta.GetType().GetMethod("Show")!.Invoke(delta,new object[] {.005m, null!}); await Task.Delay(80);
                            var edgePopup = (System.Windows.Controls.Primitives.Popup)Field(delta,"popup");
                            var edgeElement = (FrameworkElement)edgePopup.Child; var origin = edgeElement.PointToScreen(new Point(0,0));
                            Check(origin.X>=work.Left && origin.X+edgeElement.ActualWidth*dpi.DpiScaleX<=work.Right+1,"right-edge decrease stays within work area");
                            delta.GetType().GetMethod("Hide")!.Invoke(delta,null);
                            Invoke(window,"OpenSpendAlerts");var spendWindow=(Window)Field(window,"spendAlertWindow");spendWindow.UpdateLayout();Snapshot("Daily-Spend-Settings",spendWindow);spendWindow.Close();
                            settings.SpendAlerts=new SpendAlertSettings{Enabled=true,Threshold=10,Messages=new(){new("预算测试提示",true),new("不应选择",false)}};
                            await (Task)Invoke(window,"RefreshAsync")!;
                            typeof(MainWindow).GetField("showDaily",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);Invoke(window,"RenderDisplay");
                            Check(((Viewbox)Field(window,"contentBox")).Child==Field(window,"reminder") && ((TextBlock)Field(window,"reminder")).Text=="预算测试提示","budget message owns entire shared bubble");
                            Snapshot("Full-Reminder",(FrameworkElement)window.Content);
                            Check(((TextBlock)Field(window,"reminder")).FontSize>=24,"short reminder uses large readable text");
                            await Task.Delay(10500);
                            Check(((TextBlock)Field(window,"title")).Text=="今日花费" && ((TextBlock)Field(window,"amount")).Text=="-$12.34" && ((Viewbox)Field(window,"contentBox")).Child==Field(window,"normalContent"),"ten-second alert restores full daily-spend interface");
                            Check(((TextBlock)Field(window,"status")).Visibility==Visibility.Collapsed,"successful view hides already-updated status");
                            Check(((StackPanel)Field(window,"normalContent")).Children.Count==2,"timestamp is separate from main content");
                            var timestamp=(TextBlock)Field(window,"updated");
                            Check(Canvas.GetTop(timestamp)==130 && timestamp.Height==22 && timestamp.Text.StartsWith("更新 "),"timestamp has fixed independent row and valid success time");
                            var timeTop=timestamp.TranslatePoint(new Point(0,0),(UIElement)window.Content).Y;
                            var amountControl=(TextBlock)Field(window,"amount");amountControl.Text="-$1234567890.12";window.UpdateLayout();
                            Check(Math.Abs(timestamp.TranslatePoint(new Point(0,0),(UIElement)window.Content).Y-timeTop)<.01,"long amount cannot move timestamp");
                            Invoke(window,"RenderDisplay");
                            Check(((TextBlock)Field(window,"amount")).Foreground is SolidColorBrush,"daily amount uses contextual warm color");
                            Snapshot("Full-Daily",(FrameworkElement)window.Content);
                            typeof(MainWindow).GetField("showDaily",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,false);Invoke(window,"RenderDisplay");
                            Check(((TextBlock)Field(window,"title")).Text=="账户余额" && ((TextBlock)Field(window,"amount")).Text==((BalanceDisplay)Field(window,"display")).AmountText,"balance view restored without losing account amount");
                            Snapshot("Full-Balance",(FrameworkElement)window.Content);
                            await (Task)Invoke(window,"RefreshAsync")!;Check(Field(window,"alertText") is null,"same daily node is not re-alerted");
                            ((BalanceDisplay)Field(window,"display")).Fail("无法连接");Invoke(window,"RenderDisplay");
                            Check(((Viewbox)Field(window,"contentBox")).Child==Field(window,"reminder") && ((TextBlock)Field(window,"reminder")).Text.Contains("无法连接"),"connection error owns whole bubble instead of small status row");
                            Snapshot("Full-Error",(FrameworkElement)window.Content);
                            ((BalanceDisplay)Field(window,"display")).Success(10m);Invoke(window,"RenderDisplay");
                            typeof(MainWindow).GetField("alertText",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,new string('提',100));Invoke(window,"RenderDisplay");
                            var fullText=(TextBlock)Field(window,"reminder");fullText.Measure(new Size(224,double.PositiveInfinity));
                            Check(fullText.DesiredSize.Height<=110 && fullText.FontSize>=14,"long reminder height=" + fullText.DesiredSize.Height + " font=" + fullText.FontSize + " line=" + fullText.LineHeight);
                            Snapshot("Full-Long-Reminder",(FrameworkElement)window.Content);
                            typeof(MainWindow).GetField("alertText",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,null);Invoke(window,"RenderDisplay");
                            Check(!Directory.GetFiles(directory,"*.tmp",SearchOption.AllDirectories).Any(),"atomic writes leave no temporary files");
                            result = 0;
                        }
                        catch (Exception ex) { Console.WriteLine("FAIL UI " + ex); }
                        finally { window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle); }
                    }));
                }
                Dispatcher.Run();
            }
            catch (Exception ex) { Console.WriteLine("FAIL UI initialization " + ex); result = 1; window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (interactive) thread.Join();
        else if (!thread.Join(TimeSpan.FromSeconds(60))) { Console.WriteLine("FAIL UI timeout"); result = 1; }
        Environment.SetEnvironmentVariable("BALANCEPLUG_DATA_DIR",original);
        if (!thread.IsAlive && Directory.Exists(directory))
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.Combine(Path.GetTempPath(),"BalancePlug-ui-"),StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
            Directory.Delete(resolved,true);
        }
        return result;
    }
    private static Window? testHost;
    private static Window InteractiveHost(MainWindow window,UiHandler handler)
    {
        var host = new Window { Title = "Balance Plug 1.1 · 隔离交互测试", Width = 760,Height = 660,Left = 70,Top = 50,WindowStartupLocation = WindowStartupLocation.Manual, Background = Brushes.White };
        testHost = host;
        var root = new DockPanel { Margin = new Thickness(18) }; host.Content = root;
        var heading = new TextBlock { Text = "模拟余额 · 不会访问真实账户或修改正常配置",FontSize = 16,Margin = new Thickness(0,0,0,16) };
        DockPanel.SetDock(heading,Dock.Top); root.Children.Add(heading);
        var panel = new StackPanel { Width = 315,HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(panel);
        void Button(string title,Action run) { var b = new Button { Content = title,Margin = new Thickness(0,0,0,10) }; b.Click += (_,_) => run(); panel.Children.Add(b); }
        Button("从托盘右键入口打开菜单",() => Invoke(window,"OnTrayMouseUp",null!,new Forms.MouseEventArgs(Forms.MouseButtons.Right,1,0,0,0)));
        Button("手动刷新模拟余额",() => { _ = (Task)Invoke(window,"RefreshAsync")!; });
        Button("模拟下一次网络失败",() => handler.FailNext=true);
        Button("打开独立大小设置",() => Invoke(window,"OpenSizes"));
        Button("恢复测试窗位置",() => PositionWithinHost(window));
        Button("外部区域（用于关闭菜单）",() => Console.WriteLine("NATIVE outside clicked"));
        Button("关闭测试（不关闭旧版）",() => host.Close());
        panel.Children.Add(new TextBlock { Text = "左侧为真实 WPF 悬浮窗，可直接右键。\n通过菜单测试主题、形象、穿透和连续选择。\n本测试不操作开机自启或真实登录。",TextWrapping = TextWrapping.Wrap,Margin = new Thickness(0,12,0,0) });
        host.Closing += (_,e) => { if(window.IsVisible) { e.Cancel=true; window.Close(); } else Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle); };
        return host;
    }
    private static void PositionWithinHost(MainWindow window)
    {
        if (testHost is null) return;
        var point = testHost.PointToScreen(new Point(32,85)); var h = new WindowInteropHelper(window).Handle;
        DesktopPosition(h,(int)point.X,(int)point.Y);
    }
    [DllImport("user32.dll",EntryPoint="SetWindowPos")] private static extern bool DesktopPositionNative(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
    private static void DesktopPosition(IntPtr h,int x,int y) => DesktopPositionNative(h,IntPtr.Zero,x,y,0,0,0x1|0x4|0x10);
}

internal sealed class UiHandler : HttpMessageHandler
{
    private int calls;
    public int Calls => calls;
    public bool FailNext { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(request.RequestUri!.AbsolutePath.EndsWith("/usage/stats")) return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent("{\"code\":0,\"data\":{\"total_actual_cost\":12.34}}")};
        calls++;
        if (calls == 2 || FailNext) { FailNext=false; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
        if (calls == 4) await Task.Delay(100,ct);
        var amount = calls == 1 ? 12.5m : calls <=4 ? 10m : 10m-(calls-4)*.05m;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{\"balance\":"+amount.ToString(System.Globalization.CultureInfo.InvariantCulture)+"}}") };
    }
}
