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
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        _overlay = new FenceOverlayController();

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