using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SteamVault.Models;

namespace SteamVault.Controls;

/// <summary>
/// Value-over-time line: smoothed silver stroke, gradient fill underneath,
/// endpoint dot and a value badge. Reuses the ChartBar list the bar charts take.
/// </summary>
public sealed class Sparkline : Control
{
    private static readonly Color Stroke = Color.Parse("#F0F0F2");
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#1F1F24"));
    private static readonly IBrush DotFill = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BadgeBg = new SolidColorBrush(Color.Parse("#EDEDF0"));
    // Badge is a light chip — its text has to be the page background, not white.
    private static readonly IBrush BadgeFg = new SolidColorBrush(Color.Parse("#0A0A0B"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6C6C75"));
    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.Parse("#4A4A52"));

    /// <summary>Light fading into the page — the curve looks lit from above, not tinted.</summary>
    private static readonly LinearGradientBrush AreaFill = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(56, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
        }
    };

    public static readonly StyledProperty<IList<ChartBar>?> PointsProperty =
        AvaloniaProperty.Register<Sparkline, IList<ChartBar>?>(nameof(Points));

    /// <summary>Text for the endpoint badge; hidden when empty.</summary>
    public static readonly StyledProperty<string> EndLabelProperty =
        AvaloniaProperty.Register<Sparkline, string>(nameof(EndLabel), "");

    public static readonly StyledProperty<bool> ShowAxisProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(ShowAxis), true);

    public IList<ChartBar>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string EndLabel
    {
        get => GetValue(EndLabelProperty);
        set => SetValue(EndLabelProperty, value);
    }

    public bool ShowAxis
    {
        get => GetValue(ShowAxisProperty);
        set => SetValue(ShowAxisProperty, value);
    }

    static Sparkline()
    {
        AffectsRender<Sparkline>(PointsProperty, EndLabelProperty, ShowAxisProperty);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8) return;

        var pts = Points;
        if (pts == null || pts.Count < 2)
        {
            var ft = Text(pts is { Count: 1 } ? "need more data" : "no data yet", 12, EmptyBrush);
            context.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
            return;
        }

        var badge = string.IsNullOrEmpty(EndLabel) ? null : Text(EndLabel, 11, BadgeFg, FontWeight.Bold);
        var padL = 6.0;
        var padR = badge is null ? 12.0 : badge.Width + 26;
        var padT = 12.0;
        var padB = ShowAxis ? 22.0 : 8.0;
        var chartW = w - padL - padR;
        var chartH = h - padT - padB;
        if (chartW < 4 || chartH < 4) return;

        if (ShowAxis)
        {
            var gridPen = new Pen(GridBrush, 1);
            for (var g = 0; g <= 2; g++)
            {
                var y = padT + chartH * g / 2.0;
                context.DrawLine(gridPen, new Point(padL, y), new Point(padL + chartW, y));
            }
        }

        var n = pts.Count;
        var step = chartW / (n - 1);
        var xy = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var norm = Math.Clamp(pts[i].Normalized, 0, 1);
            // inset the curve so the stroke never clips against the frame
            xy[i] = new Point(padL + step * i, padT + chartH - (chartH - 4) * norm - 2);
        }

        var line = BuildCurve(xy);

        // area under the curve, closed along the baseline
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            var baseY = padT + chartH;
            ctx.BeginFigure(new Point(xy[0].X, baseY), true);
            ctx.LineTo(xy[0]);
            AppendCurve(ctx, xy);
            ctx.LineTo(new Point(xy[n - 1].X, baseY));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(AreaFill, null, area);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Stroke), 2,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        // endpoint: halo, dot, badge
        var end = xy[n - 1];
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)), null, end, 7, 7);
        context.DrawEllipse(DotFill, new Pen(new SolidColorBrush(Stroke), 2), end, 3.5, 3.5);

        if (badge is not null)
        {
            var bw = badge.Width + 14;
            var bh = badge.Height + 8;
            var bx = Math.Min(end.X + 10, w - bw - 2);
            var by = Math.Clamp(end.Y - bh / 2, 2, h - bh - 2);
            context.FillRectangle(BadgeBg, new Rect(bx, by, bw, bh), (float)(bh / 2));
            context.DrawText(badge, new Point(bx + 7, by + 4));
        }

        if (ShowAxis)
        {
            // first / middle / last only — a full axis would crowd the panel
            DrawTick(context, pts[0].Label, xy[0].X, padT + chartH + 6, padL, padL + chartW);
            if (n > 2)
                DrawTick(context, pts[n / 2].Label, xy[n / 2].X, padT + chartH + 6, padL, padL + chartW);
            DrawTick(context, pts[n - 1].Label, xy[n - 1].X, padT + chartH + 6, padL, padL + chartW);
        }
    }

    private void DrawTick(DrawingContext context, string label, double cx, double y, double min, double max)
    {
        if (string.IsNullOrEmpty(label)) return;
        var ft = Text(label.Length > 6 ? label[..5] : label, 10, LabelBrush);
        context.DrawText(ft, new Point(Math.Clamp(cx - ft.Width / 2, min, max - ft.Width), y));
    }

    /// <summary>Catmull-Rom-ish smoothing via cubic segments through every point.</summary>
    private static StreamGeometry BuildCurve(Point[] p)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p[0], false);
            AppendCurve(ctx, p);
            ctx.EndFigure(false);
        }
        return geo;
    }

    private static void AppendCurve(StreamGeometryContext ctx, Point[] p)
    {
        for (var i = 0; i < p.Length - 1; i++)
        {
            var p0 = i == 0 ? p[0] : p[i - 1];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = i + 2 < p.Length ? p[i + 2] : p2;

            // tension 1/6 keeps the curve tight to the data — no phantom overshoot
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.CubicBezierTo(c1, c2, p2);
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, weight), size, brush);
}
