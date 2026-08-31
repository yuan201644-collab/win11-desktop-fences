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

namespace DesktopOrganizer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly FenceOverlayController _overlay;

    public MainWindow()
    {
        InitializeComponent();
        _overlay = new FenceOverlayController();
        Closed += (_, _) => _overlay.Dispose();
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

            // Detailed diagnostics: each icon's resolved info + where it was sent
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