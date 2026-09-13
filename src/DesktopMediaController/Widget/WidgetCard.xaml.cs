using System.Windows;
using System.Windows.Controls;

namespace DesktopMediaController.Widget;

/// <summary>
/// The card the user actually sees. Layout and nothing else: it publishes its design size in DIPs
/// and raises <see cref="CloseRequested"/>. It knows nothing about window handles, dragging,
/// monitors or media sessions, which is what keeps it trivially replaceable in phase 2.
/// </summary>
public sealed partial class WidgetCard : UserControl
{
    /// <summary>
    /// Design width in DIPs. The window's <b>physical</b> width is this times the current monitor
    /// scale, so the card keeps its apparent size across the 100 % / 125 % monitor mix.
    /// </summary>
    public const double DesignWidthDip = 320;

    /// <summary>Design height in DIPs. See <see cref="DesignWidthDip"/>.</summary>
    public const double DesignHeightDip = 112;

    public WidgetCard()
    {
        InitializeComponent();
        Width = DesignWidthDip;
        Height = DesignHeightDip;
    }

    /// <summary>Raised when the user asks the widget to close.</summary>
    public event EventHandler? CloseRequested;

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
