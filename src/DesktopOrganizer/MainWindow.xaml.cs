using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Services;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace DesktopOrganizer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly FenceOverlayController _overlay;
    private readonly Forms.NotifyIcon _tray;
    private readonly List<ColorChannel> _colorChannels = new();
    private SoftwareGroupingConfig _ruleConfig = new();
    private readonly HashSet<string> _defaultGroupTitles = new(
        SoftwareGroupStore.Default().Groups.Select(g => g.Title), StringComparer.OrdinalIgnoreCase);
    // Category-manager draft: the box display order the user is building up, plus the titles they
    // unchecked. Only pushed to the overlay (and disk) by ApplyCategories.
    private List<string> _categoryOrder = new();
    private readonly HashSet<string> _hiddenBoxes = new(StringComparer.OrdinalIgnoreCase);
    private OverlayAppearance _appearance = OverlayAppearance.Default;
    private StartupSettings _startup = StartupSettings.Default;
    // Guard for the 启动 page: assigning IsChecked / Slider.Value in code fires the same handlers a
    // user click does, which would write the settings file before it has even been read once.
    private bool _loadingStartup;
    private bool _exiting;
    private bool _hasArranged;
    private bool _trayHintShown;
    private Forms.ToolStripMenuItem? _trayAutoStartItem;
    private Forms.ToolStripMenuItem? _trayResetLayoutsItem;
    // The fence context menu and the tiny activatable window anchoring it (see OnFenceContextMenu).
    private System.Windows.Controls.ContextMenu? _fenceMenu;
    private Window? _fenceMenuHost;
    private readonly System.Windows.Threading.DispatcherTimer _savedTimer;
    // Fences are foreground overlay sheets layered ON TOP of the real icons, so a crash-opaque box
    // body would hide the icons underneath. Cap the fill's alpha so it can get very solid but never
    // fully opaque — the icons always stay visible. Other channels (border/header/text) don't cover
    // icons, so only the fill (框底色) is capped.
    private const int MaxFillAlpha = 225;

    public MainWindow()
    {
        InitializeComponent();
        // Surface the build identity in the title bar so it's obvious at a glance which version is
        // running — the project has repeatedly wasted cycles on the user launching a stale exe.
        Title = $"桌面图标整理 · {LoadVersion()}";
        _overlay = new FenceOverlayController();
        _overlay.FenceContextMenu += OnFenceContextMenu;

        // Pull the last-saved fence palette into the live overlay, then build the color rows.
        _appearance = OverlayAppearanceStore.Load(AppearanceFilePath);
        _overlay.Appearance = _appearance;
        // Init the saved-hint timer BEFORE building the color/inset sections: those builders set
        // slider values, which fires CommitAppearance → FlashSaved; a null timer here would NRE.
        _savedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _savedTimer.Tick += (_, _) => SavedHint.Visibility = Visibility.Collapsed;

        BuildColorSection();
        BuildPerBoxColorSection();
        BuildInsetSection();
        BuildRuleSection();
        BuildCategorySection();
        BuildLayoutSection();
        InitStartupSection();

        // Closing the window (×) no longer quits — the app keeps running in the background so the
        // overlay can follow the desktop. The tray icon is the way back in (and the way out).
        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "桌面图标整理（右键查看更多功能）",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        // Rebuild the data-driven per-box rows when the user switches to their tabs (see handler).
        // Wired here, after InitializeComponent, so the initial selection never fires it with
        // partially-constructed fields.
        SettingsTabs.SelectionChanged += Tabs_SelectionChanged;
        Closing += OnClosing;
    }

    /// <summary>Build identity for the title bar: prefers the VERSION.txt written at publish time
    /// (git hash + timestamp); falls back to the exe's last-write time so a debug build still shows
    /// something identifiable.</summary>
    private static string LoadVersion()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null)
            {
                var v = Path.Combine(Path.GetDirectoryName(exe)!, "VERSION.txt");
                if (File.Exists(v)) return File.ReadAllText(v).Trim();
                return "build " + File.GetLastWriteTime(exe).ToString("MM-dd HH:mm");
            }
        }
        catch { /* best-effort */ }
        return "unknown";
    }

    /// <summary>
    /// The tray icon should be the app's own icon, not the generic WinForms placeholder — it is now
    /// the app's permanent home, so it has to be recognizable at a glance. Falls back quietly if the
    /// exe has no embedded icon (or the extraction is blocked).
    /// </summary>
    /// <summary>Loads the app icon. Prefers the resource embedded in this assembly (works in single-file
    /// publish too), and falls back to the placeholder system icon if anything goes wrong.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            // pack://application:,,,/app.ico resolves to the WPF resource set in the csproj.
            // Reading from the assembly works in both `dotnet run` and self-contained publish.
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (sri?.Stream is { } stream) return new System.Drawing.Icon(stream);
        }
        catch (Exception) { /* fall through */ }
        return SystemIcons.Application;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true; // swallow the × — keep running from the tray
        Hide();
        // Hiding to the tray is invisible to a first-time user: say it once so they know where the
        // app went (and that the tray icon is how to quit).
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "桌面图标整理",
                "已最小化到托盘。右键托盘图标可整理、设置或退出。", Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// Full tray context menu. The app lives in the tray between settings visits, so this menu is
    /// the real entry point — it exposes the arrange/restore/refresh actions, the auto-start switch,
    /// and an exit, without having to open the window first.
    /// </summary>
    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        var open = new Forms.ToolStripMenuItem("打开设置");
        // Fully qualified: System.Windows is imported for WPF, so a bare FontStyle would bind to
        // the WPF enum (Italic/Oblique/Normal) instead of the WinForms one that has Bold.
        if (open.Font is { } f) open.Font = new System.Drawing.Font(f, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => ShowFromTray();
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("整理并显示分组", null, (_, _) => ArrangeFromTray());
        menu.Items.Add("恢复上次布局", null, (_, _) => _overlay.RestoreSavedLayout());

        // "所有框恢复自动布局" — one-shot unpin: every box falls back to auto-packing (colors and
        // edge-padding overrides stay). Grayed out while no box is pinned; the Opening hook keeps
        // that fresh, since the menu is built once but the pin state changes any time.
        _trayResetLayoutsItem = new Forms.ToolStripMenuItem("所有框恢复自动布局");
        _trayResetLayoutsItem.Click += (_, _) => _overlay.ResetAllFenceLayouts();
        menu.Items.Add(_trayResetLayoutsItem);
        menu.Opening += (_, _) => _trayResetLayoutsItem.Enabled = _overlay.AnyPinnedLayouts;

        menu.Items.Add("刷新分组框", null, (_, _) => _overlay.RefreshOverlay());
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("显示所有分类框", null, (_, _) => ShowAllBoxes());

        // Checked item, kept as a field so the settings page and the tray can't drift apart.
        _trayAutoStartItem = new Forms.ToolStripMenuItem("开机自启动")
        {
            Checked = AutoStartService.IsEnabled(),
        };
        _trayAutoStartItem.Click += (_, _) => SetRunAtLogon(!AutoStartService.IsEnabled());
        menu.Items.Add(_trayAutoStartItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开数据文件夹", null, (_, _) => OpenDataFolder());
        menu.Items.Add("关于", null, (_, _) => ShowAbout());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        return menu;
    }

    // Same as the 整理 button, but reachable with the window closed: mark it arranged so a later
    // sort/inset change knows a layout already exists to re-apply.
    private void ArrangeFromTray()
    {
        _hasArranged = true;
        _overlay.ArrangeAndShow();
    }

    /// <summary>Un-hides every fence box; the overlay redraws them on its next tick.</summary>
    private void ShowAllBoxes()
    {
        var cfg = _overlay.Categories;
        if (cfg.Hidden.Count == 0) return;
        _overlay.SetCategories(new FenceCategoryConfig
        {
            Order = cfg.Order,
            Hidden = new List<string>(),
        });
        BuildCategorySection();
        FlashSaved();
    }

    /// <summary>Opens %LOCALAPPDATA%\DesktopOrganizer, where palettes, rules and layouts live.</summary>
    private static void OpenDataFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrganizer");
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception) { /* best-effort: the user can still navigate there manually */ }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"桌面图标整理\n版本：{LoadVersion()}\n\n{AutoStartService.CurrentExePath()}\n\n数据目录：{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrganizer")}",
            "关于 桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExitApp()
    {
        _exiting = true;
        CloseFenceMenu(); // don't leave the 1×1 menu anchor alive past shutdown
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Right-click on a fence header (incl. a collapsed "隐藏图标栏" tab) — build a context menu of
    /// extra per-fence actions at the cursor. The menu stays focused on what a header click is for:
    /// the one toggle for THIS box, the global collapse actions only while they would actually do
    /// something (全部展开 only while some box is collapsed, 全部折叠 only while some box is expanded),
    /// and 打开设置. 整理/恢复/退出 live in the tray menu instead, so no action is duplicated across
    /// the two entry points.
    /// </summary>
    private void OnFenceContextMenu(string title, int x, int y)
    {
        var cm = new System.Windows.Controls.ContextMenu();
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        var collapsed = _overlay.IsCollapsed(title);
        var toggle = new System.Windows.Controls.MenuItem
        {
            Header = $"{(collapsed ? "展开" : "折叠")}「{title}」",
            FontWeight = FontWeights.SemiBold,
        };
        toggle.Click += (_, _) => _overlay.ToggleFence(title);
        cm.Items.Add(toggle);

        cm.Items.Add(new System.Windows.Controls.Separator());

        if (_overlay.AnyCollapsed)
        {
            var expandAll = new System.Windows.Controls.MenuItem { Header = "全部展开" };
            expandAll.Click += (_, _) => _overlay.ExpandAll();
            cm.Items.Add(expandAll);
        }
        if (_overlay.AnyExpanded)
        {
            var collapseAll = new System.Windows.Controls.MenuItem { Header = "全部折叠" };
            collapseAll.Click += (_, _) => _overlay.CollapseAll();
            cm.Items.Add(collapseAll);
        }
        if (_overlay.AnyPinnedLayouts)
        {
            var resetLayouts = new System.Windows.Controls.MenuItem { Header = "所有框恢复自动布局" };
            resetLayouts.Click += (_, _) => _overlay.ResetAllFenceLayouts();
            cm.Items.Add(resetLayouts);
        }

        cm.Items.Add(new System.Windows.Controls.Separator());

        cm.Items.Add(BuildColorMenu(title));
        cm.Items.Add(BuildInsetsMenu(title));

        cm.Items.Add(new System.Windows.Controls.Separator());

        var settings = new System.Windows.Controls.MenuItem { Header = "打开设置" };
        settings.Click += (_, _) => ShowFromTray();
        cm.Items.Add(settings);

        // Hand off to OpenFenceMenu: the menu needs a live, activated owner window to be able to
        // auto-close, and this window only ever lives in the tray.
        OpenFenceMenu(cm, x, y);
    }

    /// <summary>Opens a fence context menu on a throwaway 1×1 activatable window placed at the
    /// cursor. A WPF ContextMenu auto-closes when its owner window loses activation — but this
    /// app's main window is tray-only and never shown, so the menu had no activation to lose and
    /// stayed painted on the desktop after the user clicked away. The helper window is shown and
    /// activated first, so any click outside the menu (desktop, a fence, another app) deactivates
    /// it and the menu closes the way a context menu is supposed to.</summary>
    private void OpenFenceMenu(System.Windows.Controls.ContextMenu cm, int x, int y)
    {
        CloseFenceMenu();

        var host = new Window
        {
            Width = 1,
            Height = 1,
            Left = x,
            Top = y,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0.01,
        };
        // If the popup itself (wrongly) took activation from the host, that Deactivated has already
        // fired by the time IsOpen returns. Arm the auto-close only after the input queue drains,
        // so only a genuine outside click closes the menu — never the menu opening itself.
        bool armed = false;
        host.Deactivated += (_, _) => { if (armed) cm.IsOpen = false; };
        // Guard on identity: a previously-open menu's Closed can fire on a later dispatcher tick
        // (after A.IsOpen=false, before/after the next menu opens). Without this, re-opening a fence
        // menu while one is already open would let the stale Closed tear down the NEW menu.
        cm.Closed += (_, _) => { if (_fenceMenu == cm) CloseFenceMenu(); };
        cm.PlacementTarget = host;

        _fenceMenu = cm;
        _fenceMenuHost = host;
        host.Show();
        host.Activate();
        cm.IsOpen = true;
        host.Dispatcher.BeginInvoke(
            new Action(() => armed = true),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Tears down the menu anchor, if one is up. Safe to call repeatedly (re-entrant from
    /// the menu's own Closed handler) and safe to call at shutdown.</summary>
    private void CloseFenceMenu()
    {
        var menu = _fenceMenu;
        var host = _fenceMenuHost;
        _fenceMenu = null;
        _fenceMenuHost = null;
        try { if (menu is not null) menu.IsOpen = false; } catch (Exception) { }
        try { host?.Close(); } catch (Exception) { }
    }

    // --- 换色「title」submenu: one entry per FencePalette preset, plus a system color dialog and a
    //     reset back to the global palette. Picking a preset applies FencePalette.FromPrimary, so a
    //     single click yields a coherent four-channel look instead of asking for four colors.
    private System.Windows.Controls.MenuItem BuildColorMenu(string title)
    {
        var sub = new System.Windows.Controls.MenuItem { Header = $"换色「{title}」" };
        foreach (var c in FencePalette.Presets)
        {
            var item = new System.Windows.Controls.MenuItem { Header = BuildSwatchHeader(c) };
            var captured = c;
            item.Click += (_, _) => ApplyFenceColor(title, captured);
            sub.Items.Add(item);
        }

        sub.Items.Add(new System.Windows.Controls.Separator());

        var custom = new System.Windows.Controls.MenuItem { Header = "自定义…" };
        custom.Click += (_, _) => PickFenceColor(title);
        sub.Items.Add(custom);

        if (_overlay.GetFenceAppearance(title) is not null)
        {
            var reset = new System.Windows.Controls.MenuItem { Header = "恢复默认" };
            reset.Click += (_, _) => ResetFenceColor(title);
            sub.Items.Add(reset);
        }
        return sub;
    }

    /// <summary>A 14px color chip + hex label, used as a MenuItem header so each preset is
    /// recognizable at a glance without reading the code.</summary>
    private static object BuildSwatchHeader(ArgbColor c)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, c.R, c.G, c.B)),
            Margin = new Thickness(0, 0, 6, 0),
        });
        panel.Children.Add(new TextBlock { Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}", VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>Applies a preset primary color to one box, live and persisted.</summary>
    private void ApplyFenceColor(string title, ArgbColor primary)
    {
        _overlay.SetFenceAppearance(title, FencePalette.FromPrimary(primary));
        FlashSaved();
        RefreshBoxColorRow(title);
    }

    /// <summary>System color dialog → derived four-channel appearance for one box.</summary>
    private void PickFenceColor(string title)
    {
        var current = _overlay.GetFenceAppearance(title)?.Header ?? _appearance.Header;
        using var dlg = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        var c = dlg.Color;
        ApplyFenceColor(title, ArgbColor.FromArgb(0xFF, c.R, c.G, c.B));
    }

    private void ResetFenceColor(string title)
    {
        _overlay.ResetFenceAppearance(title);
        FlashSaved();
        RefreshBoxColorRow(title);
    }

    // --- 边距「title」submenu: one compact slider per box edge, reshaping ONLY this box live.
    //     Mirrors the settings page's four inset sliders, but funneled through the per-box override
    //     (SetFenceInsets) so one box never widens every other box on the desktop. 恢复默认 clears
    //     the override back to the global default — shown only while an override actually exists.

    private System.Windows.Controls.MenuItem BuildInsetsMenu(string title)
    {
        var sub = new System.Windows.Controls.MenuItem { Header = $"边距「{title}」" };
        var cur = _overlay.BoxInsetsFor(title);
        AddInsetSlider(sub, "左", cur.Left, v => _overlay.SetFenceInsets(title, _overlay.BoxInsetsFor(title) with { Left = v }));
        AddInsetSlider(sub, "右", cur.Right, v => _overlay.SetFenceInsets(title, _overlay.BoxInsetsFor(title) with { Right = v }));
        AddInsetSlider(sub, "上", cur.Top, v => _overlay.SetFenceInsets(title, _overlay.BoxInsetsFor(title) with { Top = v }));
        AddInsetSlider(sub, "下", cur.Bottom, v => _overlay.SetFenceInsets(title, _overlay.BoxInsetsFor(title) with { Bottom = v }));

        if (_overlay.GetFenceInsets(title) is not null)
        {
            sub.Items.Add(new System.Windows.Controls.Separator());
            var reset = new System.Windows.Controls.MenuItem { Header = "恢复默认（跟随全局边距）" };
            reset.Click += (_, _) => _overlay.ResetFenceInsets(title);
            sub.Items.Add(reset);
        }
        return sub;
    }

    /// <summary>One menu row: a side label, a compact slider (range -30..80 like the settings page),
    /// and the live value. The initial assignment is guarded so opening the menu can't write the
    /// store with a redundant no-op change.</summary>
    private static void AddInsetSlider(
        System.Windows.Controls.MenuItem sub, string side, int initial, Action<int> onChanged)
    {
        var slider = new Slider
        {
            Minimum = -30, Maximum = 80,
            Width = 110, VerticalAlignment = VerticalAlignment.Center,
        };
        var valueText = new TextBlock
        {
            Width = 30,
            Text = initial.ToString(),
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var item = new System.Windows.Controls.MenuItem();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = side,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 16,
            Margin = new Thickness(0, 0, 6, 0),
        });
        header.Children.Add(slider);
        header.Children.Add(valueText);
        item.Header = header;

        bool init = true;
        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            valueText.Text = v.ToString();
            if (!init) onChanged(v);
        };
        slider.Value = initial;
        init = false;

        sub.Items.Add(item);
    }

    /// <summary>
    /// Per-box color rows on the 配色 page: each row = the box's current color swatch (opens the
    /// system color dialog), one chip per preset, and a 恢复默认 button that is only enabled while
    /// the box actually has an override. Rows are kept in sync with the header 换色 submenu — both
    /// funnel through <see cref="ApplyFenceColor"/>.
    /// </summary>
    private void BuildPerBoxColorSection()
    {
        PerBoxColorSection.Children.Clear();
        foreach (var title in _overlay.AvailableBoxTitles)
            PerBoxColorSection.Children.Add(BuildBoxColorRow(title));
    }

    private UIElement BuildBoxColorRow(string title)
    {
        var label = new TextBlock
        {
            Text = title,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var swatch = new Button { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
        swatch.Click += (_, _) => PickFenceColor(title);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(label);
        row.Children.Add(swatch);

        foreach (var c in FencePalette.Presets)
        {
            var chip = new Button
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, c.R, c.G, c.B)),
            };
            var captured = c;
            chip.Click += (_, _) => ApplyFenceColor(title, captured);
            row.Children.Add(chip);
        }

        var reset = new Button { Content = "恢复默认", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        reset.Click += (_, _) => ResetFenceColor(title);
        row.Children.Add(reset);

        // After a color change the swatch and the reset button must reflect the new override state.
        row.Tag = new Action(() =>
        {
            var eff = _overlay.GetFenceAppearance(title)?.Header ?? _appearance.Header;
            swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                0xFF, eff.R, eff.G, eff.B));
            reset.IsEnabled = _overlay.GetFenceAppearance(title) is not null;
        });
        ((Action)row.Tag)();
        return row;
    }

    /// <summary>Re-paints the per-box color row for one title (swatch + reset state) after the
    /// header 换色 menu changes it, so the settings page and the desktop never drift.</summary>
    private void RefreshBoxColorRow(string title)
    {
        foreach (var child in PerBoxColorSection.Children)
        {
            if (child is not StackPanel row) continue;
            var label = row.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is not null && label.Text == title && row.Tag is Action refresh)
            {
                refresh();
                return;
            }
        }
    }

    /// <summary>
    /// Per-box size editor on the 分类布局 page: one row per box with pixel-exact width/height
    /// inputs. A box with no pinned rectangle auto-packs with the rest on arrange; typing a size
    /// pins it to that rectangle (anchored at its current position). 清除固定 unpins it again.
    /// Rows are data-driven from the controller, mirroring the 配色 page's per-box rows.
    /// </summary>
    private void BuildLayoutSection()
    {
        LayoutSection.Children.Clear();
        foreach (var title in _overlay.AvailableBoxTitles)
            LayoutSection.Children.Add(BuildLayoutRow(title));
    }

    private UIElement BuildLayoutRow(string title)
    {
        var label = new TextBlock
        {
            Text = title,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var wBox = MakeIntBox();
        var hBox = MakeIntBox();
        // 宽/高 label sits above the numeric box so the column reads as "宽 300 / 高 200".
        var size = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        size.Children.Add(BuildLabeledInt("宽", wBox));
        size.Children.Add(BuildLabeledInt("高", hBox));

        var statePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Commit on Enter or on focus loss (not per keystroke — each commit re-lays-out real icons).
        void Commit()
        {
            if (!int.TryParse(wBox.Text, out var w) || !int.TryParse(hBox.Text, out var h)
                || w <= 0 || h <= 0)
                return;
            // Anchor X/Y at the box's current position: pinned rect if present, else live window.
            RectI? anchor = null;
            if (_overlay.GetFenceLayout(title) is { } pinned)
                anchor = new RectI(pinned.X, pinned.Y, pinned.Width, pinned.Height);
            else
                anchor = _overlay.GetCurrentFenceBounds(title);
            if (anchor is null)
            {
                // No window was ever drawn for this box (hidden or never arranged) — nothing to
                // anchor to. Don't silently drop the edit: tell the user what to do first.
                MessageBox.Show($"「{title}」框还没有显示过，无法固定位置。\n\n请先在主界面点「整理并显示分组」，再回来调整尺寸。",
                    "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _overlay.SetFenceLayout(title, new FenceLayout(anchor.Value.X, anchor.Value.Y, w, h));
            FlashSaved();
            RefreshLayoutState(title, statePanel);
        }
        wBox.LostFocus += (_, _) => Commit();
        wBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        hBox.LostFocus += (_, _) => Commit();
        hBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(label);
        row.Children.Add(size);
        row.Children.Add(statePanel);
        RefreshLayoutState(title, statePanel);
        SeedLayoutValues(title, wBox, hBox);
        return row;
    }

    /// <summary>A "宽"/"高" label + numeric input pair, aligned to a common column width.</summary>
    private static UIElement BuildLabeledInt(string unit, TextBox box)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        inner.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
        inner.Children.Add(box);
        return inner;
    }

    /// <summary>Prefills the row with the pinned size, or the live window size when unpinned.</summary>
    private void SeedLayoutValues(string title, TextBox wBox, TextBox hBox)
    {
        var b = _overlay.GetFenceLayout(title) is { } pinned
            ? new RectI(pinned.X, pinned.Y, pinned.Width, pinned.Height)
            : _overlay.GetCurrentFenceBounds(title);
        if (b is { } r)
        {
            wBox.Text = r.Width.ToString();
            hBox.Text = r.Height.ToString();
            wBox.IsEnabled = hBox.IsEnabled = true;
        }
        else
        {
            wBox.IsEnabled = hBox.IsEnabled = false;
            wBox.ToolTip = hBox.ToolTip = "该框尚未显示（可能被隐藏或还没整理），先整理后再调整。";
        }
    }

    /// <summary>Repaints the trailing action area of one layout row. Three states, mirroring
    /// <see cref="SeedLayoutValues"/> exactly so the hint never contradicts the input boxes:
    /// pinned → 清除固定; unpinned with a live window → auto-pack (editable); unpinned and never
    /// drawn (hidden box / not arranged yet) → inputs disabled with an explanatory hint.</summary>
    private void RefreshLayoutState(string title, StackPanel statePanel)
    {
        statePanel.Children.Clear();
        TextBlock Hint(string text) => new()
        {
            Text = text,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (_overlay.GetFenceLayout(title) is not null)
        {
            var clear = new Button { Content = "清除固定", Margin = new Thickness(8, 0, 0, 0) };
            clear.Click += (_, _) =>
            {
                _overlay.ClearFenceLayout(title);
                FlashSaved();
                // The box went back to auto-pack; rebuild the whole page so this row re-seeds from
                // the live window rect and shows the auto-pack hint again.
                BuildLayoutSection();
            };
            statePanel.Children.Add(clear);
        }
        else
        {
            statePanel.Children.Add(_overlay.GetCurrentFenceBounds(title) is null
                ? Hint("尚未显示，先整理后再调整")
                : Hint("自动打包（可拖动调整）"));
        }
    }

    /// <summary>A width-constrained TextBox accepting digits only (Enter/focus-loss commit is wired
    /// by the caller). Filters both keystrokes (PreviewTextInput) and paste (DataObject.Pasting) —
    /// paste goes through a different channel, so filtering only keystrokes would let junk in.</summary>
    private static TextBox MakeIntBox()
    {
        var box = new TextBox { Width = 56, VerticalContentAlignment = VerticalAlignment.Center };
        box.PreviewTextInput += (_, e) =>
        {
            foreach (var ch in e.Text)
                if (ch is < '0' or > '9') { e.Handled = true; return; }
        };
        // Paste bypasses PreviewTextInput: handle it here so "123abc" never lands in the box.
        box.AddHandler(DataObject.PastingEvent, new DataObjectPastingEventHandler((_, e) =>
        {
            if (!e.DataObject.GetDataPresent(typeof(string))) return;
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (text.Length > 0 && !text.All(char.IsAsciiDigit)) e.CancelCommand();
        }));
        return box;
    }

    /// <summary>
    /// Rebuilds the data-driven 分类布局 / 分类配色 rows whenever the user switches to those tabs.
    /// The overlay is live (boxes can be dragged/resized/recolored on the desktop while the window
    /// stays open), so rows built once at startup would go stale. A rebuild on tab entry keeps them
    /// honest. The 配色 tab's global four rows are NOT rebuilt — they reflect _appearance, which is
    /// only touched by that page itself — but its per-box section (below the global rows) is.
    /// </summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutTab.IsSelected) BuildLayoutSection();
        else if (ColorTab.IsSelected) BuildPerBoxColorSection();
    }

    private sealed class ColorChannel
    {
        public required string Label;
        public required Button Swatch;
        public required Slider Alpha;
        public required TextBlock AlphaText;
        public required ArgbColor Color;
    }

    // %LOCALAPPDATA%\DesktopOrganizer\overlay-appearance.json — the saved fence palette.
    private static string AppearanceFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "overlay-appearance.json");

    /// <summary>
    /// Builds one row per OverlayAppearance channel (fill / border / header / text) inside
    /// the XAML ColorSection panel. Each row = a swatch button opening the system color dialog
    /// + a 0-255 alpha slider. Every change recommits the palette live to the overlay and saves.
    /// </summary>
    private void BuildColorSection()
    {
        ColorSection.Children.Clear();

        foreach (var (label, get) in new[]
        {
            ("框底色", (System.Func<OverlayAppearance, ArgbColor>)(a => a.Fill)),
            ("边框", a => a.Border),
            ("标题栏", a => a.Header),
            ("标题文字", a => a.HeaderText),
        })
        {
            ColorSection.Children.Add(BuildColorRow(label, get(_appearance)));
        }
    }

    private UIElement BuildColorRow(string label, ArgbColor initial)
    {
        var swatch = new Button { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
        var alpha = new Slider { Minimum = 0, Maximum = label == "框底色" ? MaxFillAlpha : 255, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        var alphaText = new TextBlock { Width = 36, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

        var channel = new ColorChannel { Label = label, Swatch = swatch, Alpha = alpha, AlphaText = alphaText, Color = initial };
        _colorChannels.Add(channel);

        swatch.Click += (_, _) => PickColor(channel);
        alpha.ValueChanged += (_, e) =>
        {
            channel.Color = channel.Color with { A = (byte)e.NewValue };
            UpdateRow(channel);
            CommitAppearance();
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 5),
        };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(swatch);
        row.Children.Add(new TextBlock { Text = "透明度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        row.Children.Add(alpha);
        row.Children.Add(alphaText);

        alpha.Value = channel.Color.A;
        UpdateRow(channel);
        return row;
    }

    /// <summary>
    /// Builds one slider per box edge (left/right/top/bottom) inside the InsetSection panel. Moving a
    /// slider reshapes the boxes live (via <see cref="FenceOverlayController.BoxInsets"/>) and persists.
    /// </summary>
    private void BuildInsetSection()
    {
        InsetSection.Children.Clear();
        // The sort dropdown lives in its own sub-group panel (SortSection) so the page can label
        // 图标排序 / 边距 separately instead of one undifferentiated list.
        SortSection.Children.Clear();
        SortSection.Children.Add(BuildSortRow());
        var b = _overlay.BoxInsets;
        InsetSection.Children.Add(BuildInsetRow("左边距", b.Left, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Left = v }; FlashSaved(); }));
        InsetSection.Children.Add(BuildInsetRow("右边距", b.Right, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Right = v }; FlashSaved(); }));
        InsetSection.Children.Add(BuildInsetRow("上边距", b.Top, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Top = v }; FlashSaved(); }));
        InsetSection.Children.Add(BuildInsetRow("下边距", b.Bottom, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Bottom = v }; FlashSaved(); }));
    }

    /// <summary>Dropdown choosing the in-fence icon ordering; re-arranges live when a fence is already drawn.</summary>
    private UIElement BuildSortRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 6),
        };
        row.Children.Add(new TextBlock { Text = "排序", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        var cb = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
        cb.Items.Add("按名称");
        cb.Items.Add("按类型");
        cb.Items.Add("按修改时间");
        cb.SelectedIndex = (int)_overlay.SortMode;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedIndex < 0) return;
            _overlay.SortMode = (FenceSortMode)cb.SelectedIndex;
            FlashSaved();
            if (_hasArranged) _overlay.ArrangeAndShow();
        };
        row.Children.Add(cb);
        return row;
    }

    private UIElement BuildInsetRow(string label, int initial, Action<int> onChanged)
    {
        var slider = new Slider { Minimum = -30, Maximum = 80, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        var valueText = new TextBlock { Width = 36, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            valueText.Text = v.ToString();
            onChanged(v);
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 5),
        };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(slider);
        row.Children.Add(valueText);

        slider.Value = initial;
        valueText.Text = initial.ToString();
        return row;
    }

    /// <summary>
    /// Builds the rule editor: one row per purpose box (title + keywords + delete), an add button,
    /// and a commit "保存并应用". Editing mutates a local draft; commit pushes it live via
    /// <see cref="FenceOverlayController.SetGrouping"/> and, if a layout is already drawn, re-arranges.
    /// Default seed boxes are protected from deletion.
    /// </summary>
    private void BuildRuleSection()
    {
        _ruleConfig = _overlay.Grouping;
        RebuildRuleRows();
    }

    private void RebuildRuleRows()
    {
        RuleSection.Children.Clear();

        foreach (var group in _ruleConfig.Groups)
            RuleSection.Children.Add(BuildGroupRow(group, deletable: !_defaultGroupTitles.Contains(group.Title)));

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        var add = new Button { Content = "+ 新增分组", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => AddGroup();
        addRow.Children.Add(add);
        RuleSection.Children.Add(addRow);

        var applyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        var apply = new Button { Content = "保存并应用", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += (_, _) => ApplyGrouping();
        applyRow.Children.Add(apply);
        RuleSection.Children.Add(applyRow);
    }

    private UIElement BuildGroupRow(SoftwareGroup group, bool deletable)
    {
        var titleBox = new TextBox { Text = group.Title, Width = 88, VerticalContentAlignment = VerticalAlignment.Center };
        var kwBox = new TextBox { Text = string.Join("，", group.Keywords), Width = 250, VerticalContentAlignment = VerticalAlignment.Center };
        titleBox.TextChanged += (_, _) => group.Title = titleBox.Text.Trim();
        kwBox.TextChanged += (_, _) => group.Keywords = new List<string>(KeywordsParser.Split(kwBox.Text));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(titleBox);
        row.Children.Add(new TextBlock { Text = "关键字", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        row.Children.Add(kwBox);

        var del = new Button { Content = "删除", Width = 44, Margin = new Thickness(6, 0, 0, 0), IsEnabled = deletable };
        if (deletable)
        {
            var captured = group;
            del.Click += (_, _) => { _ruleConfig.Groups.Remove(captured); RebuildRuleRows(); };
        }
        row.Children.Add(del);
        return row;
    }

    private void AddGroup()
    {
        _ruleConfig.Groups.Add(new SoftwareGroup("新分组", Array.Empty<string>()));
        RebuildRuleRows();
    }

    private void ApplyGrouping()
    {
        _overlay.SetGrouping(_ruleConfig);
        // Saving normalized the keywords (trimmed + lowercased), so re-read them into the rows;
        // boxes may also have been added/removed, so the category list is rebuilt from scratch.
        BuildRuleSection();
        BuildCategorySection();
        if (_hasArranged) _overlay.ArrangeAndShow();
        FlashSaved();
    }

    /// <summary>
    /// Builds the category manager: one row per fence box with a visibility checkbox and up/down
    /// buttons for the display order. Edits mutate a local draft; "保存并应用" pushes them through
    /// <see cref="FenceOverlayController.SetCategories"/> and redraws the overlay.
    /// </summary>
    private void BuildCategorySection()
    {
        _categoryOrder = new List<string>(_overlay.AvailableBoxTitles);
        _hiddenBoxes.Clear();
        foreach (var t in _overlay.Categories.Hidden) _hiddenBoxes.Add(t);
        RebuildCategoryRows();
    }

    private void RebuildCategoryRows()
    {
        CategorySection.Children.Clear();

        for (var i = 0; i < _categoryOrder.Count; i++)
            CategorySection.Children.Add(BuildCategoryRow(_categoryOrder[i], i));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        var apply = new Button { Content = "保存并应用", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += (_, _) => ApplyCategories();
        footer.Children.Add(apply);
        CategorySection.Children.Add(footer);
    }

    private UIElement BuildCategoryRow(string title, int index)
    {
        var box = new CheckBox
        {
            Content = title,
            IsChecked = !_hiddenBoxes.Contains(title),
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 190,
        };
        box.Checked += (_, _) => _hiddenBoxes.Remove(title);
        box.Unchecked += (_, _) => _hiddenBoxes.Add(title);

        var up = new Button { Content = "↑", Width = 24, Margin = new Thickness(6, 0, 0, 0), IsEnabled = index > 0 };
        var down = new Button { Content = "↓", Width = 24, Margin = new Thickness(4, 0, 0, 0), IsEnabled = index < _categoryOrder.Count - 1 };
        up.Click += (_, _) => MoveCategory(index, -1);
        down.Click += (_, _) => MoveCategory(index, 1);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(box);
        row.Children.Add(up);
        row.Children.Add(down);
        return row;
    }

    private void MoveCategory(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _categoryOrder.Count) return;
        (_categoryOrder[index], _categoryOrder[target]) = (_categoryOrder[target], _categoryOrder[index]);
        RebuildCategoryRows();
    }

    private void ApplyCategories()
    {
        _overlay.SetCategories(new FenceCategoryConfig
        {
            Order = new List<string>(_categoryOrder),
            Hidden = _categoryOrder.Where(_hiddenBoxes.Contains).ToList(),
        });
        // The controller drops titles that no longer exist — re-sync the draft so the rows match
        // what is actually stored, instead of drifting out of step with the overlay.
        _categoryOrder = new List<string>(_overlay.AvailableBoxTitles);
        RebuildCategoryRows();
        FlashSaved();
    }

    /// <summary>
    /// Wires the 启动 page: loads the persisted switch/delay into the controls and paints the status
    /// line. The controls themselves are declared in XAML (they are static), unlike the data-driven
    /// color/inset/rule sections.
    /// </summary>
    private void InitStartupSection()
    {
        _startup = StartupSettingsStore.Load(StartupSettingsStore.DefaultFilePath);
        _loadingStartup = true;
        RunAtLogonBox.IsChecked = _startup.RunAtLogon;
        LogonDelaySlider.Value = _startup.LogonDelaySeconds;
        LogonDelayText.Text = _startup.LogonDelaySeconds + " 秒";
        _loadingStartup = false;
        RefreshStartupStatus();
    }

    private void RunAtLogonBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup) return;
        SetRunAtLogon(RunAtLogonBox.IsChecked == true);
    }

    private void LogonDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var seconds = (int)e.NewValue;
        LogonDelayText.Text = seconds + " 秒";
        if (_loadingStartup) return;
        _startup = _startup with { LogonDelaySeconds = seconds };
        SaveStartupSettings();
        FlashSaved();
    }

    /// <summary>
    /// Single writer for the auto-start switch, shared by the settings checkbox and the tray item.
    /// The registry is only the cache — the persisted setting is the truth — so both entry points
    /// funnel through here and then re-sync every surface that displays the state.
    /// </summary>
    private void SetRunAtLogon(bool enabled)
    {
        if (!AutoStartService.Sync(enabled))
        {
            MessageBox.Show("无法写入开机自启动项（注册表被拒绝）。\n\n如果你用组策略或安全软件锁定了启动项，请在那里设置。",
                "桌面图标整理", MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncStartupUi(); // roll the controls back to whatever the registry actually says
            return;
        }

        _startup = _startup with { RunAtLogon = enabled };
        SaveStartupSettings();
        SyncStartupUi();
        FlashSaved();
    }

    private void SaveStartupSettings()
    {
        try { StartupSettingsStore.Save(StartupSettingsStore.DefaultFilePath, _startup); }
        catch (IOException) { /* best-effort */ }
    }

    /// <summary>Pushes the registry state back into the checkbox, the tray item and the status line.</summary>
    private void SyncStartupUi()
    {
        var enabled = AutoStartService.IsEnabled();
        _loadingStartup = true;
        RunAtLogonBox.IsChecked = enabled;
        _loadingStartup = false;
        if (_trayAutoStartItem is not null) _trayAutoStartItem.Checked = enabled;
        RefreshStartupStatus();
    }

    /// <summary>
    /// Status line under the switch. Also flags the one state the user actually needs to see: a
    /// registered entry pointing at a different (moved/deleted) copy of the exe.
    /// </summary>
    private void RefreshStartupStatus()
    {
        var cmd = AutoStartService.CurrentCommand();
        if (cmd is null)
        {
            StartupStatusText.Text = "当前未注册开机启动。";
            return;
        }

        var exe = AutoStartService.ResolveTargetExe();
        var expected = exe is null ? null : AutoStartService.BuildCommand(exe);
        StartupStatusText.Text = expected is not null && cmd != expected
            ? $"已注册开机启动，但指向的是另一个副本：\n{cmd}\n下次启动会自动修正为当前程序。"
            : $"已注册开机启动：\n{exe}";
    }

    private void PickColor(ColorChannel channel)
    {
        using var dlg = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(channel.Color.R, channel.Color.G, channel.Color.B),
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        var c = dlg.Color;
        channel.Color = channel.Color with { R = c.R, G = c.G, B = c.B };
        UpdateRow(channel);
        CommitAppearance();
    }

    private void UpdateRow(ColorChannel channel)
    {
        channel.Swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
            channel.Color.A, channel.Color.R, channel.Color.G, channel.Color.B));
        channel.AlphaText.Text = channel.Color.A.ToString();
    }

    /// <summary>
    /// Rebuilds the palette from the current channel values, pushes it to the overlay for the live
    /// preview, and persists it so the choice survives restarts.
    /// </summary>
    private void CommitAppearance()
    {
        var a = OverlayAppearance.Default;
        foreach (var ch in _colorChannels)
        {
            a = ch.Label switch
            {
                "框底色" => a with { Fill = ch.Color },
                "边框" => a with { Border = ch.Color },
                "标题栏" => a with { Header = ch.Color },
                _ => a with { HeaderText = ch.Color },
            };
        }
        _appearance = a;
        _overlay.Appearance = a;
        try { OverlayAppearanceStore.Save(AppearanceFilePath, a); }
        catch (IOException) { /* best-effort */ }
        // Boxes without a per-box override follow the global palette — repaint their swatches so
        // the 分类配色 rows show the color they would actually use now.
        BuildPerBoxColorSection();
        FlashSaved();
    }

    /// <summary>Shows the "已保存" hint briefly; re-flashing on rapid edits resets the fade-out.</summary>
    private void FlashSaved()
    {
        SavedHint.Visibility = Visibility.Visible;
        _savedTimer.Stop();
        _savedTimer.Start();
    }

    // "整理并显示分组" — reposition icons into clusters then draw the labeled overlay.
    private void GroupButton_Click(object sender, RoutedEventArgs e)
    {
        _hasArranged = true;
        _overlay.ArrangeAndShow();
    }

    // "恢复上次布局" — re-apply the last saved icon positions (clusters + manual tweaks).
    private void RestoreButton_Click(object sender, RoutedEventArgs e)
        => _overlay.RestoreSavedLayout();

    // "清除所有个性化设置" — wipe every per-box override (color / edge-padding / pinned layout) in
    // one shot. Guarded by a confirmation dialog because it is destructive; the global default edge
    // padding is intentionally not part of this (it is a base setting, cleared separately if wanted).
    private void ClearPersonalizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlay.HasPersonalization)
        {
            MessageBox.Show("当前没有任何分类框的个性化设置（单独的框颜色 / 框边距 / 固定位置）。",
                "清除个性化设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            "将清除所有分类框的个性化设置：\n" +
            "  • 单独设置的框颜色\n" +
            "  • 单独设置的框边距\n" +
            "  • 手动固定的框位置\n\n" +
            "清除后所有框将恢复为全局默认外观并自动打包。此操作不可撤销，是否继续？",
            "清除所有个性化设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _overlay.ResetAllPersonalization();
        // Rebuild the settings rows that reflect per-box overrides so they snap back to "no override".
        BuildPerBoxColorSection();
        BuildLayoutSection();
        FlashSaved();
    }

    // "所有框恢复自动布局" — unpin every box at once so they re-pack automatically. Colors and
    // edge-padding overrides are kept on purpose: position and appearance are managed separately.
    private void ResetLayoutsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlay.AnyPinnedLayouts)
        {
            MessageBox.Show("当前没有任何分类框被固定位置。",
                "恢复自动布局", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            "将取消所有分类框的固定位置，恢复为自动打包布局。\n" +
            "框颜色与框边距设置不受影响。是否继续？",
            "所有框恢复自动布局", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _overlay.ResetAllFenceLayouts();
        BuildLayoutSection();
        FlashSaved();
    }
}
