using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace JobRadar.Desktop.Controls;

/// <summary>Circular score gauge: a track ring + an arc filled to Score/100, coloured by tier.</summary>
public partial class ScoreDial : UserControl
{
    public static readonly StyledProperty<int> ScoreProperty =
        AvaloniaProperty.Register<ScoreDial, int>(nameof(Score));
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ScoreDial, string>(nameof(Label), "");

    public int Score { get => GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public ScoreDial()
    {
        AvaloniaXamlLoader.Load(this);
        Redraw();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScoreProperty || change.Property == LabelProperty)
            Redraw();
    }

    private void Redraw()
    {
        var arc = this.FindControl<Path>("Arc");
        var num = this.FindControl<TextBlock>("Num");
        var lbl = this.FindControl<TextBlock>("Lbl");
        if (arc is null || num is null || lbl is null) return;

        int s = Math.Clamp(Score, 0, 100);
        // Ring colour carries the tier; the number stays neutral (theme-aware Text) for legibility/calm.
        var ring = s >= 80 ? Color.Parse("#34D17F") : s >= 60 ? Color.Parse("#8E72FF") : Color.Parse("#8E8AA3");
        arc.Stroke = new SolidColorBrush(ring);
        num.Text = s.ToString();
        lbl.Text = Label ?? "";

        const double size = 52, thickness = 5;
        double r = (size - thickness) / 2, cx = size / 2, cy = size / 2;
        double sweep = Math.Min(359.99, s / 100.0 * 360.0);
        double a0 = -90 * Math.PI / 180.0, a1 = (-90 + sweep) * Math.PI / 180.0;
        var start = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        var end = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));

        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(start, false);
            c.ArcTo(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
            c.EndFigure(false);
        }
        arc.Data = geo;
    }
}
