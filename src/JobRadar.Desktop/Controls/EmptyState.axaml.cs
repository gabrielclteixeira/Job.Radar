using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FluentAvalonia.UI.Controls;

namespace JobRadar.Desktop.Controls;

/// <summary>Shared empty-state card: icon in a soft circle + title + body + optional CTA, so every view's
/// "nothing here yet" moment looks the same. Properties are synced in code-behind (no bindings inside the
/// control — the ScoreDial pattern) so the host window's compiled-binding context stays out of it.</summary>
public partial class EmptyState : UserControl
{
    public static readonly StyledProperty<Symbol> SymbolProperty =
        AvaloniaProperty.Register<EmptyState, Symbol>(nameof(Symbol), Symbol.Find);
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Title), "");
    public static readonly StyledProperty<string> BodyProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Body), "");
    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<EmptyState, object?>(nameof(ActionContent));

    public Symbol Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public object? ActionContent { get => GetValue(ActionContentProperty); set => SetValue(ActionContentProperty, value); }

    public EmptyState()
    {
        AvaloniaXamlLoader.Load(this);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty || change.Property == TitleProperty
            || change.Property == BodyProperty || change.Property == ActionContentProperty)
            Sync();
    }

    private void Sync()
    {
        var icon = this.FindControl<SymbolIcon>("Icon");
        var title = this.FindControl<TextBlock>("TitleText");
        var body = this.FindControl<TextBlock>("BodyText");
        var action = this.FindControl<ContentControl>("Action");
        if (icon is null || title is null || body is null || action is null) return;
        icon.Symbol = Symbol;
        title.Text = Title;
        title.IsVisible = !string.IsNullOrWhiteSpace(Title);
        body.Text = Body;
        body.IsVisible = !string.IsNullOrWhiteSpace(Body);
        action.Content = ActionContent;
        action.IsVisible = ActionContent is not null;
    }
}
