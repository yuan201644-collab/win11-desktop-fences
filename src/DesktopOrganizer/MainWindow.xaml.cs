using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using DesktopOrganizer.Core.Classification;
using DesktopOrganizer.Core.Config;
using DesktopOrganizer.Core.Layout;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services;
using DesktopOrganizer.Win32;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace DesktopOrganizer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly FenceOverlayController _overlay;
    private readonly Forms.NotifyIcon _tray;
    private readonly List<ColorChannel> _colorChannels = new();
    private OverlayAppearance _appearance = OverlayAppearance.Default;
    private bool _exiting;
    private readonly System.Windows.Threading.DispatcherTimer _savedTimer;
    // Fences are foreground overlay sheets layered ON TOP of the real icons, so a crash-opaque box
    // body would hide the icons underneath. Cap the fill's alpha so it can get very solid but never
    // fully opaque — the icons always stay visible. Other channels (border/header/text) don't cover
    // icons, so only the fill (框底色) is capped.
    private const int MaxFillAlpha = 225;

    public MainWindow()
    {
        InitializeComponent();
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
        var b = _overlay.BoxInsets;
        InsetSection.Children.Add(BuildInsetRow("左边距", b.Left, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Left = v }; FlashSaved(); }));
        InsetSection.Children.Add(BuildInsetRow("右边距", b.Right, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Right = v }; FlashSaved(); }));
        InsetSection.Children.Add(BuildInsetRow("上边距", b.Top, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Top = v }; FlashSaved(); }));
        InsetSection.Children.Add(BuildInsetRow("下边距", b.Bottom, v => { _overlay.BoxInsets = _overlay.BoxInsets with { Bottom = v }; FlashSaved(); }));
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
        => _overlay.ArrangeAndShow();

    // "恢复上次布局" — re-apply the last saved icon positions (clusters + manual tweaks).
    private void RestoreButton_Click(object sender, RoutedEventArgs e)
        => _overlay.RestoreSavedLayout();

    // M2 PoC debug button — remove in M6
    private void ArrangeDebugButton_Click(object sender, RoutedEventArgs e)
    {
        var logPath = AppContext.BaseDirectory + "m2-poc.log";
        void Log(string line)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n"); } catch (IOException) { /* best-effort */ }
        }

        Log("=== click ===");
        try
        {
            using var provider = new SysListView32Provider();
            Log($"ctor ok: IsAvailable={provider.IsAvailable}, Handle=0x{provider.Handle.ToInt64():X}, Count={provider.Count}, Spacing=({provider.IconSpacingX},{provider.IconSpacingY})");
            var style = NativeMethods.GetWindowLong(provider.Handle, NativeMethods.GWL_STYLE);
            Log($"GWL_STYLE=0x{style:X8}, AUTOARRANGE_bit={(style & NativeMethods.LVS_AUTOARRANGE) != 0}");
            NativeMethods.GetWindowRect(provider.Handle, out var lv);
            Log($"ListView rect: L={lv.Left},T={lv.Top},R={lv.Right},B={lv.Bottom} (W={lv.Right - lv.Left},H={lv.Bottom - lv.Top})");
            var pX = NativeMethods.GetSystemMetrics(0); var pY = NativeMethods.GetSystemMetrics(1);
            var vL = NativeMethods.GetSystemMetrics(76); var vT = NativeMethods.GetSystemMetrics(77);
            var vW = NativeMethods.GetSystemMetrics(78); var vH = NativeMethods.GetSystemMetrics(79);
            Log($"Screen: primary={pX}x{pY}, virtual=({vL},{vT})-({vL + vW},{vT + vH})");
            if (!provider.IsAvailable)
            {
                Log("NOT AVAILABLE - desktop SysListView32 not found");
                MessageBox.Show("IsAvailable=False：未找到桌面 SysListView32 窗口。\n日志: " + logPath, "M2 PoC", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var icons = provider.GetIcons();
            Log($"icons read = {icons.Count}");
            foreach (var ic in icons)
                Log($"  icon[{ic.Index}] '{ic.Name}' path={ic.Path ?? "(none)"} pos=({ic.Position.X},{ic.Position.Y})");

            var engine = new ClassifierEngine();
            var config = new ClassifierConfig();
            var service = new DesktopLayoutService(provider, engine, config);

            var count = provider.Count;
            var maxRows = Math.Max(1, (int)(pY / (double)provider.IconSpacingY));
            Log($"layout: count={count}, spacing=({provider.IconSpacingX},{provider.IconSpacingY}), maxRows={maxRows}");

            var report = service.ArrangeIntoFence(new RectI(0, 0, pX, pY), maxRows);

            foreach (var (icon, cat, tgt) in report)
            {
                var linkApp = icon.Path is not null ? DesktopShellEnumerator.LinkTargetAppFromPath(icon.Path) : null;
                Log($"  [{cat}] icon[{icon.Index}] '{icon.Name}' path={icon.Path ?? "(none)"} linkApp={linkApp ?? "(null)"} → target=({tgt.X},{tgt.Y})");
            }

            var hist = report.GroupBy(r => r.Category).OrderBy(g => (int)g.Key)
                .ToDictionary(g => g.Key, g => g.Count());
            Log("category histogram:");
            foreach (var kv in hist) Log($"  {kv.Key} = {kv.Value}");

            var after = provider.GetIcons();
            Log($"AFTER readback: {after.Count} icons");
            foreach (var ic in after.Take(20))
                Log($"  AFTER icon[{ic.Index}] '{ic.Name}' pos=({ic.Position.X},{ic.Position.Y})");
            Log($"done: arranged {report.Count} icons; categories={hist.Count}");

            var summary = string.Join("\n", hist.OrderBy(kv => (int)kv.Key).Select(kv => $"  {kv.Key}: {kv.Value}"));
            MessageBox.Show($"Arranged {report.Count} desktop icons by category.\n\nCategories:\n{summary}\n\n日志: {logPath}", "M2 PoC", MessageBoxButton.OK);
        }
        catch (DesktopAutoArrangeException ex)
        {
            Log($"AutoArrange: {ex.Message}");
            MessageBox.Show(ex.Message + "\n日志: " + logPath, "Cannot arrange (M2 PoC)", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log($"EX: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"M2 PoC failed: {ex.GetType().Name}: {ex.Message}\n日志: {logPath}", "Cannot arrange (M2 PoC)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}