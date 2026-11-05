using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace CullByte;

public sealed class Preferences
{
    public bool Watch { get; set; } = true;
    public bool Streams { get; set; }
    public bool Flash { get; set; } = true;
    public bool Free { get; set; }
    public bool Unknown { get; set; }
    public bool GrayExcluded { get; set; }
    public bool Logical { get; set; }
    public bool ClassColors { get; set; } = true;
    public bool Sort { get; set; } = true;
    public bool AnimateZoom { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public int Detail { get; set; } = 2;
    public int LabelSize { get; set; } = 13;
    public int TileMinimum { get; set; } = 8;
    public int RefreshMilliseconds { get; set; } = 350;
    public string FolderColor { get; set; } = StoragePalette.Folder;
    public string FileColor { get; set; } = StoragePalette.File;
    public string ClassDefinitions { get; set; } = StoragePalette.Classes;

    private static string Location
    {
        get
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "CullByte.settings.xml");
            if (File.Exists(portable)) return portable;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CullByte", "CullByte.settings.xml");
        }
    }

    internal ScanOptions ScanOptions => new(Streams, Watch, Flash);
    internal IReadOnlyList<FileClass> Classes() => ClassDefinitions.Split('\n', StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0)
                throw new FormatException("Each class needs a name, a color and one or more patterns.");
            _ = Avalonia.Media.Color.Parse(parts[1]);
            return new FileClass(parts[0], parts[2], parts[1]);
        }).ToArray();

    internal static Preferences Load()
    {
        try
        {
            if (!File.Exists(Location)) return new Preferences();
            using var input = File.OpenRead(Location);
            var preferences = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(input)!;
            _ = preferences.Classes();
            _ = Avalonia.Media.Color.Parse(preferences.FolderColor);
            _ = Avalonia.Media.Color.Parse(preferences.FileColor);
            preferences.Detail = Math.Clamp(preferences.Detail, 1, 8);
            preferences.LabelSize = Math.Clamp(preferences.LabelSize, 9, 24);
            preferences.TileMinimum = Math.Clamp(preferences.TileMinimum, 3, 40);
            preferences.RefreshMilliseconds = Math.Clamp(preferences.RefreshMilliseconds, 100, 1500);
            return preferences;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            return new Preferences();
        }
    }

    internal void Save() => Storage.WriteAtomic(Location, output =>
        new XmlSerializer(typeof(Preferences)).Serialize(output.BaseStream, this));
}

internal static partial class Storage
{
    private sealed record Header(string Format, VolumeInfo? Volume);
    private sealed record Row(int Depth, FileEntry Entry);
    public const string DefaultTemplate = "{allocatedBytes}\t{bytes}\t{path}";

    public static void Save(string path, DiskNode root, VolumeInfo? volume) => WriteAtomic(path, writer =>
    {
        writer.WriteLine(JsonSerializer.Serialize(new Header("CullByte-1", volume)));
        foreach (var (node, depth) in Rows(root))
        {
            var entry = depth == 0 ? node.Entry with { Name = node.FullPath } : node.Entry;
            writer.WriteLine(JsonSerializer.Serialize(new Row(depth, entry)));
        }
    });

    public static (DiskNode Root, VolumeInfo? Volume) Load(string path)
    {
        using var reader = File.OpenText(path);
        var header = JsonSerializer.Deserialize<Header>(reader.ReadLine() ?? "") ??
            throw new InvalidDataException("The snapshot header is missing.");
        if (header.Format != "CullByte-1" || header.Volume is { } v &&
            (v.Capacity < 0 || v.Free < 0 || v.Free > v.Capacity))
            throw new InvalidDataException("Unsupported or invalid CullByte snapshot.");

        var stack = new List<DiskNode>();
        DiskNode? root = null;
        while (reader.ReadLine() is { } line)
        {
            var row = JsonSerializer.Deserialize<Row>(line);
            if (row?.Entry?.Name is not { Length: > 0 } name || row.Depth < 0 || row.Depth > stack.Count ||
                row.Depth > 2048 || row.Entry.Bytes < 0 || row.Entry.Allocated < 0 ||
                (row.Depth == 0 && (root is not null || !row.Entry.Directory || !Path.IsPathFullyQualified(name))) ||
                (row.Depth > 0 && (name is "." or ".." || name.IndexOfAny(['\\', '/', '\0']) >= 0 ||
                                  (!row.Entry.Stream && name.Contains(':')))))
                throw new InvalidDataException("The snapshot contains an invalid tree entry.");

            if (stack.Count > row.Depth) stack.RemoveRange(row.Depth, stack.Count - row.Depth);
            var node = new DiskNode(row.Entry);
            if (stack.Count == 0) root = node;
            else if (stack[^1].Children is not null) stack[^1].Add(node);
            else throw new InvalidDataException("A snapshot entry is nested under a file.");
            stack.Add(node);
        }
        return (root ?? throw new InvalidDataException("The snapshot is empty."), header.Volume);
    }

    private static IEnumerable<(DiskNode Node, int Depth)> Rows(DiskNode root)
    {
        var stack = new Stack<(DiskNode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var item))
        {
            yield return item;
            if (item.Node.Children is not { } children) continue;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], item.Depth + 1));
        }
    }

    public static IEnumerable<string> Report(DiskNode root, ScanFilter filter, IReadOnlyDictionary<string, int> tags,
        string format, string template)
    {
        if (format == "CSV") yield return "Path,LogicalBytes,AllocatedBytes,ModifiedUtc,Attributes,Tag";
        foreach (var (node, depth) in Rows(root))
        {
            if (node.Entry.Directory || !filter.Matches(node, tags)) continue;
            var tag = tags.GetValueOrDefault(node.FullPath);
            if (format == "CSV")
            {
                yield return string.Join(",", Csv(node.FullPath), node.Bytes.ToString(CultureInfo.InvariantCulture),
                    node.Allocated.ToString(CultureInfo.InvariantCulture), Csv(node.Entry.Modified.ToString("O")),
                    Csv(node.Entry.Attributes.ToString()), tag.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                yield return Token().Replace(template, match => match.Groups[1].Value switch
                {
                    "path" => node.FullPath,
                    "name" => node.Entry.Name,
                    "bytes" => node.Bytes.ToString(CultureInfo.InvariantCulture),
                    "allocatedBytes" => node.Allocated.ToString(CultureInfo.InvariantCulture),
                    "modified" => node.Entry.Modified.ToString("O"),
                    "created" => node.Entry.Created.ToString("O"),
                    "accessed" => node.Entry.Accessed.ToString("O"),
                    "attributes" => node.Entry.Attributes.ToString(),
                    "tag" => tag.ToString(CultureInfo.InvariantCulture),
                    "depth" => depth.ToString(CultureInfo.InvariantCulture),
                    _ => match.Value
                });
            }
        }
    }

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    [GeneratedRegex(@"\{([a-zA-Z]+)\}")]
    private static partial Regex Token();

    public static void WriteAtomic(string path, Action<StreamWriter> write)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".cullbyte-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false))) write(writer);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class CommandLine
{
    public static int Run(string[] args)
    {
        if (OperatingSystem.IsWindows()) AttachConsole(uint.MaxValue);
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("CullByte --headless (--scan PATH | --load FILE) [--filter QUERY] [--ads] [--save FILE] [--report FILE]");
                return 0;
            }

            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var ads = false;
            for (var i = 0; i < args.Length; i++)
            {
                var flag = args[i];
                if (flag == "--headless") continue;
                if (flag == "--ads") { ads = true; continue; }
                if (flag is not ("--scan" or "--load" or "--filter" or "--save" or "--report") ||
                    ++i == args.Length || !options.TryAdd(flag, args[i]))
                    throw new ArgumentException($"Invalid or repeated argument {flag}. Use --help.");
            }
            if (options.ContainsKey("--scan") == options.ContainsKey("--load"))
                throw new ArgumentException("Provide exactly one of --scan or --load.");
            DiskNode root;
            VolumeInfo? volume;
            if (options.TryGetValue("--scan", out var path))
            {
                path = Path.GetFullPath(path);
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
                root = DiskScanner.CreateRoot(path);
                volume = DiskScanner.GetVolume(path);
                DiskScanner.Scan(root, new object(), new ScanOptions(ads, false, false),
                    issue => Console.Error.WriteLine($"{issue.Path}  {issue.Message}"), CancellationToken.None);
            }
            else (root, volume) = Storage.Load(options["--load"]);
            if (options.TryGetValue("--save", out var snapshot)) Storage.Save(snapshot, root, volume);
            if (options.TryGetValue("--report", out var report))
            {
                var filter = ScanFilter.Parse(options.GetValueOrDefault("--filter", ""), Preferences.Load().Classes());
                var format = report.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV" : "Text";
                Storage.WriteAtomic(report, writer =>
                {
                    foreach (var line in Storage.Report(root, filter, new Dictionary<string, int>(), format, Storage.DefaultTemplate))
                        writer.WriteLine(line);
                });
            }
            Console.WriteLine($"{root.Files:N0} files, {root.Folders:N0} folders, {root.Bytes:N0} logical bytes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
}

