using System.Text.Json;

namespace OpenSimsStudio3.App;

/// <summary>OSS3's own preferences, in %APPDATA%\OpenSimsStudio3\settings.json. Holds paths only, never game data.</summary>
public sealed class AppSettings
{
    /// <summary>The game folder the player chose; empty means "find it automatically".</summary>
    public string GameRoot { get; set; } = "";

    /// <summary>Where new projects go; empty means Documents\OpenSimsStudio3\Projects. OSS3_PROJECTS_ROOT overrides it (tests).</summary>
    public string ProjectsRoot { get; set; } = "";

    /// <summary>blender.exe to open meshes in; empty means the Blender Launcher favourite, else the newest with GEOM Tools.</summary>
    public string BlenderPath { get; set; } = "";

    /// <summary>Project folders, most recent first.</summary>
    public List<string> RecentProjects { get; set; } = [];

    public string EffectiveProjectsRoot =>
        Environment.GetEnvironmentVariable("OSS3_PROJECTS_ROOT") is { Length: > 0 } env ? env
        : ProjectsRoot.Length > 0 ? ProjectsRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenSimsStudio3", "Projects");

    public void AddRecent(string folder)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, folder);
        if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
    }

    // OSS3_SETTINGS_DIR moves the settings file (test runs keep theirs out of the real profile).
    static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("OSS3_SETTINGS_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimsStudio3"), "settings.json");

    static AppSettings? s_current;

    /// <summary>The one settings instance every window shares, so one window's save never drops another's change.</summary>
    public static AppSettings Current => s_current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"OSS3: settings at {FilePath} could not be read ({error.Message}); using defaults.");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
