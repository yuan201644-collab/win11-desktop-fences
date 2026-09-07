using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.UI;

/// <summary>
/// The drag snap preview: one borderless, transparent, click-through window that draws a dashed
/// rounded ghost at the spot the dragged box will magnetically land on if released right now.
/// A separate window (not a child of the dragged FenceWindow) because the preview can sit far
/// outside the dragged box's own window rectangle — a WPF window clips its content at its HWND
/// bounds. Purely visual: zero input, zero cross-process traffic, shown/hidden by the controller
/// through <see cref="FenceHost"/> while a drag is in flight.
/// </summary>
public sealed class SnapPreviewWindow : Window
{
    private readonly Rectangle _ghost;

    public SnapPreviewWindow(OverlayAppearance appearance)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;

        var c = (appearance ?? OverlayAppearance.Default).Border;
        _ghost = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1.5,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 },
            Stroke = new SolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B)),
            Fill = new SolidColorBrush(Color.FromArgb(18, c.R, c.G, c.B)),
        };
        Content = _ghost;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            OverlayNative.ApplyFenceStyles(hwnd); // click-through + never activates, like the fences
        };
    }

    /// <summary>Positions the ghost over a screen-px rectangle (px → DIPs per this window's DPI).</summary>
    public void ShowAt(RectI boundsPx)
    {
        double sx = GetScaleX(), sy = GetScaleY();
        Left = boundsPx.Left / Math.Max(0.1, sx);
        Top = boundsPx.Top / Math.Max(0.1, sy);
        Width = Math.Max(1, boundsPx.Width / Math.Max(0.1, sx));
        Height = Math.Max(1, boundsPx.Height / Math.Max(0.1, sy));
        if (!IsVisible) Show(); // ShowActivated=false + WS_EX_NOACTIVATE: never steals focus
    }

    private double GetScaleX() => PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
    private double GetScaleY() => PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
}
