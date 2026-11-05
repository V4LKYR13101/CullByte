using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using System.IO.Enumeration;

namespace CullByte;

internal sealed class MobileMap : Control
{
    private sealed record Tile(StorageItem? Item, long Bytes, Rect Area);
    private static readonly Color Folder = Color.Parse(StoragePalette.Folder);
    private static readonly Color File = Color.Parse(StoragePalette.File);
    private static readonly (string[] Masks, Color Color)[] FileClasses = StoragePalette.Classes.Split('\n')
        .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
        .Select(parts => (parts[2].Split(';'), Color.Parse(parts[1]))).ToArray();
    private readonly List<Tile> tiles = [];
    private (StorageItem? Item, long Bytes)[] items = [];
    private Size renderedSize;
    private bool dirty = true;
    private string emptyText = "";

    public event Action<StorageItem>? ItemTapped;

    public MobileMap()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Tapped += (_, e) =>
        {
            var point = e.GetPosition(this);
            if (tiles.FirstOrDefault(tile => tile.Area.Contains(point))?.Item is { } item)
                ItemTapped?.Invoke(item);
        };
        Avalonia.Automation.AutomationProperties.SetName(this, "Storage treemap. Items are also available in the list.");
    }

    public void SetItems(IReadOnlyList<(StorageItem Item, long Bytes)> sorted, string empty)
    {
        var visible = sorted.Where(item => item.Bytes > 0).ToArray();
        var result = visible.Take(60).Select(item => ((StorageItem?)item.Item, item.Bytes)).ToList();
        if (visible.Length > 60) result.Add((null, visible.Skip(60).Sum(item => item.Bytes)));
        items = result.ToArray();
        tiles.Clear();
        emptyText = empty;
        dirty = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var surface = new Rect(Bounds.Size).Deflate(0.5);
        context.DrawRectangle(MapPaint.Surface(light), new Pen(MapPaint.Outline(light)), surface, 9, 9);
        if (items.Length == 0)
        {
            MapPaint.Text(context, emptyText, surface.Deflate(12), 14, MapPaint.Ink(light));
            return;
        }
        if (dirty || renderedSize != Bounds.Size)
        {
            LayoutTiles(surface.Deflate(4));
            dirty = false;
            renderedSize = Bounds.Size;
        }
        foreach (var tile in tiles)
        {
            var color = ColorFor(tile.Item);
            var fill = MapPaint.TileColor(color, light);
            var outline = MapPaint.Blend(fill, light ? Colors.Black : Colors.White, 0.21);
            context.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(outline)), tile.Area, 4, 4);
            if (tile.Area.Width < 42 || tile.Area.Height < 22) continue;
            var ink = MapPaint.OnTile(fill);
            var label = tile.Area.Deflate(new Thickness(7, 5));
            MapPaint.Text(context, tile.Item?.Name ?? "Smaller items", label, 13, ink, tile.Item?.Children is not null);
            if (label.Height >= 36)
                MapPaint.Text(context, SizeText.Format(tile.Bytes), new Rect(label.X, label.Y + 19, label.Width, 16), 11, ink);
        }
    }

    private static Color ColorFor(StorageItem? item)
    {
        if (item is null) return Color.Parse("#435D69");
        if (item.Children is not null) return Folder;
        foreach (var group in FileClasses)
            if (group.Masks.Any(mask => FileSystemName.MatchesSimpleExpression(mask, item.Name, true))) return group.Color;
        return File;
    }

    private void LayoutTiles(Rect area)
    {
        tiles.Clear();
        var scale = area.Width * area.Height / items.Sum(item => (double)item.Bytes);
        var start = 0;
        while (start < items.Length)
        {
            var side = Math.Min(area.Width, area.Height);
            if (side < 1) break;
            var end = start + 1;
            var sum = items[start].Bytes * scale;
            var largest = sum;
            var smallest = sum;
            while (end < items.Length)
            {
                var next = items[end].Bytes * scale;
                var max = Math.Max(largest, next);
                var min = Math.Min(smallest, next);
                if (Aspect(sum + next, max, min, side) > Aspect(sum, largest, smallest, side)) break;
                sum += next;
                largest = max;
                smallest = min;
                end++;
            }

            var vertical = area.Width >= area.Height;
            var width = Math.Min(sum / side, vertical ? area.Width : area.Height);
            var offset = 0d;
            for (; start < end; start++)
            {
                var item = items[start];
                var length = Math.Max(0, Math.Min(side - offset, item.Bytes * scale / width));
                var rect = vertical
                    ? new Rect(area.X, area.Y + offset, width, length)
                    : new Rect(area.X + offset, area.Y, length, width);
                offset += length;
                if (rect.Width > 4 && rect.Height > 4) tiles.Add(new Tile(item.Item, item.Bytes, rect.Deflate(1)));
            }

            area = vertical
                ? new Rect(area.X + width, area.Y, Math.Max(0, area.Width - width), area.Height)
                : new Rect(area.X, area.Y + width, area.Width, Math.Max(0, area.Height - width));
        }
    }

    private static double Aspect(double sum, double max, double min, double side) =>
        Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
}

