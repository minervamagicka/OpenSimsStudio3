using System.Collections;
using System.Text;
using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>What a clone copied, renumbered and left pointing at the game.</summary>
public sealed record CloneResult(
    ResourceKey NewCasp,
    IReadOnlyDictionary<ResourceKey, ResourceKey> Copied,
    IReadOnlyList<ResourceKey> GameReferences,
    IReadOnlyList<ResourceKey> Missing);

/// <summary>
/// Clones one game CAS part into a new package, as a new item (S3OC's plain clone). Copied and renumbered: the CASP,
/// its VPXY mesh containers, their GEOMs and BONE resources, and its TXTC compositors: what OSS3 edits in place.
/// Kept as references to the game: textures (_IMG), BOND morph links, XML and anything else; recolouring copies a texture
/// only when it is changed. Groups are kept; new instances are FNV-64 of "oss3:{projectId}:{old key}", checked against
/// the install and each other. Every key inside the copies (TGI lists, RCOL resource lists, RCOL chunk keys) is
/// remapped. Writes only <paramref name="packagePath" />. Provisional design (BRIEF.md "Projects"): not yet tested in game.
/// </summary>
public static class CasClone
{
    const uint Casp = 0x034AEECB, Vpxy = 0x736884F1, Geom = 0x015A1849, Bone = 0x00AE6C67, Txtc = 0x033A1435;
    static readonly HashSet<uint> CopiedTypes = [Casp, Vpxy, Geom, Bone, Txtc];

    /// <param name="keepKeys">Default replacement: copy the same resources under their original keys, so the package
    /// overrides the game's copies (no renumbering, no remapping).</param>
    public static CloneResult Clone(IResourceSource game, ResourceKey caspKey, string projectId, string packagePath, bool keepKeys = false)
    {
        // 1. The copy set: the CASP and what it reaches through VPXY (GEOM, BONE) plus its TXTCs.
        var resources = new Dictionary<ResourceKey, IResource?>();
        var raw = new Dictionary<ResourceKey, byte[]>();
        var references = new HashSet<ResourceKey>();
        var queue = new Queue<ResourceKey>([caspKey]);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (resources.ContainsKey(key)) continue;
            var bytes = game.Read(key) ?? throw new InvalidDataException($"{key} is not in the install or the source package.");
            // BONE holds no resource keys, so it is copied byte for byte.
            var resource = key.Type == Bone ? null : Parse(key, bytes);
            resources[key] = resource;
            raw[key] = bytes;
            if (resource == null) continue;
            foreach (var child in KeysIn(resource).Select(ResourceKey.Of).Distinct())
            {
                references.Add(child);
                if (CopiedTypes.Contains(child.Type) && child.Type != Casp && game.Contains(child) && !resources.ContainsKey(child)) queue.Enqueue(child);
            }
        }

        // 2. New keys. EA gives VPXY and BONE 32-bit instances (e.g. 0x00BEF569); a new key keeps the original's width
        // in case the engine reads those as 32-bit hashes (unverified either way).
        var taken = new HashSet<ResourceKey>();
        var map = new Dictionary<ResourceKey, ResourceKey>();
        foreach (var old in resources.Keys)
        {
            if (keepKeys) { map[old] = old; continue; }
            var salt = 0;
            ResourceKey fresh;
            do
            {
                var hash = Fnv64($"oss3:{projectId}:{old}{(salt++ == 0 ? "" : ":" + salt)}");
                fresh = old with { Instance = old.Instance <= uint.MaxValue ? (uint)(hash ^ (hash >> 32)) : hash };
            }
            while (game.Contains(fresh) || !taken.Add(fresh));
            map[old] = fresh;
        }

        // 3. Remap every key inside the copies, then write them.
        var package = s3pi.Package.Package.NewPackage(0);
        try
        {
            foreach (var (old, resource) in resources)
            {
                var target = map[old];
                var key = new TGIBlock(0, null, target.Type, target.Group, target.Instance);
                // A default replacement changes nothing, so EA's bytes are written untouched (s3pi does not
                // reproduce every VPXY byte for byte: size fields and the empty TGI count differ).
                if (resource == null || keepKeys)
                {
                    (package.AddResource(key, new MemoryStream(raw[old], false), true) ?? throw new InvalidOperationException($"duplicate key {target} in the clone")).Compressed = 0xFFFF;
                    continue;
                }
                foreach (var k in KeysIn(resource))
                    if (map.TryGetValue(ResourceKey.Of(k), out var to)) { k.ResourceType = to.Type; k.ResourceGroup = to.Group; k.Instance = to.Instance; }
                if (resource is GenericRCOLResource rcol)
                    // Edit the chunk key in place: RCOL stores it instance-type-group, and a replacement TGIBlock would
                    // be written type-group-instance, scrambling the key so the chunk re-reads as an untyped blob.
                    foreach (var chunk in rcol.ChunkEntries)
                        if (map.TryGetValue(ResourceKey.Of(chunk.TGIBlock), out var to))
                        {
                            chunk.TGIBlock.ResourceType = to.Type;
                            chunk.TGIBlock.ResourceGroup = to.Group;
                            chunk.TGIBlock.Instance = to.Instance;
                        }
                var entry = package.AddResource(key, resource.Stream, true)
                            ?? throw new InvalidOperationException($"duplicate key {target} in the clone");
                entry.Compressed = 0xFFFF;
            }
            package.SaveAs(packagePath);
        }
        finally
        {
            s3pi.Package.Package.ClosePackage(0, package);
        }

        var kept = references.Where(r => !map.ContainsKey(r) && r.Type != 0).ToList();
        return new CloneResult(map[caspKey], map, kept.Where(game.Contains).ToList(), kept.Where(r => !game.Contains(r)).ToList());
    }

    /// <summary>Parses a resource OSS3 copies (CASP, RCOL, TXTC); other types are not copied.</summary>
    public static IResource Parse(ResourceKey key, byte[] bytes)
    {
        var s = new MemoryStream(bytes);
        return key.Type switch
        {
            Casp => new CASPartResource.CASPartResource(0, s),
            Vpxy or Geom => new GenericRCOLResource(0, s),
            Txtc => (IResource)Activator.CreateInstance(Type.GetType("TxtcResource.TxtcResource, s3pi.TxtcResource", true)!, 0, s)!,
            _ => throw new NotSupportedException($"{key}: type is not copied"),
        };
    }

    /// <summary>Every resource key a resource holds that may point at another resource.</summary>
    public static IEnumerable<IResourceKey> KeysIn(IResource resource)
    {
        switch (resource)
        {
            case CASPartResource.CASPartResource casp:
                foreach (var t in casp.TGIBlocks) yield return t;
                break;
            case GenericRCOLResource rcol:
                foreach (var t in rcol.Resources) yield return t;
                foreach (var chunk in rcol.ChunkEntries)
                    if (chunk.RCOLBlock.GetType().GetProperty("TGIBlocks")?.GetValue(chunk.RCOLBlock) is IEnumerable list)
                        foreach (IResourceKey t in list) yield return t;
                break;
            default:
                if (resource.GetType().GetProperty("TGIBlocks")?.GetValue(resource) is IEnumerable tgis)
                    foreach (IResourceKey t in tgis) yield return t;
                break;
        }
    }

    /// <summary>FNV-1 64-bit over the UTF-8 text, the TS3 name hash (s3pi System.Custom FNV64).</summary>
    public static ulong Fnv64(string text) => BitConverter.ToUInt64(new System.Security.Cryptography.FNV64().ComputeHash(Encoding.UTF8.GetBytes(text)), 0);
}
