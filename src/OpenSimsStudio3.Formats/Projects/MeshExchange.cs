using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;
using GEOM = meshExpImp.ModelBlocks.GEOM;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>What a mesh re-import changed and anything the user should know.</summary>
public sealed record MeshImportResult(int VerticesBefore, int VerticesAfter, int TrianglesBefore, int TrianglesAfter, bool ColourBefore, bool ColourAfter,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Moves one mesh group (GEOM) between a project and a .simgeom file, the format Blender GEOM Tools and S3PE use
/// (the GEOM resource's RCOL bytes as stored). Import reads leniently, because GEOM Tools writes an MTNF data length
/// that disagrees with its contents (research/undocumented-fields.md), and re-writes the GEOM through s3pi, which also
/// corrects that length. The group keeps its key. Hair vertex COLOR is checked: it must survive (all four bytes matter).
/// </summary>
public static class MeshExchange
{
    public static void Export(ProjectSession session, ResourceKey group, string path)
    {
        var bytes = session.Read(group) ?? throw new InvalidDataException($"{group} is not in the project or the game");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Reads <paramref name="path" /> as a GEOM, checks it, and puts it into the project under <paramref name="group" />'s
    /// key (saved with the project). Throws InvalidDataException when the file is not a usable GEOM; nothing changes then.
    /// </summary>
    public static MeshImportResult Import(ProjectSession session, ResourceKey group, string path)
    {
        var before = Geom(session.Read(group) ?? throw new InvalidDataException($"{group} is not in the project or the game"), out _);
        var after = Geom(File.ReadAllBytes(path), out var rcol);
        if (after.VertexData.Count == 0 || after.Faces.Count == 0) throw new InvalidDataException($"{Path.GetFileName(path)} has no vertices or faces");

        var warnings = new List<string>();
        var colourBefore = HasColour(before);
        var colourAfter = HasColour(after);
        if (colourBefore && !colourAfter)
            warnings.Add("The vertex colour stream is gone (GEOM Tools writes it only when the mesh has its SIMGEOM_TAGVAL colour layer). " +
                         "Hair loses its painted shading; the game uses its default.");
        if (colourAfter && after.VertexData.SelectMany(v => v.Vertex.OfType<GEOM.ColorElement>()).All(c => ProjectSession.ColourOf(c) == 0))
            warnings.Add("The vertex colour stream is all zero, which renders hair black.");
        if ((uint)before.Shader != (uint)after.Shader) warnings.Add($"The shader changed from 0x{(uint)before.Shader:X8} to 0x{(uint)after.Shader:X8}.");
        var bonesBefore = before.BoneHashes.Count;
        var bonesAfter = after.BoneHashes.Count;
        if (bonesAfter == 0) warnings.Add("The mesh has no bones; it will not follow the Sim.");
        else if (bonesAfter != bonesBefore) warnings.Add($"The bone list changed ({bonesBefore} → {bonesAfter}).");

        // Tangents (research/undocumented-fields.md, "Tangents"): GEOM Tools recalculates every tangent along +U on export.
        // EA's are mostly +U too, but smoothed across seams, kept consistent across mirrored UVs, and along +V for
        // SimHorse. So every vertex that still matches an original one (position and first UV) gets its original tangent
        // back; for SimHorse, vertices that moved get a tangent along V instead of GEOM Tools' U.
        var (restored, horse) = RestoreTangents(before, after);
        var moved = after.VertexData.Count - restored;
        if (restored > 0)
            warnings.Add(moved == 0
                ? "No vertex moved, so every original tangent was kept (GEOM Tools recalculates them)."
                : $"Kept the original tangents on {restored:N0} unchanged vertices; {moved:N0} new or moved vertices use recalculated tangents.");
        if (horse > 0)
            warnings.Add($"SimHorse coat: {horse:N0} new or moved vertices got tangents along V, the direction EA's horse coats use " +
                         "(from EA data; GEOM Tools writes them along U).");

        using var copy = new MemoryStream();
        rcol.Stream.CopyTo(copy);
        session.Replace(group, copy.ToArray());
        return new MeshImportResult(before.VertexData.Count, after.VertexData.Count, before.Faces.Count, after.Faces.Count, colourBefore, colourAfter, warnings);
    }

    static GEOM Geom(byte[] bytes, out GenericRCOLResource rcol)
    {
        try
        {
            rcol = new GenericRCOLResource(0, new MemoryStream(bytes));
        }
        catch (Exception error) when (error is System.Reflection.TargetInvocationException or EndOfStreamException or ArgumentException)
        {
            throw new InvalidDataException($"not a readable GEOM ({(error.InnerException ?? error).Message})", error);
        }
        return rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().FirstOrDefault() ?? throw new InvalidDataException("no GEOM chunk");
    }

    /// <summary>
    /// The tangent rules on their own, for checks: applies them to <paramref name="edited" /> against
    /// <paramref name="original" /> and returns the re-written GEOM with the counts.
    /// </summary>
    public static (byte[] Bytes, int Restored, int Horse) ApplyTangentRules(byte[] original, byte[] edited)
    {
        var before = Geom(original, out _);
        var after = Geom(edited, out var rcol);
        var (restored, horse) = RestoreTangents(before, after);
        using var copy = new MemoryStream();
        rcol.Stream.CopyTo(copy);
        return (copy.ToArray(), restored, horse);
    }

    const uint SimHorse = 0x05F75EF1; // FNV-32 of "SimHorse"

    /// <summary>
    /// Puts each matching vertex's original tangent back (match: position and first UV, rounded to 1e-4, which absorbs
    /// GEOM Tools' float noise of about 3e-8 on UVs). For SimHorse, unmatched vertices get the V direction made
    /// orthogonal to the normal. Returns (vertices restored, SimHorse vertices recalculated).
    /// </summary>
    static (int Restored, int Horse) RestoreTangents(GEOM before, GEOM after)
    {
        static (long, long, long, long, long) Key(System.Numerics.Vector3 p, System.Numerics.Vector2 uv) =>
            ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f), (long)MathF.Round(uv.X * 1e4f), (long)MathF.Round(uv.Y * 1e4f));
        var (posA, uvA, _, tanA) = Read(before);
        var (posB, uvB, nrmB, _) = Read(after);
        var original = new Dictionary<(long, long, long, long, long), System.Numerics.Vector3>();
        for (var i = 0; i < posA.Length; i++) original.TryAdd(Key(posA[i], uvA[i]), tanA[i]);

        var horse = (uint)after.Shader == SimHorse;
        var dv = horse ? VDirections(after, posB, uvB) : null;
        int restored = 0, recalculated = 0;
        for (var i = 0; i < posB.Length; i++)
        {
            if (after.VertexData[i].Vertex.OfType<GEOM.TangentNormalElement>().FirstOrDefault() is not { } t) continue;
            System.Numerics.Vector3 value;
            // Same index first: GEOM Tools keeps vertex order, and split vertices on mirror seams share a position and
            // UV but carry different tangents, so a position lookup alone would give them all the first one's.
            var key = Key(posB[i], uvB[i]);
            if (i < posA.Length && Key(posA[i], uvA[i]) == key) { value = tanA[i]; restored++; }
            else if (original.TryGetValue(key, out value)) restored++;
            else if (horse && dv![i].LengthSquared() > 1e-12f)
            {
                var n = nrmB[i].LengthSquared() > 1e-12f ? System.Numerics.Vector3.Normalize(nrmB[i]) : System.Numerics.Vector3.Zero;
                var v = dv[i] - n * System.Numerics.Vector3.Dot(dv[i], n);
                if (v.LengthSquared() < 1e-12f) continue;
                value = System.Numerics.Vector3.Normalize(v);
                recalculated++;
            }
            else continue;
            t.X = value.X; t.Y = value.Y; t.Z = value.Z;
        }
        return (restored, recalculated);
    }

    static (System.Numerics.Vector3[] Pos, System.Numerics.Vector2[] Uv, System.Numerics.Vector3[] Nrm, System.Numerics.Vector3[] Tan) Read(GEOM g)
    {
        var n = g.VertexData.Count;
        var pos = new System.Numerics.Vector3[n]; var uv = new System.Numerics.Vector2[n];
        var nrm = new System.Numerics.Vector3[n]; var tan = new System.Numerics.Vector3[n];
        for (var i = 0; i < n; i++)
        {
            var seenUv = false;
            foreach (var el in g.VertexData[i].Vertex)
                switch (el)
                {
                    case GEOM.TangentNormalElement t: tan[i] = new(t.X, t.Y, t.Z); break;
                    case GEOM.NormalElement m: nrm[i] = new(m.X, m.Y, m.Z); break;
                    case GEOM.PositionElement p: pos[i] = new(p.X, p.Y, p.Z); break;
                    case GEOM.UVElement u when !seenUv: uv[i] = new(u.U, u.V); seenUv = true; break;
                }
        }
        return (pos, uv, nrm, tan);
    }

    /// <summary>Per-vertex direction of increasing V (dP/dv), summed over the vertex's triangles.</summary>
    static System.Numerics.Vector3[] VDirections(GEOM g, System.Numerics.Vector3[] pos, System.Numerics.Vector2[] uv)
    {
        var dv = new System.Numerics.Vector3[pos.Length];
        foreach (var f in g.Faces)
        {
            int a = f.VertexDataIndex0, b = f.VertexDataIndex1, c = f.VertexDataIndex2;
            if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
            var e1 = pos[b] - pos[a]; var e2 = pos[c] - pos[a];
            var d1 = uv[b] - uv[a]; var d2 = uv[c] - uv[a];
            var det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            var bt = (e2 * d1.X - e1 * d2.X) / det;
            dv[a] += bt; dv[b] += bt; dv[c] += bt;
        }
        return dv;
    }

    static bool HasColour(GEOM g) => g.VertexData.Count > 0 && g.VertexData[0].Vertex.OfType<GEOM.ColorElement>().Any();
}
