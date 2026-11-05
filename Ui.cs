using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CullByte;

internal static class Ui
{
    public static IBrush Color(string value) => new SolidColorBrush(Avalonia.Media.Color.Parse(value));
    public static StackPanel Row(params Control[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }

    public static Button Button(string text, Action action, string? help = null)
    {
        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => action();
        if (help is not null) ToolTip.SetTip(button, help);
        return button;
    }

    public static TextBlock Text(string text, double size = 13, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value };
    public static ScrollViewer Scroll(Control content) => new() { Content = content };
    public static MenuItem Item(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }
}
