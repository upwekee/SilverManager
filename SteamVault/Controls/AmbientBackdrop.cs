using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SteamVault.Controls;

/// <summary>
/// Deep-black base with two soft white light sources and an edge vignette.
/// Radial gradients only — static, no per-frame work.
/// </summary>
public sealed class AmbientBackdrop : Control
{
    private static readonly IBrush Base = new SolidColorBrush(Color.FromRgb(10, 10, 11));

    // Top-left key light. Pure white at very low alpha: it lifts the corner without
    // tinting it, so the page reads as black lit from above — never as a coloured wash.
    private static readonly RadialGradientBrush KeyLight = new()
    {
        GradientOrigin = new RelativePoint(0.14, 0.0, RelativeUnit.Relative),
        Center = new RelativePoint(0.14, 0.0, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.62, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.72, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(22, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(9, 255, 255, 255), 0.45),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
        }
    };

    // Bottom-right fill light — weaker than the key so the frame keeps a direction.
    private static readonly RadialGradientBrush FillLight = new()
    {
        GradientOrigin = new RelativePoint(0.96, 1.0, RelativeUnit.Relative),
        Center = new RelativePoint(0.96, 1.0, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.55, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.62, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(12, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
        }
    };

    // Vertical vignette: darkens under the top bar and above the status bar.
    private static readonly LinearGradientBrush Vignette = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(120, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.16),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.82),
            new GradientStop(Color.FromArgb(110, 0, 0, 0), 1)
        }
    };

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 1 || h < 1) return;

        var full = new Rect(0, 0, w, h);
        context.FillRectangle(Base, full);
        context.FillRectangle(KeyLight, full);
        context.FillRectangle(FillLight, full);
        context.FillRectangle(Vignette, full);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }
}
