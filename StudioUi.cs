using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sada;

internal static class StudioUi
{
    internal static readonly SolidColorBrush Night = Brush("#0B1325"), Side = Brush("#111D32"), Panel = Brush("#142239"),
        Field = Brush("#1B2B47"), Line = Brush("#304364"), Paper = Brush("#EDF5FF"), Soft = Brush("#A8B9D1"),
        Mint = Brush("#AAEDD7"), Violet = Brush("#C4B7FF"), Ink = Brush("#102E32");

    internal static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal static SolidColorBrush ValidColor(string value, SolidColorBrush fallback)
    {
        try { return Brush(value); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return fallback; }
    }

    internal static TextBlock Text(string text, double size = 14, Brush? brush = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Paper,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap
    };

    internal static TextBlock Label(string text) => Text(text, 11, Mint, true);

    internal static Border Card(UIElement content, Brush? color = null, double pad = 16) => new()
    {
        Background = color ?? Panel, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
        Padding = new Thickness(pad), Child = content
    };

    internal static Button Button(object content, Action action, bool primary = false)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 7, 7), VerticalAlignment = VerticalAlignment.Center };
        if (primary) { button.Background = Mint; button.Foreground = Ink; button.BorderBrush = Mint; }
        button.Click += (_, _) => action();
        return button;
    }

    internal static Button AsyncButton(object content, Func<Task> run)
    {
        var button = Button(content, () => { });
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await run(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    internal static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    internal static WrapPanel Wrap(params UIElement[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}
