using OpenSimsStudio3.Formats.Install;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Tests;

/// <summary>Writes .package files with made-up resources, and a temp folder that deletes itself.</summary>
static class TestPackages
{
    public static void Write(string path, params (ResourceKey Key, byte[] Bytes)[] resources)
    {
        var package = s3pi.Package.Package.NewPackage(0);
        try
        {
            foreach (var (key, bytes) in resources)
                package.AddResource(new TGIBlock(0, null, key.Type, key.Group, key.Instance), new MemoryStream(bytes, false), true)!.Compressed = 0xFFFF;
            package.SaveAs(path);
        }
        finally { s3pi.Package.Package.ClosePackage(0, package); }
    }

    public static Dictionary<ResourceKey, byte[]> ReadAll(string path)
    {
        var package = s3pi.Package.Package.OpenPackage(0, path, false);
        try
        {
            var all = new Dictionary<ResourceKey, byte[]>();
            foreach (var e in package.GetResourceList.Where(e => !e.IsDeleted))
            {
                using var s = ((s3pi.Package.Package)package).GetResource(e);
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                all[ResourceKey.Of(e)] = copy.ToArray();
            }
            return all;
        }
        finally { s3pi.Package.Package.ClosePackage(0, package); }
    }
}

sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("oss3-test-").FullName;
    public string this[string name] => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

/// <summary>An in-memory resource source standing in for the game.</summary>
sealed class FakeGame(Dictionary<ResourceKey, byte[]> resources) : IResourceSource
{
    public bool Contains(ResourceKey key) => resources.ContainsKey(key);
    public byte[]? Read(ResourceKey key) => resources.TryGetValue(key, out var b) ? b : null;
}
