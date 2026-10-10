using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BalancePlug.Core;

namespace BalancePlug;

internal sealed class BubbleMotionWindow : SoftWindow
{
    private readonly Canvas pathCanvas = new() { Width = 360, Height = 150 };
    private readonly Ellipse startPoint = new() { Width = 18, Height = 18, StrokeThickness = 2 };
    private readonly Ellipse targetPoint = new() { Width = 18, Height = 18, StrokeThickness = 2 };
    private readonly Line pathLine = new() { StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 3, 3 } };
    private readonly TextBlock startLabel = new() { Text = "出现", FontSize = 11 };
    private readonly TextBlock targetLabel = new() { Text = "目标", FontSize = 11 };
    private readonly BubbleMotionSettings draft;
    private readonly Action<BubbleMotionSettings> preview;
    private PointEditor? dragging;
    private bool pickStart = true;
    private readonly TextBlock hint = new() { FontSize = 11 };
    private readonly Action<BubbleMotionSettings>? showPreview;
    private const double RegionLeft = 18, RegionTop = 18, RegionWidth = 128, RegionHeight = 110;

    private enum PointEditor { Start, Target }
    public BubbleMotionWindow(BubbleMotionSettings settings, Action<BubbleMotionSettings> preview, Action commit, Action<BubbleMotionSettings>? showPreview = null) : base("余额减少提示", 430, 560)
    {
        draft = settings.Clone(); draft.Normalize(); this.preview = preview; this.showPreview = showPreview;
        Body.Children.Add(new TextBlock { Text = "在淡红色区域先点“出现点”，再点上方“目标点”；也可拖动两个点微调。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        BuildPathEditor(); Body.Children.Add(pathCanvas);
        hint.Text = "① 点击起点 → ② 点击上方终点"; hint.SetResourceReference(TextBlock.ForegroundProperty,"DangerBrush"); Body.Children.Add(hint);
        AddSlider("上升速度", 10, 300, 5, draft.Speed, "像素 / 秒", value => { draft.Speed = value; preview(draft); });
        AddSlider("气泡大小", 50, 200, 5, draft.Scale * 100, "%", value => { draft.Scale = value / 100; preview(draft); });
        var test = new Button { Content = "预览上升效果", Margin = new Thickness(0,10,0,0) };
        test.Click += (_,_) => this.showPreview?.Invoke(draft.Clone()); Body.Children.Add(test);
        var reset = new Button { Content = "恢复默认路径", Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) => { draft.StartX=.16; draft.StartY=.62; draft.TargetX=.28; draft.TargetY=.10; draft.Speed=55; draft.Scale=1;
            var sliders=Body.Children.OfType<Slider>().ToArray(); sliders[0].Value=55; sliders[1].Value=100;
            pickStart=true; hint.Text="① 点击起点 → ② 点击上方终点"; UpdatePoints(); preview(draft); };
        Body.Children.Add(reset);
        var note = new TextBlock { Text = "轨迹位于主余额气泡左侧；关闭窗口后保存", FontSize = 11, Margin = new Thickness(0, 10, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(note);
        Closed += (_, _) => { EndDrag(); commit(); };
    }
    private void BuildPathEditor()
    {
        var region = new Border { Width = RegionWidth, Height = RegionHeight, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(255, 235, 238)), BorderBrush = new SolidColorBrush(Color.FromRgb(244, 177, 190)), BorderThickness = new Thickness(1) };
        Canvas.SetLeft(region, RegionLeft); Canvas.SetTop(region, RegionTop); pathCanvas.Children.Add(region);
        
        region.MouseLeftButtonDown += (_,e) =>
        {
            var position=e.GetPosition(pathCanvas); SetPathPoint(pickStart ? PointEditor.Start : PointEditor.Target,position);
            pickStart=!pickStart; hint.Text=pickStart ? "已设置路径，可点击重选起点" : "② 点击上方终点";
            e.Handled=true; if(pickStart) this.showPreview?.Invoke(draft.Clone());
        };
        pathLine.IsHitTestVisible=false; startLabel.IsHitTestVisible=targetLabel.IsHitTestVisible=false;
        pathLine.SetResourceReference(Line.StrokeProperty, "DangerBrush"); pathCanvas.Children.Add(pathLine);
        startPoint.Fill = new SolidColorBrush(Color.FromRgb(255, 146, 167)); startPoint.SetResourceReference(Shape.StrokeProperty, "DangerBrush");
        targetPoint.Fill = new SolidColorBrush(Color.FromRgb(255, 210, 219)); targetPoint.SetResourceReference(Shape.StrokeProperty, "DangerBrush");
        pathCanvas.Children.Add(startPoint); pathCanvas.Children.Add(targetPoint);
        startLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); targetLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); pathCanvas.Children.Add(startLabel); pathCanvas.Children.Add(targetLabel);
        startPoint.MouseLeftButtonDown += (_, e) => BeginDrag(PointEditor.Start, e); targetPoint.MouseLeftButtonDown += (_, e) => BeginDrag(PointEditor.Target, e);
        pathCanvas.MouseMove += (_, e) => MoveDrag(e); pathCanvas.MouseLeftButtonUp += (_, _) => EndDrag(); pathCanvas.MouseLeave += (_, _) => EndDrag();
        UpdatePoints();
    }
    private void BeginDrag(PointEditor point, MouseButtonEventArgs e) { dragging = point; pathCanvas.CaptureMouse(); e.Handled = true; }
    private void MoveDrag(MouseEventArgs e)
    {
        if (dragging is null) return; SetPathPoint(dragging.Value,e.GetPosition(pathCanvas));
    }
    private void SetPathPoint(PointEditor which, Point position)
    {
        var x=Math.Clamp((position.X-RegionLeft)/RegionWidth,0,1);
        var y=Math.Clamp((position.Y-RegionTop)/RegionHeight,0,1);
        if(which==PointEditor.Start) { draft.StartX=x; draft.StartY=Math.Max(.05,y); if(draft.TargetY>=draft.StartY) draft.TargetY=Math.Max(0,draft.StartY-.05); }
        else { draft.TargetX=x; draft.TargetY=Math.Min(y,Math.Max(0,draft.StartY-.05)); }
        UpdatePoints(); preview(draft.Clone());
    }
    private void EndDrag()
    {
        if(dragging is null)return; dragging=null; pathCanvas.ReleaseMouseCapture(); this.showPreview?.Invoke(draft.Clone());
    }
    private void UpdatePoints()
    {
        var start = new Point(RegionLeft + draft.StartX * RegionWidth, RegionTop + draft.StartY * RegionHeight); var target = new Point(RegionLeft + draft.TargetX * RegionWidth, RegionTop + draft.TargetY * RegionHeight);
        SetPoint(startPoint,start); SetPoint(targetPoint,target); Canvas.SetLeft(startLabel,start.X+12); Canvas.SetTop(startLabel,start.Y-4); Canvas.SetLeft(targetLabel,target.X+12); Canvas.SetTop(targetLabel,target.Y-4);
        pathLine.X1=start.X; pathLine.Y1=start.Y; pathLine.X2=target.X; pathLine.Y2=target.Y;
    }
    private static void SetPoint(FrameworkElement point, Point position) { Canvas.SetLeft(point,position.X-point.Width/2); Canvas.SetTop(point,position.Y-point.Height/2); }
    private void AddSlider(string label,double min,double max,double tick,double initial,string suffix,Action<double> write)
    {
        var row=new DockPanel { Margin=new Thickness(0,3,0,0) }; var value=new TextBlock { Text=Math.Round(initial,0)+suffix,HorizontalAlignment=HorizontalAlignment.Right }; DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);row.Children.Add(new TextBlock { Text=label });Body.Children.Add(row);
        var slider=new Slider { Minimum=min,Maximum=max,TickFrequency=tick,IsSnapToTickEnabled=true,SmallChange=tick,Value=initial,Margin=new Thickness(0,4,0,7) }; System.Windows.Automation.AutomationProperties.SetName(slider,label); slider.ValueChanged+=(_,_)=>{value.Text=Math.Round(slider.Value,0)+suffix;write(slider.Value);};Body.Children.Add(slider);
    }
}
