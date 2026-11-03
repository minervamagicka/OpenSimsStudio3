using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenSimsStudio3.App;

/// <summary>A small modal question with up to three buttons; the first is the primary action.</summary>
public static class ConfirmDialog
{
    /// <summary>Shows the dialog over <paramref name="owner" /> and returns the index of the chosen button (the last on Escape or close).</summary>
    public static async Task<int> Ask(Window owner, string title, string message, params string[] buttons)
    {
        var choice = buttons.Length - 1;
        var dialog = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#161C25")),
            ShowInTaskbar = false,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], Padding = new Thickness(16, 6), HorizontalAlignment = HorizontalAlignment.Left };
            if (i == 0) { button.Classes.Add("primary"); button.FontSize = 14; }
            button.Click += (_, _) => { choice = index; dialog.Close(); };
            row.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22, 20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap },
                row,
            },
        };
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(); };
        await dialog.ShowDialog(owner);
        return choice;
    }
}
