using System.Numerics;
using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>
/// The highest-detail mesh of a CAS part, for previews: the CASP's VPXY, its LOD 0 entry (VPXY Entry00 with
/// EntryID 0), and every GEOM that entry lists (one per mesh group), merged into one mesh. Read-only.
/// </summary>
public static class CasMeshes
{
    const uint Vpxy = 0x736884F1, Geom = 0x015A1849;

    /// <summary>The merged LOD 0 mesh, or null when the part has no mesh (makeup, overlays) or it cannot be read.</summary>
    public static GeomMesh? Lod0(IResourceSource game, ResourceKey casp)
    {
        if (game.Read(casp) is not { } caspBytes) return null;
        var part = new CASPartResource.CASPartResource(0, new MemoryStream(caspBytes));
        var vpxyKey = part.TGIBlocks.Select(ResourceKey.Of).FirstOrDefault(k => k.Type == Vpxy);
        if (vpxyKey.Type == 0 || game.Read(vpxyKey) is not { } vpxyBytes) return null;

        var vpxy = new GenericRCOLResource(0, new MemoryStream(vpxyBytes)).ChunkEntries.Select(c => c.RCOLBlock).OfType<VPXY>().FirstOrDefault();
        if (vpxy == null) return null;
        // Each Entry00 is one LOD (its GEOMs, one per mesh group). EA numbers them from 1 and which end is most detailed
        // is not established, so the LOD with the most vertices wins.
        GeomMesh? best = null;
        foreach (var lod in vpxy.Entries.OfType<VPXY.Entry00>().GroupBy(e => e.EntryID))
        {
            var meshes = new List<GeomMesh>();
            foreach (var key in lod.SelectMany(e => (IEnumerable<int>)e.TGIIndexes)
                         .Select(i => i >= 0 && i < vpxy.TGIBlocks.Count ? ResourceKey.Of(vpxy.TGIBlocks[i]) : default)
                         .Where(k => k.Type == Geom).Distinct())
                if (game.Read(key) is { } bytes)
                    try { meshes.Add(GeomLoader.Read(new MemoryStream(bytes), key.ToString())); }
                    catch (InvalidDataException) { }
            if (meshes.Count == 0) continue;
            var merged = Merge(part.Unknown1.TrimEnd('\0'), meshes);
            if (best == null || merged.Positions.Length > best.Positions.Length) best = merged;
        }
        return best;
    }

    static GeomMesh Merge(string name, List<GeomMesh> meshes) => GeomMesh.Merge(name, meshes);
}
