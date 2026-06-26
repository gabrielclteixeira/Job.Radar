using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using JobRadar;

namespace JobRadar.Desktop.Localization;

/// <summary>
/// XAML markup extension: <c>Text="{l:T nav.home}"</c>. Binds to the live <see cref="Loc"/>
/// indexer so switching the language updates every label instantly (no restart).
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TExtension() { }
    public TExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
