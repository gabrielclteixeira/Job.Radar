using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JobRadar.Desktop.Controls;

/// <summary>One spoke of the skills radar: how much the candidate's matched market DEMANDS this skill vs. how
/// much they already have it (both normalised 0..1).</summary>
public sealed record SkillAxis(string Label, double Demand, double You);

/// <summary>
/// A small spider/radar chart (on-brand with the radar mark). Axes are the top demanded skills; one polygon is
/// market DEMAND, the other is what YOU have — a high-demand/low-you spoke is a visible gap notch. Brushes are
/// styled properties so the view can bind them to the app palette via DynamicResource.
/// </summary>
public class SkillsRadar : Control
{
    public static readonly StyledProperty<IReadOnlyList<SkillAxis>?> ItemsSourceProperty =
        AvaloniaProperty.Register<SkillsRadar, IReadOnlyList<SkillAxis>?>(nameof(ItemsSource));
    public static readonly StyledProperty<IBrush?> DemandBrushProperty =
        AvaloniaProperty.Register<SkillsRadar, IBrush?>(nameof(DemandBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> YouBrushProperty =
        AvaloniaProperty.Register<SkillsRadar, IBrush?>(nameof(YouBrush), Brushes.SeaGreen);
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<SkillsRadar, IBrush?>(nameof(GridBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<SkillsRadar, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    public IReadOnlyList<SkillAxis>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IBrush? DemandBrush { get => GetValue(DemandBrushProperty); set => SetValue(DemandBrushProperty, value); }
    public IBrush? YouBrush { get => GetValue(YouBrushProperty); set => SetValue(YouBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    static SkillsRadar()
        => AffectsRender<SkillsRadar>(ItemsSourceProperty, DemandBrushProperty, YouBrushProperty, GridBrushProperty, LabelBrushProperty);

    public override void Render(DrawingContext ctx)
    {
        var axes = ItemsSource;
        if (axes is null || axes.Count < 3) return;
        int n = axes.Count;

        var b = Bounds;
        var center = new Point(b.Width / 2, b.Height / 2);
        double radius = Math.Max(10, Math.Min(b.Width, b.Height) / 2 - 34); // leave room for labels

        var gridPen = new Pen(GridBrush ?? Brushes.Gray, 1);
        foreach (double frac in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            var ring = new List<Point>();
            for (int i = 0; i < n; i++) ring.Add(Polar(center, radius * frac, Angle(i, n)));
            ctx.DrawGeometry(null, gridPen, new PolylineGeometry(ring, true));
        }
        for (int i = 0; i < n; i++)
            ctx.DrawLine(gridPen, center, Polar(center, radius, Angle(i, n)));

        DrawPolygon(ctx, center, radius, n, axes.Select(a => a.Demand), DemandBrush ?? Brushes.MediumPurple, 0.22, 2);
        DrawPolygon(ctx, center, radius, n, axes.Select(a => a.You), YouBrush ?? Brushes.SeaGreen, 0.0, 2);

        var labelBrush = LabelBrush ?? Brushes.Gray;
        for (int i = 0; i < n; i++)
        {
            var p = Polar(center, radius + 13, Angle(i, n));
            var ft = new FormattedText(axes[i].Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10.5, labelBrush);
            double ox = p.X < center.X - 2 ? -ft.Width : p.X > center.X + 2 ? 0 : -ft.Width / 2;
            double oy = p.Y < center.Y - 2 ? -ft.Height : p.Y > center.Y + 2 ? 0 : -ft.Height / 2;
            ctx.DrawText(ft, new Point(p.X + ox, p.Y + oy));
        }
    }

    private static void DrawPolygon(DrawingContext ctx, Point center, double radius, int n,
        IEnumerable<double> values, IBrush brush, double fillOpacity, double thickness)
    {
        var pts = new List<Point>();
        int i = 0;
        foreach (var v in values) { pts.Add(Polar(center, radius * Math.Clamp(v, 0, 1), Angle(i, n))); i++; }
        IBrush? fill = fillOpacity > 0 ? new SolidColorBrush(AsColor(brush), fillOpacity) : null;
        ctx.DrawGeometry(fill, new Pen(brush, thickness), new PolylineGeometry(pts, true));
    }

    private static double Angle(int i, int n) => -Math.PI / 2 + i * 2 * Math.PI / n;
    private static Point Polar(Point c, double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    private static Color AsColor(IBrush b) => (b as ISolidColorBrush)?.Color ?? Colors.MediumPurple;
}
