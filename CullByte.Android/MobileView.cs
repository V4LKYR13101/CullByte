using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace CullByte;

internal sealed class MobileView : UserControl
{
    private readonly IMobileStorage storage;
    private readonly TabControl tabs = new() { IsVisible = false };
    private readonly Control landing;
    private readonly TextBlock message = Ui.Text("");
    private readonly Button choose;
    private readonly Dictionary<string, TabItem> open = new(StringComparer.Ordinal);

    public MobileView(IMobileStorage storage)
    {
        this.storage = storage;
        ApplyTheme(storage.Theme);
        tabs.Classes.Add("scan-tabs");
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == tabs) (tabs.SelectedItem as TabItem)?.BringIntoView();
        };

        var logo = new Border
        {
            Background = Ui.Color("#50D9B6"), CornerRadius = new CornerRadius(9), Width = 34, Height = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "C", Foreground = Ui.Color("#0F302D"), FontSize = 21,
                FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center }
        };
        var wordmark = Ui.Row(logo, Ui.Text("CullByte", 19, true));
        choose = TouchButton("+ Scan", () => _ = ChooseAsync(), primary: true);
        var theme = TouchButton("Theme", () => { });
        var themeChoices = new StackPanel { Spacing = 5 };
        var themeFlyout = new Flyout { Content = themeChoices };
        foreach (var value in new[] { "System", "Light", "Dark" })
            themeChoices.Children.Add(TouchButton(value, () =>
            {
                storage.Theme = value;
                ApplyTheme(value);
                themeFlyout.Hide();
            }));
        theme.Flyout = themeFlyout;

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 6, Margin = new Thickness(12, 5) };
        top.Children.Add(wordmark);
        Put(top, choose, 0, 1);
        Put(top, theme, 0, 2);
        message.Classes.Add("error");
        message.TextWrapping = TextWrapping.Wrap;
        message.TextTrimming = TextTrimming.None;
        message.IsVisible = false;
        message.Margin = new Thickness(14, 4);
        landing = Landing();

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        body.Children.Add(top);
        Put(body, message, 1);
        Put(body, landing, 2);
        Put(body, tabs, 2);
        var frame = new Border { Child = body };
        frame.Classes.Add("mobile-canvas");
        Content = frame;
        TopLevel? topLevel = null;
        AttachedToVisualTree += (_, _) =>
        {
            topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not null) topLevel.BackRequested += OnBackRequested;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (topLevel is not null) topLevel.BackRequested -= OnBackRequested;
            topLevel = null;
            foreach (var item in open.Values) ((MobileScanPage)item.Content!).Cancel();
        };
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (!e.Handled && (tabs.SelectedItem as TabItem)?.Content is MobileScanPage page)
            e.Handled = page.NavigateBack();
    }

    private Control Landing()
    {
        var intro = new StackPanel { Spacing = 12 };
        var eyebrow = Ui.Text("STORAGE OVERVIEW", 11, true);
        eyebrow.Classes.Add("emphasis");
        intro.Children.Add(eyebrow);
        intro.Children.Add(new TextBlock
        {
            Text = "See what’s taking up space.", FontSize = 31, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        intro.Children.Add(Muted("Scan a folder to see its biggest files. Bigger tiles use more space."));
        intro.Children.Add(TouchButton("Choose a folder", () => _ = ChooseAsync(), primary: true));
        intro.Children.Add(Muted("Android shares only the folders you choose."));

        var sample = new StackPanel { Spacing = 7 };
        var caption = Ui.Text("EXAMPLE TREEMAP", 11, true);
        caption.Classes.Add("muted");
        sample.Children.Add(caption);
        sample.Children.Add(new MapPreview { Height = 190 });
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 18 };
        layout.Children.Add(intro);
        Put(layout, sample, 1);
        var card = new Border { Child = layout, Padding = new Thickness(17), Margin = new Thickness(12, 6, 12, 12),
            MaxWidth = 940, VerticalAlignment = VerticalAlignment.Top };
        card.Classes.Add("surface");
        card.SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= 650;
            layout.ColumnDefinitions = new ColumnDefinitions(wide ? "*,*" : "*");
            layout.ColumnSpacing = wide ? 20 : 0;
            Grid.SetRow(sample, wide ? 0 : 1);
            Grid.SetColumn(sample, wide ? 1 : 0);
        };
        return new ScrollViewer { Content = card };
    }

    private async Task ChooseAsync()
    {
        if (!choose.IsEnabled) return;
        choose.IsEnabled = false;
        message.IsVisible = false;
        try
        {
            var folder = await storage.ChooseAsync();
            if (folder is null) return;
            if (open.TryGetValue(folder.Id, out var existing))
            {
                tabs.SelectedItem = existing;
                return;
            }

            var page = new MobileScanPage(storage, folder);
            var title = Ui.Text(folder.Name, 13, true);
            title.MaxWidth = 150;
            var tab = new TabItem { Content = page, MinHeight = 48 };
            tab.Classes.Add("scan-tab");
            var close = TouchButton("×", () => Close(folder.Id, tab), quiet: true);
            close.Padding = new Thickness(8, 3);
            Avalonia.Automation.AutomationProperties.SetName(close, $"Close scan of {folder.Name}");
            tab.Header = Ui.Row(title, close);
            page.TitleChanged += value => title.Text = value;
            open.Add(folder.Id, tab);
            tabs.Items.Add(tab);
            tabs.IsVisible = true;
            landing.IsVisible = false;
            tabs.SelectedItem = tab;
            _ = page.ScanAsync();
        }
        catch (Exception ex)
        {
            message.Text = $"Could not open the folder. {ex.Message}";
            message.IsVisible = true;
        }
        finally { choose.IsEnabled = true; }
    }

    private void Close(string id, TabItem tab)
    {
        ((MobileScanPage)tab.Content!).Cancel();
        open.Remove(id);
        tabs.Items.Remove(tab);
        tabs.IsVisible = tabs.Items.Count > 0;
        landing.IsVisible = !tabs.IsVisible;
    }

    private static void ApplyTheme(string theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light :
                theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Default;
    }

    internal static Button TouchButton(string text, Action action, bool primary = false, bool quiet = false)
    {
        var button = Ui.Button(text, action);
        button.Classes.Add("touch");
        if (primary) button.Classes.Add("accent");
        if (quiet) button.Classes.Add("quiet");
        return button;
    }

    internal static TextBlock Muted(string text)
    {
        var block = Ui.Text(text, 13);
        block.Classes.Add("muted");
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    internal static void Put(Grid grid, Control child, int row, int column = 0)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }
}
