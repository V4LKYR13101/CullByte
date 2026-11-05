using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CullByte;

internal static class Dialogs
{
    public static async Task<Preferences?> SettingsAsync(MainWindow owner, Preferences current)
    {
        var dialog = MainWindow.Dialog("CullByte settings", 590, 435);
        var watch = Ui.Check("Monitor file-system changes", current.Watch);
        var streams = Ui.Check("Include alternate data streams on supported volumes", current.Streams);
        var flash = Ui.Check("Highlight changed folders", current.Flash);
        var free = Ui.Check("Show free space by default", current.Free);
        var unknown = Ui.Check("Show unaccounted space by default", current.Unknown);
        var logical = Ui.Check("Size by logical bytes by default", current.Logical);
        var classColors = Ui.Check("Color files by class by default", current.ClassColors);
        var gray = Ui.Check("Keep filtered-out space visible in gray", current.GrayExcluded);
        var sort = Ui.Check("Sort tiles by size", current.Sort);
        var zoom = Ui.Check("Animate zoom transitions", current.AnimateZoom);
        var depth = Number(current.Detail, 1, 8);
        var label = Number(current.LabelSize, 9, 24);
        var minimum = Number(current.TileMinimum, 3, 40);
        var rate = Number(current.RefreshMilliseconds, 100, 1500);
        rate.Increment = 50;
        var folder = new TextBox { Text = current.FolderColor, MinWidth = 150 };
        var file = new TextBox { Text = current.FileColor, MinWidth = 150 };
        var classes = new TextBox { Text = current.ClassDefinitions, AcceptsReturn = true, Height = 240,
            FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap };
        var theme = new ComboBox { ItemsSource = new[] { "Dark", "Light", "System" }, SelectedItem = current.Theme };
        var tab = new TabControl
        {
            ItemsSource = new[]
            {
                Tab("Geometry", Field("Initial detail level", depth), Field("Label size", label), Field("Minimum tile size", minimum), sort),
                Tab("Animation", Field("Live refresh interval in milliseconds", rate), zoom, flash),
                Tab("Behavior", watch, streams, free, unknown, logical, gray,
                    new TextBlock { Text = "Scanner and initial-view settings apply when you open a new scan or view.",
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }),
                Tab("Look", Field("Theme", theme), Field("Folder color", folder), Field("Default file color", file),
                    classColors),
                Tab("File classes", Ui.Text("Name | #RRGGBB | *.ext;*.ext", 13, true), classes)
            }
        };
        tab.Classes.Add("scan-tabs");
        var error = Ui.Text("");
        error.Classes.Add("error");
        var save = Ui.Button("Save settings", () =>
        {
            try
            {
                var result = new Preferences
                {
                    Watch = watch.IsChecked == true, Streams = streams.IsChecked == true, Flash = flash.IsChecked == true,
                    Free = free.IsChecked == true, Unknown = unknown.IsChecked == true, Logical = logical.IsChecked == true,
                    GrayExcluded = gray.IsChecked == true, Sort = sort.IsChecked == true, AnimateZoom = zoom.IsChecked == true,
                    ClassColors = classColors.IsChecked == true,
                    Detail = (int)(depth.Value ?? current.Detail), LabelSize = (int)(label.Value ?? current.LabelSize),
                    TileMinimum = (int)(minimum.Value ?? current.TileMinimum),
                    RefreshMilliseconds = (int)(rate.Value ?? current.RefreshMilliseconds),
                    FolderColor = folder.Text?.Trim() ?? "", FileColor = file.Text?.Trim() ?? "",
                    Theme = theme.SelectedItem as string ?? "Dark", ClassDefinitions = classes.Text ?? ""
                };
                _ = Color.Parse(result.FolderColor);
                _ = Color.Parse(result.FileColor);
                _ = result.Classes();
                result.Save();
                dialog.Close(result);
            }
            catch (Exception ex) { error.Text = ex.Message; }
        });
        save.Classes.Add("accent");
        var buttons = Ui.Row(Ui.Button("Cancel", () => dialog.Close()), save);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = WithFooter(tab, error, buttons);
        return await dialog.ShowDialog<Preferences?>(owner);
    }

    public static async Task ExportAsync(MainWindow owner, ScanView view)
    {
        var dialog = MainWindow.Dialog("Export filtered report", 760, 540);
        var format = new ComboBox { ItemsSource = new[] { "CSV", "Text" }, SelectedIndex = 0, MinWidth = 140 };
        var template = new TextBox { Text = Storage.DefaultTemplate.Replace("\t", "\\t"), IsVisible = false };
        var tokens = Ui.Text("{path}  {name}  {bytes}  {allocatedBytes}  {modified}  {created}  {accessed}  {attributes}  {tag}  {depth}", 11);
        tokens.IsVisible = false;
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas") };
        var error = Ui.Text("");
        error.Classes.Add("error");
        var job = 0;
        var state = view.Session;
        var root = view.FocusedNode;
        var filter = view.Filter;
        var tags = new Dictionary<string, int>(state.Tags, StringComparer.OrdinalIgnoreCase);

        async Task Preview()
        {
            var request = ++job;
            var style = format.SelectedItem as string ?? "CSV";
            template.IsVisible = tokens.IsVisible = style == "Text";
            var pattern = (template.Text ?? "").Replace("\\t", "\t");
            preview.Text = "Building preview…";
            try
            {
                var lines = await Task.Run(() =>
                {
                    lock (state.Gate) return Storage.Report(root, filter, tags, style, pattern).Take(28).ToArray();
                });
                if (job == request) preview.Text = lines.Length == 0 ? "No matching files." : string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex) { if (job == request) error.Text = ex.Message; }
        }

        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        debounce.Tick += (_, _) => { debounce.Stop(); _ = Preview(); };
        format.SelectionChanged += (_, _) => { debounce.Stop(); _ = Preview(); };
        template.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        dialog.Closed += (_, _) => { debounce.Stop(); job++; };

        var save = new Button { Content = "Save report…" };
        save.Classes.Add("accent");
        save.Click += (_, _) => _ = Save();
        async Task Save()
        {
            try
            {
                var style = format.SelectedItem as string ?? "CSV";
                var extension = style == "CSV" ? "csv" : "txt";
                var pattern = (template.Text ?? "").Replace("\\t", "\t");
                var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save report", SuggestedFileName = $"CullByte report.{extension}", DefaultExtension = extension,
                    ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType(style) { Patterns = ["*." + extension] }]
                });
                if (file?.TryGetLocalPath() is not { } path) return;
                save.IsEnabled = false;
                await Task.Run(() =>
                {
                    lock (state.Gate) Storage.WriteAtomic(path, writer =>
                    {
                        foreach (var line in Storage.Report(root, filter, tags, style, pattern)) writer.WriteLine(line);
                    });
                });
                dialog.Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { save.IsEnabled = true; }
        }

        var content = new DockPanel { LastChildFill = true };
        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(Ui.Text(root.FullPath, 15, true));
        top.Children.Add(Field("Format", format));
        top.Children.Add(template);
        top.Children.Add(tokens);
        top.Children.Add(Ui.Text("Preview  /  first 28 lines", 12, true));
        DockPanel.SetDock(top, Dock.Top);
        content.Children.Add(top);
        content.Children.Add(preview);
        dialog.Content = WithFooter(content, error, Ui.Row(Ui.Button("Close", () => dialog.Close()), save));
        dialog.Opened += (_, _) => _ = Preview();
        await dialog.ShowDialog(owner);
    }

    public static async Task LogAsync(MainWindow owner, ScanSession session)
    {
        var dialog = MainWindow.Dialog("Scan log", 800, 535);
        var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas") };
        var count = Ui.Text("");
        var summary = Ui.Text(session.Source, 15, true);
        var content = new DockPanel();
        DockPanel.SetDock(summary, Dock.Top);
        content.Children.Add(summary);
        content.Children.Add(details);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var displayed = -1;
        void Refresh()
        {
            var issues = session.IssueCount;
            if (issues == displayed) return;
            displayed = issues;
            var snapshot = session.Issues.ToArray();
            count.Text = $"{issues:N0} issues  /  {snapshot.Length:N0} retained";
            details.Text = snapshot.Length == 0 ? "No scan issues recorded." : string.Join("\n",
                snapshot.Select(issue => $"{issue.Path}\n  {issue.Message}"));
        }
        timer.Tick += (_, _) => Refresh();
        dialog.Closed += (_, _) => timer.Stop();
        dialog.Content = WithFooter(content, count, Ui.Row(Ui.Button("Copy log", () => _ = owner.CopyAsync(details.Text ?? "")),
            Ui.Button("Close", () => dialog.Close())));
        Refresh();
        timer.Start();
        await dialog.ShowDialog(owner);
    }

    private static NumericUpDown Number(int value, int min, int max) => new()
        { Value = value, Minimum = min, Maximum = max, MinWidth = 125 };

    private static Control Field(string label, Control field)
    {
        var text = Ui.Text(label);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 8 };
        grid.Children.Add(text);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        Avalonia.Automation.AutomationProperties.SetName(field, label);
        return grid;
    }

    private static TabItem Tab(string title, params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 9, Margin = new Thickness(8, 12) };
        foreach (var control in controls) stack.Children.Add(control);
        var item = new TabItem { Header = new TextBlock { Text = title, FontSize = 12,
                FontWeight = FontWeight.SemiBold, Margin = new Thickness(1, 0, 5, 0) },
            Content = Ui.Scroll(stack) };
        item.Classes.Add("scan-tab");
        return item;
    }

    private static Control WithFooter(Control body, Control status, StackPanel buttons)
    {
        var panel = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        if (status is TextBlock text)
        {
            text.IsVisible = !string.IsNullOrEmpty(text.Text);
            text.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty) text.IsVisible = !string.IsNullOrEmpty(text.Text);
            };
        }
        footer.Children.Add(status);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(body);
        return panel;
    }
}


