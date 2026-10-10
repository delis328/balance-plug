using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BalancePlug.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BalancePlug;

public sealed class MainWindow : Window
{
    private readonly LocalStore store = new(Environment.GetEnvironmentVariable("BALANCEPLUG_DATA_DIR"));
    private readonly BalanceApi api;
    private readonly BalanceDisplay display = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer sizeSave = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly AppSettings settings;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly Canvas scene = new();
    private readonly Viewbox bubbleGroup;
    private readonly Viewbox mascotGroup;
    private readonly Ellipse bubble;
    private readonly Ellipse thought1;
    private readonly Ellipse thought2;
    private readonly TextBlock title;
    private readonly TextBlock amount;
    private readonly TextBlock updated;
    private readonly TextBlock status;
    private readonly StackPanel normalContent;
    private readonly TextBlock reminder;
    private readonly Viewbox amountBox;
    private readonly Viewbox contentBox;
    private readonly Image mascot;
    private readonly TextBlock placeholder;
    private readonly DrawingImage defaultMascot;
    private readonly DecreaseBubble decrease;
    private IntPtr handle;
    private HwndSource? source;
    private AccountWindow? accountWindow;
    private SizeWindow? sizeWindow;
    private BubbleMotionWindow? bubbleMotionWindow;
    private Window? intervalWindow;
    private SpendAlertWindow? spendAlertWindow;
    private decimal? dailyAmount;
    private DateOnly? dailyDay;
    private DateTimeOffset? dailyUpdatedAt;
    private string? dailyError;
    private bool showDaily;
    private string? alertText;
    private readonly DispatcherTimer dayBoundary = new();
    private readonly DispatcherTimer alertExpiry = new() { Interval=TimeSpan.FromSeconds(10) };
    private ContextMenu? openMenu;
    private Window? menuHost;
    private readonly List<(MenuItem Item, Func<bool> Check)> menuChecks = new();
    private Point? dragOrigin;
    private bool closing;
    private bool closeRequested;
    private bool closeAllowed;
    private bool sessionSaveFailed;
    private string? startupNotice;

    public MainWindow(BalanceApi? client = null, Func<bool>? animationsEnabled = null)
    {
        api = client ?? new BalanceApi();
        settings = store.LoadSettings(out var badSettings, deferMigration: true);
        timer.Interval = TimeSpan.FromSeconds(settings.UpdateIntervalSeconds);
        var session = store.LoadSession(out var badSession);
        if (session is not null)
        {
            try { api.RestoreSession(session); settings.SiteUrl = session.SiteUrl; }
            catch (BalanceException) { badSession = true; }
        }
        startupNotice = store.MigrationNotice;
        if (badSettings) startupNotice = "配置损坏，已恢复默认设置";
        if (badSession) startupNotice = "会话无法解密，请重新登录";
        api.SessionChanged += SessionChanged;
        Title = "Balance Plug · 账户余额";
        AllowsTransparency = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = settings.Topmost; Background = Brushes.Transparent;
        Content = new Viewbox { Child = scene, Stretch = Stretch.Uniform };
        var bubbleCanvas = new Canvas { Width = 300, Height = 210 };
        bubbleGroup = new Viewbox { Child = bubbleCanvas, Stretch = Stretch.Uniform }; scene.Children.Add(bubbleGroup);
        bubble = new Ellipse { Width = 292, Height = 158, StrokeThickness = 5 }; Add(bubbleCanvas, bubble, 4, 4);
        normalContent = new StackPanel { Width = 224 };
        title = new TextBlock { FontSize = 22, Height = 32, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        amount = new TextBlock { Text = "—", FontSize = 46, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center };
        amountBox = new Viewbox { Child = amount, Height = 56, MaxWidth = 224, StretchDirection = StretchDirection.DownOnly };
        updated = new TextBlock { FontSize = 15, Width = 180, Height = 22, TextAlignment = TextAlignment.Center };
        status = new TextBlock { FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,3,0,0) };
        normalContent.Children.Add(title); normalContent.Children.Add(amountBox);
        reminder = new TextBlock { Width = 224, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        reminder.SetResourceReference(TextBlock.ForegroundProperty,"DangerBrush");
        contentBox = new Viewbox { Width = 224, Height = 110, Child = normalContent, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        Add(bubbleCanvas,contentBox,42,18);
        Add(bubbleCanvas,updated,64,130);
        thought1 = new Ellipse { Width = 27, Height = 17, StrokeThickness = 4 };
        thought2 = new Ellipse { Width = 16, Height = 11, StrokeThickness = 3 };
        Add(bubbleCanvas, thought1, 101, 171); Add(bubbleCanvas, thought2, 132, 199);
        var mascotCanvas = new Canvas { Width = 225, Height = 162 };
        mascotGroup = new Viewbox { Child = mascotCanvas, Stretch = Stretch.Uniform }; scene.Children.Add(mascotGroup);
        defaultMascot = CreateWhale(); mascot = new Image { Source = defaultMascot, Width = 225, Height = 150, Stretch = Stretch.Uniform };
        Add(mascotCanvas, mascot, 0, 0);
        placeholder = new TextBlock { Text = "鲸鱼占位 · 可右键换图", FontSize = 10 };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Add(mascotCanvas, placeholder, 46, 147);
        decrease = new DecreaseBubble(bubbleGroup, animationsEnabled);
        ApplyAppearance(); if (!store.MigrationPending) LoadMascot(startup: true); RenderDisplay();
        trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        tray = new Forms.NotifyIcon { Text = "Balance Plug · 账户余额", Icon = trayIcon, Visible = true };
        tray.MouseUp += OnTrayMouseUp;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { SetClickThrough(false); RestorePosition(); });
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
            try { DesktopNative.SetClickThrough(handle, settings.ClickThrough); }
            catch (Win32Exception) { settings.ClickThrough = false; startupNotice = "无法启用鼠标穿透"; }
        };
        Loaded += (_, _) =>
        {
            RestorePosition(useSaved: true);
            // Draw the window before first-run image migration flushes files to disk.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                if (closing || closeRequested) return;
                store.CompleteMigration(settings); startupNotice ??= store.MigrationNotice; LoadMascot(startup: true);
                if (startupNotice is not null) RenderDisplay();
                if (api.Session is not null) { timer.Start(); await RefreshAsync(); }
            }));
        };
        MouseLeftButtonDown += (_,e)=> {if(!settings.ClickThrough && e.ChangedButton==MouseButton.Left)dragOrigin=e.GetPosition(this);};
        MouseLeftButtonUp += (_,_)=>dragOrigin=null;
        MouseMove += (_,e)=>
        {
            if(settings.ClickThrough || dragOrigin is null || e.LeftButton!=MouseButtonState.Pressed)return;
            var point=e.GetPosition(this);var origin=dragOrigin.Value;
            if(Math.Abs(point.X-origin.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y-origin.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            dragOrigin=null;try{DragMove();ClampPosition();SavePosition();}catch(InvalidOperationException){}
        };
        MouseRightButtonUp += (_, e) => { if (!settings.ClickThrough) { OpenMenu(); e.Handled = true; } };
        alertExpiry.Tick += (_,_) => { alertExpiry.Stop(); alertText=null; RenderDisplay(); };
        mascot.MouseLeftButtonDown += (_,e) => { if(e.ClickCount==2 && !settings.ClickThrough){dragOrigin=null;showDaily=!showDaily;RenderDisplay();e.Handled=true;} };
        dayBoundary.Tick+=(_,_)=> {ConfigureDayBoundary();if(dailyDay.HasValue && dailyDay!=DailySpend.BeijingDay(DateTimeOffset.UtcNow)){dailyAmount=null;dailyDay=null;dailyUpdatedAt=null;dailyError=null;alertText=null;alertExpiry.Stop();RenderDisplay();_ = RefreshAsync();}};
        ConfigureDayBoundary();
        timer.Tick += async (_, _) => await RefreshAsync();
        sizeSave.Tick += (_, _) => CommitSizes();
        SystemEvents.PowerModeChanged += PowerModeChanged;
        Closing += OnClosing;
    }

    private static void Add(Canvas canvas, UIElement item, double left, double top)
    { Canvas.SetLeft(item, left); Canvas.SetTop(item, top); canvas.Children.Add(item); }
    private void SessionChanged(AccountSession? session)
    {
        try { store.SaveSession(session); sessionSaveFailed = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { sessionSaveFailed = true; }
    }
    private async Task RefreshAsync()
    {
        if (closing || closeRequested || api.Session is null || !await queryGate.WaitAsync(0)) return;
        try
        {
            // Keep the current view unchanged while a request is in flight.
            var balance = await api.GetBalanceAsync(shutdown.Token);
            if (closeRequested || closing) return;
            var delta = display.Success(balance);
            if (delta.HasValue) decrease.Show(delta.Value, settings.BubbleMotion);
            await RefreshDailyAsync();
        }
        catch (SessionExpiredException) { timer.Stop(); display.Expire(); }
        catch (BalanceException ex) { display.Fail(ex.Message); }
        catch (OperationCanceledException) { }
        catch { display.Fail("无法完成查询，请稍后重试"); }
        finally { queryGate.Release(); if (!closing && !closeRequested) RenderDisplay(); }
    }
    private void ConfigureDayBoundary()
    {
        var now=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var next=new DateTimeOffset(now.Date.AddDays(1),TimeSpan.FromHours(8));
        dayBoundary.Stop();dayBoundary.Interval=next-now+TimeSpan.FromMilliseconds(100);dayBoundary.Start();
    }
    private async Task RefreshDailyAsync()
    {
        var day=DailySpend.BeijingDay(DateTimeOffset.UtcNow);
        try
        {
            var value=await api.GetDailySpendAsync(day,shutdown.Token);
            if(closing||closeRequested||day!=DailySpend.BeijingDay(DateTimeOffset.UtcNow))return;
            dailyDay=day;dailyAmount=value;dailyUpdatedAt=DateTimeOffset.Now;dailyError=null;
            var account=api.Session!.SiteUrl + "|" + settings.Email.Trim().ToLowerInvariant();
            var progress=settings.SpendProgress;
            var previous=(progress.Account,progress.Day,progress.Threshold,progress.LastNode);
            var message=DailySpend.Evaluate(value,day,account,settings.SpendAlerts,progress);
            if(previous!=(progress.Account,progress.Day,progress.Threshold,progress.LastNode))
            {
                try {store.SaveSettings(settings);} catch {progress.Account=previous.Account;progress.Day=previous.Day;progress.Threshold=previous.Threshold;progress.LastNode=previous.LastNode;dailyError="提醒记录未能保存";return;}
            }
            if(message is not null){alertText=message;alertExpiry.Stop();alertExpiry.Start();}
        }
        catch(SessionExpiredException){throw;}
        catch(BalanceException ex){dailyError=ex.Message;}
    }
    private void OpenSpendAlerts()
    {
        CloseMenu();if(spendAlertWindow is not null){spendAlertWindow.Activate();return;}
        spendAlertWindow=new SpendAlertWindow(settings.SpendAlerts,value=>
        {
            var old=settings.SpendAlerts;settings.SpendAlerts=value;
            try{store.SaveSettings(settings);}catch{settings.SpendAlerts=old;throw;}
            alertText=null;alertExpiry.Stop();RenderDisplay();_ = RefreshAsync();
        }){Owner=this};
        spendAlertWindow.Closed+=(_,_)=>spendAlertWindow=null;spendAlertWindow.Show();spendAlertWindow.Activate();
    }
    private void RenderDisplay()
    {
        if(dailyDay.HasValue && dailyDay!=DailySpend.BeijingDay(DateTimeOffset.UtcNow))
        {dailyAmount=null;dailyUpdatedAt=null;dailyError=null;alertText=null;alertExpiry.Stop();}
        title.Text = showDaily ? "今日花费" : "账户余额";
        amount.Text = showDaily ? (dailyAmount.HasValue ? "-" + BalanceApi.FormatBalance(dailyAmount.Value) : "—") : display.AmountText;
        var overBudget=showDaily && dailyAmount.HasValue && settings.SpendAlerts.Enabled && dailyAmount.Value>settings.SpendAlerts.Threshold;
        title.Foreground=new SolidColorBrush(showDaily ? Color.FromRgb(180,83,119) : Color.FromRgb(79,106,173));
        amount.Foreground=showDaily
            ? new SolidColorBrush(overBudget ? Color.FromRgb(181,48,80) : Color.FromRgb(203,91,130))
            : new LinearGradientBrush(Color.FromRgb(126,185,237),Color.FromRgb(243,156,188),0);
        contentBox.ToolTip=(showDaily ? "北京时间今日实际美元花费；" : "")+display.UpdatedText;
        var time=showDaily ? dailyUpdatedAt : display.UpdatedAt;
        updated.Text=time.HasValue ? "更新 " + time.Value.ToLocalTime().ToString("HH:mm:ss") : "尚未查询成功";
        updated.ToolTip = showDaily ? "北京时间 00:00—24:00 · 实际美元计费" : null;
        var message = display.Status == "已更新" ? "" : display.Status;
        if(showDaily && dailyError is not null)message="今日花费更新失败 · "+dailyError;
        if(sessionSaveFailed)message="会话未能保存，下次启动需重新登录";
        if(startupNotice is not null)message+=(message.Length>0 ? " · " : "")+startupNotice;
        status.Text=message;status.ToolTip=message;status.Visibility=Visibility.Collapsed;
        var fullMessage=alertText ?? (message.Length>0 && message!="查询中…" ? message : null);
        if(fullMessage is not null)
        {
            reminder.Text=fullMessage;reminder.ToolTip=fullMessage;
            contentBox.ToolTip=fullMessage;
            // Prefer large readable text; reduce only until the full wrapped message fits the ellipse inset.
            for(var font=32; font>=14; font--)
            {
                var measure=new TextBlock {Text=fullMessage,Width=224,FontFamily=reminder.FontFamily,FontWeight=reminder.FontWeight,
                    FontSize=font,LineHeight=font*1.05,LineStackingStrategy=LineStackingStrategy.BlockLineHeight,TextWrapping=TextWrapping.Wrap};
                measure.Measure(new Size(224,double.PositiveInfinity));
                reminder.FontSize=font;reminder.LineHeight=font*1.05;
                if(measure.DesiredSize.Height<=110)break;
            }
            contentBox.Child=reminder;
        }
        else contentBox.Child=normalContent;
        if(tray is not null)tray.Text="Balance Plug · "+display.AmountText;
    }
    private async Task LoginAsync(string site, string email, string password)
    {
        if (!await queryGate.WaitAsync(0)) throw new BalanceException("当前正在查询，请稍后再登录");
        try
        {
            await api.LoginAsync(site, email, password, shutdown.Token);
            settings.SiteUrl = api.Session!.SiteUrl; settings.Email = email.Trim(); SaveSettings();
            startupNotice = null; decrease.Hide(); display.Reset(); dailyAmount=null;dailyDay=null;dailyError=null;alertText=null;alertExpiry.Stop(); timer.Start();
            if (sessionSaveFailed) throw new BalanceException("连接成功，但会话无法加密保存，请检查本地目录权限");
        }
        finally { queryGate.Release(); }
        await RefreshAsync();
    }
    private void OpenAccount()
    {
        CloseMenu();
        if (accountWindow is not null) { accountWindow.Activate(); return; }
        accountWindow = new AccountWindow(settings, LoginAsync) { Owner = this };
        accountWindow.Closed += (_, _) => accountWindow = null; accountWindow.Show(); accountWindow.Activate();
    }
    private void OpenBubbleMotion()
    {
        CloseMenu();
        if (bubbleMotionWindow is not null) { bubbleMotionWindow.Activate(); return; }
        bubbleMotionWindow = new BubbleMotionWindow(settings.BubbleMotion, PreviewBubbleMotion, CommitBubbleMotion, value => decrease.Show(.492472m,value)) { Owner = this };
        bubbleMotionWindow.Closed += (_, _) => bubbleMotionWindow = null; bubbleMotionWindow.Show(); bubbleMotionWindow.Activate();
    }
    private void PreviewBubbleMotion(BubbleMotionSettings value)
    { settings.BubbleMotion = value; decrease.Hide(); SaveSettings(false); }
    private void CommitBubbleMotion() { settings.BubbleMotion.Normalize(); SaveSettings(); }
    private void OpenInterval()
    {
        CloseMenu();
        if (intervalWindow is not null) { intervalWindow.Activate(); return; }
        intervalWindow = new UpdateIntervalWindow(settings.UpdateIntervalSeconds, value => { settings.UpdateIntervalSeconds = value; timer.Interval = TimeSpan.FromSeconds(value); SaveSettings(false); }, () => SaveSettings());
        intervalWindow.Owner = this; intervalWindow.Closed += (_, _) => intervalWindow = null; intervalWindow.Show(); intervalWindow.Activate();
    }

    private void OpenSizes()
    {
        CloseMenu();
        if (sizeWindow is not null) { sizeWindow.Activate(); return; }
        sizeWindow = new SizeWindow(settings, PreviewSizes, CommitSizes) { Owner = this };
        sizeWindow.Closed += (_, _) => sizeWindow = null; sizeWindow.Show(); sizeWindow.Activate();
    }
    private void PreviewSizes(double bubbleScale, double mascotScale)
    {
        settings.BubbleScale = AppSettings.NormalizeScale(bubbleScale); settings.MascotScale = AppSettings.NormalizeScale(mascotScale);
        ApplyLayout(); Dispatcher.BeginInvoke(new Action(ClampPosition)); sizeSave.Stop(); sizeSave.Start();
    }
    private void CommitSizes()
    {
        sizeSave.Stop(); if (closing) return;
        LoadMascot(); ClampPosition(); SavePosition(showError: true);
    }

    private sealed record Entry(string Label, Action? Run = null, Func<bool>? Check = null, Entry[]? Children = null, bool CloseBefore = false);
    private Entry[] MenuEntries()
    {
                var images = new List<Entry> { new("默认鲸鱼（占位）", () => SelectMascot(null), () => settings.ActiveMascotId is null) };
        images.AddRange(settings.Mascots.Select(item => new Entry(item.Name, () => SelectMascot(item.Id), () => settings.ActiveMascotId == item.Id)));
        if (settings.ActiveMascotId is not null) images.Add(new Entry("重命名当前形象…", RenameMascot, CloseBefore: true));
        images.Add(new Entry("导入本地 PNG…", ChooseMascot, CloseBefore: true));
        return new[] {
            new Entry("手动刷新", () => { _ = RefreshAsync(); }), new Entry("API / 账户配置…", OpenAccount, CloseBefore: true),
            new Entry("自动更新间隔…", OpenInterval, CloseBefore: true), new Entry("气泡 / 吉祥物大小…", OpenSizes, CloseBefore: true),
            new Entry("余额减少提示设置…", OpenBubbleMotion, CloseBefore: true),
            new Entry("吉祥物选择", Children: images.ToArray()),
            new Entry("开机自启", ToggleAutoStart, IsAutoStart), new Entry("鼠标穿透", () => SetClickThrough(!settings.ClickThrough), () => settings.ClickThrough),
            new Entry("恢复窗口位置", () => RestorePosition()),
            new Entry("是否置顶", () => SetTopmost(!settings.Topmost), () => settings.Topmost), new Entry("今日花费提醒…", OpenSpendAlerts, CloseBefore: true), new Entry("关闭", Close, CloseBefore: true) };
    }
    private static bool IsAutoStart() { try { return DesktopNative.IsAutoStart(); } catch { return false; } }
    private ContextMenu BuildWindowMenu()
    {
        menuChecks.Clear(); var menu = new ContextMenu { StaysOpen = false, Placement = PlacementMode.MousePoint };
        foreach (var entry in MenuEntries()) menu.Items.Add(WindowItem(entry)); return menu;
    }
    private MenuItem WindowItem(Entry entry)
    {
        var header = new TextBlock { Text = entry.Label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
        var item = new MenuItem { Header = header, IsCheckable = entry.Check is not null, IsChecked = entry.Check?.Invoke() ?? false, StaysOpenOnClick = !entry.CloseBefore };
        System.Windows.Automation.AutomationProperties.SetName(item, entry.Label);
        if (entry.Check is not null) menuChecks.Add((item, entry.Check));
        if (entry.Run is not null) item.Click += (_, e) =>
        {
            e.Handled = true; if (entry.CloseBefore) CloseMenu(); entry.Run();
            foreach (var check in menuChecks) check.Item.IsChecked = check.Check();
        };
        if (entry.Children is not null) foreach (var child in entry.Children) item.Items.Add(WindowItem(child));
        return item;
    }
    private void OpenMenu()
    {
        if (closing || closeRequested || store.MigrationPending) return; CloseMenu();
        var menu = BuildWindowMenu(); openMenu = menu;
        var host = new Window { Title = "Balance Plug · 操作菜单", Width = 1, Height = 1, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, Owner = this };
        menuHost = host;
        host.Deactivated += (_, _) => { if (menuHost == host) CloseMenu(); };
        menu.Closed += (_, _) => { if (openMenu == menu) CloseMenu(); };
        menu.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseMenu(); } };
        host.Show();
        var point = Forms.Cursor.Position;
        DesktopNative.SetWindowPos(new WindowInteropHelper(host).Handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x0001 | 0x0004);
        menu.Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (menuHost != host || !menu.IsOpen) return;
            host.Width = menu.ActualWidth; host.Height = menu.ActualHeight;
            if (PresentationSource.FromVisual(menu) is HwndSource popupSource && DesktopNative.GetWindowRect(popupSource.Handle, out var bounds))
                DesktopNative.SetWindowPos(new WindowInteropHelper(host).Handle, IntPtr.Zero, bounds.Left, bounds.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }));
        host.Activate(); menu.PlacementTarget = host; menu.IsOpen = true;
    }
    private void CloseMenu()
    {
        var menu = openMenu; var host = menuHost; openMenu = null; menuHost = null; menuChecks.Clear();
        if (menu is not null) { menu.StaysOpen = true; menu.IsOpen = false; }
        // Finish popup capture teardown before destroying its focus HWND.
        if (host is not null) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(host.Close));
    }
    private void OnTrayMouseUp(object? sender, Forms.MouseEventArgs e)
    { if (e.Button == Forms.MouseButtons.Right) Dispatcher.Invoke(OpenMenu); }
    private void ToggleAutoStart()
    {
        try { DesktopNative.SetAutoStart(!DesktopNative.IsAutoStart()); }
        catch { Notify("无法更改开机自启，请检查权限并使用发布目录中的 exe"); }
    }
    private void SetTopmost(bool enabled)
    {
        var previous = settings.Topmost;
        try
        {
            settings.Topmost = enabled; store.SaveSettings(settings); Topmost = enabled;
        }
        catch
        {
            settings.Topmost = previous; Topmost = previous; Notify("无法保存置顶设置，已保留原状态");
        }
    }
    private string? AskMascotName(string name)
    {
        var dialog = new MascotNameWindow(name) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.ResultName : null;
    }
    private void RenameMascot()
    {
        CloseMenu();
        var current = settings.Mascots.FirstOrDefault(m => m.Id == settings.ActiveMascotId);
        if (current is null) return;
        var name = AskMascotName(current.Name); if (name is null) return;
        try { store.RenameMascot(settings, current.Id, name); }
        catch (BalanceException ex) { Notify(ex.Message); }
        catch { Notify("无法保存形象名称，已保留原名称"); }
    }
    private void SetClickThrough(bool enabled)
    {
        try { DesktopNative.SetClickThrough(handle, enabled); settings.ClickThrough = enabled; SaveSettings(); }
        catch { Notify("无法更改鼠标穿透，已保留原状态"); }
    }
    private void Notify(string message)
    {
        CloseMenu(); var notice = new SoftWindow("Balance Plug", 420, 220) { Owner = this };
        var body = notice.Body;
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var ok = new Button { Content = "知道了" }; ok.Click += (_, _) => notice.Close(); body.Children.Add(ok); notice.ShowDialog();
    }
    private void SaveSettings(bool showError = true)
    {
        if (store.MigrationPending) return;
        try { store.SaveSettings(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (showError && !closing) Notify("无法保存设置，请检查本地目录权限；本次操作不会在重启后保留"); }
    }
    private void ApplyAppearance()
    {
        settings.Theme = "Blue"; UiTheme.Apply("Blue");
        var edge = Color.FromRgb(41,61,117);
        var fill = Colors.White;
        foreach (var shape in new[] { bubble, thought1, thought2 }) { shape.Fill = new SolidColorBrush(fill); shape.Stroke = new SolidColorBrush(edge); }
        title.Foreground = new SolidColorBrush(Color.FromRgb(79,106,173));
        amount.Foreground = new LinearGradientBrush(Color.FromRgb(126,185,237), Color.FromRgb(243,156,188), 0);
        updated.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); ApplyLayout();
    }
    private void ApplyLayout()
    {
        var b = settings.BubbleScale; var m = settings.MascotScale;
        scene.Width = 300 * Math.Max(b, m); scene.Height = 203 * b + 162 * m;
        bubbleGroup.Width = 300 * b; bubbleGroup.Height = 210 * b;
        mascotGroup.Width = 225 * m; mascotGroup.Height = 162 * m;
        Canvas.SetLeft(mascotGroup, scene.Width - 234 * m); Canvas.SetTop(mascotGroup, 203 * b);
        var tailX = scene.Width - 160 * m;
        Canvas.SetLeft(thought1, (114.5 * b + (tailX - 140 * b) * .35) / b - 13.5);
        Canvas.SetLeft(thought2, tailX / b - 8);
        Width = scene.Width; Height = scene.Height;
    }
    private int MascotPixels() => Math.Clamp((int)Math.Ceiling(225 * settings.MascotScale * VisualTreeHelper.GetDpi(this).DpiScaleX), 1, 900);
    private void LoadMascot(bool startup = false)
    {
        if (settings.ActiveMascotId is null) { mascot.Source = defaultMascot; placeholder.Visibility = Visibility.Visible; return; }
        try { mascot.Source = MascotImages.Decode(File.ReadAllBytes(store.GetMascotPath(settings.ActiveMascotId)), MascotPixels()); placeholder.Visibility = Visibility.Collapsed; }
        catch
        {
            if (startup) { settings.ActiveMascotId = null; mascot.Source = defaultMascot; placeholder.Visibility = Visibility.Visible; startupNotice = "当前形象缺失或损坏，已回退默认；图库已保留"; SaveSettings(false); }
            else Notify("无法重新加载形象，已保留当前画面；请重新导入图片");
        }
    }
    private void SelectMascot(string? id)
    {
        try
        {
            var image = id is null ? (ImageSource)defaultMascot : MascotImages.Decode(File.ReadAllBytes(store.GetMascotPath(id)), MascotPixels());
            var previous = settings.ActiveMascotId; settings.ActiveMascotId = id;
            try { store.SaveSettings(settings); } catch { settings.ActiveMascotId = previous; throw; }
            mascot.Source = image; placeholder.Visibility = id is null ? Visibility.Visible : Visibility.Collapsed; startupNotice = null; RenderDisplay();
        }
        catch (BalanceException ex) { Notify(ex.Message); }
        catch { Notify("形象无法加载或保存，已保留原选择"); }
    }
    private void ChooseMascot()
    {
        CloseMenu(); var picker = new Microsoft.Win32.OpenFileDialog { Filter = "PNG 图片|*.png", Title = "导入静态吉祥物" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 10 * 1024 * 1024) throw new BalanceException("图片不得超过 10 MB");
            var bytes = File.ReadAllBytes(picker.FileName); var image = MascotImages.Decode(bytes, MascotPixels());
            var duplicate = settings.Mascots.FirstOrDefault(m => m.Id == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
            var name = AskMascotName(duplicate?.Name ?? System.IO.Path.GetFileNameWithoutExtension(picker.FileName));
            if (name is null) return;
            store.ImportMascot(settings, bytes, System.IO.Path.GetFileName(picker.FileName), displayName: name); mascot.Source = image; placeholder.Visibility = Visibility.Collapsed; startupNotice = null; RenderDisplay();
        }
        catch (BalanceException ex) { Notify(ex.Message); }
        catch { Notify("图片损坏或无法保存，已保留原形象"); }
    }
    private void RestorePosition(bool useSaved = false)
    {
        if (handle == IntPtr.Zero) return;
        var work = Forms.Screen.PrimaryScreen!.WorkingArea; DesktopNative.GetWindowRect(handle, out var rect);
        var x = useSaved && settings.Left.HasValue ? (int)Math.Clamp(settings.Left.Value, -100000, 100000) : work.Right - (rect.Right-rect.Left) - 24;
        var y = useSaved && settings.Top.HasValue ? (int)Math.Clamp(settings.Top.Value, -100000, 100000) : work.Bottom - (rect.Bottom-rect.Top) - 24;
        DesktopNative.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); ClampPosition(); SavePosition();
    }
    private void ClampPosition()
    {
        if (handle == IntPtr.Zero || closing || !DesktopNative.GetWindowRect(handle, out var rect)) return;
        var work = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this); MaxWidth = work.Width/dpi.DpiScaleX; MaxHeight = work.Height/dpi.DpiScaleY; UpdateLayout();
        DesktopNative.GetWindowRect(handle, out rect); var width = rect.Right-rect.Left; var height = rect.Bottom-rect.Top;
        var x = Math.Clamp(rect.Left,work.Left,Math.Max(work.Left,work.Right-width)); var y = Math.Clamp(rect.Top,work.Top,Math.Max(work.Top,work.Bottom-height));
        if (x != rect.Left || y != rect.Top) DesktopNative.SetWindowPos(handle,IntPtr.Zero,x,y,0,0,0x0001 | 0x0004 | 0x0010);
    }
    private void SavePosition(bool showError = false)
    { if (DesktopNative.GetWindowRect(handle, out var rect)) { settings.Left = rect.Left; settings.Top = rect.Top; SaveSettings(showError); } }
    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if (message is 0x007E or 0x02E0 or 0x001A or 0x0005) Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() =>
        { ClampPosition(); if (message == 0x02E0) LoadMascot(); if (message != 0x0005 && !sizeSave.IsEnabled) SavePosition(); }));
        return IntPtr.Zero;
    }
    private void PowerModeChanged(object sender,PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume && !closing) Dispatcher.BeginInvoke(new Action(() => { ClampPosition(); ConfigureDayBoundary(); RenderDisplay(); _ = RefreshAsync(); })); }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if (closing) return;
        if (!closeAllowed)
        {
            e.Cancel = true;
            if (!closeRequested)
            {
                closeRequested = true; timer.Stop(); sizeSave.Stop(); alertExpiry.Stop(); dayBoundary.Stop(); shutdown.Cancel(); decrease.Hide(); CloseMenu();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { closeAllowed = true; Close(); }));
            }
            return;
        }
        SavePosition(); closing = true; timer.Stop(); sizeSave.Stop(); shutdown.Cancel(); CloseMenu(); decrease.Dispose();
        accountWindow?.Close(); sizeWindow?.Close(); bubbleMotionWindow?.Close(); intervalWindow?.Close(); spendAlertWindow?.Close(); SystemEvents.PowerModeChanged -= PowerModeChanged; source?.RemoveHook(WindowMessage);
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); api.Dispose(); Application.Current.Shutdown();
    }

    private static DrawingImage CreateWhale()
    {
        var group = new DrawingGroup();
        void Draw(string path, Color fill, Color? stroke = null, double width = 3)
        { group.Children.Add(new GeometryDrawing(new SolidColorBrush(fill), stroke.HasValue ? new Pen(new SolidColorBrush(stroke.Value), width) : null, Geometry.Parse(path))); }
        var blue = Color.FromRgb(109, 157, 210); var edge = Color.FromRgb(58, 83, 140);
        Draw("M 175,77 C 193,74 201,60 216,62 C 208,79 213,92 226,98 C 202,108 185,100 175,91 Z", blue, edge);
        Draw("M 22,89 C 12,41 57,16 112,24 C 161,24 193,53 182,93 C 168,133 61,140 30,111 Z", blue, edge);
        Draw("M 24,95 C 66,81 126,85 179,99 C 158,134 58,132 30,110 Z", Color.FromRgb(235, 246, 255));
        Draw("M 102,86 C 99,109 112,117 135,109 C 127,91 118,88 102,86 Z", Color.FromRgb(88, 138, 194), edge, 2);
        Draw("M 92,22 C 79,5 86,0 95,2 C 103,2 104,13 107,22 M 108,22 C 115,0 126,3 127,11 C 126,17 117,18 108,22", Color.FromRgb(185, 222, 249));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(edge), null, new EllipseGeometry(new Point(54, 70), 5, 7)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(240, 179, 199)), null, new EllipseGeometry(new Point(45, 87), 10, 5)));
        Draw("M 63,84 Q 73,92 82,82", Colors.Transparent, edge, 2);
        var image = new DrawingImage(group); image.Freeze(); return image;
    }
}




