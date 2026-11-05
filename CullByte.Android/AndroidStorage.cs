using System.Diagnostics;
using System.Globalization;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace CullByte;

internal sealed class AndroidStorage(MainActivity activity) : IMobileStorage
{
    private static readonly string[] Columns = [DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize];

    public string Theme
    {
        get => activity.GetPreferences(FileCreationMode.Private)?.GetString("theme", "System") ?? "System";
        set => activity.GetPreferences(FileCreationMode.Private)?.Edit()?.PutString("theme", value)?.Apply();
    }

    public async Task<FolderSource?> ChooseAsync()
    {
        using var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        var uri = await activity.PickAsync(intent);
        if (uri is null) return null;
        var id = DocumentsContract.GetTreeDocumentId(uri) ?? throw new IOException("Android returned an invalid folder.");
        var tail = id.TrimEnd('/').Split('/', ':').LastOrDefault();
        return new FolderSource(uri.ToString()!, string.IsNullOrWhiteSpace(tail) ? "Selected folder" : tail);
    }

    public ScanResult Read(FolderSource folder, IProgress<ScanProgress> progress, CancellationToken token)
    {
        using var tree = AndroidUri.Parse(folder.Id) ?? throw new IOException("Folder access is unavailable.");
        var rootId = DocumentsContract.GetTreeDocumentId(tree) ?? throw new IOException("The folder has no document ID.");
        var resolver = activity.ContentResolver ?? throw new IOException("Android storage is unavailable.");
        using var signal = new CancellationSignal();
        using var registration = token.Register(signal.Cancel);
        using var rootUri = DocumentsContract.BuildDocumentUriUsingTree(tree, rootId);
        var rootName = folder.Name;
        if (rootUri is not null)
        {
            using var rootCursor = resolver.Query(rootUri, [DocumentsContract.Document.ColumnDisplayName],
                null, null, null, signal);
            if (rootCursor?.MoveToFirst() == true) rootName = rootCursor.GetString(0) ?? rootName;
        }

        var root = new StorageItem(rootName, rootName, 0, true) { DocumentId = rootId };
        var queue = new Queue<(string Id, StorageItem Item)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var files = 0;
        var folders = 0;
        var unreadable = 0;
        var unknown = 0;
        var clock = Stopwatch.StartNew();
        queue.Enqueue((rootId, root));

        while (queue.TryDequeue(out var entry))
        {
            token.ThrowIfCancellationRequested();
            folders++;
            try
            {
                using var uri = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, entry.Id)
                    ?? throw new IOException("The folder URI could not be resolved.");
                using var cursor = resolver.Query(uri, Columns, null, null, null, signal)
                    ?? throw new IOException("Android could not list this folder.");
                while (cursor.MoveToNext())
                {
                    token.ThrowIfCancellationRequested();
                    var id = cursor.GetString(0);
                    if (string.IsNullOrEmpty(id)) { unreadable++; continue; }
                    var name = cursor.GetString(1) ?? "Unnamed item";
                    var directory = cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir;
                    var bytes = cursor.IsNull(3) ? -1 : cursor.GetLong(3);
                    var child = new StorageItem(name, entry.Item.Path + "/" + name, directory ? 0 : Math.Max(0, bytes), directory)
                        { DocumentId = id, ParentDocumentId = entry.Id, Parent = entry.Item };
                    if (directory)
                    {
                        if (!visited.Add(id)) { unreadable++; continue; }
                        queue.Enqueue((id, child));
                    }
                    else
                    {
                        files++;
                        if (bytes < 0) unknown++;
                        for (var ancestor = entry.Item; ancestor is not null; ancestor = ancestor.Parent)
                            ancestor.AddTotals(child);
                    }
                    entry.Item.Children!.Add(child);
                    if (clock.ElapsedMilliseconds >= 125)
                    {
                        progress.Report(new ScanProgress(files, folders, root.Preview()));
                        clock.Restart();
                    }
                }
            }
            catch (Exception ex) when (entry.Item != root && !token.IsCancellationRequested &&
                ex is IOException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
            {
                unreadable++;
            }
        }
        token.ThrowIfCancellationRequested();
        return new ScanResult(root, unreadable, unknown);
    }

    public async Task RevealAsync(FolderSource folder, StorageItem item)
    {
        using var tree = AndroidUri.Parse(folder.Id) ?? throw new IOException("Folder access is unavailable.");
        var containerId = (item.Children is null ? item.ParentDocumentId : item.DocumentId)
            ?? DocumentsContract.GetTreeDocumentId(tree) ?? throw new IOException("Folder location is unavailable.");
        using var location = DocumentsContract.BuildDocumentUriUsingTree(tree, containerId)
            ?? throw new IOException("Folder location is unavailable.");
        using var view = new Intent(Intent.ActionView);
        view.SetDataAndType(location, DocumentsContract.Document.MimeTypeDir);
        view.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            activity.StartActivity(view);
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            using var browse = new Intent(Intent.ActionOpenDocument);
            browse.AddCategory(Intent.CategoryOpenable);
            browse.SetType("*/*");
            browse.PutExtra(DocumentsContract.ExtraInitialUri, location);
            using var selected = await activity.PickAsync(browse);
        }
    }

    public async Task<bool> ExportAsync(string suggestedName, IEnumerable<StorageItem> files)
    {
        using var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("text/csv");
        intent.PutExtra(Intent.ExtraTitle, suggestedName);
        using var uri = await activity.PickAsync(intent);
        if (uri is null) return false;
        await Task.Run(() =>
        {
            using var stream = activity.ContentResolver?.OpenOutputStream(uri, "wt")
                ?? throw new IOException("Android could not write the selected file.");
            using var writer = new StreamWriter(stream);
            writer.WriteLine("Path,Bytes");
            foreach (var item in files)
            {
                var path = item.Path.Replace("\"", "\"\"");
                if (path.Length > 0 && path[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') path = "'" + path;
                writer.WriteLine($"\"{path}\",{item.Bytes.ToString(CultureInfo.InvariantCulture)}");
            }
        });
        return true;
    }
}
