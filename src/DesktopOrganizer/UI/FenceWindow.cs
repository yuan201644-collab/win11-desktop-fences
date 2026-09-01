using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.UI;

/// <summary>
/// One draggable fence: a borderless, transparent layered WPF window that floats over the desktop
/// (never reparented into the shell — see <see cref="OnSourceInitialized"/>). Its body is an
/// HTTRANSPARENT hit-test region, so mouse clicks pass through to the real desktop icons underneath
/// (they still open/select normally), while the translucent box/header is drawn around them and the
/// header strip stays grabbable to drag the whole cluster. Dragging reports cumulative pixel deltas
/// via <see cref="ClusterDrag"/> so the controller can move the underlying icons to match.
/// </summary>
public sealed class FenceWindow : Window
{
    private readonly Border _box;
    private readonly Border _header;
    private readonly TextBlock _title;
    private Border _toggleBtn = null!;
    private TextBlock _toggleGlyph = null!;
    private OverlayAppearance _appearance = OverlayAppearance.Default;

    // Drag tracking (screen pixels).
    private bool _dragCandidate;
    private bool _dragging;
    private int _startX, _startY;
    private int _lastX, _lastY;
    private DateTime _lastHeaderClick;

    /// <summary>The cluster box title this fence draws (used to route drags to the right icon group).</summary>
    public string ClusterTitle { get; }

    /// <summary>Raises once when a drag is committed (the dead-zone is crossed). Snapshot icon positions here.</summary>
    public event Action<string>? ClusterDragStart;

    /// <summary>Raises on every mouse move during a drag, with cumulative pixel deltas from drag start.</summary>
    public event Action<string, int, int>? ClusterDrag;

    /// <summary>Raises once when a drag ends (mouse up), so the controller can finalize and persist positions.</summary>
    public event Action<string>? ClusterDragEnd;

    /// <summary>Raises when the header is double-clicked — the controller flips this box's collapsed state.</summary>
    public event Action<string>? TitleToggleCollapse;

    public FenceWindow(string clusterTitle, OverlayAppearance appearance)
    {
        ClusterTitle = clusterTitle;
        _appearance = appearance;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;

        var root = new Canvas { Background = Brushes.Transparent };

        _box = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5),
        };
        Canvas.SetZIndex(_box, 0);
        root.Children.Add(_box);

        _header = new Border { CornerRadius = new CornerRadius(12, 12, 0, 0) };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };
        Grid.SetColumn(_title, 0);
        _toggleGlyph = new TextBlock
        {
            Text = "▾",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _toggleBtn = new Border
        {
            Child = _toggleGlyph,
            Width = 22,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)),
            Cursor = Cursors.Hand,
            ToolTip = "折叠 / 展开",
        };
        _toggleBtn.MouseLeftButtonDown += OnToggleClicked;
        Grid.SetColumn(_toggleBtn, 1);
        headerGrid.Children.Add(_title);
        headerGrid.Children.Add(_toggleBtn);
        _header.Child = headerGrid;
        Canvas.SetZIndex(_header, 1);
        root.Children.Add(_header);

        Content = root;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        LostMouseCapture += (_, _) => EndDrag();

        ApplyAppearance();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        OverlayNative.ApplyFenceStyles(hwnd);
        // Stay a plain top-level layered window — never reparented into the shell. Reparenting a
        // WPF AllowsTransparency window via SetParent hangs the UI thread in a shell handshake, so
        // we keep full transparency the easy way and let WM_NCHITTEST do the hit-testing instead.
        if (System.Windows.Interop.HwndSource.FromHwnd(hwnd) is { } src)
            src.AddHook(WndProc);
    }

    /// <summary>
    /// Per-pixel input routing: the box body returns HTTRANSPARENT so mouse clicks pass through to
    /// the real desktop icons underneath (they still open/select normally); the header strip returns
    /// the default HTCLIENT so it stays grabbable to drag the whole cluster.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg == WM_NCHITTEST && !handled && NativeMethods.GetWindowRect(hwnd, out var r))
        {
            // lParam packs the cursor as two signed shorts in screen pixels.
            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom
                && y - r.Top >= HeaderDevicePx)
            {
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            }
        }
        return IntPtr.Zero;
    }

    private int HeaderDevicePx
    {
        get
        {
            double sy = GetScaleY();
            return _header.ActualHeight > 0 ? (int)Math.Max(1, _header.ActualHeight * sy) : 0;
        }
    }

    /// <summary>Positions the fence over a cluster's bounds (screen px → DIPs per this window's DPI) and lays out its visuals.
/// When <paramref name="collapsed"/> the box shrinks to a thin title-band tab (icons underneath stay in place).</summary>
    public void Render(int leftPx, int topPx, int widthPx, int heightPx, int headerPx, bool collapsed)
    {
        double sx = GetScaleX(), sy = GetScaleY();
        Left = leftPx / Math.Max(0.1, sx);
        Top = topPx / Math.Max(0.1, sy);
        Width = widthPx / Math.Max(0.1, sx);

        double headerDip = headerPx / Math.Max(0.1, sy);
        Height = collapsed ? Math.Max(1, headerDip) : heightPx / Math.Max(0.1, sy);

        // Glyph mirrors the state so the tab itself stays discoverable: ▾ = can collapse, ▸ = can expand.
        _toggleGlyph.Text = collapsed ? "▸" : "▾";

        // Collapsed: hide the box body and keep just the header (title) spanning the tab width.
        if (collapsed)
        {
            _box.Width = 0; // hide body: set size 0 so nothing draws behind the header
            _box.Height = 0;
            _header.Width = Math.Max(0, Width - 3);
            _header.Height = headerDip - 2;
        }
        else
        {
            _box.Width = Width;
            _box.Height = Height;
            _header.Width = Math.Max(0, Width - 3);
            _header.Height = headerDip - 2;
        }
    }

    public void SetIconCount(int count)
    {
        _boxCount = count;
        _title.Text = $"{ClusterTitle} · {count}";
    }

    /// <summary>Recolors box/header/title from the current palette. Cheap, for live preview.</summary>
    public void ApplyAppearance()
    {
        _title.Foreground = MakeBrush(_appearance.HeaderText);
        _header.Background = MakeBrush(_appearance.Header);
        _box.Background = MakeBrush(_appearance.Fill);
        _box.BorderBrush = MakeBrush(_appearance.Border);
    }

    public void SetAppearance(OverlayAppearance value)
    {
        _appearance = value ?? OverlayAppearance.Default;
        ApplyAppearance();
    }

    private static SolidColorBrush MakeBrush(ArgbColor c)
        => new(Color.FromArgb(c.A, c.R, c.G, c.B));

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only the header reaches us (the body is HTTRANSPARENT), so a quick second press here is
        // a header double-click — toggle collapse and cancel any pending drag so the box doesn't move.
        var now = DateTime.UtcNow;
        bool isDoubleClick = (now - _lastHeaderClick) < TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime());
        _lastHeaderClick = now;
        if (isDoubleClick)
        {
            _dragCandidate = false;
            _dragging = false;
            _lastHeaderClick = default;
            TitleToggleCollapse?.Invoke(ClusterTitle);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt)) return;
        _startX = _lastX = pt.X;
        _startY = _lastY = pt.Y;
        _dragCandidate = true;
    }

    private void OnToggleClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // swallow the click so the window-level handler doesn't arm a drag
        TitleToggleCollapse?.Invoke(ClusterTitle);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = false; return; }
        if (!_dragCandidate && !_dragging) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;

        if (!_dragging)
        {
            // Exceed the small dead-zone before committing to a drag (so a click isn't a drag).
            if (Math.Abs(pt.X - _startX) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(pt.Y - _startY) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragging = true;
                CaptureMouse();
            }
            else return;
            _lastX = pt.X;
            _lastY = pt.Y;
            ClusterDragStart?.Invoke(ClusterTitle);
        }

        int dx = pt.X - _lastX;
        int dy = pt.Y - _lastY;
        _lastX = pt.X;
        _lastY = pt.Y;

        // Slide the box live with the cursor (window geometry uses DIPs).
        Left += dx / Math.Max(0.1, GetScaleX());
        Top += dy / Math.Max(0.1, GetScaleY());
        ClusterDrag?.Invoke(ClusterTitle, pt.X - _startX, pt.Y - _startY);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging && !_dragCandidate) return;
        bool wasDragging = _dragging;
        _dragging = false;
        _dragCandidate = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDragging) ClusterDragEnd?.Invoke(ClusterTitle);
    }

    private double GetScaleX() => PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
    private double GetScaleY() => PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M22 ?? 1.0;

    private int _boxCount;
}