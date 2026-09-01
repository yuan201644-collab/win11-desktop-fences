using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
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
        BuildInsetSection();
        BuildRuleSection();
        BuildCategorySection();
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
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Right-click on a fence header (incl. a collapsed "隐藏图标栏" tab) — build a context menu of
    /// extra per-fence actions at the cursor. Same actions the tray exposes, plus a one-box
    /// 展开/折叠 and the all-at-once 全部展开 / 全部折叠, so the desktop itself is fully operable
    /// without opening the settings window.
    /// </summary>
    private void OnFenceContextMenu(string title, int x, int y)
    {
        var cm = new System.Windows.Controls.ContextMenu();
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        cm.PlacementTarget = this;

        var collapsed = _overlay.IsCollapsed(title);
        var toggle = new System.Windows.Controls.MenuItem
        {
            Header = $"{(collapsed ? "展开" : "折叠")}「{title}」",
            FontWeight = FontWeights.SemiBold,
        };
        toggle.Click += (_, _) => _overlay.ToggleFence(title);
        cm.Items.Add(toggle);

        cm.Items.Add(new System.Windows.Controls.Separator());

        var expandAll = new System.Windows.Controls.MenuItem { Header = "全部展开" };
        expandAll.Click += (_, _) => _overlay.ExpandAll();
        cm.Items.Add(expandAll);

        var collapseAll = new System.Windows.Controls.MenuItem { Header = "全部折叠" };
        collapseAll.Click += (_, _) => _overlay.CollapseAll();
        cm.Items.Add(collapseAll);

        var arrange = new System.Windows.Controls.MenuItem { Header = "重新整理全部" };
        arrange.Click += (_, _) => { _hasArranged = true; _overlay.ArrangeAndShow(); };
        cm.Items.Add(arrange);

        cm.Items.Add(new System.Windows.Controls.Separator());

        var settings = new System.Windows.Controls.MenuItem { Header = "打开设置" };
        settings.Click += (_, _) => ShowFromTray();
        cm.Items.Add(settings);

        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();
        cm.Items.Add(exit);

        cm.IsOpen = true;
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

        var exe = AutoStartService.CurrentExePath();
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
}
