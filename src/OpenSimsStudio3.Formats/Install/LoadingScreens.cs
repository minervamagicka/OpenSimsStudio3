using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Install;

/// <summary>
/// The game's interactive loading screens (the hide-and-seek ones), read from the player's install for the start screen.
/// Each is a UI layout (LAYO 0x025C95B6) in Game/Bin/UI/UI.package whose background is the PNG (0x2F7D0004) with the
/// same instance in the base game's Shared packages. The layout carries the findables' button holder
/// (Sims3.UI.LoadingScreenController+ControlIDs.ButtonHolderId, 0x0FDAEA40; 124 such layouts in PC 1.67).
/// EA images stay in memory: nothing here writes them anywhere.
/// </summary>
public sealed class LoadingScreens
{
    const uint LayoutType = 0x025C95B6;
    const uint PngType = 0x2F7D0004;
    const string FindablesHolderId = "0x0fdaea40";

    readonly string _uiPackage;
    readonly IReadOnlyList<(string Package, ulong Instance)> _backgrounds;

    LoadingScreens(string uiPackage, IReadOnlyList<(string, ulong)> backgrounds)
    {
        _uiPackage = uiPackage;
        _backgrounds = backgrounds;
    }

    public int Count => _backgrounds.Count;

    /// <summary>
    /// Indexes the loading screens of an install: layout instances that have a same-instance background PNG.
    /// Patch packages (DeltaBuild) are searched before FullBuild so the patched image wins. Reads package indexes only.
    /// </summary>
    public static LoadingScreens Open(GameInstall install)
    {
        var ui = Path.Combine(install.Root, "Game", "Bin", "UI", "UI.package");
        var shared = Path.Combine(install.Root, "GameData", "Shared", "Packages");
        if (!File.Exists(ui) || !Directory.Exists(shared)) return new LoadingScreens(ui, []);

        var layouts = new HashSet<ulong>();
        WithPackage(ui, p =>
        {
            foreach (var e in p.GetResourceList)
                if (e.ResourceType == LayoutType && !e.IsDeleted) layouts.Add(e.Instance);
        });

        var found = new Dictionary<ulong, string>();
        var packages = Directory.GetFiles(shared, "DeltaBuild*.package").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .Concat(Directory.GetFiles(shared, "FullBuild*.package").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase));
        foreach (var path in packages)
            WithPackage(path, p =>
            {
                foreach (var e in p.GetResourceList)
                    if (e.ResourceType == PngType && !e.IsDeleted && layouts.Contains(e.Instance)) found.TryAdd(e.Instance, path);
            });
        return new LoadingScreens(ui, found.Select(kv => (kv.Value, kv.Key)).OrderBy(b => b.Key).ToArray());
    }

    /// <summary>
    /// A random loading screen's background as PNG bytes, kept in memory. Only screens whose layout has the findables
    /// holder count; null when the install has none.
    /// </summary>
    public byte[]? RandomBackground(Random random)
    {
        var order = _backgrounds.OrderBy(_ => random.Next()).ToList();
        foreach (var (package, instance) in order.Take(8))
        {
            if (!IsInteractive(instance)) continue;
            byte[]? bytes = null;
            WithPackage(package, p =>
            {
                var entry = p.GetResourceList.FirstOrDefault(e => e.ResourceType == PngType && e.Instance == instance && !e.IsDeleted);
                if (entry != null && ((s3pi.Package.Package)p).GetResource(entry) is { } s)
                {
                    using var copy = new MemoryStream();
                    s.CopyTo(copy);
                    bytes = copy.ToArray();
                }
            });
            if (bytes is { Length: > 0 }) return bytes;
        }
        return null;
    }

    bool IsInteractive(ulong instance)
    {
        var interactive = false;
        WithPackage(_uiPackage, p =>
        {
            var entry = p.GetResourceList.FirstOrDefault(e => e.ResourceType == LayoutType && e.Instance == instance && !e.IsDeleted);
            if (entry == null || ((s3pi.Package.Package)p).GetResource(entry) is not { } s) return;
            interactive = new StreamReader(s).ReadToEnd().Contains(FindablesHolderId, StringComparison.OrdinalIgnoreCase);
        });
        return interactive;
    }

    static void WithPackage(string path, Action<IPackage> read)
    {
        IPackage? package = null;
        try
        {
            package = s3pi.Package.Package.OpenPackage(0, path, false);
            read(package);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // An unreadable package contributes no loading screens; the start screen falls back to OSS3's own art.
        }
        finally
        {
            if (package != null) s3pi.Package.Package.ClosePackage(0, package);
        }
    }
}
