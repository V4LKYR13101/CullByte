using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace CullByte;

internal sealed record MapTile(DiskNode? Node, string Label, long Size, Rect Area, int Level, bool Excluded, bool Expanded)
{
    public bool Folder => Node?.Children is not null;
}

internal sealed class Treemap : Control
{
    private readonly ScanSession session;
    private readonly List<MapTile> tiles = [];
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private Preferences preferences;
    private IReadOnlyList<FileClass> classes;
    private Dictionary<DiskNode, long>? weights;
    private DiskNode? weightedRoot;
    private int weightedRevision = -1;
    private int weightedFiles = -1;
    private DiskNode? focus;
    private Size laidOut;
    private bool dirty = true;
    private MapTile? hovered;
    private MapTile? pressedFolder;
    private string? selected;
    private DateTime zoomStarted;
    private Rect zoomOrigin;
    private bool zooming;

    public ScanFilter Filter { get; set; } = new();
    public int Detail { get; set; }
    public bool Free { get; set; }
    public bool Unknown { get; set; }
    public bool Logical { get; set; }
    public bool ClassColors { get; set; }
    public double VisibleShare { get; private set; }
    public string EmptyText { get; set; } = "Waiting for scan results...";
    public DiskNode? Hovered => hovered?.Node;
    public event Action<DiskNode>? Selected;
    public event Action<DiskNode, Rect>? Zoomed;
    public event Action<DiskNode, Control>? ItemMenu;
    public event Action? LayoutCompleted;

    private static readonly Color[] Tags = [Colors.Transparent, Color.Parse("#E65765"), Color.Parse("#E1A528"),
        Color.Parse("#36A96A"), Color.Parse("#4E8DDA")];

    public Treemap(ScanSession session, Preferences preferences)
    {
        this.session = session;
        this.preferences = preferences;
        classes = preferences.Classes();
        Detail = preferences.Detail;
        Free = preferences.Free;
        Unknown = preferences.Unknown;
        Logical = preferences.Logical;
        ClassColors = preferences.ClassColors;
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ToolTip.SetShowDelay(this, 300);
        Avalonia.Automation.AutomationProperties.SetName(this, "Disk usage treemap");
    }

    public void UpdatePreferences(Preferences value)
    {
        preferences = value;
        classes = value.Classes();
        Refresh(clearWeights: true);
    }

    public void Show(DiskNode value, Rect? source = null)
    {
        if (focus?.FullPath != value.FullPath)
        {
            expanded.Clear();
            collapsed.Clear();
            hovered = null;
            ToolTip.SetTip(this, null);
            selected = null;
        }
        focus = value;
        if (source is { } rect && preferences.AnimateZoom && Bounds.Width > 0 && Bounds.Height > 0)
        {
            zoomOrigin = rect;
            zoomStarted = DateTime.UtcNow;
            zooming = true;
        }
        Refresh();
    }

    public void Select(DiskNode node)
    {
        selected = node.FullPath;
        InvalidateVisual();
    }

    public void Refresh(bool clearWeights = false)
    {
        dirty = true;
        if (clearWeights) weightedRoot = null;
        InvalidateVisual();
    }

    public long? ListedSize(DiskNode node)
    {
        if (!Filter.Active) return Logical ? node.Bytes : node.Allocated;
        if (weights is null || !weights.TryGetValue(node, out var size) || node.Entry.Directory && size == 0)
            return null;
        return size;
    }

    public override void Render(DrawingContext context)
    {
        var canvas = new Rect(Bounds.Size);
        var light = ActualThemeVariant == ThemeVariant.Light;
        context.DrawRectangle(MapPaint.Surface(light), new Pen(MapPaint.Outline(light)), canvas.Deflate(0.5), 9, 9);
        if (canvas.Width < 1 || canvas.Height < 1) return;
        if (dirty || laidOut != Bounds.Size)
        {
            lock (session.Gate) Build(canvas);
            dirty = false;
            laidOut = Bounds.Size;
            LayoutCompleted?.Invoke();
        }

        if (tiles.Count == 0)
        {
            MapPaint.Text(context, EmptyText, canvas.Deflate(18), 14, MapPaint.Ink(light));
            return;
        }

        IDisposable? transform = null;
        if (zooming)
        {
            var elapsed = (DateTime.UtcNow - zoomStarted).TotalMilliseconds / 160;
            zooming = elapsed < 1;
            if (zooming)
            {
                var remaining = Math.Pow(1 - Math.Clamp(elapsed, 0, 1), 3);
                var sx = 1 - remaining * (1 - zoomOrigin.Width / canvas.Width);
                var sy = 1 - remaining * (1 - zoomOrigin.Height / canvas.Height);
                transform = context.PushTransform(new Matrix(sx, 0, 0, sy, zoomOrigin.X * remaining, zoomOrigin.Y * remaining));
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        try
        {
            foreach (var tile in tiles) DrawTile(context, tile, light);
            if (hovered is not null && tiles.Contains(hovered))
                context.DrawRectangle(null, new Pen(MapPaint.Ink(light), 1.5), hovered.Area.Deflate(1), 4, 4);
        }
        finally { transform?.Dispose(); }
    }

    private void Build(Rect canvas)
    {
        tiles.Clear();
        var root = focus ?? session.Root;
        var filterActive = Filter.Active;
        if (filterActive && (weightedRoot != session.Root || weightedRevision != session.Revision ||
                             weightedFiles != session.Root.Files))
        {
            weights = new Dictionary<DiskNode, long>();
            var nodes = session.Root.Walk().ToList();
            for (var i = nodes.Count - 1; i >= 0; i--)
            {
                var node = nodes[i];
                if (node.Children is { } children) weights[node] = children.Sum(child => weights.GetValueOrDefault(child));
                else if (Filter.Matches(node, session.Tags)) weights[node] = Logical ? node.Bytes : node.Allocated;
            }
            weightedRoot = session.Root;
            weightedRevision = session.Revision;
            weightedFiles = session.Root.Files;
        }

        long Full(DiskNode node) => Logical ? node.Bytes : node.Allocated;
        long Match(DiskNode node) => filterActive ? weights!.GetValueOrDefault(node) : Full(node);
        VisibleShare = Full(root) > 0 ? Match(root) / (double)Full(root) : 0;
        var items = Items(root);
        if (root == session.Root && session.Volume is { } volume)
        {
            if (Free && volume.Free > 0) items.Add((null, "Free space", volume.Free, false));
            var unknown = Math.Max(0, volume.Capacity - volume.Free - root.Allocated);
            if (Unknown && unknown > 0) items.Add((null, "Unaccounted space", unknown, false));
        }
        Layout(items, canvas.Deflate(4), 0);

        List<(DiskNode? Node, string Label, long Size, bool Excluded)> Items(DiskNode parent)
        {
            var result = new List<(DiskNode?, string, long, bool)>();
            if (parent.Children is not { } children) return result;
            foreach (var node in children)
            {
                var size = preferences.GrayExcluded ? Full(node) : Match(node);
                if (size > 0) result.Add((node, node.Entry.Name, size, Match(node) == 0));
            }
            return result;
        }

        void Layout(List<(DiskNode? Node, string Label, long Size, bool Excluded)> items, Rect area, int level)
        {
            if (items.Count == 0 || tiles.Count >= 1600 || area.Width < preferences.TileMinimum ||
                area.Height < preferences.TileMinimum) return;
            if (preferences.Sort) items.Sort((a, b) => b.Size.CompareTo(a.Size));
            var total = items.Sum(item => (double)item.Size);
            var scale = area.Width * area.Height / total;
            var remaining = area;
            var index = 0;
            while (index < items.Count && tiles.Count < 1600)
            {
                var side = Math.Min(remaining.Width, remaining.Height);
                if (side < 1) break;
                var end = index + 1;
                var sum = items[index].Size * scale;
                if (preferences.Sort && sum < preferences.TileMinimum * preferences.TileMinimum)
                {
                    if (remaining.Width >= 22 && remaining.Height >= 14)
                        tiles.Add(new MapTile(null, "Smaller items", items.Skip(index).Sum(item => item.Size),
                            remaining.Deflate(1), level, false, false));
                    break;
                }
                var biggest = sum;
                var smallest = sum;
                while (end < items.Count)
                {
                    var next = items[end].Size * scale;
                    var newBiggest = Math.Max(biggest, next);
                    var newSmallest = Math.Min(smallest, next);
                    if (Worst(sum + next, newBiggest, newSmallest, side) > Worst(sum, biggest, smallest, side)) break;
                    sum += next;
                    biggest = newBiggest;
                    smallest = newSmallest;
                    end++;
                }

                var vertical = remaining.Width >= remaining.Height;
                var thickness = Math.Min(sum / side, vertical ? remaining.Width : remaining.Height);
                var offset = 0d;
                for (; index < end && tiles.Count < 1600; index++)
                {
                    var item = items[index];
                    var length = Math.Max(0, Math.Min(side - offset, item.Size * scale / thickness));
                    var tile = vertical
                        ? new Rect(remaining.X, remaining.Y + offset, thickness, length)
                        : new Rect(remaining.X + offset, remaining.Y, length, thickness);
                    offset += length;
                    if (tile.Width < preferences.TileMinimum || tile.Height < preferences.TileMinimum) continue;
                    tile = tile.Deflate(1);
                    var child = item.Node;
                    var path = child?.FullPath;
                    var recurse = child?.Children is { Count: > 0 } && level < 20 &&
                        !collapsed.Contains(path!) && (expanded.Contains(path!) || level + 1 < Detail) &&
                        tile.Width > 45 && tile.Height > 40;
                    tiles.Add(new MapTile(child, item.Label, item.Size, tile, level, item.Excluded, recurse));
                    if (recurse)
                        Layout(Items(child!), new Rect(tile.X + 3, tile.Y + 23, tile.Width - 6, tile.Height - 26), level + 1);
                }
                remaining = vertical
                    ? new Rect(remaining.X + thickness, remaining.Y, Math.Max(0, remaining.Width - thickness), remaining.Height)
                    : new Rect(remaining.X, remaining.Y + thickness, remaining.Width, Math.Max(0, remaining.Height - thickness));
            }
        }
    }

    private static double Worst(double sum, double max, double min, double side) =>
        Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));

    private void DrawTile(DrawingContext context, MapTile tile, bool light)
    {
        var color = tile.Excluded ? Color.Parse("#5B6770") :
            tile.Node is null ? Color.Parse(tile.Label switch
            {
                "Free space" => "#526774",
                "Unaccounted space" => "#7C6249",
                _ => "#435D69"
            }) :
            Color.Parse(tile.Folder ? preferences.FolderColor : preferences.FileColor);
        if (ClassColors && !tile.Folder && !tile.Excluded && tile.Node is not null)
        {
            var group = classes.FirstOrDefault(group => ScanFilter.MatchesClass(group, tile.Label));
            if (group is not null) color = Color.Parse(group.Color);
        }
        var tint = ((uint)StringComparer.Ordinal.GetHashCode(tile.Label) % 9 + tile.Level % 4 * 3) / 100d;
        color = MapPaint.TileColor(color, light, tint);

        var path = tile.Node?.FullPath;
        var border = new Pen(new SolidColorBrush(MapPaint.Blend(color, light ? Colors.Black : Colors.White, 0.21)), 1);
        if (path is not null && path.Equals(selected, StringComparison.OrdinalIgnoreCase))
            border = new Pen(MapPaint.Accent(light), 2);
        else if (path is not null && path.Equals(session.LastUpdated, StringComparison.OrdinalIgnoreCase) &&
                 session.FlashUntil > DateTime.UtcNow && DateTime.UtcNow.Millisecond < 500)
            border = new Pen(MapPaint.Brush(light ? "#8A4C00" : "#F4BC6C"), 2);
        context.DrawRectangle(new SolidColorBrush(color), border, tile.Area, 4, 4);
        var tag = path is null ? 0 : session.Tags.GetValueOrDefault(path);
        if (tag is >= 1 and <= 4 && tile.Area.Width > 8 && tile.Area.Height > 8)
            context.DrawEllipse(new SolidColorBrush(Tags[tag]), new Pen(MapPaint.Ink(light), 0.8),
                new Point(tile.Area.Right - 6, tile.Area.Y + 6), 3, 3);

        if (tile.Area.Width < 35 || tile.Area.Height < 17) return;
        var label = tile.Area.Deflate(new Thickness(6, 3));
        var ink = MapPaint.OnTile(color);
        MapPaint.Text(context, tile.Expanded ? $"{tile.Label}  ·  {SizeText.Format(tile.Size)}" : tile.Label,
            new Rect(label.X, label.Y, Math.Max(0, label.Width - (tag > 0 ? 10 : 0)),
                Math.Min(label.Height, preferences.LabelSize + 6)), preferences.LabelSize, ink, tile.Folder);
        if (!tile.Expanded && tile.Area.Height > preferences.LabelSize + 25)
            MapPaint.Text(context, SizeText.Format(tile.Size), new Rect(label.X, label.Y + preferences.LabelSize + 4,
                label.Width, 16), Math.Max(10, preferences.LabelSize - 2), ink);
    }

    private MapTile? Hit(Point point) => tiles.LastOrDefault(tile => tile.Area.Contains(point));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var tile = Hit(e.GetPosition(this));
        if (Equals(tile, hovered)) return;
        hovered = tile;
        var node = tile?.Node;
        ToolTip.SetTip(this, node is null ? tile?.Label : Describe(node));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hovered = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pointer = e.GetCurrentPoint(this).Properties;
        // A single click can reveal children underneath the pointer before the second click.
        var tile = pointer.IsLeftButtonPressed && e.ClickCount == 2 && pressedFolder is not null
            ? pressedFolder : Hit(e.GetPosition(this));
        pressedFolder = pointer.IsLeftButtonPressed && e.ClickCount == 1 && tile?.Folder == true ? tile : null;
        if (tile?.Node is not { } node) return;
        Focus();
        Select(node);
        Selected?.Invoke(node);
        if (pointer.IsRightButtonPressed)
        {
            ItemMenu?.Invoke(node, this);
            e.Handled = true;
        }
        else if (pointer.IsLeftButtonPressed && tile.Folder)
        {
            if (e.ClickCount == 2) Zoomed?.Invoke(node, tile.Area);
            else
            {
                var path = node.FullPath;
                if (tile.Expanded) { expanded.Remove(path); collapsed.Add(path); }
                else { collapsed.Remove(path); expanded.Add(path); }
                Refresh();
            }
            e.Handled = true;
        }
    }

    public static string Describe(DiskNode node)
    {
        var entry = node.Entry;
        var text = $"{node.FullPath}\n{SizeText.Format(node.Bytes)} logical  /  {SizeText.Format(node.Allocated)} allocated";
        if (entry.Directory) text += $"\n{node.Files:N0} files  /  {node.Folders - 1:N0} folders";
        if (entry.Modified != default) text += $"\nModified {entry.Modified.ToLocalTime():g}";
        return text;
    }
}

