using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.VisualBasic.FileIO;

namespace CullByte;

internal sealed class ScanView : UserControl
{
    private readonly MainWindow owner;
    private Preferences preferences;
    private readonly Treemap map;
    private readonly TextBox query = new() { PlaceholderText = @"*.zip;>500mb;|:red", MinWidth = 155 };
    private readonly TextBlock path = Ui.Text("", 15, true);
    private readonly TextBlock status = Ui.Text("");
    private readonly TextBlock share = Ui.Text("");
    private readonly TextBlock mode = Ui.Text("", 11, true);
    private readonly TextBlock validation = Ui.Text("");
    private readonly TextBlock inspectorTitle = Ui.Text("", 15, true);
    private readonly TextBlock inspectorInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ListBox files = new() { MinHeight = 100 };
    private readonly ProgressBar progress = new() { Width = 96, Height = 4 };
    private readonly List<string> history = [];
    private readonly Button back;
    private readonly Button forward;
    private readonly Button up;
    private readonly Button stop;
    private readonly Button refine;
    private readonly Button rescan;
    private readonly Button menuButton;
    private readonly Button inspectorZoom;
    private readonly StackPanel selectionDetails;
    private int historyIndex;
    private int revision = -1;
    private int lastFiles = -1;
    private int lastFolders = -1;
    private int selectedRevision = -1;
    private bool lastFlash;
    private DiskNode? selected;
    private DiskNode? current;

    public ScanSession Session { get; }
    public DiskNode FocusedNode => current ?? Session.Root;
    public ScanFilter Filter => map.Filter;
    public string Query => query.Text ?? "";
    public string AppliedQuery { get; private set; } = "";
    public string CurrentPath => history[historyIndex];
    public event Action? FilterApplied;

    public ScanView(MainWindow owner, ScanSession session, Preferences preferences, string initialQuery = "")
    {
        this.owner = owner;
        Session = session;
        this.preferences = preferences;
        history.Add(session.Source);
        map = new Treemap(session, preferences) { MinHeight = 110 };
        map.Selected += Select;
        map.Zoomed += (node, rect) => Navigate(node.FullPath, rect);
        map.ItemMenu += ShowItemMenu;
        map.LayoutCompleted += () => Dispatcher.UIThread.Post(() =>
        {
            share.IsVisible = Filter.Active;
            share.Text = $"{map.VisibleShare:P0} matches";
            UpdateItems();
        }, DispatcherPriority.Background);

        back = NavigationButton("←", Back, "Back (Backspace)");
        forward = NavigationButton("→", Forward, "Forward (Shift+Backspace)");
        up = NavigationButton("↑", Up, "Parent folder (Ctrl+Up)");
        stop = Ui.Button("Stop", session.Stop);
        rescan = Ui.Button("Rescan", () => _ = session.ScanAsync(), "Rescan the source (F5)");
        refine = Ui.Button("Refine", () => _ = session.RefreshBranchAsync(FocusedNode.FullPath), "Rescan this branch (Shift+F5)");

        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        var arrows = Ui.Row(back, forward, up, NavigationButton("Root", Home, "Root (Ctrl+Home)"));
        arrows.Spacing = 2;
        navigation.Children.Add(arrows);
        var location = new Border { Child = path, Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), MinHeight = 30 };
        location.Classes.Add("recess");
        AddColumn(navigation, location, 1);
        var jobs = Ui.Row(rescan, refine, stop);
        Grid.SetColumn(jobs, 2);
        navigation.Children.Add(jobs);

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 7 };
        var filterLabel = Ui.Text("Filter", 12, true);
        filterLabel.Classes.Add("muted");
        filters.Children.Add(filterLabel);
        AddColumn(filters, query, 1);
        Avalonia.Automation.AutomationProperties.SetName(query, "Filter scan results");
        query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ApplyFilter(); e.Handled = true; }
        };
        AddColumn(filters, Ui.Button("Apply", ApplyFilter, "Apply filter (Enter)"), 2);
        AddColumn(filters, NavigationButton("Clear", () => { query.Text = ""; ApplyFilter(); }, "Clear the active filter"), 3);
        menuButton = Ui.Button("Map ▾", ShowMapMenu, "Detail level, colors and space overlays");
        AddColumn(filters, menuButton, 4);
        validation.Classes.Add("error");
        validation.IsVisible = false;
        files.Classes.Add("compact");
        files.ItemTemplate = new FuncDataTemplate<ItemRow>((row, _) =>
        {
            if (row is null) return null;
            var caption = Ui.Text(row.Caption, 12);
            ToolTip.SetTip(caption, row.Node.Entry.Name);
            var size = Ui.Text(row.Size, 12);
            size.Classes.Add("muted");
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            line.Children.Add(caption);
            AddColumn(line, size, 1);
            return line;
        });

        files.SelectionChanged += (_, _) =>
        {
            if (files.SelectedItem is ItemRow row) Select(row.Node);
        };
        files.DoubleTapped += (_, _) => OpenSelectedFolder();
        files.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelectedFolder(); e.Handled = true; }
        };

        var zoomItem = Ui.Button("Zoom", OpenSelectedFolder);
        var actionItem = Ui.Button("Actions ▾", () =>
        {
            if (selected is not null) ShowItemMenu(selected, inspectorTitle);
        });
        var itemActions = Ui.Row(zoomItem, actionItem);
        var selection = new StackPanel { Spacing = 5, IsVisible = false };
        selection.Children.Add(inspectorTitle);
        selection.Children.Add(inspectorInfo);
        selection.Children.Add(itemActions);
        selectionDetails = selection;
        inspectorZoom = zoomItem;
        inspectorInfo.Classes.Add("muted");
        inspectorInfo.MaxLines = 7;
        inspectorInfo.TextTrimming = TextTrimming.CharacterEllipsis;
        var listLabel = Ui.Text("LARGEST ITEMS", 11, true);
        listLabel.Classes.Add("muted");
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 7 };
        sidebar.Children.Add(listLabel);
        AddRow(sidebar, selection, 1);
        AddRow(sidebar, files, 2);
        var inspector = new Border { Child = sidebar, Padding = new Thickness(11), Width = 248 };
        inspector.Classes.Add("surface");
        var work = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        work.Children.Add(map);
        AddColumn(work, inspector, 1);
        bool? inspectorOverride = null;
        var inspectorButton = new Button { Content = "Inspector" };
        inspectorButton.Click += (_, _) => ToggleInspector();
        ToolTip.SetTip(inspectorButton, "Show or hide the item inspector");
        AddColumn(filters, inspectorButton, 5);
        SizeChanged += (_, e) => SetInspector(inspectorOverride ?? e.NewSize.Width >= 940);
        void ToggleInspector()
        {
            inspectorOverride = !inspector.IsVisible;
            SetInspector(inspectorOverride.Value);
        }
        void SetInspector(bool visible)
        {
            inspector.IsVisible = visible;
            work.ColumnSpacing = visible ? 10 : 0;
            inspectorButton.Content = visible ? "Inspector ✓" : "Inspector";
        }
        SetInspector(true);

        mode.Classes.Add("emphasis");
        status.Classes.Add("muted");
        share.Classes.Add("muted");
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        bottom.Children.Add(mode);
        AddColumn(bottom, status, 1);
        AddColumn(bottom, share, 2);
        AddColumn(bottom, progress, 3);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(13, 9, 13, 8), RowSpacing = 7 };
        layout.Children.Add(navigation);
        AddRow(layout, filters, 1);
        AddRow(layout, validation, 2);
        AddRow(layout, work, 3);
        AddRow(layout, bottom, 4);
        Content = layout;

        query.Text = initialQuery;
        ApplyFilter();
        Tick();
    }

    private static Button NavigationButton(string text, Action action, string help)
    {
        var button = Ui.Button(text, action, help);
        button.Classes.Add("quiet");
        Avalonia.Automation.AutomationProperties.SetName(button, help);
        return button;
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static void AddColumn(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    public void UpdatePreferences(Preferences value)
    {
        preferences = value;
        map.UpdatePreferences(value);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        try
        {
            map.Filter = ScanFilter.Parse(Query, preferences.Classes());
            AppliedQuery = Query.Trim();
            FilterApplied?.Invoke();
            validation.IsVisible = false;
            map.Refresh(clearWeights: true);
        }
        catch (FormatException ex)
        {
            validation.Text = ex.Message;
            validation.IsVisible = true;
        }
    }

    public void Tick()
    {
        DiskNode node;
        int fileCount, folderCount, focusFiles, focusFolders;
        long bytes, allocated;
        lock (Session.Gate)
        {
            var root = Session.Root;
            var candidate = CurrentPath;
            node = root.Find(candidate) ?? root;
            while (node == root && !candidate.Equals(Session.Source, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetDirectoryName(candidate) is { } parent && parent.Length >= Session.Source.Length)
            {
                candidate = parent;
                node = root.Find(candidate) ?? root;
            }
            fileCount = root.Files;
            folderCount = root.Folders;
            focusFiles = node.Files;
            focusFolders = Math.Max(0, node.Folders - 1);
            bytes = node.Bytes;
            allocated = node.Allocated;
        }
        var updated = revision != Session.Revision || fileCount != lastFiles || folderCount != lastFolders;
        if (updated || node != current)
        {
            revision = Session.Revision;
            lastFiles = fileCount;
            lastFolders = folderCount;
            current = node;
            map.Show(node);
        }
        var flash = Session.FlashUntil > DateTime.UtcNow;
        if (flash || lastFlash) map.InvalidateVisual();
        lastFlash = flash;
        map.EmptyText = Session.Scanning ? "Scanning folders..." :
            map.Filter.Active ? "No nonzero items match this filter." : "This folder has no nonzero items.";
        path.Text = node.FullPath;
        ToolTip.SetTip(path, node.FullPath);
        mode.Text = Session.Offline ? "SNAPSHOT" : Session.Refining ? "REFINING" : Session.Scanning ?
            "SCANNING" : Session.Cancelled ? "PARTIAL SCAN" : "READY";
        var free = Session.Volume is { } volume ? $"  ·  {SizeText.Format(volume.Free)} free" : "";
        var issues = Session.IssueCount > 0 ? $"  ·  {Session.IssueCount:N0} issues" : "";
        status.Text = $"{SizeText.Format(map.Logical ? bytes : allocated)}  ·  {focusFiles:N0} files  ·  " +
            $"{focusFolders:N0} folders{free}{issues}";
        progress.IsIndeterminate = Session.Scanning || Session.Refining;
        progress.IsVisible = progress.IsIndeterminate;
        stop.IsVisible = Session.Scanning && !Session.Offline;
        rescan.IsEnabled = !Session.Offline && !Session.Scanning;
        refine.IsEnabled = !Session.Offline && !Session.Refining && node.Parent is not null;
        back.IsEnabled = historyIndex > 0;
        forward.IsEnabled = historyIndex < history.Count - 1;
        up.IsEnabled = node.Parent is not null;

        if (selected is not null && selectedRevision != Session.Revision)
        {
            DiskNode? resolved;
            lock (Session.Gate) resolved = Session.Root.Find(selected.FullPath);
            if (resolved is not null) Select(resolved);
            else
            {
                selected = null;
                selectionDetails.IsVisible = false;
            }
        }
    }

    private void UpdateItems()
    {
        List<ItemRow> top;
        lock (Session.Gate)
        {
            top = (FocusedNode.Children ?? []).Select(node => (Node: node, Size: map.ListedSize(node)))
                .Where(item => item.Size.HasValue).OrderByDescending(item => item.Size).Take(40)
                .Select(item => new ItemRow(item.Node, $"{(item.Node.Entry.Directory ? "▸ " : "")}{item.Node.Entry.Name}",
                    SizeText.Format(item.Size!.Value))).ToList();
        }
        files.ItemsSource = top;
    }

    private sealed record ItemRow(DiskNode Node, string Caption, string Size);

    private void Select(DiskNode node)
    {
        selected = node;
        selectedRevision = Session.Revision;
        selectionDetails.IsVisible = true;
        inspectorZoom.IsVisible = node.Entry.Directory;
        map.Select(node);
        lock (Session.Gate)
        {
            inspectorTitle.Text = node.Entry.Name;
            ToolTip.SetTip(inspectorTitle, node.FullPath);
            inspectorInfo.Text = Treemap.Describe(node);
        }
    }

    private void OpenSelectedFolder()
    {
        if (selected?.Children is not null) Navigate(selected.FullPath);
    }

    public void Navigate(string target, Rect? source = null)
    {
        if (target == CurrentPath) return;
        history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(target);
        historyIndex++;
        Move(source);
    }

    private void Move(Rect? source = null)
    {
        lock (Session.Gate) current = Session.Root.Find(CurrentPath) ?? Session.Root;
        selected = null;
        selectionDetails.IsVisible = false;
        map.Show(current, source);
        Tick();
    }

    private void Back() { if (historyIndex > 0) { historyIndex--; Move(); } }
    private void Forward() { if (historyIndex < history.Count - 1) { historyIndex++; Move(); } }
    private void Up() { if (FocusedNode.Parent is { } parent) Navigate(parent.FullPath); }
    private void Home() => Navigate(Session.Source);

    private void ShowMapMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item($"More detail ({map.Detail})", () => ChangeDetail(1)));
        menu.Items.Add(Ui.Item("Less detail", () => ChangeDetail(-1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Toggle("Show free space", map.Free, value => map.Free = value));
        menu.Items.Add(Toggle("Show unaccounted space", map.Unknown, value => map.Unknown = value));
        menu.Items.Add(Toggle("Size by logical bytes", map.Logical, value => map.Logical = value));
        menu.Items.Add(Toggle("Color by file class", map.ClassColors, value => map.ClassColors = value));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Clear tags in focused folder", () => Session.ClearTags(FocusedNode.FullPath)));
        menu.Open(menuButton);

        MenuItem Toggle(string name, bool value, Action<bool> set) => Ui.Item((value ? "✓  " : "     ") + name, () =>
        {
            set(!value);
            map.Refresh(clearWeights: true);
            Tick();
        });
    }

    private void ChangeDetail(int step)
    {
        map.Detail = Math.Clamp(map.Detail + step, 1, 8);
        map.Refresh();
    }

    private void ShowItemMenu(DiskNode node, Control anchor)
    {
        var menu = new ContextMenu();
        if (node.Entry.Directory) menu.Items.Add(Ui.Item("Zoom into folder", () => Navigate(node.FullPath)));
        if (!Session.Offline && !node.Entry.Stream)
        {
            menu.Items.Add(Ui.Item("Open", () => Shell(node, "open")));
            if (OperatingSystem.IsWindows())
            {
                menu.Items.Add(Ui.Item("Show in Explorer", () => Shell(node, "reveal")));
                menu.Items.Add(Ui.Item("Properties", () => Shell(node, "properties")));
                if (node.Parent is not null)
                    menu.Items.Add(Ui.Item("Move to Recycle Bin…", () => _ = RecycleAsync(node)));
            }
        }
        menu.Items.Add(Ui.Item("Copy path", () => _ = owner.CopyAsync(node.FullPath)));
        menu.Items.Add(new Separator());
        string[] names = ["Red", "Yellow", "Green", "Blue"];
        for (var i = 0; i < names.Length; i++)
        {
            var color = i + 1;
            menu.Items.Add(Ui.Item($"Toggle {names[i]} tag", () => { Session.ToggleTag(node, color); Tick(); }));
        }
        menu.Open(anchor);
    }

    private void Shell(DiskNode node, string action)
    {
        try
        {
            if (action == "reveal")
            {
                var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                explorer.ArgumentList.Add("/select," + node.FullPath);
                Process.Start(explorer)?.Dispose();
            }
            else if (action == "properties")
            {
                var info = new ShellExecuteInfo
                {
                    Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x0000000C, Verb = "properties",
                    File = node.FullPath, Show = 1
                };
                if (!ShellExecuteEx(ref info)) throw new System.ComponentModel.Win32Exception();
            }
            else Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { _ = owner.MessageAsync("Windows action failed", ex.Message); }
    }

    private async Task RecycleAsync(DiskNode node)
    {
        var target = node.FullPath;
        var warning = node.Entry.Directory ? "\n\nThis includes all contents, including items hidden by filters." : "";
        if (!await owner.MessageAsync("Move to Recycle Bin?", target + warning, "Recycle")) return;
        try
        {
            await Task.Run(() =>
            {
                if (node.Entry.Directory)
                    FileSystem.DeleteDirectory(target, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
                else FileSystem.DeleteFile(target, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            });
            if (node.Parent is { } parent) await Session.RefreshBranchAsync(parent.FullPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await owner.MessageAsync("Recycle failed", ex.Message); }
    }

    public bool Shortcut(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.F5)
        {
            if (shift) _ = Session.RefreshBranchAsync(FocusedNode.FullPath);
            else _ = Session.ScanAsync();
            return true;
        }
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Up: Up(); return true;
                case Key.Home: Home(); return true;
                case Key.F: map.Free = !map.Free; map.Refresh(); return true;
                case Key.U: map.Unknown = !map.Unknown; map.Refresh(); return true;
                case Key.T: map.ClassColors = !map.ClassColors; map.InvalidateVisual(); return true;
                case Key.OemPlus or Key.Add: ChangeDetail(1); return true;
                case Key.OemMinus or Key.Subtract: ChangeDetail(-1); return true;
                case Key.L: query.Focus(); query.SelectAll(); return true;
            }
        }
        if (!query.IsFocused && e.Key == Key.Back)
        {
            if (shift) Forward(); else Back();
            return true;
        }
        if ((ctrl || !query.IsFocused) && e.Key is >= Key.D0 and <= Key.D4)
        {
            var color = e.Key - Key.D0;
            if (color == 0) Session.ClearTags(FocusedNode.FullPath);
            else if ((map.Hovered ?? selected) is { } node) Session.ToggleTag(node, color);
            Tick();
            return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        public string Verb;
        public string File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public nint Instance, IdList, Class, ClassKey;
        public uint HotKey;
        public nint Icon, Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteExW", SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}




