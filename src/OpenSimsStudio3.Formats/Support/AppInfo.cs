using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenSimsStudio3.Formats.Support;

/// <summary>What About and Report a bug show: the version, the OS and the project's links.</summary>
public static class AppInfo
{
    public const string Name = "OpenSimsStudio³";
    public const string Licence = "GPL-3.0-or-later";
    public const string Repository = "https://github.com/minervamagicka/OpenSimsStudio3";
    public const string NewIssue = Repository + "/issues/new";
    public const string Provenance = Repository + "/blob/main/PROVENANCE.md";
    public const string LicenceText = Repository + "/blob/main/LICENSE";

    /// <summary>The assembly's informational version (the SDK adds "+commit"), shortened by <see cref="ShortVersion" />.</summary>
    public static string VersionOf(Assembly assembly) =>
        ShortVersion(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? assembly.GetName().Version?.ToString() ?? "");

    /// <summary>Cuts a "+commit" suffix to its first 7 characters ("1.0.0+0123456789ab" → "1.0.0+0123456"); "unknown" when empty.</summary>
    public static string ShortVersion(string version)
    {
        version = version.Trim();
        if (version.Length == 0) return "unknown";
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length - plus - 1 > 7 ? version[..(plus + 8)] : version;
    }

    /// <summary>This machine's OS and architecture, e.g. "Microsoft Windows 10.0.22631 (X64)".</summary>
    public static string OperatingSystem => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// The repository's new-issue page with the version and OS filled in the body. Only builds the link: the user
    /// sees and sends the issue themselves.
    /// </summary>
    public static string BugReportUrl(string version, string operatingSystem)
    {
        var body = $"\n\n---\n{Name} {version}\n{operatingSystem}\n";
        return $"{NewIssue}?body={Uri.EscapeDataString(body)}";
    }
}
