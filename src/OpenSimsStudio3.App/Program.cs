using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace OpenSimsStudio3.App;

public sealed class App : Application
{
    // Prototype default: the base game's main package, opened read-only.
    const string DefaultPackage = @"C:\Program Files (x86)\Electronic Arts\The Sims 3\GameData\Shared\Packages\FullBuild0.package";

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add((IStyle)AvaloniaXamlLoader.Load(new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml")));
        Styles.Add((IStyle)AvaloniaXamlLoader.Load(new Uri("avares://OpenSimsStudio3/Styles/Oss3Styles.axaml")));
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --viewport [package]: the GEOM viewport prototype; otherwise the start screen.
            var args = desktop.Args ?? [];
            desktop.MainWindow = args is ["--viewport", ..]
                ? new MainWindow(args.Length > 1 ? args[1] : DefaultPackage)
                : new StartWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Read packages leniently: tool-made CC often has inconsistent length fields (GEOM Tools' MTNF, research
        // undocumented-fields.md) that s3pi's checks reject. s3pi captures this flag on first use, so set it first.
        typeof(s3pi.Settings.Settings).GetField("checking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, false);
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}
