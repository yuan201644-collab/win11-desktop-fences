using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.UI;

/// <summary>
/// A borderless, transparent, topmost, click-through window that draws a labeled
/// rounded box around each desktop-icon cluster. Rendered via WPF's compositor
/// (layered window), which sidesteps the GDI+ draw-brush failure that crashes
/// NoFences on this machine.
///
/// Lifecycle: created once and kept hidden until the controller arranges icons;
/// <see cref="Render"/> positions it over the primary screen and rebuilds the
/// cluster visuals; <see cref="SetVisible"/> toggles it with the desktop.
/// </summary>
public partial class FenceOverlayWindow : Window
{
    private double _scaleX = 1.0;
    private double _scaleY = 1.0;

    public FenceOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
        => OverlayNative.ApplyOverlayStyles(new System.Windows.Interop.WindowInteropHelper(this).Handle);

    /// <summary>
    /// Rebuilds the overlay to cover the primary screen and draws one box per cluster.
    /// <paramref name="screenWidthPx"/>/<paramref name="screenHeightPx"/> are the primary
    /// monitor's dimensions in physical pixels; cluster <see cref="RectI"/> bounds are
    /// in the same space and are scaled to DIPs here.
    /// </summary>
    public void Render(int originXPx, int originYPx, int widthPx, int heightPx, IReadOnlyList<FenceCluster> clusters)
    {
        var view = PresentationSource.FromVisual(this);
        if (view != null)
        {
            var transform = view.CompositionTarget.TransformToDevice;
            _scaleX = transform.M11;
            _scaleY = transform.M22;
        }

        // Position over the whole virtual desktop (multi-monitor: origin may be negative).
        // Cluster bounds are in the same virtual-screen space, so canvas coordinates map 1:1.
        Left = originXPx / Math.Max(0.1, _scaleX);
        Top = originYPx / Math.Max(0.1, _scaleY);
        Width = widthPx / Math.Max(0.1, _scaleX);
        Height = heightPx / Math.Max(0.1, _scaleY);

        Root.Children.Clear();
        foreach (var cluster in clusters)
            AddCluster(cluster);
    }

    private void AddCluster(FenceCluster cluster)
    {
        var left = cluster.Bounds.Left / _scaleX;
        var top = cluster.Bounds.Top / _scaleY;
        var width = cluster.Bounds.Width / _scaleX;
        var height = cluster.Bounds.Height / _scaleY;

        var box = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0x55, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x66, 0x99, 0xD0)),
            BorderThickness = new Thickness(1),
        };
        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);
        Canvas.SetZIndex(box, 0);
        Root.Children.Add(box);

        var title = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x3B, 0x74, 0xA8)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3, 10, 4),
            Child = new TextBlock
            {
                Text = $"{cluster.Title} · {cluster.IconCount}",
                FontSize = 13,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
            },
        };
        Canvas.SetLeft(title, left + 10);
        Canvas.SetTop(title, top + 10);
        Canvas.SetZIndex(title, 1);
        Root.Children.Add(title);
    }

    public void SetVisible(bool visible)
    {
        if (visible && !IsVisible)
        {
            Show();
            Topmost = true;
        }
        else if (!visible && IsVisible)
        {
            Hide();
        }
    }
}