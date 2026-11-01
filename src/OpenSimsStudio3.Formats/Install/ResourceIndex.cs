using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Install;

/// <summary>A resource key (type, group, instance).</summary>
public readonly record struct ResourceKey(uint Type, uint Group, ulong Instance)
{
    public static ResourceKey Of(IResourceKey key) => new(key.ResourceType, key.ResourceGroup, key.Instance);

    public override string ToString() => $"{Type:X8}:{Group:X8}:{Instance:X16}";
}

/// <summary>
/// Where each resource of the install currently comes from: every package under GameData/Shared of the base game and
/// each pack, patch packages (Delta*) first so the patched copy wins; a deleted entry seen first hides the key.
/// The packages stay open read-only (index in memory) so reads are a dictionary lookup plus one decompress; build it
/// once per session (under a second for the full game) and dispose it to close the packages. Reads are thread-safe.
/// </summary>
public sealed class ResourceIndex : IResourceSource, IDisposable
{
    readonly Dictionary<ResourceKey, (IPackage Package, IResourceIndexEntry Entry)?> _where;
    readonly List<(string Path, IPackage Package)> _packages;

    ResourceIndex(Dictionary<ResourceKey, (IPackage, IResourceIndexEntry)?> where, List<(string, IPackage)> packages)
    {
        _where = where;
        _packages = packages;
    }

    public int Count => _where.Count;

    public static ResourceIndex Build(GameInstall install)
    {
        var roots = new[] { install.BaseGame }.Concat(install.Packs).Select(p => Path.Combine(p.Root, "GameData", "Shared")).Where(Directory.Exists);
        var paths = roots.SelectMany(r => Directory.GetFiles(r, "*.package", SearchOption.AllDirectories))
            .OrderBy(p => Path.GetFileName(p).StartsWith("Delta", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var where = new Dictionary<ResourceKey, (IPackage, IResourceIndexEntry)?>();
        var open = new List<(string, IPackage)>();
        foreach (var path in paths)
            try
            {
                var package = s3pi.Package.Package.OpenPackage(0, path, false);
                open.Add((path, package));
                foreach (var e in package.GetResourceList)
                    where.TryAdd(ResourceKey.Of(e), e.IsDeleted ? null : (package, e));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine($"OSS3: {path} could not be indexed ({error.Message}).");
            }
        return new ResourceIndex(where, open);
    }

    /// <summary>The package file currently supplying the key, or null when the install has no live copy.</summary>
    public string? PackageOf(ResourceKey key) =>
        _where.TryGetValue(key, out var hit) && hit is { } h ? _packages.First(p => ReferenceEquals(p.Package, h.Package)).Path : null;

    public bool Contains(ResourceKey key) => _where.TryGetValue(key, out var hit) && hit != null;

    /// <summary>The resource's raw (decompressed) bytes from the install, or null.</summary>
    public byte[]? Read(ResourceKey key)
    {
        if (!_where.TryGetValue(key, out var hit) || hit is not { } h) return null;
        lock (h.Package)
        {
            if (((s3pi.Package.Package)h.Package).GetResource(h.Entry) is not { } s) return null;
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return copy.ToArray();
        }
    }

    public void Dispose()
    {
        foreach (var (_, package) in _packages) s3pi.Package.Package.ClosePackage(0, package);
        _packages.Clear();
    }
}
