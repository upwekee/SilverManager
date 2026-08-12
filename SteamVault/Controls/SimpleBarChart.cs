using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SteamVault.Models;

namespace SteamVault.Controls;

/// <summary>
/// Lightweight bar chart: gradient bars, baseline gridlines, value label on the peak.
/// No extra chart package; pure DrawingContext.
/// </summary>
public sealed class SimpleBarChart : Control
{
    // Bars are lit from above: bright silver at the cap, dimmer at the base.
    private static readonly Color AccentTop = Color.Parse("#D8D8DE");
    private static readonly Color AccentBottom = Color.Parse("#7A7A84");
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#1F1F24"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6C6C75"));
    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#FAFAFA"));
    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.Parse("#4A4A52"));

    public static readonly StyledProperty<IList<ChartBar>?> BarsProperty =
        AvaloniaProperty.Register<SimpleBarChart, IList<ChartBar>?>(nameof(Bars));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<SimpleBarChart, IBrush?>(nameof(BarBrush));

    /// <summary>Draw the tallest bar's label above it.</summary>
    public static readonly StyledProperty<bool> ShowPeakProperty =
        AvaloniaProperty.Register<SimpleBarChart, bool>(nameof(ShowPeak), true);

    public IList<ChartBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>Optional override; defaults to the silver bar gradient.</summary>
    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public bool ShowPeak
    {
        get => GetValue(ShowPeakProperty);
        set => SetValue(ShowPeakProperty, value);
    }

    static SimpleBarChart()
    {
        AffectsRender<SimpleBarChart>(BarsProperty, BarBrushProperty, ShowPeakProperty);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8) return;

        var bars = Bars;
        if (bars == null || bars.Count == 0)
        {
            DrawCentered(context, "no data yet", w, h);
            return;
        }

        const double padL = 4;
        const double padR = 4;
        const double padT = 22;
        const double padB = 24;
        var chartW = w - padL - padR;
        var chartH = h - padT - padB;
        if (chartW < 4 || chartH < 4) return;

        var n = bars.Count;
        var baseY = padT + chartH;

        // horizontal gridlines — quiet depth cue behind the bars
        var gridPen = new Pen(GridBrush, 1);
        for (var g = 0; g <= 3; g++)
        {
            var y = padT + chartH * g / 3.0;
            context.DrawLine(gridPen, new Point(padL, y), new Point(w - padR, y));
        }

        var slot = chartW / n;
        var barW = Math.Max(5, Math.Min(38, slot * 0.56));
        var fill = BarBrush ?? new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AccentTop, 0),
                new GradientStop(AccentBottom, 1)
            }
        };

        var peak = 0;
        for (var i = 1; i < n; i++)
            if (bars[i].Normalized > bars[peak].Normalized) peak = i;

        for (var i = 0; i < n; i++)
        {
            var b = bars[i];
            var norm = Math.Clamp(b.Normalized, 0, 1);
            var bh = Math.Max(3, chartH * norm);
            var x = padL + slot * i + (slot - barW) / 2;
            var y = baseY - bh;
            var radius = (float)Math.Min(6, barW / 2);

            // empty-value track keeps the rhythm visible on zero days
            if (norm < 0.02)
                context.FillRectangle(GridBrush, new Rect(x, baseY - 3, barW, 3), radius);
            else
                context.FillRectangle(fill, new Rect(x, y, barW, bh), radius);

            var label = b.Label.Length > 6 ? b.Label[..5] : b.Label;
            var ft = Text(label, 10, LabelBrush);
            context.DrawText(ft, new Point(x + barW / 2 - ft.Width / 2, baseY + 7));

            if (ShowPeak && i == peak && norm >= 0.02 && !string.IsNullOrEmpty(b.ValueText))
            {
                var vt = Text(b.ValueText, 11, PeakBrush, FontWeight.Bold);
                context.DrawText(vt, new Point(
                    Math.Clamp(x + barW / 2 - vt.Width / 2, padL, w - padR - vt.Width),
                    Math.Max(2, y - vt.Height - 5)));
            }
        }
    }

    private void DrawCentered(DrawingContext context, string text, double w, double h)
    {
        var ft = Text(text, 12, EmptyBrush);
        context.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, weight), size, brush);
}
