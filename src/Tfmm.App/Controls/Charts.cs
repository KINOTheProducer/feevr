using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tfmm.App.Controls;

/// <summary>Anything that can sit in a <see cref="WeightPanel"/>.</summary>
public interface IWeighted
{
    double Weight { get; }
}

/// <summary>A segment of a proportional bar.</summary>
public sealed record BarPart(double Weight, IBrush Brush, string Label = "", IBrush? LabelBrush = null) : IWeighted
{
    public bool HasLabel => Label.Length > 0;
}

/// <summary>Lays children out in a row, widths proportional to each child's DataContext weight (<see cref="IWeighted"/>).</summary>
public sealed class WeightPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<WeightPanel, double>(nameof(Spacing), 2);
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<WeightPanel, double>(nameof(MinItemWidth), 3);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }

    static double W(Control c) => c.DataContext is IWeighted w ? Math.Max(w.Weight, 0) : 1;

    protected override Size MeasureOverride(Size available)
    {
        double h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var kids = Children.Where(c => c.IsVisible).ToList();
        if (kids.Count == 0) return final;
        var avail = Math.Max(0, final.Width - Spacing * (kids.Count - 1));
        var total = kids.Sum(W);
        var widths = kids.Select(c => total <= 0 ? avail / kids.Count : Math.Max(MinItemWidth, avail * W(c) / total)).ToArray();
        var scale = widths.Sum() > avail && widths.Sum() > 0 ? avail / widths.Sum() : 1;
        double x = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            var w = widths[i] * scale;
            kids[i].Arrange(new Rect(x, 0, w, final.Height));
            x += w + Spacing;
        }
        return final;
    }
}

/// <summary>Filled line chart for a rolling series. Values are clamped to [0, Maximum].</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 0);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Fixed number of x slots so a live chart scrolls from the right; 0 = fit the values.</summary>
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, MaximumProperty, StrokeProperty, CapacityProperty);

    static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#1C2129")), 1, DashStyle.Dash).ToImmutable();

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        for (int i = 1; i < 4; i++)
        {
            var y = Math.Round(b.Height * i / 4) + 0.5;
            ctx.DrawLine(Grid, new Point(0, y), new Point(b.Width, y));
        }
        var v = Values;
        if (v is null || v.Count < 2 || b.Width <= 0 || b.Height <= 0) return;
        var slots = Math.Max(Capacity, v.Count);
        var step = b.Width / (slots - 1);
        var x0 = (slots - v.Count) * step;
        var max = Maximum <= 0 ? 1 : Maximum;
        Point P(int i) => new(x0 + i * step, b.Height - Math.Clamp(v[i] / max, 0, 1) * (b.Height - 2) - 1);

        var stroke = Stroke ?? Brushes.Orange;
        var color = (stroke as ISolidColorBrush)?.Color ?? Colors.Orange;
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, b.Height), true);
            for (int i = 0; i < v.Count; i++) g.LineTo(P(i));
            g.LineTo(new Point(x0 + (v.Count - 1) * step, b.Height));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(color, 0.14), null, area);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false);
            for (int i = 1; i < v.Count; i++) g.LineTo(P(i));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(stroke, 2, lineJoin: PenLineJoin.Round), line);
        var last = P(v.Count - 1);
        ctx.DrawEllipse(stroke, null, last, 3.5, 3.5);
    }
}
