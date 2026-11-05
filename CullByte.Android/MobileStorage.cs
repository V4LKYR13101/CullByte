using System.Globalization;
using System.IO.Enumeration;

namespace CullByte;

internal sealed record FolderSource(string Id, string Name);
internal sealed record ScanProgress(int Files, int Folders, StorageItem? Preview = null);
internal sealed record ScanResult(StorageItem Root, int Unreadable, int UnknownSizes);

internal sealed class StorageItem(string name, string path, long size, bool folder)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public long Bytes { get; private set; } = size;
    public int Files { get; private set; } = folder ? 0 : 1;
    public List<StorageItem>? Children { get; } = folder ? [] : null;
    public string? DocumentId { get; init; }
    public string? ParentDocumentId { get; init; }
    public StorageItem? Parent { get; init; }

    public void AddTotals(StorageItem child)
    {
        Bytes = checked(Bytes + child.Bytes);
        Files = checked(Files + child.Files);
    }

    public StorageItem Preview()
    {
        var root = Copy();
        foreach (var child in Children!.OrderByDescending(item => item.Bytes).Take(80))
            root.Children!.Add(child.Copy());
        return root;
    }

    private StorageItem Copy() => new(Name, Path, Bytes, Children is not null)
    {
        Files = Files, DocumentId = DocumentId, ParentDocumentId = ParentDocumentId
    };
}

internal interface IMobileStorage
{
    string Theme { get; set; }
    Task<FolderSource?> ChooseAsync();
    ScanResult Read(FolderSource folder, IProgress<ScanProgress> progress, CancellationToken token);
    Task<bool> ExportAsync(string suggestedName, IEnumerable<StorageItem> files);
    Task RevealAsync(FolderSource folder, StorageItem item);
}

internal sealed class MobileFilter
{
    private readonly List<Func<StorageItem, bool>> included = [];
    private readonly List<Func<StorageItem, bool>> required = [];

    public bool IsActive => included.Count + required.Count > 0;

    public static MobileFilter Parse(string value)
    {
        var result = new MobileFilter();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var excluded = part[0] == '|';
            var term = excluded ? part[1..].Trim() : part;
            if (term.Length == 0) throw new FormatException("Enter a term after |.");
            if (term[0] is '>' or '<')
            {
                var greater = term[0] == '>';
                var input = term[1..].Trim().ToLowerInvariant();
                var end = input.IndexOfAny("bkmgt".ToCharArray());
                var unit = end < 0 ? "" : input[end..].Trim();
                var number = end < 0 ? input : input[..end].Trim();
                var factor = unit switch
                {
                    "" or "b" => 1L,
                    "kb" or "kib" => 1L << 10,
                    "mb" or "mib" => 1L << 20,
                    "gb" or "gib" => 1L << 30,
                    "tb" or "tib" => 1L << 40,
                    _ => 0
                };
                if (factor == 0 || !double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
                    || !double.IsFinite(size) || size < 0 || size * factor > long.MaxValue)
                    throw new FormatException("Enter a size such as >50mb or <2gb.");
                result.required.Add(file => (greater ? file.Bytes > size * factor : file.Bytes < size * factor) != excluded);
            }
            else
            {
                bool Match(StorageItem file) => term.Contains('*') || term.Contains('?')
                    ? FileSystemName.MatchesSimpleExpression(term, file.Name, true)
                    : file.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
                if (excluded) result.required.Add(file => !Match(file));
                else result.included.Add(Match);
            }
        }
        return result;
    }

    private bool Matches(StorageItem file) => required.All(test => test(file)) &&
        (included.Count == 0 || included.Any(test => test(file)));

    public Dictionary<StorageItem, (long Bytes, int Files)> Project(StorageItem root)
    {
        var weights = new Dictionary<StorageItem, (long Bytes, int Files)>();
        var pending = new Stack<StorageItem>();
        var folders = new List<StorageItem>();
        pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (item.Children is { } children)
            {
                folders.Add(item);
                foreach (var child in children) pending.Push(child);
            }
            else if (Matches(item)) weights[item] = (item.Bytes, 1);
        }
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var item = folders[i];
            long bytes = 0;
            var files = 0;
            foreach (var child in item.Children!)
            {
                var weight = weights.GetValueOrDefault(child);
                bytes += weight.Bytes;
                files += weight.Files;
            }
            if (files > 0) weights[item] = (bytes, files);
        }
        return weights;
    }
}
