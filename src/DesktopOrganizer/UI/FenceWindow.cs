using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Win32;

namespace DesktopOrganizer.UI;

/// <summary>
/// One draggable fence: a borderless, transparent layered WPF window that floats over the desktop
/// (never reparented into the shell — see <see cref="OnSourceInitialized"/>). Its body is an
/// HTTRANSPARENT hit-test region, so mouse clicks pass through to the real desktop icons underneath
/// (they still open/select normally), while the translucent box/header is drawn around them and the
/// header strip stays grabbable to drag the whole cluster. The box's icons are hidden the moment
/// the header is PRESSED (the controller parks them on <see cref="ClusterDragStart"/>, before any
/// movement), the window glides itself during the drag, and the cumulative delta is still reported
/// via <see cref="ClusterDrag"/> as the controller's fallback drop anchor when it cannot read live
/// window geometry on release. A bare click (press + release, no drag) also ends the gesture, which
/// restores the parked icons in place.
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

    // Edge-resize tracking. The box body is click-through (HTTRANSPARENT) except a thin hot band on
    // the left/right/bottom edges and the two bottom corners; grabbing one of those resizes the box
    // (the controller re-lays-out the icons to fit), while the header still drags the whole cluster.
    private const int ResizeHotPx = 6;    // screen px width of each edge hot band
    private const int CornerHotPx = 14;   // screen px corner grab square
    private ResizeEdge _resizeEdge = ResizeEdge.None;
    private int _resizeStartX, _resizeStartY; // cursor at drag start (screen px)
    private RectI _resizeStartRect;           // window rect at drag start (screen px)

    private enum ResizeEdge { None, Left, Right, Bottom, BottomLeft, BottomRight }

    /// <summary>The cluster box title this fence draws (used to route drags to the right icon group).</summary>
    public string ClusterTitle { get; }

    /// <summary>Raises the instant the header is pressed — NOT after the dead-zone is crossed.
    /// The controller parks the box's icons here; paying that burst of cross-process writes at
    /// press time is what keeps the first drag frame completely free of them.</summary>
    public event Action<string>? ClusterDragStart;

    /// <summary>Raises on every mouse move during a drag, with cumulative pixel deltas from drag start.</summary>
    public event Action<string, int, int>? ClusterDrag;

    /// <summary>Raises once when a drag ends (mouse up), so the controller can finalize and persist positions.</summary>
    public event Action<string>? ClusterDragEnd;

    /// <summary>Raises when the header is double-clicked — the controller flips this box's collapsed state.</summary>
    public event Action<string>? TitleToggleCollapse;

    /// <summary>Raises when the header (incl. the collapsed tab) is right-clicked — the controller
    /// opens a context menu of extra actions at the cursor. Carries the screen-pixel cursor location.</summary>
    public event Action<string, int, int>? ContextMenuRequested;

    /// <summary>Raises when an edge grab starts a resize, before the first move — the controller
    /// parks the box's icons for the gesture's duration (the same trick a drag uses), so the frame
    /// can grow and shrink with zero cross-process icon traffic per frame.</summary>
    public event Action<string>? ResizeStarted;

    /// <summary>Raises live while an edge resize is dragged, with the candidate screen-px rectangle.</summary>
    public event Action<string, RectI>? ResizeMoved;

    /// <summary>Raises once when an edge resize ends (mouse up).</summary>
    public event Action<string>? ResizeEnded;

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
        MouseRightButtonDown += OnMouseRightButtonDown;
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
    /// Per-pixel input routing: the box body returns HTTRANSPARENT so clicks pass through to the
    /// real desktop icons underneath (they still open/select normally); the header strip returns the
    /// default HTCLIENT so it stays grabbable to drag the whole cluster. A thin hot band on the box
    /// edges also returns HTCLIENT — that's where the resize handles live, so edge grabs resize the
    /// box while the interior stays click-through.
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
                && y - r.Top >= HeaderDevicePx) // below the header → box body
            {
                int dl = x - r.Left, dr = r.Right - x, db = r.Bottom - y;
                bool onHotBand = dl <= ResizeHotPx || dr <= ResizeHotPx || db <= ResizeHotPx;
                if (!onHotBand)
                {
                    handled = true;
                    return new IntPtr(-1); // HTTRANSPARENT
                }
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
    /// When <paramref name="collapsed"/> the box shrinks to a thin title-band tab (the controller has already
    /// parked the cluster's real icons off-screen, so the tab is all that remains on the desktop).</summary>
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
        // A collapsed tab carries no icons (they are parked off-screen), so showing "· 0" would
        // be misleading — the tab keeps just the box title.
        _title.Text = count > 0 ? $"{ClusterTitle} · {count}" : ClusterTitle;
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
        // The box edges are hittable for resizing; the header reaches us too. An edge grab starts a
        // resize (the controller re-lays-out the icons); anything else on the header is a drag or a
        // double-click toggle.
        var edge = HitResizeEdge();
        if (edge != ResizeEdge.None)
        {
            _resizeEdge = edge;
            _dragCandidate = false;
            _dragging = false;
            if (!NativeMethods.GetCursorPos(out var cursor)) return;
            _resizeStartX = cursor.X;
            _resizeStartY = cursor.Y;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _resizeStartRect = NativeMethods.GetWindowRect(hwnd, out var wr)
                ? new RectI(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top)
                : default;
            ResizeStarted?.Invoke(ClusterTitle);
            CaptureMouse();
            return;
        }

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

        // Park the icons NOW, at press time — not after the dead-zone is crossed. The park is one
        // burst of cross-process writes; paying it inside the first mouse move (the old timing) is
        // what read as a jolt the instant the drag started. By the time the hand moves, the frame
        // is already free to glide with zero per-frame traffic. Capture here too, so the release
        // is always seen (even off-window) and the icons can never be left parked by a lost mouse-up.
        ClusterDragStart?.Invoke(ClusterTitle);
        CaptureMouse();
    }

    private void OnToggleClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // swallow the click so the window-level handler doesn't arm a drag
        TitleToggleCollapse?.Invoke(ClusterTitle);
    }

    /// <summary>
    /// Right-click on the header opens the per-fence context menu. Only the header is hittable (the
    /// body is HTTRANSPARENT and passes clicks through to the real icons), so any window-level
    /// right-click necessarily landed on the header — both the expanded strip and the collapsed tab.
    /// </summary>
    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var pt)) pt = default;
        ContextMenuRequested?.Invoke(ClusterTitle, pt.X, pt.Y);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Resize drag: recompute the candidate rectangle from the drag-start rect + cursor delta.
        if (_resizeEdge != ResizeEdge.None)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndResize(); return; }
            if (!NativeMethods.GetCursorPos(out var cursor)) return;
            var r = _resizeStartRect;
            int resizeDx = cursor.X - _resizeStartX;
            int resizeDy = cursor.Y - _resizeStartY;
            int x = r.X, y = r.Y, w = r.Width, h = r.Height;
            switch (_resizeEdge)
            {
                case ResizeEdge.Left: x = r.X + resizeDx; w = r.Width - resizeDx; break;
                case ResizeEdge.Right: w = r.Width + resizeDx; break;
                case ResizeEdge.Bottom: h = r.Height + resizeDy; break;
                case ResizeEdge.BottomLeft: x = r.X + resizeDx; w = r.Width - resizeDx; h = r.Height + resizeDy; break;
                case ResizeEdge.BottomRight: w = r.Width + resizeDx; h = r.Height + resizeDy; break;
            }
            ResizeMoved?.Invoke(ClusterTitle, new RectI(x, y, Math.Max(1, w), Math.Max(1, h)));
            return;
        }

        // Hover feedback: show the resize cursor while over a hot band (only the band is hittable).
        if (!_dragging && !_dragCandidate && e.LeftButton == MouseButtonState.Released)
        {
            Cursor = HitResizeEdge() switch
            {
                ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
                ResizeEdge.Bottom => Cursors.SizeNS,
                ResizeEdge.BottomLeft or ResizeEdge.BottomRight => Cursors.SizeNWSE,
                _ => Cursors.Arrow,
            };
        }

        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = false; return; }
        if (!_dragCandidate && !_dragging) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;

        if (!_dragging)
        {
            // Exceed the small dead-zone before committing to a drag (so a click isn't a drag).
            // The icons are already parked (park happens at press time, see OnMouseLeftButtonDown).
            if (Math.Abs(pt.X - _startX) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(pt.Y - _startY) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragging = true;
            }
            else return;
            _lastX = pt.X;
            _lastY = pt.Y;
        }

        int dx = pt.X - _lastX;
        int dy = pt.Y - _lastY;
        _lastX = pt.X;
        _lastY = pt.Y;

        // Glide the box with the cursor directly. The controller parked this box's icons on drag
        // start (they reappear at the drop spot on release), so a frame costs zero cross-process
        // writes and can never lag or wobble behind its own contents — the per-frame icon pushes
        // (and the same-frame coalescing scheme built to tame them) are gone.
        double sx = GetScaleX(), sy = GetScaleY();
        Left += dx / Math.Max(0.1, sx);
        Top += dy / Math.Max(0.1, sy);

        // Keep reporting the cumulative delta: it is the controller's fallback drop anchor when
        // the host cannot report live window geometry on release (headless hosts).
        ClusterDrag?.Invoke(ClusterTitle, pt.X - _startX, pt.Y - _startY);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_resizeEdge != ResizeEdge.None) { EndResize(); return; }
        if (!_dragging && !_dragCandidate) return;
        bool wasDragging = _dragging;
        bool wasArmed = _dragCandidate;
        _dragging = false;
        _dragCandidate = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // The press already parked the icons, so EVERY release must close the gesture — including
        // a bare click that never crossed the dead-zone (its drag-end restores the parked icons
        // in place). Skipping it here would leave the box's icons hidden on the desktop.
        if (wasDragging || wasArmed) ClusterDragEnd?.Invoke(ClusterTitle);
    }

    private void EndResize()
    {
        if (_resizeEdge == ResizeEdge.None) return;
        _resizeEdge = ResizeEdge.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ResizeEnded?.Invoke(ClusterTitle);
    }

    /// <summary>Which resize edge the cursor is currently over (screen px), or None.</summary>
    private ResizeEdge HitResizeEdge()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetCursorPos(out var pt) || !NativeMethods.GetWindowRect(hwnd, out var r)) return ResizeEdge.None;
        if (pt.X < r.Left || pt.X >= r.Right || pt.Y < r.Top || pt.Y >= r.Bottom) return ResizeEdge.None;
        // The header band is not a resize edge — it drags the cluster.
        if (pt.Y - r.Top < HeaderDevicePx) return ResizeEdge.None;

        int dl = pt.X - r.Left, dr = r.Right - pt.X, db = r.Bottom - pt.Y;
        if (dl <= CornerHotPx && db <= CornerHotPx) return ResizeEdge.BottomLeft;
        if (dr <= CornerHotPx && db <= CornerHotPx) return ResizeEdge.BottomRight;
        if (dr <= ResizeHotPx) return ResizeEdge.Right;
        if (dl <= ResizeHotPx) return ResizeEdge.Left;
        if (db <= ResizeHotPx) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }

    private double GetScaleX() => PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
    private double GetScaleY() => PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M22 ?? 1.0;

    private int _boxCount;
}