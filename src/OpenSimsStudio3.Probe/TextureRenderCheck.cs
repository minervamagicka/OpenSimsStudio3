using System.Numerics;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Rendering;

/// <summary>texrender &lt;casp name&gt; &lt;out.bmp&gt;: renders the part's mesh with a UV test texture (red = U, green = V, checker), to check UV mapping.</summary>
static class TextureRenderCheck
{
    public static int Run(string name, string outPath)
    {
        var install = GameInstall.Find(null)!;
        using var index = ResourceIndex.Build(install);
        var item = CasCatalog.Load(install).Items.First(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var mesh = CasMeshes.Lod0(index, new ResourceKey(0x034AEECB, item.Group, item.Instance))!;
        Console.WriteLine($"{mesh.Positions.Length} vertices, UVs {mesh.Uvs.Length}, U {mesh.Uvs.Min(u => u.X):F2}..{mesh.Uvs.Max(u => u.X):F2}, V {mesh.Uvs.Min(u => u.Y):F2}..{mesh.Uvs.Max(u => u.Y):F2}");
        const int T = 256;
        var tex = new byte[T * T * 4];
        for (var y = 0; y < T; y++)
        for (var x = 0; x < T; x++)
        {
            var check = ((x / 32) + (y / 32)) % 2 == 0 ? 1f : 0.6f;
            var o = (y * T + x) * 4;
            tex[o] = (byte)(255 * x / (T - 1) * check); tex[o + 1] = (byte)(255 * y / (T - 1) * check); tex[o + 2] = (byte)(60 * check); tex[o + 3] = 255;
        }
        using var renderer = new OffscreenMeshRenderer { ClearColor = new Vector4(0.047f, 0.063f, 0.086f, 1f) };
        renderer.SetTexture(T, T, tex);
        var pixels = new byte[512 * 512 * 4];
        Thumbnails.Draw(renderer, mesh, 512, pixels);
        using (var w = new BinaryWriter(File.Create(outPath)))
        {
            var row = 512 * 4;
            w.Write((ushort)0x4D42); w.Write(54 + row * 512); w.Write(0); w.Write(54);
            w.Write(40); w.Write(512); w.Write(512); w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(row * 512);
            w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
            for (var y = 511; y >= 0; y--) w.Write(pixels, y * row, row);
        }
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }
}
