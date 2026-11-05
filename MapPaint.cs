using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;

namespace CullByte;

internal static class MapPaint
{
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Semibold = new("Segoe UI", weight: FontWeight.SemiBold);
    public static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
    public static IBrush Ink(bool light) => light ? Brushes.Black : Brushes.White;
    public static IBrush Accent(bool light) => Brush(light ? "#006650" : "#6BE4BF");
    public static IBrush Surface(bool light) => Brush(light ? "#E5EDF0" : "#0D151D");
    public static IBrush Outline(bool light) => Brush(light ? "#A0B3BE" : "#3A4D5A");

    public static Color TileColor(Color color, bool light, double tint = 0)
    {
        if (light) return Blend(color, Color.Parse("#F3F7F9"), Math.Clamp(0.57 + tint, 0, 0.78));
        color = Blend(color, Color.Parse("#BDDADE"), tint);
        while (Luminance(color) > 0.155) color = Blend(color, Colors.Black, 0.12);
        return color;
    }

    public static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));

    private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + (to - from) * amount);

    private static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            var normalized = component / 255d;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    // Choose the higher-contrast ink for custom colors, including user-supplied colors.
    public static IBrush OnTile(Color color) => Luminance(color) > 0.179 ? Brushes.Black : Brushes.White;

    public static void Text(DrawingContext context, string value, Rect area, double size, IBrush brush, bool bold = false)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        using var clip = context.PushClip(area);
        using var layout = new TextLayout(value, bold ? Semibold : Regular, size, brush,
            textWrapping: TextWrapping.NoWrap, textTrimming: TextTrimming.CharacterEllipsis,
            maxWidth: area.Width, maxHeight: area.Height, maxLines: 1);
        layout.Draw(context, area.TopLeft);
    }
}

internal sealed class MapPreview : Control
{
    public MapPreview()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Avalonia.Automation.AutomationProperties.SetName(this, "Example map. The largest tile is Projects at 46 GiB.");
    }

    public override void Render(DrawingContext context)
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        var area = new Rect(Bounds.Size).Deflate(1);
        if (area.Width <= 12 || area.Height <= 12) return;
        context.DrawRectangle(MapPaint.Surface(light), new Pen(MapPaint.Outline(light)), area, 9, 9);
        var map = area.Deflate(8);
        Tile(0, 0, 0.56, 1, "#315865", "Projects", "46 GiB");
        Tile(0.56, 0, 0.44, 0.52, "#328278", "Media", "19 GiB");
        Tile(0.56, 0.52, 0.27, 0.48, "#746183", "Archives", "11 GiB");
        Tile(0.83, 0.52, 0.17, 0.27, "#97604A", "", "");
        Tile(0.83, 0.79, 0.17, 0.21, "#526774", "", "");

        void Tile(double x, double y, double width, double height, string fill, string title, string size)
        {
            var rect = new Rect(map.X + x * map.Width, map.Y + y * map.Height, width * map.Width, height * map.Height).Deflate(2);
            var color = MapPaint.TileColor(Color.Parse(fill), light);
            var ink = MapPaint.OnTile(color);
            context.DrawRectangle(new SolidColorBrush(color),
                new Pen(new SolidColorBrush(MapPaint.Blend(color, light ? Colors.Black : Colors.White, 0.24))), rect, 5, 5);
            if (title.Length == 0 || rect.Width < 52 || rect.Height < 42) return;
            var text = rect.Deflate(10);
            MapPaint.Text(context, title, new Rect(text.X, text.Y, text.Width, 22), 15, ink, true);
            MapPaint.Text(context, size, new Rect(text.X, text.Y + 23, text.Width, 22), 12, ink);
        }
    }
}

