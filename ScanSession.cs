using System.Collections.Concurrent;

namespace CullByte;

internal sealed class ScanSession : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, byte> dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScanOptions options;
    private FileSystemWatcher? watcher;
    private long lastChange;
    private int issueCount;
    private bool disposed;
    private int generation;
    private CancellationTokenSource? master;
    private readonly SemaphoreSlim branchLock = new(1);

    public object Gate { get; } = new();
    public DiskNode Root { get; private set; }
    public VolumeInfo? Volume { get; private set; }
    public string Source { get; }
    public bool Offline { get; }
    public bool Scanning { get; private set; }
    public bool Refining { get; private set; }
    public bool Cancelled { get; private set; }
    public DateTime FlashUntil { get; private set; }
    public string? LastUpdated { get; private set; }
    public int Revision { get; private set; }
    public ConcurrentQueue<ScanIssue> Issues { get; } = new();
    public int IssueCount => Volatile.Read(ref issueCount);
    public Dictionary<string, int> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ScanSession(string source, ScanOptions options, DiskNode? snapshot = null, VolumeInfo? volume = null)
    {
        Source = source;
        this.options = options;
        Root = snapshot ?? DiskScanner.CreateRoot(source);
        Offline = snapshot is not null;
        Volume = volume;
        if (!Offline && options.Watch) StartWatcher();
    }

    public async Task ScanAsync()
    {
        if (Offline || Scanning || disposed) return;
        var request = ++generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        master = cancellation;
        Scanning = true;
        Cancelled = false;
        var root = DiskScanner.CreateRoot(Source);
        Root = root;
        Revision++;
        try
        {
            Volume = await Task.Run(() => DiskScanner.GetVolume(Source));
            await Task.Run(() => DiskScanner.Scan(root, Gate, options, Log, cancellation.Token));
        }
        catch (OperationCanceledException) { Cancelled = true; }
        catch (Exception ex) { Log(new ScanIssue(Source, ex.Message)); }
        finally
        {
            if (generation == request)
            {
                Scanning = false;
                master = null;
                Revision++;
            }
        }
    }

    public void Stop() => master?.Cancel();

    public async Task RefreshBranchAsync(string path)
    {
        if (Offline || disposed || !await branchLock.WaitAsync(0)) return;
        if (path == Source)
        {
            branchLock.Release();
            await ScanAsync();
            return;
        }
        Refining = true;
        var request = generation;
        try
        {
            DiskNode? old;
            lock (Gate) old = Root.Find(path);
            if (old?.Children is null) return;
            var result = await Task.Run(() =>
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
                if (!attributes.HasFlag(FileAttributes.Directory)) return null;
                var replacement = new DiskNode(old.Entry with { Name = path, Attributes = attributes });
                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    DiskScanner.Scan(replacement, new object(), options, Log, lifetime.Token);
                return replacement;
            });
            if (disposed || generation != request) return;

            // Wait for the master to release this tree before replacing one of its branches.
            while (Scanning && !disposed && generation == request)
                await Task.Delay(100, lifetime.Token);
            if (disposed || generation != request) return;
            lock (Gate)
            {
                var current = Root.Find(path);
                if (current?.Parent is not { } parent) return;
                var replacement = result is null ? null : new DiskNode(result.Entry with { Name = old.Entry.Name });
                if (replacement is not null)
                    foreach (var child in result!.Children!) replacement.Add(child);
                parent.Replace(current, replacement);
                Revision++;
            }
            if (options.AnimateChanges)
            {
                LastUpdated = path;
                FlashUntil = DateTime.UtcNow.AddSeconds(2);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(new ScanIssue(path, ex.Message)); }
        finally
        {
            Refining = false;
            branchLock.Release();
        }
    }

    public async Task RefreshPendingAsync()
    {
        if (disposed || Scanning || Refining || dirty.IsEmpty ||
            Environment.TickCount64 - Interlocked.Read(ref lastChange) < 650) return;
        var paths = dirty.Keys.Take(32).Where(path => dirty.TryRemove(path, out _)).ToArray();
        if (paths.Length == 0) return;
        var path = paths[0];
        foreach (var next in paths.Skip(1))
        {
            while (path != Source && !Inside(path, next))
                path = Path.GetDirectoryName(path) ?? Source;
        }
        lock (Gate)
        {
            while (path != Source && Root.Find(path) is null)
                path = Path.GetDirectoryName(path) ?? Source;
        }
        await RefreshBranchAsync(path);
    }

    private void StartWatcher()
    {
        try
        {
            watcher = new FileSystemWatcher(Source)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes
            };
            watcher.Created += (_, e) => Mark(e.FullPath);
            watcher.Changed += (_, e) => Mark(e.FullPath);
            watcher.Deleted += (_, e) => Mark(e.FullPath);
            watcher.Renamed += (_, e) => { Mark(e.OldFullPath); Mark(e.FullPath); };
            watcher.Error += (_, e) =>
            {
                Log(new ScanIssue(Source, e.GetException().Message));
                dirty[Source] = 0;
                Interlocked.Exchange(ref lastChange, Environment.TickCount64);
            };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log(new ScanIssue(Source, $"Live updates unavailable. {ex.Message}"));
            watcher?.Dispose();
            watcher = null;
        }
    }

    private void Mark(string path)
    {
        if (dirty.Count > 256) dirty[Source] = 0;
        else dirty[Path.GetDirectoryName(path) ?? Source] = 0;
        Interlocked.Exchange(ref lastChange, Environment.TickCount64);
    }

    public void ToggleTag(DiskNode node, int color)
    {
        var path = node.FullPath;
        if (Tags.GetValueOrDefault(path) == color) Tags.Remove(path);
        else Tags[path] = color;
        Revision++;
    }

    public void ClearTags(string path)
    {
        foreach (var key in Tags.Keys.Where(key => Inside(path, key)).ToArray()) Tags.Remove(key);
        Revision++;
    }

    private static bool Inside(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private void Log(ScanIssue issue)
    {
        Interlocked.Increment(ref issueCount);
        if (Issues.Count < 3000) Issues.Enqueue(issue);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        watcher?.Dispose();
        // In-flight workers retain the token until their next cancellation check.
    }
}

