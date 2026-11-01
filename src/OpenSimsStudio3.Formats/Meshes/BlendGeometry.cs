using System.Numerics;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>Per-vertex morph deltas aligned to the vertex order of a base mesh (zero where the BGEO has none).</summary>
/// <param name="Covered">Base vertices whose VertexID falls inside the ID range of the BGEO LOD block.</param>
public sealed record MorphDeltas(Vector3[] Positions, Vector3[] Normals, int Covered, int PositionDeltas, int NormalDeltas);

/// <summary>One BGEO LOD block: the VertexID range it covers and how many 6-byte deltas it owns.</summary>
public sealed record BgeoLod(int FirstVertexId, int VertexCount, int DeltaCount);

/// <summary>One BGEO section-1 entry: age/gender and region flags plus the LOD blocks (block 0 is empty in EA data).</summary>
public sealed record BgeoEntry(uint AgeGender, uint Region, BgeoLod[] Lods);

/// <summary>
/// Blend geometry (BGEO, 0x067CAA11): the fat/fit/thin/special morph of a CAS part, all LODs in one resource.
/// Layout established against EA morph GEOMs (docs/cas-studio/research/cas-formats.md section 2.6): section 2 holds one
/// u16 per VertexID of each LOD block range (bit0 position delta, bit1 normal delta, bits 2-15 a signed step added to a
/// 14-bit running delta index that is not reset between LOD blocks); section 3 holds 3 x u16 per delta, value = (u16 - 32768)
/// / 2000, in X Y Z order. A position delta, when present, comes first and the normal delta follows it.
/// </summary>
public static class BlendGeometry
{
    public const uint BgeoType = 0x067CAA11, BblnType = 0x062C8204;
    const uint Magic = 0x4F454742; // "BGEO"

    public enum Morph { Fat, Fit, Thin, Special }

    /// <summary>The parsed sections, kept raw: section 2 as u16 words, section 3 as u16 triples.</summary>
    public sealed record Parsed(uint Version, BgeoEntry[] Entries, ushort[] VertexWords, ushort[] Deltas);

    public static Parsed Parse(byte[] bgeo)
    {
        using var r = new BinaryReader(new MemoryStream(bgeo));
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a BGEO resource");
        var version = r.ReadUInt32();
        var entryCount = r.ReadInt32();
        var lodCount = r.ReadInt32();
        var wordCount = r.ReadInt32();
        var deltaCount = r.ReadInt32();
        r.ReadUInt32(); r.ReadUInt32(); // 8 and 12 in every EA BGEO seen; meaning unconfirmed
        var (o1, o2, o3) = (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
        if (entryCount < 0 || lodCount is < 0 or > 16 || wordCount < 0 || deltaCount < 0 || o1 + (8L + 12L * lodCount) * entryCount > o2
            || o2 + 2L * wordCount > o3 || o3 + 6L * deltaCount > bgeo.Length)
            throw new InvalidDataException("BGEO header out of range");

        r.BaseStream.Position = o1;
        var entries = new BgeoEntry[entryCount];
        for (var e = 0; e < entryCount; e++)
        {
            var (ageGender, region) = (r.ReadUInt32(), r.ReadUInt32());
            var lods = new BgeoLod[lodCount];
            for (var l = 0; l < lodCount; l++) lods[l] = new BgeoLod(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            entries[e] = new BgeoEntry(ageGender, region, lods);
        }
        if (entries.Sum(e => e.Lods.Sum(l => (long)l.VertexCount)) != wordCount || entries.Sum(e => e.Lods.Sum(l => (long)l.DeltaCount)) != deltaCount)
            throw new InvalidDataException("BGEO LOD counts do not add up to the section sizes");
        var words = new ushort[wordCount];
        Buffer.BlockCopy(bgeo, (int)o2, words, 0, 2 * wordCount);
        var deltas = new ushort[3 * deltaCount];
        Buffer.BlockCopy(bgeo, (int)o3, deltas, 0, 6 * deltaCount);
        return new Parsed(version, entries, words, deltas);
    }

    /// <summary>The LOD block whose VertexID range holds all IDs of the base mesh (null when none does).</summary>
    public static int? LodFor(Parsed bgeo, GeomMesh baseMesh, int entry = 0)
    {
        if (baseMesh.VertexIds.Length == 0 || entry >= bgeo.Entries.Length) return null;
        var (min, max) = (baseMesh.VertexIds.Min(), baseMesh.VertexIds.Max());
        var lods = bgeo.Entries[entry].Lods;
        for (var l = 0; l < lods.Length; l++)
            if (lods[l].VertexCount > 0 && min >= lods[l].FirstVertexId && max < (long)lods[l].FirstVertexId + lods[l].VertexCount) return l;
        return null;
    }

    /// <summary>
    /// Deltas of BGEO LOD block <paramref name="lod" /> for <paramref name="baseMesh" />, matched by VertexID (vertices
    /// sharing an ID share the delta). In EA data block n goes with the VPXY LOD whose EntryID is n (the GEOM named
    /// _lodn); <see cref="LodFor" /> finds it by ID range. Null when the block is empty or the mesh has no VertexIDs.
    /// </summary>
    public static MorphDeltas? Read(byte[] bgeo, int lod, GeomMesh baseMesh) => Read(Parse(bgeo), lod, baseMesh);

    public static MorphDeltas? Read(Parsed bgeo, int lod, GeomMesh baseMesh, int entry = 0)
    {
        if (baseMesh.VertexIds.Length == 0 || entry >= bgeo.Entries.Length || lod < 0 || lod >= bgeo.Entries[entry].Lods.Length) return null;
        var block = bgeo.Entries[entry].Lods[lod];
        if (block.VertexCount == 0) return null;

        // Sections 2 and 3 run entry by entry, block by block. The running index starts at 0 and carries on (mod 16384)
        // through every earlier block; the deltas of each block are addressed from the start of that block in section 3.
        int wordStart = 0, deltaStart = 0, running = 0;
        for (var e = 0; e <= entry; e++)
            for (var l = 0; l < (e == entry ? lod : bgeo.Entries[e].Lods.Length); l++)
            {
                var b = bgeo.Entries[e].Lods[l];
                for (var i = 0; i < b.VertexCount; i++) running = Step(running, bgeo.VertexWords[wordStart + i]);
                wordStart += b.VertexCount;
                deltaStart += b.DeltaCount;
            }

        var byId = new (Vector3 P, Vector3 N, bool HasP, bool HasN)[block.VertexCount];
        for (var i = 0; i < block.VertexCount; i++)
        {
            var word = bgeo.VertexWords[wordStart + i];
            running = Step(running, word);
            var k = running;
            if ((word & 1) != 0) { byId[i].P = Delta(bgeo, block, deltaStart, k++); byId[i].HasP = true; }
            if ((word & 2) != 0) { byId[i].N = Delta(bgeo, block, deltaStart, k); byId[i].HasN = true; }
        }

        var count = baseMesh.VertexIds.Length;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        int covered = 0, withP = 0, withN = 0;
        for (var v = 0; v < count; v++)
        {
            var slot = (long)baseMesh.VertexIds[v] - block.FirstVertexId;
            if (slot < 0 || slot >= block.VertexCount) continue;
            covered++;
            var d = byId[slot];
            positions[v] = d.P;
            normals[v] = d.N;
            if (d.HasP) withP++;
            if (d.HasN) withN++;
        }
        return new MorphDeltas(positions, normals, covered, withP, withN);
    }

    /// <summary>Adds the signed 14-bit step of a section-2 word; the index wraps at 16384 (needed by blocks of more than 8192 deltas).</summary>
    static int Step(int running, ushort word) => (running + ((short)word >> 2)) & 0x3FFF;

    static Vector3 Delta(Parsed bgeo, BgeoLod block, int deltaStart, int k)
    {
        if (k < 0 || k >= block.DeltaCount) throw new InvalidDataException($"BGEO delta index {k} outside its LOD block ({block.DeltaCount} deltas)");
        var at = 3 * (deltaStart + k);
        return new Vector3(Unpack(bgeo.Deltas[at]), Unpack(bgeo.Deltas[at + 1]), Unpack(bgeo.Deltas[at + 2]));
    }

    static float Unpack(ushort value) => (value - 32768) / 2000f;

    /// <summary>
    /// The BGEO of a CAS part for one morph: the CASP BlendInfo{Fat,Fit,Thin,Special}Index picks a BBLN (0x062C8204, v8)
    /// from its TGI list, and the blend-geometry key of that BBLN names the BGEO (same instance in EA data). Null when absent.
    /// </summary>
    public static ResourceKey? Find(IResourceSource game, CASPartResource.CASPartResource casp, Morph morph)
    {
        var index = morph switch
        {
            Morph.Fat => casp.BlendInfoFatIndex, Morph.Fit => casp.BlendInfoFitIndex,
            Morph.Thin => casp.BlendInfoThinIndex, _ => casp.BlendInfoSpecialIndex,
        };
        if (index >= casp.TGIBlocks.Count) return null;
        var bbln = ResourceKey.Of(casp.TGIBlocks[index]);
        if (bbln.Type != BblnType || game.Read(bbln) is not { } bytes) return null;
        var blend = new CASPartResource.FaceClothingResource(0, new MemoryStream(bytes));
        if (blend.BlendGeometry is not { } tgi) return null;
        var key = ResourceKey.Of(tgi);
        return key.Type == BgeoType && game.Contains(key) ? key : null;
    }
}
