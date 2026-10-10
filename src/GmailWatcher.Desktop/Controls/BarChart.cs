using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GmailWatcher.Desktop.Controls;

/// <summary>
/// A plain column chart: one bar per value, scaled to the largest, in the theme's accent. Drawn only when its values, size
/// or theme change, never animated. Decorative: the numbers it shows are always also given as text next to it.
/// </summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<double>?>(nameof(Values));

    static BarChart()
    {
        AffectsRender<BarChart>(ValuesProperty);
    }

    public BarChart()
    {
        MinHeight = 64;
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is not { Count: > 0 } || Bounds.Width < 2 || Bounds.Height < 2)
        {
            return;
        }

        var accent = this.TryFindResource("HudAccentBrush", ActualThemeVariant, out var accentResource) && accentResource is ISolidColorBrush a ? a.Color : Colors.Cyan;
        var frame = this.TryFindResource("HudFrameBrush", ActualThemeVariant, out var frameResource) && frameResource is ISolidColorBrush f ? f.Color : Colors.Gray;

        var max = values.Max();
        var width = Bounds.Width;
        var height = Bounds.Height;
        var slot = width / values.Count;
        var gap = slot > 6 ? 1.5 : slot > 3 ? 1 : 0;
        var bar = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));

        // A baseline, so an empty month still shows as a month.
        context.DrawLine(new Pen(new SolidColorBrush(frame), 1), new Point(0, height - 0.5), new Point(width, height - 0.5));
        if (max <= 0)
        {
            return;
        }

        for (var i = 0; i < values.Count; i++)
        {
            var h = Math.Max(values[i] > 0 ? 1.5 : 0, values[i] / max * (height - 3));
            var rect = new Rect((i * slot) + (gap / 2), height - 1 - h, Math.Max(1, slot - gap), h);
            context.DrawRectangle(bar, null, rect, 1, 1);
        }
    }
}
