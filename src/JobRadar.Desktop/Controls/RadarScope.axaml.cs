using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace JobRadar.Desktop.Controls;

/// <summary>Animated radar sweep used in the scanning state — the product's signature motif.</summary>
public partial class RadarScope : UserControl
{
    public RadarScope() => AvaloniaXamlLoader.Load(this);
}
