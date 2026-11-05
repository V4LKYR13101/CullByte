using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace CullByte;

internal sealed record FileEntry(
    string Name, FileAttributes Attributes, long Bytes, long Allocated,
    DateTimeOffset Created, DateTimeOffset Modified, DateTimeOffset Accessed, bool Stream = false)
{
    public bool Directory => Attributes.HasFlag(FileAttributes.Directory);
}

internal sealed class DiskNode(FileEntry entry, DiskNode? parent = null)
{
    public FileEntry Entry { get; } = entry;
    public DiskNode? Parent { get; set; } = parent;
    public List<DiskNode>? Children { get; } = entry.Directory ? [] : null;
    public long Bytes { get; private set; } = entry.Bytes;
    public long Allocated { get; private set; } = entry.Allocated;
    public int Files { get; private set; } = entry.Directory ? 0 : 1;
    public int Folders { get; private set; } = entry.Directory ? 1 : 0;
    public string FullPath => Parent is null ? Entry.Name : Path.Join(Parent.FullPath, Entry.Name);

    public void Add(DiskNode node)
    {
        node.Parent = this;
        Children!.Add(node);
        Adjust(node, 1);
    }

    public void Replace(DiskNode old, DiskNode? replacement)
    {
        var index = Children!.IndexOf(old);
        if (index < 0) return;
        Adjust(old, -1);
        if (replacement is null)
        {
            Children.RemoveAt(index);
            return;
        }
        replacement.Parent = this;
        Children[index] = replacement;
        Adjust(replacement, 1);
    }

    private void Adjust(DiskNode child, int sign)
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            node.Bytes = checked(node.Bytes + sign * child.Bytes);
            node.Allocated = checked(node.Allocated + sign * child.Allocated);
            node.Files = checked(node.Files + sign * child.Files);
            node.Folders = checked(node.Folders + sign * child.Folders);
        }
    }

    public DiskNode? Find(string path)
    {
        var relative = Path.GetRelativePath(FullPath, path);
        if (relative == ".") return this;
        if (Path.IsPathRooted(relative)) return null;
        DiskNode? node = this;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            node = node?.Children?.FirstOrDefault(item => item.Entry.Name.Equals(component, StringComparison.OrdinalIgnoreCase));
        return node;
    }

    public IEnumerable<DiskNode> Walk()
    {
        var stack = new Stack<DiskNode>();
        stack.Push(this);
        while (stack.TryPop(out var node))
        {
            yield return node;
            if (node.Children is not { } children) continue;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }
}

internal sealed record ScanIssue(string Path, string Message);
internal sealed record VolumeInfo(long Capacity, long Free);
internal sealed record ScanOptions(bool Streams, bool Watch, bool AnimateChanges);

internal static class DiskScanner
{
    public static DiskNode CreateRoot(string path) => new(new FileEntry(
        path, FileAttributes.Directory, 0, 0, default, default, default));

    public static VolumeInfo? GetVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path)!;
            if (!Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase)) return null;
            var drive = new DriveInfo(root);
            return new VolumeInfo(drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static void Scan(DiskNode root, object gate, ScanOptions options, Action<ScanIssue> log, CancellationToken token)
    {
        var rootPath = root.FullPath;
        var volumeRoot = Path.GetPathRoot(rootPath)!;
        var cluster = OperatingSystem.IsWindows() && GetDiskFreeSpace(volumeRoot, out var sectors, out var sectorSize,
            out _, out _) ? (long)sectors * sectorSize : 1;
        var streams = options.Streams && OperatingSystem.IsWindows()
            && GetVolumeInformation(volumeRoot, null, 0, out _, out _, out var flags, null, 0)
            && (flags & 0x40000) != 0;
        var queue = new Queue<(string Path, DiskNode Node)>();
        queue.Enqueue((rootPath, root));
        var entries = new List<DiskNode>(128);
        var enumeration = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };

        while (queue.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new FileSystemEnumerable<FileEntry>(directory.Path, (ref FileSystemEntry item) =>
                    new FileEntry(item.FileName.ToString(), item.Attributes, item.IsDirectory ? 0 : item.Length, 0,
                        item.CreationTimeUtc, item.LastWriteTimeUtc, item.LastAccessTimeUtc), enumeration))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Join(directory.Path, entry.Name);
                    var reparse = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    var node = new DiskNode(entry with
                    {
                        Allocated = GetAllocation(path, entry.Bytes, entry.Attributes, cluster)
                    });
                    entries.Add(node);
                    if (node.Entry.Directory && !reparse) queue.Enqueue((path, node));
                    if (streams && !reparse && !entry.Attributes.HasFlag(FileAttributes.Offline))
                    {
                        foreach (var stream in ReadStreams(path, entry, cluster))
                            entries.Add(new DiskNode(stream));
                    }
                    if (entries.Count >= 128) Publish();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log(new ScanIssue(directory.Path, ex.Message));
            }
            Publish();

            void Publish()
            {
                lock (gate)
                {
                    foreach (var item in entries) directory.Node.Add(item);
                }
                entries.Clear();
            }
        }
    }

    private static long GetAllocation(string path, long bytes, FileAttributes attributes, long cluster)
    {
        const FileAttributes special = FileAttributes.Compressed | FileAttributes.SparseFile;
        if (bytes > 0 && (attributes & special) != 0 && OperatingSystem.IsWindows() &&
            (attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) == 0)
        {
            var low = GetCompressedFileSize(Extended(path), out var high);
            if (low != uint.MaxValue || Marshal.GetLastPInvokeError() == 0)
                return ((long)high << 32) | low;
        }
        return bytes == 0 ? 0 : ((bytes - 1) / cluster + 1) * cluster;
    }

    private static IEnumerable<FileEntry> ReadStreams(string path, FileEntry entry, long cluster)
    {
        var handle = FindFirstStream(Extended(path), 0, out var data, 0);
        if (handle == -1) yield break;
        try
        {
            do
            {
                if (data.Name == "::$DATA" || !data.Name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase)) continue;
                var suffix = data.Name[..^6];
                yield return entry with
                {
                    Name = entry.Name + suffix,
                    Attributes = entry.Attributes & ~FileAttributes.Directory,
                    Bytes = data.Size,
                    Allocated = GetAllocation(path + suffix, data.Size, entry.Attributes, cluster),
                    Stream = true
                };
            } while (FindNextStream(handle, out data));
        }
        finally { FindClose(handle); }
    }

    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpace(string path, out uint sectors, out uint sectorSize, out uint free, out uint total);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformation(string root, string? volume, int volumeSize, out uint serial,
        out uint maxComponent, out uint flags, string? format, int formatSize);
    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode)]
    private static extern nint FindFirstStream(string path, int level, out StreamData data, int flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode)]
    private static extern bool FindNextStream(nint handle, out StreamData data);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(nint handle);
}

