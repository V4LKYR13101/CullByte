using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace CullByte;

internal sealed class MainWindow : Window
{
    private static readonly FilePickerFileType Snapshots = new("CullByte snapshot") { Patterns = ["*.cullscan"] };
    private readonly TabControl tabs = new();
    private readonly Border empty;
    private readonly DispatcherTimer timer;
    private readonly List<(Window Window, ScanView View)> detached = [];
    private Preferences preferences = Preferences.Load();
    private bool closing;

    private IEnumerable<ScanView> Views => tabs.Items.OfType<TabItem>().Select(tab => (ScanView)tab.Content!)
        .Concat(detached.Select(item => item.View));
    private ScanView? Active => (tabs.SelectedItem as TabItem)?.Content as ScanView;

    public MainWindow(string[] args)
    {
        Title = "CullByte  /  Disk space explorer";
        Width = 1230;
        Height = 820;
        MinWidth = 730;
        MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyTheme();
        tabs.Classes.Add("scan-tabs");
        tabs.IsVisible = false;
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs) return;
            Active?.Tick();
            (tabs.SelectedItem as TabItem)?.BringIntoView();
        };

        var mark = new Border
        {
            Background = Ui.Color("#50D9B6"), CornerRadius = new CornerRadius(8), Width = 34, Height = 34,
            Child = new TextBlock { Text = "C", Foreground = Ui.Color("#0F302D"), FontSize = 21,
                FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center }
        };
        var heading = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(Ui.Text("CullByte", 17, true));
        var tagline = Ui.Text("Disk space explorer", 11);
        tagline.Classes.Add("muted");
        heading.Children.Add(tagline);
        var wordmark = Ui.Row(mark, heading);
        wordmark.Spacing = 9;
        var primary = Ui.Button("+ New scan", () => _ = ChooseAsync(), "Ctrl+N");
        primary.Classes.Add("accent");
        var actions = Ui.Row(primary, Ui.Button("Open snapshot", () => _ = OpenSnapshotAsync(), "Ctrl+O"),
            Ui.Button("Settings", () => _ = SettingsAsync()));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(14, 9), ColumnSpacing = 14 };
        header.Children.Add(wordmark);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);

        var menu = new Menu
        {
            ItemsSource = new[]
            {
                Group("_File", Ui.Item("New scan   Ctrl+N", () => _ = ChooseAsync()),
                    Ui.Item("Open snapshot   Ctrl+O", () => _ = OpenSnapshotAsync()),
                    Ui.Item("Save snapshot   Ctrl+S", () => _ = SaveSnapshotAsync(Active)),
                    Ui.Item("Export report   Ctrl+E", () => _ = ExportAsync(Active)),
                    Ui.Item("Close view   Ctrl+W", CloseActive)),
                Group("_View", Ui.Item("Duplicate current view", () => Duplicate(false)),
                    Ui.Item("Open side-by-side window", () => Duplicate(true)),
                    Ui.Item("Scan log", () => _ = LogAsync(Active)), Ui.Item("Settings", () => _ = SettingsAsync())),
                Group("_Help", Ui.Item("Filters and shortcuts", () => _ = HelpAsync()),
                    Ui.Item("About CullByte", () => _ = MessageAsync("CullByte", "Version 0.1.0\nA disk space explorer for Windows.")))
            }
        };
        menu.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(menu, 1);
        header.Children.Add(menu);
        empty = BuildEmpty();
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        main.Children.Add(header);
        Grid.SetRow(tabs, 1);
        main.Children.Add(tabs);
        Grid.SetRow(empty, 1);
        main.Children.Add(empty);
        Content = main;

        DragDrop.SetAllowDrop(main, true);
        main.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects =
            e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        main.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths is { Length: > 0 }) _ = OpenPathsAsync(paths);
        });
        AddHandler(KeyDownEvent, (_, e) => HandleKey(Active, e), RoutingStrategies.Tunnel);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(preferences.RefreshMilliseconds) };
        timer.Tick += (_, _) =>
        {
            Active?.Tick();
            foreach (var item in detached) item.View.Tick();
            foreach (var session in Views.Select(view => view.Session).Distinct()) _ = session.RefreshPendingAsync();
        };
        timer.Start();
        Closed += (_, _) =>
        {
            closing = true;
            timer.Stop();
            foreach (var session in Views.Select(view => view.Session).Distinct()) session.Dispose();
            foreach (var item in detached.ToArray()) item.Window.Close();
        };
        Opened += (_, _) =>
        {
            if (args.Length > 0) _ = StartAsync(args);
        };
    }

    private static MenuItem Group(string title, params MenuItem[] children) => new() { Header = title, ItemsSource = children };

    private Border BuildEmpty()
    {
        var content = new StackPanel { Spacing = 13, MaxWidth = 490, VerticalAlignment = VerticalAlignment.Center };
        var badge = Ui.Text("STORAGE OVERVIEW", 11, true);
        badge.Classes.Add("emphasis");
        content.Children.Add(badge);
        content.Children.Add(new TextBlock
        {
            Text = "See what’s taking up space.", TextWrapping = TextWrapping.Wrap, FontSize = 39,
            FontWeight = FontWeight.SemiBold, LineHeight = 43
        });
        var description = new TextBlock
        {
            Text = "Scan a drive or folder to find what’s using the most storage. The bigger the tile, the more space it uses.",
            TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 0, 0, 5)
        };
        description.Classes.Add("muted");
        content.Children.Add(description);
        var start = Ui.Button("Choose a drive or folder", () => _ = ChooseAsync());
        start.Classes.Add("accent");
        content.Children.Add(Ui.Row(start, Ui.Button("Open saved scan", () => _ = OpenSnapshotAsync())));
        var drives = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).Take(5))
        {
            var name = drive.Name;
            var button = Ui.Button(name, () => _ = OpenPathsAsync([name]));
            button.Margin = new Thickness(0, 0, 8, 5);
            drives.Children.Add(button);
        }
        if (drives.Children.Count > 0)
        {
            var quick = Ui.Text("QUICK SCAN", 11, true);
            quick.Classes.Add("muted");
            quick.Margin = new Thickness(0, 8, 0, -8);
            content.Children.Add(quick);
            content.Children.Add(drives);
        }
        var drop = Ui.Text("You can also drop folders or snapshots here.", 12);
        drop.Classes.Add("muted");
        content.Children.Add(drop);
        var sample = new MapPreview { Height = 245, MinWidth = 270 };
        var sampleLabel = Ui.Text("EXAMPLE TREEMAP", 11, true);
        sampleLabel.Classes.Add("muted");
        var preview = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(sampleLabel);
        preview.Children.Add(sample);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 40,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 1020 };
        layout.Children.Add(content);
        Grid.SetColumn(preview, 1);
        layout.Children.Add(preview);
        var card = new Border { Child = layout, Padding = new Thickness(34), Margin = new Thickness(14, 0, 14, 14) };
        card.Classes.Add("surface");
        card.SizeChanged += (_, e) =>
        {
            var show = e.NewSize.Width >= 870 && e.NewSize.Height >= 360;
            if (preview.IsVisible == show) return;
            preview.IsVisible = show;
            layout.ColumnDefinitions[1].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            layout.ColumnSpacing = show ? 40 : 0;
            content.HorizontalAlignment = show ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        };
        return card;
    }

    private async Task StartAsync(string[] args)
    {
        var paths = new List<string>();
        var filter = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (args[i] is "--scan" or "--load" && i + 1 < args.Length) paths.Add(args[++i]);
            else paths.Add(args[i]);
        }
        await OpenPathsAsync(paths, filter);
    }

    private async Task OpenPathsAsync(IEnumerable<string> paths, string filter = "")
    {
        foreach (var raw in paths)
        {
            if (closing) return;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim().Trim('"')));
                if (File.Exists(full) && full.EndsWith(".cullscan", StringComparison.OrdinalIgnoreCase))
                {
                    var (tree, volume) = await Task.Run(() => Storage.Load(full));
                    if (closing) return;
                    OpenView(new ScanSession(tree.FullPath, preferences.ScanOptions, tree, volume),
                        Path.GetFileNameWithoutExtension(full) + "  [snapshot]", filter);
                    continue;
                }
                var existing = Views.Select(view => view.Session).FirstOrDefault(session =>
                    !session.Offline && session.Source.Equals(full, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    OpenView(existing, TitleOf(full), filter);
                    continue;
                }
                if (!await Task.Run(() => Directory.Exists(full)))
                    throw new IOException($"Could not open {full}. Choose an accessible folder or a .cullscan file.");
                if (closing) return;
                var newSession = new ScanSession(full, preferences.ScanOptions);
                OpenView(newSession, TitleOf(full), filter);
                _ = newSession.ScanAsync();
            }
            catch (Exception ex) { await MessageAsync("Could not open source", ex.Message); }
        }
    }

    private static string TitleOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length == 0 ? path : name;
    }

    private ScanView OpenView(ScanSession session, string label, string filter = "")
    {
        var view = new ScanView(this, session, preferences, filter);
        var tab = new TabItem { Content = view };
        tab.Classes.Add("scan-tab");
        var close = Ui.Button("×", () => CloseView(tab), "Close scan view");
        close.Classes.Add("quiet");
        close.Classes.Add("tab-close");
        Avalonia.Automation.AutomationProperties.SetName(close, $"Close {label}");
        var title = Ui.Text("", 12, true);
        title.MaxWidth = 210;
        tab.Header = Ui.Row(title, close);
        view.FilterApplied += UpdateTabLabel;
        UpdateTabLabel();
        tabs.Items.Add(tab);
        tabs.SelectedItem = tab;
        tabs.IsVisible = true;
        empty.IsVisible = false;
        return view;

        void UpdateTabLabel()
        {
            title.Text = view.AppliedQuery.Length == 0 ? label : $"{label} · {view.AppliedQuery}";
            ToolTip.SetTip(tab, view.AppliedQuery.Length == 0 ? session.Source :
                $"{session.Source}\nFilter  {view.AppliedQuery}");
        }
    }

    private void CloseActive()
    {
        if (tabs.SelectedItem is TabItem tab) CloseView(tab);
    }

    private void CloseView(TabItem tab)
    {
        var session = ((ScanView)tab.Content!).Session;
        tabs.Items.Remove(tab);
        if (!Views.Any(view => view.Session == session)) session.Dispose();
        empty.IsVisible = tabs.Items.Count == 0;
        tabs.IsVisible = !empty.IsVisible;
    }

    private void Duplicate(bool separate)
    {
        if (Active is not { } active) return;
        var session = active.Session;
        var label = TitleOf(session.Source);
        if (!separate)
        {
            var number = Views.Count(view => view.Session == session) + 1;
            OpenView(session, $"{label} ({number})", active.AppliedQuery).Navigate(active.CurrentPath);
            return;
        }
        var view = new ScanView(this, session, preferences, active.AppliedQuery);
        view.Navigate(active.CurrentPath);
        var window = new Window { Title = $"CullByte  /  {label}", Width = 930, Height = 690, MinWidth = 670,
            MinHeight = 430, Content = view };
        window.AddHandler(KeyDownEvent, (_, e) => HandleKey(view, e), RoutingStrategies.Tunnel);
        detached.Add((window, view));
        window.Closed += (_, _) =>
        {
            detached.Remove((window, view));
            if (!Views.Any(other => other.Session == session)) session.Dispose();
        };
        window.Show();
    }

    private async Task ChooseAsync()
    {
        var paths = new TextBox { PlaceholderText = @"C:\Folder; D:\Other or \\server\share", MinWidth = 340 };
        var error = Ui.Text("");
        error.Classes.Add("error");
        error.IsVisible = false;
        var drives = DriveInfo.GetDrives();
        var dialog = Dialog("Choose a scan source", 555, 211 + Math.Clamp(drives.Length, 2, 6) * 28);
        var layout = new DockPanel { LastChildFill = true, Margin = new Thickness(20) };
        var lower = new StackPanel { Spacing = 9, Margin = new Thickness(0, 12, 0, 0) };
        lower.Children.Add(Ui.Text("Folder paths, separated by semicolons", 13, true));
        lower.Children.Add(paths);
        lower.Children.Add(error);
        var start = Ui.Button("Start scan", () =>
        {
            var selected = (paths.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (selected.Length > 0) dialog.Close(selected);
            else
            {
                error.Text = "Select a drive or enter a folder path.";
                error.IsVisible = true;
            }
        });
        start.Classes.Add("accent");
        lower.Children.Add(Ui.Row(start, Ui.Button("Browse…", () => _ = Browse()), Ui.Button("Cancel", () => dialog.Close())));
        DockPanel.SetDock(lower, Dock.Bottom);
        layout.Children.Add(lower);
        var driveLabel = Ui.Text("DRIVES", 11, true);
        driveLabel.Classes.Add("muted");
        driveLabel.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(driveLabel, Dock.Top);
        layout.Children.Add(driveLabel);
        var list = new ListBox();
        list.Classes.Add("compact");
        foreach (var drive in drives)
            list.Items.Add(new DriveRow(drive));
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is DriveRow selected) paths.Text = selected.Drive.Name;
        };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is DriveRow selected) dialog.Close(new[] { selected.Drive.Name }); };
        paths.KeyDown += (_, e) => { if (e.Key == Key.Enter) start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        var drivePanel = new Border { Child = list, Padding = new Thickness(4), ClipToBounds = true };
        drivePanel.Classes.Add("surface");
        layout.Children.Add(drivePanel);
        dialog.Content = layout;
        var result = await dialog.ShowDialog<string[]?>(this);
        if (result is { Length: > 0 }) await OpenPathsAsync(result);

        async Task Browse()
        {
            var selected = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = "Choose folders to scan", AllowMultiple = true });
            var result = selected.Select(folder => folder.TryGetLocalPath()).OfType<string>().ToArray();
            if (result.Length > 0) paths.Text = string.Join("; ", result);
        }
    }

    private sealed record DriveRow(DriveInfo Drive)
    {
        public override string ToString() => $"{Drive.Name}    {Drive.DriveType}";
    }

    private async Task OpenSnapshotAsync()
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a saved scan", AllowMultiple = true, FileTypeFilter = [Snapshots]
        });
        await OpenPathsAsync(result.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private async Task SaveSnapshotAsync(ScanView? view)
    {
        if (view is null) return;
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save scan snapshot", SuggestedFileName = "CullByte scan.cullscan",
            DefaultExtension = "cullscan", FileTypeChoices = [Snapshots], ShowOverwritePrompt = true
        });
        if (destination?.TryGetLocalPath() is not { } path) return;
        var session = view.Session;
        try
        {
            await Task.Run(() =>
            {
                lock (session.Gate) Storage.Save(path, session.Root, session.Volume);
            });
            await MessageAsync("Snapshot saved", path);
        }
        catch (Exception ex) { await MessageAsync("Snapshot failed", ex.Message); }
    }

    private async Task ExportAsync(ScanView? view)
    {
        if (view is null) { await MessageAsync("Open a scan first", "Reports use the focused folder and the current view filter."); return; }
        await Dialogs.ExportAsync(this, view);
    }

    private async Task LogAsync(ScanView? view)
    {
        if (view is null) return;
        await Dialogs.LogAsync(this, view.Session);
    }

    private async Task SettingsAsync()
    {
        var result = await Dialogs.SettingsAsync(this, preferences);
        if (result is null) return;
        preferences = result;
        ApplyTheme();
        foreach (var view in Views) view.UpdatePreferences(preferences);
        timer.Interval = TimeSpan.FromMilliseconds(preferences.RefreshMilliseconds);
    }

    private void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = preferences.Theme == "Light" ? ThemeVariant.Light :
            preferences.Theme == "System" ? ThemeVariant.Default : ThemeVariant.Dark;
    }

    private void HandleKey(ScanView? view, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.N: _ = ChooseAsync(); e.Handled = true; return;
                case Key.O: _ = OpenSnapshotAsync(); e.Handled = true; return;
                case Key.S: _ = SaveSnapshotAsync(view); e.Handled = true; return;
                case Key.E: _ = ExportAsync(view); e.Handled = true; return;
                case Key.W:
                    if (view == Active) CloseActive();
                    else detached.FirstOrDefault(item => item.View == view).Window?.Close();
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key == Key.F1) { _ = HelpAsync(); e.Handled = true; return; }
        e.Handled = view?.Shortcut(e) == true;
    }

    public static Window Dialog(string title, double width, double height)
    {
        var window = new Window { Title = title, Width = width, Height = height, MinWidth = Math.Min(360, width),
            MinHeight = Math.Min(190, height), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        return window;
    }

    public async Task<bool> MessageAsync(string title, string message, string? confirm = null)
    {
        var dialog = Dialog(title, 530, 250);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var ok = Ui.Button(confirm ?? "OK", () => dialog.Close(true));
        var actions = confirm is null ? Ui.Row(ok) : Ui.Row(Ui.Button("Cancel", () => dialog.Close(false)), ok);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        panel.Children.Add(Ui.Scroll(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        }));
        dialog.Content = panel;
        return await dialog.ShowDialog<bool>(this);
    }

    public async Task CopyAsync(string text)
    {
        if (Clipboard is not null) await Clipboard.SetTextAsync(text);
    }

    private Task HelpAsync() => MessageAsync("Filters and shortcuts",
        """
        Combine conditions with semicolons. A leading | excludes a match.

        *.iso;*.zip;>500mb          ISO or ZIP files using more than 500 MiB
        \temp;size>10mb              Files under a Temp folder, by logical size
        >2years;|:red                  Older files, excluding red tags
        :attr:hidden                   Hidden files
        :class:Audio/Music           Files in an editable class

        Single-click a folder to reveal or collapse its contents. Double-click to zoom.
        Backspace / Shift+Backspace   Back / forward
        Ctrl+Up / Ctrl+Home              Parent / root
        F5 / Shift+F5                       Rescan / refine
        Ctrl+1…4 / Ctrl+0                  Toggle a tag / clear tags in the focus
        Ctrl+F / Ctrl+U / Ctrl+T         Free space / unaccounted space / class colors
        """);
}

