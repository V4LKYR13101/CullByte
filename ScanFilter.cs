using System.Globalization;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace CullByte;

internal sealed record FileClass(string Name, string Masks, string Color);
internal sealed class ScanFilter
{
    private delegate bool Test(DiskNode node, IReadOnlyDictionary<string, int> tags);
    private readonly List<Test> any = [];
    private readonly List<Test> all = [];
    public bool Active => any.Count + all.Count > 0;

    private static readonly Regex Comparison = new(
        @"^(?<kind>[a-z ]*)\s*(?<op>[<>])\s*(?<number>\d+(?:\.\d+)?)\s*(?<unit>[a-z]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, int> SizePowers = new(StringComparer.OrdinalIgnoreCase)
        { ["b"] = 0, ["kb"] = 1, ["mb"] = 2, ["gb"] = 3, ["tb"] = 4 };

    public static ScanFilter Parse(string input, IReadOnlyList<FileClass> classes)
    {
        var result = new ScanFilter();
        var now = DateTimeOffset.UtcNow;
        foreach (var raw in input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exclude = raw.StartsWith('|');
            var clause = (exclude ? raw[1..] : raw).Trim();
            if (clause.Length == 0) throw new FormatException("Enter a condition after |.");
            var combine = true;
            Test test;
            var comparison = Comparison.Match(clause);
            if (clause.StartsWith('\\'))
            {
                combine = false;
                var mask = clause[1..];
                if (mask.Length == 0) throw new FormatException("Enter a folder pattern after the backslash.");
                test = (node, _) => Ancestors(node).Any(parent =>
                    Glob(mask, Path.GetFileName(Path.TrimEndingDirectorySeparator(parent.Entry.Name))));
            }
            else if (clause.StartsWith(':'))
            {
                var parts = clause[1..].Split(':', 2);
                var kind = parts.Length == 2 ? parts[0].ToLowerInvariant() : "legacytag";
                var value = parts[^1];
                switch (kind)
                {
                    case "class":
                        var group = classes.FirstOrDefault(group => group.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
                            ?? throw new FormatException($"Unknown file class {value}.");
                        test = (node, _) => MatchesClass(group, node.Entry.Name);
                        break;
                    case "attr":
                    case "attrs":
                        combine = false;
                        test = Flags(value, name => Attribute(name));
                        break;
                    case "tag":
                    case "tags":
                    case "legacytag":
                        test = Flags(value, name =>
                        {
                            var color = Tag(name, kind == "legacytag");
                            return (node, tags) => Tagged(node, tags, color);
                        });
                        break;
                    default: throw new FormatException($"Unknown filter type {kind}.");
                }
            }
            else if (comparison.Success)
            {
                combine = false;
                var unit = comparison.Groups["unit"].Value.ToLowerInvariant();
                var kind = comparison.Groups["kind"].Value.Trim().ToLowerInvariant();
                var threshold = double.Parse(comparison.Groups["number"].Value, CultureInfo.InvariantCulture);
                var greater = comparison.Groups["op"].Value == ">";
                if (SizePowers.TryGetValue(unit, out var power))
                {
                    threshold *= Math.Pow(1024, power);
                    var allocated = kind switch
                    {
                        "" or "disk" or "disksize" or "clustersize" or "dsk" or "dsksz" => true,
                        "file" or "filesize" or "logical" or "logicalsize" or "size" => false,
                        _ => throw new FormatException($"Unknown size field {kind}.")
                    };
                    test = (node, _) => Compare(allocated ? node.Entry.Allocated : node.Entry.Bytes);
                }
                else
                {
                    threshold *= unit switch
                    {
                        "s" or "sec" or "secs" or "second" or "seconds" => 1,
                        "m" or "min" or "mins" or "minute" or "minutes" => 60,
                        "h" or "hour" or "hours" => 3600,
                        "d" or "day" or "days" => 86400,
                        "w" or "week" or "weeks" => 604800,
                        "month" or "months" => 2592000,
                        "y" or "year" or "years" => 31536000,
                        _ => throw new FormatException($"Unknown unit {unit}.")
                    };
                    Func<FileEntry, DateTimeOffset> date = kind switch
                    {
                        "" or "m" or "modify" or "modified" or "modification" => entry => entry.Modified,
                        "c" or "create" or "created" or "creation" => entry => entry.Created,
                        "a" or "access" or "accessed" => entry => entry.Accessed,
                        _ => throw new FormatException($"Unknown date field {kind}.")
                    };
                    test = (node, _) => date(node.Entry) != default && Compare((now - date(node.Entry)).TotalSeconds);
                }
                bool Compare(double number) => greater ? number > threshold : number < threshold;
            }
            else
            {
                if (clause.Contains('>') || clause.Contains('<')) throw new FormatException($"Invalid comparison {clause}.");
                test = (node, _) => Glob(clause, node.Entry.Name);
            }
            if (exclude) result.all.Add((node, tags) => !test(node, tags));
            else (combine ? result.any : result.all).Add(test);
        }
        return result;
    }

    public bool Matches(DiskNode node, IReadOnlyDictionary<string, int> tags) =>
        all.All(test => test(node, tags)) && (any.Count == 0 || any.Any(test => test(node, tags)));

    public static bool MatchesClass(FileClass group, string name) =>
        group.Masks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(mask => Glob(mask, name));

    private static bool Glob(string mask, string name) => FileSystemName.MatchesSimpleExpression(mask, name, ignoreCase: true);
    private static IEnumerable<DiskNode> Ancestors(DiskNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) yield return parent;
    }

    private static bool Tagged(DiskNode node, IReadOnlyDictionary<string, int> tags, int color)
    {
        if (tags.Count == 0) return false;
        for (var current = node; current is not null; current = current.Parent)
            if (tags.TryGetValue(current.FullPath, out var tag) && (color == 0 || color == tag)) return true;
        return false;
    }

    private static int Tag(string value, bool legacy) => value.ToLowerInvariant() switch
    {
        "a" or "all" => 0,
        "r" or "red" or "1" => 1,
        "y" or "yellow" or "2" => 2,
        "g" or "green" => 3,
        "b" or "blu" or "blue" => 4,
        "3" => legacy ? 3 : 4,
        "4" => legacy ? 4 : 3,
        _ => throw new FormatException($"Unknown tag {value}.")
    };

    private static Test Attribute(string name)
    {
        if (name.ToLowerInvariant() is "ads" or "alternatedatastream" or "alternate" or "alt")
            return (node, _) => node.Entry.Stream;
        var attribute = name.ToLowerInvariant() switch
        {
            "a" or "archive" or "arch" => FileAttributes.Archive,
            "s" or "system" or "sys" => FileAttributes.System,
            "r" or "readonly" or "ro" => FileAttributes.ReadOnly,
            "h" or "hidden" or "hid" => FileAttributes.Hidden,
            "c" or "compressed" or "compr" => FileAttributes.Compressed,
            "e" or "encrypted" or "crypt" => FileAttributes.Encrypted,
            "o" or "offline" or "off" => FileAttributes.Offline,
            "t" or "temporary" or "temp" or "tmp" => FileAttributes.Temporary,
            "ni" or "notindexed" => FileAttributes.NotContentIndexed,
            "sp" or "sparse" => FileAttributes.SparseFile,
            "reparse" => FileAttributes.ReparsePoint,
            _ => throw new FormatException($"Unknown attribute {name}.")
        };
        return (node, _) => (node.Entry.Attributes & attribute) != 0;
    }

    private static Test Flags(string value, Func<string, Test> parse)
    {
        if (!Regex.IsMatch(value, @"^[+-]?[^+,\-\s]+(?:\s*[+,\-]\s*[+-]?[^+,\-\s]+)*$"))
            throw new FormatException($"Invalid flags {value}.");
        var terms = Regex.Matches(value, @"(?<op>[+\-,]?)\s*(?<name>[^+,\-\s]+)")
            .Select(term => (term.Groups["op"].Value, parse(term.Groups["name"].Value))).ToArray();
        return (node, tags) => terms.Where(term => term.Value == "-").All(term => !term.Item2(node, tags)) &&
            (!terms.Any(term => term.Value != "-") || terms.Where(term => term.Value != "-").Any(term => term.Item2(node, tags)));
    }
}

