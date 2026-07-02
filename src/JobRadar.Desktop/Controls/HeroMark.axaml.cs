using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace JobRadar.Desktop.Controls;

/// <summary>The home hero: the brand's concentric-ring mark with an ambient radar ping and a breathing
/// centre dot. The one animated signature moment of the idle app (the sidebar mark stays static).</summary>
public partial class HeroMark : UserControl
{
    public HeroMark() => AvaloniaXamlLoader.Load(this);
}
