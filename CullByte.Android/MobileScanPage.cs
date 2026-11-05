using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CullByte;

internal sealed class MobileScanPage : UserControl
{
    private const int PageSize = 25;
    private readonly IMobileStorage storage;
    private readonly FolderSource source;
    private readonly List<StorageItem> navigation = [];
    private readonly ScrollViewer scroll;
    private readonly MobileMap map = new() { Height = 268, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock title = Ui.Text("", 19, true);
    private readonly TextBlock summary = MobileView.Muted("");
    private readonly TextBlock status = MobileView.Muted("");
    private readonly TextBlock error = MobileView.Muted("");
    private readonly TextBox query = new() { PlaceholderText = "*.zip;>50mb", MinHeight = 48 };
    private readonly StackPanel rows = new() { Spacing = 5 };
    private readonly Button back;
    private readonly Button cancel;
    private readonly Button rescan;
    private readonly Button export;
    private readonly Button more;
    private readonly Border details;
    private readonly TextBlock detailName = Ui.Text("", 15, true);
    private readonly TextBlock detailPath = MobileView.Muted("");
    private readonly TextBlock detailSize = Ui.Text("");
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 3 };
    private CancellationTokenSource? job;
    private ScanResult? result;
    private StorageItem? scanPreview;
    private StorageItem? selectedItem;
    private Dictionary<StorageItem, (long Bytes, int Files)>? projection;
    private (StorageItem Item, long Bytes)[] items = [];
    private int shown;
    private string appliedQuery = "";
    private bool exporting;
    private int filterRequest;

    public event Action<string>? TitleChanged;

    public MobileScanPage(IMobileStorage storage, FolderSource source)
    {
        this.storage = storage;
        this.source = source;
        title.Text = source.Name;
        back = MobileView.TouchButton("‹", () => NavigateBack(), quiet: true);
        back.MinWidth = 48;
        Avalonia.Automation.AutomationProperties.SetName(back, "Go to parent folder");
        cancel = MobileView.TouchButton("Cancel", Cancel);
        rescan = MobileView.TouchButton("Rescan folder", () => _ = ScanAsync());
        export = MobileView.TouchButton("Export CSV…", () => _ = ExportAsync());
        var actions = new StackPanel { Spacing = 5 };
        actions.Children.Add(rescan);
        actions.Children.Add(export);
        var flyout = new Flyout { Content = actions };
        rescan.Click += (_, _) => flyout.Hide();
        export.Click += (_, _) => flyout.Hide();
        var menu = MobileView.TouchButton("Actions", () => { });
        menu.Flyout = flyout;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 5 };
        heading.Children.Add(back);
        MobileView.Put(heading, title, 0, 1);
        MobileView.Put(heading, menu, 0, 2);

        var filter = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        filter.Children.Add(query);
        Avalonia.Automation.AutomationProperties.SetName(query, "Filter by name or size");
        query.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = ApplyFilterAsync();
            e.Handled = true;
        };
        MobileView.Put(filter, MobileView.TouchButton("Apply", () => _ = ApplyFilterAsync()), 0, 1);
        MobileView.Put(filter, MobileView.TouchButton("Clear", () => { query.Text = ""; _ = ApplyFilterAsync(); }, quiet: true), 0, 2);
        error.Classes.Remove("muted");
        error.Classes.Add("error");
        error.IsVisible = false;

        var largest = Ui.Text("LARGEST ITEMS", 11, true);
        largest.Classes.Add("emphasis");
        more = MobileView.TouchButton("Show more", AddRows);
        var selected = new StackPanel { Spacing = 5 };
        selected.Children.Add(detailName);
        selected.Children.Add(detailSize);
        selected.Children.Add(detailPath);
        selected.Children.Add(MobileView.TouchButton("Show in Files", () =>
        {
            if (selectedItem is { } item) _ = RevealAsync(item);
        }));
        details = new Border { Child = selected, Padding = new Thickness(10), IsVisible = false };
        selected.Children.Add(MobileView.TouchButton("Close details", () => details.IsVisible = false, quiet: true));
        details.Classes.Add("surface");

        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(details);
        list.Children.Add(largest);
        list.Children.Add(rows);
        list.Children.Add(more);
        var work = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 12 };
        work.Children.Add(map);
        MobileView.Put(work, list, 1);
        map.ItemTapped += item => _ = RevealAsync(item);
        SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width;
            var wide = width >= 660;
            var preferredHeight = wide ? Math.Min(width * 0.39, e.NewSize.Height - 160) : width * 0.69;
            map.Height = Math.Clamp(preferredHeight, 180, 400);
            work.ColumnDefinitions = new ColumnDefinitions(wide ? "3*,2*" : "*");
            work.ColumnSpacing = wide ? 12 : 0;
            Grid.SetRow(list, wide ? 0 : 1);
            Grid.SetColumn(list, wide ? 1 : 0);
        };

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(12, 7, 12, 15), MaxWidth = 1100 };
        body.Children.Add(heading);
        body.Children.Add(summary);
        body.Children.Add(status);
        body.Children.Add(progress);
        body.Children.Add(cancel);
        body.Children.Add(filter);
        body.Children.Add(error);
        body.Children.Add(work);
        scroll = new ScrollViewer { Content = body };
        Content = scroll;
        Refresh();
    }

    public async Task ScanAsync()
    {
        if (job is not null) return;
        filterRequest++;
        using var cancellation = new CancellationTokenSource();
        job = cancellation;
        scanPreview = null;
        SetError("");
        status.Text = "Reading folder…";
        status.IsVisible = true;
        RefreshControls();
        try
        {
            var updates = new Progress<ScanProgress>(value =>
            {
                if (job != cancellation) return;
                status.Text = $"Reading folder… {value.Files:N0} files · {value.Folders:N0} folders";
                if (value.Preview is null) return;
                scanPreview = value.Preview;
                Refresh();
            });
            var scanned = await Task.Run(() => storage.Read(source, updates, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var filter = MobileFilter.Parse(appliedQuery);
            var scannedProjection = filter.IsActive ? await Task.Run(() => filter.Project(scanned.Root), cancellation.Token) : null;
            cancellation.Token.ThrowIfCancellationRequested();
            projection = scannedProjection;
            result = scanned;
            navigation.Clear();
            navigation.Add(scanned.Root);
            details.IsVisible = false;
            TitleChanged?.Invoke(TabTitle());
            status.Text = ScanNotice(scanned);
            Refresh();
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            status.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            status.Text = "";
            SetError($"Could not scan this folder. {ex.Message}");
        }
        finally
        {
            job = null;
            scanPreview = null;
            status.IsVisible = !string.IsNullOrEmpty(status.Text);
            Refresh();
        }
    }

    public void Cancel() => job?.Cancel();

    public bool NavigateBack()
    {
        if (details.IsVisible)
        {
            details.IsVisible = false;
            return true;
        }
        if (navigation.Count <= 1) return false;
        navigation.RemoveAt(navigation.Count - 1);
        scroll.Offset = default;
        Refresh();
        return true;
    }

    private void Open(StorageItem item)
    {
        if (job is not null) return;
        if (item.Children is not null)
        {
            navigation.Add(item);
            details.IsVisible = false;
            scroll.Offset = default;
            Refresh();
            return;
        }
        detailName.Text = item.Name;
        selectedItem = item;
        detailSize.Text = SizeText.Format(item.Bytes);
        detailPath.Text = item.Path;
        details.IsVisible = true;
        details.BringIntoView();
    }

    private async Task ApplyFilterAsync()
    {
        if (job is not null || result is not { } scanned) return;
        var request = ++filterRequest;
        var input = query.Text?.Trim() ?? "";
        try
        {
            var filter = MobileFilter.Parse(input);
            var filtered = filter.IsActive ? await Task.Run(() => filter.Project(scanned.Root)) : null;
            if (request != filterRequest || result != scanned) return;
            projection = filtered;
            appliedQuery = input;
            details.IsVisible = false;
            SetError("");
            TitleChanged?.Invoke(TabTitle());
            Refresh();
        }
        catch (FormatException ex) { SetError(ex.Message); }
    }

    private void Refresh()
    {
        var folder = scanPreview ?? navigation.LastOrDefault();
        var live = scanPreview is not null;
        title.Text = folder?.Name ?? source.Name;
        ToolTip.SetTip(title, folder?.Path);
        if (folder is null)
        {
            summary.Text = "Choose a folder to see its size and contents.";
            items = [];
        }
        else
        {
            var weight = live ? (Bytes: folder.Bytes, Files: folder.Files) : Weight(folder);
            summary.Text = $"{SizeText.Format(weight.Bytes)}  ·  {weight.Files:N0} " +
                (live || projection is null ? "files" : "matching files");
            items = folder.Children!.Where(item => live || projection is null || projection.ContainsKey(item))
                .Select(item => (Item: item, Bytes: live ? item.Bytes : Weight(item).Bytes))
                .OrderByDescending(item => item.Bytes).ThenBy(item => item.Item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        map.SetItems(items, folder is null ? "Scan results appear here." :
            projection is null ? "This folder has no items with a reported size." : "No matching items with a reported size.");
        rows.Children.Clear();
        shown = 0;
        AddRows();
        RefreshControls();
    }

    private void RefreshControls()
    {
        var busy = job is not null;
        back.IsVisible = navigation.Count > 1 || details.IsVisible;
        progress.IsVisible = cancel.IsVisible = busy;
        rescan.IsEnabled = !busy;
        export.IsEnabled = !busy && !exporting && result is not null;
        query.IsEnabled = !busy && result is not null;
    }

    private void AddRows()
    {
        foreach (var (item, bytes) in items.Skip(shown).Take(PageSize))
        {
            var label = Ui.Text((item.Children is null ? "" : "▸  ") + item.Name, 13);
            var size = Ui.Text(SizeText.Format(bytes), 12);
            size.Classes.Add("muted");
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            line.Children.Add(label);
            MobileView.Put(line, size, 0, 1);
            var row = MobileView.TouchButton("", () => _ = RevealAsync(item));
            row.IsEnabled = job is null;
            row.Classes.Add("mobile-row");
            row.Content = line;
            Avalonia.Automation.AutomationProperties.SetName(row, $"Show {item.Name} in Files, {size.Text}");
            var internalAction = MobileView.TouchButton(item.Children is null ? "Info" : "Explore", () => Open(item));
            internalAction.IsEnabled = job is null;
            Avalonia.Automation.AutomationProperties.SetName(internalAction,
                item.Children is null ? $"Details for {item.Name}" : $"Explore {item.Name} in CullByte");
            var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 5 };
            actions.Children.Add(row);
            MobileView.Put(actions, internalAction, 0, 1);
            rows.Children.Add(actions);
            shown++;
        }
        more.IsVisible = shown < items.Length;
    }

    private async Task RevealAsync(StorageItem item)
    {
        if (job is not null) return;
        SetError("");
        try { await storage.RevealAsync(source, item); }
        catch (Exception ex) { SetError($"Could not open Files. {ex.Message}"); }
    }

    private async Task ExportAsync()
    {
        if (exporting || job is not null || navigation.LastOrDefault() is not { } root) return;
        exporting = true;
        SetError("");
        RefreshControls();
        try
        {
            if (await storage.ExportAsync("CullByte report.csv", Files(root, projection)))
            {
                status.Text = "Report saved.";
                status.IsVisible = true;
            }
        }
        catch (Exception ex) { SetError($"Could not export the report. {ex.Message}"); }
        finally { exporting = false; RefreshControls(); }
    }

    private static IEnumerable<StorageItem> Files(StorageItem root,
        IReadOnlyDictionary<StorageItem, (long Bytes, int Files)>? matches)
    {
        var stack = new Stack<StorageItem>();
        stack.Push(root);
        while (stack.TryPop(out var item))
        {
            if (matches is not null && !matches.ContainsKey(item)) continue;
            if (item.Children is { } children)
            {
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            else yield return item;
        }
    }

    private (long Bytes, int Files) Weight(StorageItem item) =>
        projection is null ? (item.Bytes, item.Files) : projection.GetValueOrDefault(item);

    private string TabTitle()
    {
        var name = result?.Root.Name ?? source.Name;
        return appliedQuery.Length == 0 ? name : $"{name} · {appliedQuery}";
    }

    private static string ScanNotice(ScanResult scan)
    {
        var warnings = new List<string>();
        if (scan.Unreadable > 0) warnings.Add($"{scan.Unreadable:N0} entries skipped");
        if (scan.UnknownSizes > 0) warnings.Add($"{scan.UnknownSizes:N0} files have unknown sizes");
        return string.Join(" · ", warnings);
    }

    private void SetError(string text)
    {
        error.Text = text;
        error.IsVisible = text.Length > 0;
    }
}

