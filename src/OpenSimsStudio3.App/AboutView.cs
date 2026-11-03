using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenSimsStudio3.Formats.Support;

namespace OpenSimsStudio3.App;

/// <summary>The start screen's About page: name, version, licence, credits and the project's links.</summary>
public sealed class AboutView : ScrollViewer
{
    public AboutView(Action back)
    {
        var muted = (IBrush)Application.Current!.FindResource("Muted")!;
        var backButton = new Button { Content = "← Back", Padding = new Thickness(14, 8), HorizontalAlignment = HorizontalAlignment.Left };
        backButton.Click += (_, _) => back();
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, url) in new[] { ("Repository", AppInfo.Repository), ("Credits", AppInfo.Provenance), ("Licence", AppInfo.LicenceText) })
        {
            var button = new Button { Content = label, Padding = new Thickness(16, 8) };
            ToolTip.SetTip(button, url);
            button.Click += async (_, _) => await Open(this, url);
            links.Children.Add(button);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(34, 0, 42, 32), Spacing = 22, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                backButton,
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = AppInfo.Name, FontSize = 24, FontWeight = FontWeight.Bold },
                        new TextBlock { Text = $"Version {Version}", FontSize = 13, Foreground = muted },
                        new TextBlock { Text = AppInfo.Licence, FontSize = 13, Foreground = muted },
                    },
                },
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "CREDITS", Classes = { "eyebrow" } },
                        new TextBlock { Text = "s3pi by Peter L Jones and contributors", FontSize = 13 },
                    },
                },
                links,
            },
        };
    }

    /// <summary>This build's version (About and the bug report).</summary>
    public static string Version => AppInfo.VersionOf(typeof(AboutView).Assembly);

    /// <summary>Opens a web page in the user's browser; a failure is logged, never retried.</summary>
    public static async Task Open(Visual from, string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(from) is { } top && await top.Launcher.LaunchUriAsync(new Uri(url))) return;
            Console.Error.WriteLine($"OSS3: could not open {url}");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UriFormatException)
        {
            Console.Error.WriteLine($"OSS3: could not open {url}: {error.Message}");
        }
    }
}
