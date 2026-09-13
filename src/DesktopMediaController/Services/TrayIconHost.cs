using System.Drawing;
using System.Windows;
using WF = System.Windows.Forms;

namespace DesktopMediaController.Services;

/// <summary>
/// The tray presence: the entry point that keeps existing after the card is hidden.
/// </summary>
/// <remarks>
/// WinForms' <see cref="WF.NotifyIcon"/> rather than a hand-written <c>Shell_NotifyIcon</c> wrapper.
/// The deciding factor is Explorer restarts: the shell drops every notification icon when it
/// restarts, and <c>NotifyIcon</c> re-registers itself on the <c>TaskbarCreated</c> broadcast, while a
/// hand-rolled version has to listen for that message and get the re-registration order right. The
/// cost is that the right-click menu is a WinForms menu rather than a WPF one.
/// </remarks>
internal sealed class TrayIconHost : IDisposable
{
    private readonly Widget.WidgetWindow _widget;
    private readonly Icon _glyph;
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _toggleItem;
    private readonly WF.ToolStripMenuItem _autostartItem;
    private readonly Action _exit;

    private bool _disposed;

    public TrayIconHost(Widget.WidgetWindow widget, Action exit)
    {
        _widget = widget;
        _exit = exit;
        _glyph = LoadGlyph();

        _toggleItem = new WF.ToolStripMenuItem("隐藏卡片", null, (_, _) => ToggleCard());
        // Checked state is read back from the registry rather than toggled by the menu, so it cannot
        // drift from reality if the user deletes the Run entry behind our back.
        _autostartItem = new WF.ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutostart());

        var menu = new WF.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(new WF.ToolStripMenuItem("退出", null, (_, _) => _exit()));
        menu.Opening += (_, _) => SyncMenu();

        _icon = new WF.NotifyIcon
        {
            Icon = _glyph,
            Text = "桌面媒体控制器",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += OnIconClick;

        SyncMenu();
    }

    /// <summary>Left click peeks at the card and dismisses it again, the way a tray toggle is expected
    /// to behave.</summary>
    private void OnIconClick(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Left) ToggleCard();
    }

    private void ToggleCard()
    {
        if (_widget.IsCardVisible) _widget.HideCard();
        else _widget.ShowCard();

        SyncMenu();
    }

    private void ToggleAutostart()
    {
        if (Autostart.IsEnabled()) Autostart.Disable();
        else if (Environment.ProcessPath is { } executable) Autostart.Enable(executable);

        SyncMenu();
    }

    private void SyncMenu()
    {
        _toggleItem.Text = _widget.IsCardVisible ? "隐藏卡片" : "显示卡片";
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    /// <summary>
    /// Loads the same icon file the executable and its shortcut use, at the size the shell is asking
    /// for, so the tray glyph is crisp on a scaled taskbar instead of a resampled blur.
    /// </summary>
    private static Icon LoadGlyph()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("Assets/AppIcon.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, WF.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception)
        {
            // Fall through to the stock icon: a missing glyph is cosmetic, a crash is not.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Clear Visible before disposing, or the shell keeps drawing a ghost glyph in the tray until
        // the user happens to hover over it.
        _icon.Visible = false;
        _icon.MouseClick -= OnIconClick;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _glyph.Dispose();
    }
}
