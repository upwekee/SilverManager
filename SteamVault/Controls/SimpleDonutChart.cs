using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SteamVault.Models;

namespace SteamVault.Controls;

/// <summary>
/// Multi-segment donut: thin ring, gaps between segments, quiet track behind them.
/// </summary>
public sealed class SimpleDonutChart : Control
{
    private static readonly Color TrackColor = Color.Parse("#1B1B1F");
    /// <summary>Unresolvable segment colour: a quiet mid-grey, never a glowing one.</summary>
    private static readonly Color Fallback = Color.Parse("#90909A");
    private static readonly IBrush CenterTopBrush = new SolidColorBrush(Color.Parse("#FAFAFA"));
    private static readonly IBrush CenterBottomBrush = new SolidColorBrush(Color.Parse("#6C6C75"));
    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.Parse("#4A4A52"));

    /// <summary>Degrees of empty space between adjacent segments.</summary>
    private const double SegmentGapDeg = 2.4;

    public static readonly StyledProperty<IList<PortfolioItemRow>?> SlicesProperty =
        AvaloniaProperty.Register<SimpleDonutChart, IList<PortfolioItemRow>?>(nameof(Slices));

    public static readonly StyledProperty<string> CenterTopProperty =
        AvaloniaProperty.Register<SimpleDonutChart, string>(nameof(CenterTop), "");

    public static readonly StyledProperty<string> CenterBottomProperty =
        AvaloniaProperty.Register<SimpleDonutChart, string>(nameof(CenterBottom), "");

    public IList<PortfolioItemRow>? Slices
    {
        get => GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public string CenterTop
    {
        get => GetValue(CenterTopProperty);
        set => SetValue(CenterTopProperty, value);
    }

    public string CenterBottom
    {
        get => GetValue(CenterBottomProperty);
        set => SetValue(CenterBottomProperty, value);
    }

    static SimpleDonutChart()
    {
        AffectsRender<SimpleDonutChart>(SlicesProperty, CenterTopProperty, CenterBottomProperty);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 16 || h < 16) return;

        var size = Math.Min(w, h);
        var cx = w / 2;
        var cy = h / 2;
        var outer = size * 0.46;
        var inner = size * 0.335;

        // track ring — the donut keeps its shape even with no data
        DrawRing(context, cx, cy, outer, inner, 0, 360, TrackColor);

        var slices = Slices;
        var total = slices?.Sum(s => Math.Max(0, s.Count)) ?? 0;

        if (slices == null || slices.Count == 0 || total <= 0)
        {
            var empty = Text("no data", size * 0.075, EmptyBrush);
            context.DrawText(empty, new Point(cx - empty.Width / 2, cy - empty.Height / 2));
            return;
        }

        double angle = -90; // 12 o'clock
        foreach (var s in slices)
        {
            var value = Math.Max(0, s.Count);
            if (value <= 0) continue;

            var sweep = 360.0 * value / total;
            if (sweep < 0.5) { angle += sweep; continue; }

            // carve the gap out of the segment, never out of the neighbour
            var drawn = Math.Max(sweep * 0.35, sweep - SegmentGapDeg);
            var color = TryParseColor(s.Color) ?? Fallback;
            DrawRing(context, cx, cy, outer, inner, angle, drawn, color);
            angle += sweep;
        }

        if (!string.IsNullOrEmpty(CenterTop))
        {
            var top = Text(CenterTop, size * 0.155, CenterTopBrush, FontWeight.Bold);
            context.DrawText(top, new Point(cx - top.Width / 2, cy - top.Height + 1));
        }
        if (!string.IsNullOrEmpty(CenterBottom))
        {
            var bot = Text(CenterBottom, size * 0.072, CenterBottomBrush);
            context.DrawText(bot, new Point(cx - bot.Width / 2, cy + 4));
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, weight), size, brush);

    private static Color? TryParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Color.Parse(hex.StartsWith('#') ? hex : "#" + hex); }
        catch { return null; }
    }

    private static void DrawRing(DrawingContext context, double cx, double cy,
        double outerR, double innerR, double startDeg, double sweepDeg, Color color)
    {
        if (sweepDeg <= 0) return;

        // step count follows the arc so short segments stay cheap and long ones stay smooth
        var steps = Math.Clamp((int)(sweepDeg / 3), 4, 64);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            static double ToRad(double d) => d * Math.PI / 180.0;
            var start = ToRad(startDeg);
            var end = ToRad(startDeg + sweepDeg);

            Point Pt(double r, double a) => new(cx + r * Math.Cos(a), cy + r * Math.Sin(a));

            ctx.BeginFigure(Pt(outerR, start), true);
            for (var i = 1; i <= steps; i++)
                ctx.LineTo(Pt(outerR, start + (end - start) * i / steps));
            ctx.LineTo(Pt(innerR, end));
            for (var i = steps - 1; i >= 0; i--)
                ctx.LineTo(Pt(innerR, start + (end - start) * i / steps));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(color), null, geo);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SlicesProperty || change.Property == CenterTopProperty ||
            change.Property == CenterBottomProperty)
            InvalidateVisual();
    }
}
