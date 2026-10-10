using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BalancePlug.Core;
using Forms = System.Windows.Forms;

namespace BalancePlug;

internal sealed class DecreaseBubble : IDisposable
{
    private readonly Popup popup;
    private readonly Func<bool> animationsEnabled;
    private readonly FrameworkElement target;
    private readonly Canvas space = new() { IsHitTestVisible = false };
    private readonly Grid card = new() { IsHitTestVisible = false };
    private readonly TextBlock text;
    private readonly TranslateTransform rise = new();
    private readonly DispatcherTimer expiry = new();
    private Point placement;
    public bool IsOpen => popup.IsOpen;
    public string Text => text.Text;
    public DecreaseBubble(UIElement target, Func<bool>? animationsEnabled = null)
    {
        this.target = (FrameworkElement)target;
        this.animationsEnabled = animationsEnabled ?? (() => true);
        text = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var ellipse = new Ellipse { StrokeThickness = 1.5 };
        ellipse.SetResourceReference(Shape.FillProperty, "SurfaceBrush"); ellipse.SetResourceReference(Shape.StrokeProperty, "DangerBrush");
        card.Children.Add(ellipse); card.Children.Add(text); card.RenderTransform = rise; space.Children.Add(card);
        popup = new Popup { Child = space, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false,
            PlacementTarget = target, Placement = PlacementMode.Custom, PopupAnimation = PopupAnimation.None };
        popup.CustomPopupPlacementCallback = (_, _, _) => new[] { new CustomPopupPlacement(placement, PopupPrimaryAxis.Horizontal) };
        void SetPassiveStyles()
        {
            if (PresentationSource.FromVisual(space) is HwndSource source)
            {
                try { DesktopNative.SetClickThrough(source.Handle, true); }
                catch (System.ComponentModel.Win32Exception) { Hide(); }
            }
        }
        popup.Opened += (_, _) => SetPassiveStyles(); space.Loaded += (_, _) => SetPassiveStyles();
        expiry.Tick += (_, _) => Hide();
    }
    public void Show(decimal amount, BubbleMotionSettings? configuration = null)
    {
        Hide(); var motion = configuration?.Clone() ?? new BubbleMotionSettings(); motion.Normalize();
        text.Text = BalanceDisplay.FormatDecrease(amount); text.FontSize = 13 * motion.Scale;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Width = Math.Max(44 * motion.Scale, text.DesiredSize.Width + 32 * motion.Scale);
        card.Height = Math.Max(28 * motion.Scale, text.DesiredSize.Height + 16 * motion.Scale);
        // Both endpoints are centers in the same left-side region shown by the path editor.
        var regionWidth = Math.Clamp(target.ActualWidth * .6, 120, 240);
        var regionHeight = Math.Clamp(target.ActualHeight * .75, 100, 240);
        var start = new Point(-regionWidth - 12 + motion.StartX * regionWidth, motion.StartY * regionHeight);
        var end = new Point(-regionWidth - 12 + motion.TargetX * regionWidth, motion.TargetY * regionHeight);
        var halfWidth = card.Width / 2; var halfHeight = card.Height / 2;
        var left = Math.Min(start.X, end.X) - halfWidth - 2;
        var top = Math.Min(start.Y, end.Y) - halfHeight - 2;
        space.Width = Math.Abs(end.X-start.X) + card.Width + 4;
        space.Height = Math.Abs(end.Y-start.Y) + card.Height + 4;
        placement = new Point(left, top);
        Canvas.SetLeft(card, start.X-left-halfWidth); Canvas.SetTop(card, start.Y-top-halfHeight);
        var dx = end.X-start.X; var dy = end.Y-start.Y;
        var duration = TimeSpan.FromSeconds(Math.Clamp(Math.Sqrt(dx*dx+dy*dy)/motion.Speed, .25, 30));
        // Shift the entire trajectory together at a monitor edge; never clamp just one endpoint.
        if (PresentationSource.FromVisual(target) is not null)
        {
            var origin = target.PointToScreen(new Point(0,0)); var dpi = VisualTreeHelper.GetDpi(target);
            var work = Forms.Screen.FromPoint(new System.Drawing.Point((int)origin.X,(int)origin.Y)).WorkingArea;
            placement.X = Math.Clamp(placement.X, (work.Left-origin.X)/dpi.DpiScaleX,
                Math.Max((work.Left-origin.X)/dpi.DpiScaleX,(work.Right-origin.X)/dpi.DpiScaleX-space.Width));
            placement.Y = Math.Clamp(placement.Y, (work.Top-origin.Y)/dpi.DpiScaleY,
                Math.Max((work.Top-origin.Y)/dpi.DpiScaleY,(work.Bottom-origin.Y)/dpi.DpiScaleY-space.Height));
        }
        popup.IsOpen = true;
        if (animationsEnabled())
        {
            // Linear clocks preserve the selected speed through the whole path, including the fade.
            rise.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,dx,duration));
            rise.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(0,dy,duration));
            var fade = new DoubleAnimationUsingKeyFrames { Duration = duration };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(duration.TotalSeconds*.82))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0,KeyTime.FromTimeSpan(duration)));
            card.BeginAnimation(UIElement.OpacityProperty,fade);
        }
        expiry.Interval = duration + TimeSpan.FromMilliseconds(80); expiry.Start();
    }
    public void Hide()
    {
        expiry.Stop(); popup.IsOpen = false;
        rise.BeginAnimation(TranslateTransform.XProperty,null); rise.BeginAnimation(TranslateTransform.YProperty,null);
        card.BeginAnimation(UIElement.OpacityProperty,null); rise.X=rise.Y=0; card.Opacity=1;
    }
    public void Dispose() { Hide(); popup.Child=null; }
}
