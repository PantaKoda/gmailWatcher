using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using GmailWatcher.Desktop.Presentation;

namespace GmailWatcher.Desktop.Tests.UI;

/// <summary>Accent presets and status colors keep text readable on every surface the app paints, in both themes.</summary>
public sealed class AccentAndThemeTests
{
    private static IReadOnlyList<Color> SurfaceStops(ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource("SpaceSurfaceBrush", variant, out var brush));
        return ((LinearGradientBrush)brush!).GradientStops.Select(s => s.Color).ToList();
    }

    private static Color Brush(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var brush), key);
        return ((ISolidColorBrush)brush!).Color;
    }

    /// <summary>WCAG 2 contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte value)
            {
                var v = value / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [AvaloniaFact]
    public void Every_accent_is_readable_on_both_themes()
    {
        foreach (var preset in AccentPalette.Presets)
        {
            var (dark, light) = AccentPalette.Colors(preset.Hex);
            foreach (var stop in SurfaceStops(ThemeVariant.Dark))
            {
                Assert.True(Contrast(dark, stop) >= 4.5, $"{preset.Name} on dark {stop}: {Contrast(dark, stop):0.00}");
            }

            foreach (var stop in SurfaceStops(ThemeVariant.Light))
            {
                Assert.True(Contrast(light, stop) >= 4.5, $"{preset.Name} on light {stop}: {Contrast(light, stop):0.00}");
            }
        }
    }

    [AvaloniaFact]
    public void Secondary_text_and_every_status_color_are_readable_on_the_surface()
    {
        var failures = new List<string>();
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            foreach (var stop in SurfaceStops(variant))
            {
                foreach (var key in new[] { "HudDimBrush", "StatusSuccessBrush", "StatusFailureBrush", "StatusWarningBrush", "StatusRunningBrush" })
                {
                    var ratio = Contrast(Brush(key, variant), stop);
                    if (ratio < 4.5)
                    {
                        failures.Add($"{key} on {variant} {stop}: {ratio:0.00}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
    [AvaloniaFact]
    public void The_danger_button_text_is_readable_on_the_failure_color()
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var ratio = Contrast(Brush("DangerForegroundBrush", variant), Brush("StatusFailureBrush", variant));
            Assert.True(ratio >= 4.5, $"danger button on {variant}: {ratio:0.00}");
        }
    }

    [Fact]
    public void Unknown_or_missing_accents_fall_back_to_the_default()
    {
        Assert.Equal(AccentPalette.Presets[0], AccentPalette.Find(null));
        Assert.Equal(AccentPalette.Presets[0], AccentPalette.Find("#123456"));
        Assert.Equal(AccentPalette.Colors(null), AccentPalette.Colors("#3366FF")); // only presets are honored: no contrast guarantee otherwise
        Assert.Equal("Nebula violet", AccentPalette.Find("#9d7cff").Name);
    }

    [AvaloniaFact]
    public void Applying_an_accent_changes_the_hud_but_never_the_status_colors()
    {
        var application = Application.Current!;
        var runningBefore = Brush("StatusRunningBrush", ThemeVariant.Dark);
        try
        {
            AccentPalette.Apply(application, "#9D7CFF");

            Assert.Equal(Color.Parse("#9D7CFF"), Brush("HudAccentBrush", ThemeVariant.Dark));
            Assert.Equal(AccentPalette.Colors("#9D7CFF").Light, Brush("HudAccentBrush", ThemeVariant.Light));
            Assert.Equal(runningBefore, Brush("StatusRunningBrush", ThemeVariant.Dark));
        }
        finally
        {
            AccentPalette.Apply(application, null);
        }
    }
}
