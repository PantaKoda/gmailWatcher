using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GmailWatcher.Desktop.Controls;

/// <summary>
/// A faint, static star field for the space-station surface. Deterministic (same stars every
/// time), drawn only when the size or theme changes, never animated: it costs nothing while idle.
/// </summary>
public sealed class StarField : Control
{
    private const int StarsPer10kPixels = 3;

    public StarField()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var color = this.TryFindResource("StarBrush", ActualThemeVariant, out var resource) && resource is ISolidColorBrush brush
            ? brush.Color
            : Colors.White;

        var random = new Random(4242);
        var count = (int)(Bounds.Width * Bounds.Height / 10_000 * StarsPer10kPixels);
        for (var i = 0; i < count; i++)
        {
            var x = random.NextDouble() * Bounds.Width;
            var y = random.NextDouble() * Bounds.Height;
            var bright = random.NextDouble();
            var size = bright > 0.93 ? 1.3 : 0.7;
            var alpha = (byte)(color.A * (0.25 + (0.6 * bright)));
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), null, new Point(x, y), size, size);
        }
    }
}

/// <summary>HUD-style corner brackets framing a panel. Decorative; not hit-testable.</summary>
public sealed class HudCorners : Control
{
    public static readonly StyledProperty<double> LengthProperty =
        AvaloniaProperty.Register<HudCorners, double>(nameof(Length), 14);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<HudCorners, IBrush?>(nameof(Stroke));

    static HudCorners()
    {
        AffectsRender<HudCorners>(LengthProperty, StrokeProperty);
    }

    public HudCorners()
    {
        IsHitTestVisible = false;
    }

    public double Length
    {
        get => GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Stroke is null)
        {
            return;
        }

        var pen = new Pen(Stroke, 2, lineCap: PenLineCap.Square);
        var (w, h, l, i) = (Bounds.Width, Bounds.Height, Length, 3.0);
        context.DrawLine(pen, new Point(i, i + l), new Point(i, i));
        context.DrawLine(pen, new Point(i, i), new Point(i + l, i));
        context.DrawLine(pen, new Point(w - i - l, i), new Point(w - i, i));
        context.DrawLine(pen, new Point(w - i, i), new Point(w - i, i + l));
        context.DrawLine(pen, new Point(i, h - i - l), new Point(i, h - i));
        context.DrawLine(pen, new Point(i, h - i), new Point(i + l, h - i));
        context.DrawLine(pen, new Point(w - i - l, h - i), new Point(w - i, h - i));
        context.DrawLine(pen, new Point(w - i, h - i), new Point(w - i, h - i - l));
    }
}
