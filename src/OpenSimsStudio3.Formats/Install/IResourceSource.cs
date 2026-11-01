namespace OpenSimsStudio3.Formats.Install;

/// <summary>Somewhere resources can be read by key: the game install, or a package layered over it.</summary>
public interface IResourceSource
{
    bool Contains(ResourceKey key);

    /// <summary>The resource's raw (decompressed) bytes, or null when this source has no live copy.</summary>
    byte[]? Read(ResourceKey key);
}

/// <summary>
/// A custom-content package over the game: keys the package has (live) come from it, everything else from the game,
/// as when the game loads the package from Mods. The package is opened read-only and kept open until disposed.
/// </summary>
public sealed class PackageOverGame : IResourceSource, IDisposable
{
    readonly s3pi.Interfaces.IPackage _package;
    readonly Dictionary<ResourceKey, s3pi.Interfaces.IResourceIndexEntry?> _entries = new();

    public PackageOverGame(string packagePath, IResourceSource game)
    {
        PackagePath = packagePath;
        Game = game;
        _package = s3pi.Package.Package.OpenPackage(0, packagePath, false);
        foreach (var e in _package.GetResourceList) _entries.TryAdd(ResourceKey.Of(e), e.IsDeleted ? null : e);
    }

    public string PackagePath { get; }
    public IResourceSource Game { get; }
    public IEnumerable<ResourceKey> PackageKeys => _entries.Where(kv => kv.Value != null).Select(kv => kv.Key);

    /// <summary>A deleted entry in the package hides the game's copy, as in game.</summary>
    public bool Contains(ResourceKey key) => _entries.TryGetValue(key, out var e) ? e != null : Game.Contains(key);

    public byte[]? Read(ResourceKey key)
    {
        if (!_entries.TryGetValue(key, out var e)) return Game.Read(key);
        if (e == null) return null;
        lock (_package)
        {
            if (((s3pi.Package.Package)_package).GetResource(e) is not { } s) return null;
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return copy.ToArray();
        }
    }

    public void Dispose() => s3pi.Package.Package.ClosePackage(0, _package);
}
