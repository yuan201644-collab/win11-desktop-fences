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
    public MainWindow()
    {
        InitializeComponent();
    }

    // M2 PoC debug button — remove in M6
    private void ArrangeDebugButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var provider = new SysListView32Provider();
            var service = new DesktopLayoutService(provider, new ClassifierEngine(), new ClassifierConfig());
            var report = service.ArrangeIntoFence(new RectI(0, 0, 800, 600), 4);
            MessageBox.Show($"Arranged {report.Count} desktop icons into a 4-column grid.", "M2 PoC", MessageBoxButton.OK);
        }
        catch (DesktopAutoArrangeException ex)
        {
            MessageBox.Show(ex.Message, "Cannot arrange (M2 PoC)", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}