using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private bool _exiting;
    private bool _hasArranged;
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

        // Closing the window (×) no longer quits — the app keeps running in the background so the
        // overlay can follow the desktop. The tray icon is the way back in (and the way out).
        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "桌面图标整理（运行中，回到桌面即显示分类框）",
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
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开控制窗", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        return menu;
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        Application.Current.Shutdown();
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
        InsetSection.Children.Add(BuildSortRow());
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
