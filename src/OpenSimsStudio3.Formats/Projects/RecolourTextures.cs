using System.Security.Cryptography;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>
/// Texture-slot edits on a project's recolours: put new DDS bytes in a slot, clear it, or point every recolour's slot
/// at one resource. A slot is a complate value naming an _IMG (Multiplier, Mask…), read and written through
/// <see cref="Cas.PresetModel" />.
/// <para>
/// Imported textures get a new key in the project package: the group of the texture they replace, and an instance that
/// is FNV-64 of "oss3:{project id}:img:{SHA-256 of the DDS}" (salted on a clash). The same image imported twice gets
/// the same key, and an imported texture that no recolour uses any more can be recognised from its bytes alone and is
/// dropped from the package. Textures the project did not import (a clone's copies, the game's) are never removed.
/// </para>
/// <para>A cleared slot keeps its value, pointed at the _IMG type with group and instance 0, so it stays listed and can be filled again.</para>
/// </summary>
public static class RecolourTextures
{
    public const uint Img = 0x00B2D882;
    const int MaxSalt = 8;

    /// <summary>The key a cleared slot holds.</summary>
    public static ResourceKey EmptyKey => new(Img, 0, 0);

    public static bool IsEmpty(ResourceKey key) => key.Instance == 0;

    /// <summary>The slot's key in one recolour; null when that recolour has no such slot or its XML can't be read.</summary>
    public static ResourceKey? Slot(ProjectSession session, int preset, string slot) =>
        TryPreset(session, preset)?.Textures.FirstOrDefault(t => t.Name == slot)?.Key;

    /// <summary>True when there are two or more recolours and every one has the slot pointing at the same resource.</summary>
    public static bool IsShared(ProjectSession session, string slot)
    {
        var count = session.Part.Presets.Count;
        if (count < 2) return false;
        var first = Slot(session, 0, slot);
        if (first == null) return false;
        for (var i = 1; i < count; i++)
            if (Slot(session, i, slot) != first) return false;
        return true;
    }

    /// <summary>
    /// Adds <paramref name="dds" /> to the project under a new key and points the slot at it, in this recolour or in
    /// every recolour that has the slot. Returns the key.
    /// </summary>
    public static ResourceKey Replace(ProjectSession session, int preset, string slot, byte[] dds, bool allRecolours)
    {
        var old = Slot(session, preset, slot) ?? throw new KeyNotFoundException($"recolour {preset + 1} has no {slot} texture");
        var key = KeyFor(session, dds, IsEmpty(old) ? 0 : old.Group);
        if (!session.PackageKeys.Contains(key)) session.Replace(key, dds);
        var replaced = Point(session, Targets(session, preset, allRecolours), slot, key);
        Prune(session, replaced);
        return key;
    }

    /// <summary>Empties the slot, in this recolour or in every recolour that has it.</summary>
    public static void Clear(ProjectSession session, int preset, string slot, bool allRecolours)
    {
        if (Slot(session, preset, slot) == null) throw new KeyNotFoundException($"recolour {preset + 1} has no {slot} texture");
        Prune(session, Point(session, Targets(session, preset, allRecolours), slot, EmptyKey));
    }

    /// <summary>Points the slot of every recolour that has it at this recolour's resource. Returns how many recolours changed.</summary>
    public static int ShareAcross(ProjectSession session, int preset, string slot)
    {
        var key = Slot(session, preset, slot) ?? throw new KeyNotFoundException($"recolour {preset + 1} has no {slot} texture");
        var replaced = Point(session, Targets(session, preset, all: true), slot, key);
        Prune(session, replaced);
        return replaced.Count;
    }

    /// <summary>The key an import of these bytes gets: reused when the project already holds the same bytes under it.</summary>
    public static ResourceKey KeyFor(ProjectSession session, byte[] dds, uint group)
    {
        for (var salt = 0; salt < MaxSalt; salt++)
        {
            var key = Candidate(session.Project.Id, dds, group, salt);
            if (key.Instance == 0) continue;
            if (session.PackageKeys.Contains(key) && session.Read(key) is { } held && held.AsSpan().SequenceEqual(dds)) return key;
            if (!session.Contains(key)) return key;
        }
        throw new InvalidOperationException("no free texture key");
    }

    static ResourceKey Candidate(string projectId, byte[] dds, uint group, int salt) =>
        new(Img, group, Projects.CasClone.Fnv64($"oss3:{projectId}:img:{Convert.ToHexString(SHA256.HashData(dds))}{(salt == 0 ? "" : ":" + salt)}"));

    static IEnumerable<int> Targets(ProjectSession session, int preset, bool all) =>
        all ? Enumerable.Range(0, session.Part.Presets.Count) : [preset];

    /// <summary>Sets the slot in each target recolour that has it; returns the keys it held before, where they changed.</summary>
    static List<ResourceKey> Point(ProjectSession session, IEnumerable<int> presets, string slot, ResourceKey key)
    {
        var before = new List<ResourceKey>();
        foreach (var i in presets)
        {
            var model = TryPreset(session, i);
            var current = model?.Textures.FirstOrDefault(t => t.Name == slot);
            if (model == null || current == null || current.Key == key) continue;
            model.SetTexture(slot, key);
            session.SetPreset(i, model);
            before.Add(current.Key);
        }
        return before;
    }

    /// <summary>Drops imported textures (see the class notes) that no recolour and no CASP key list refers to any more.</summary>
    static void Prune(ProjectSession session, IEnumerable<ResourceKey> candidates)
    {
        var unused = candidates.Where(k => !IsEmpty(k) && session.PackageKeys.Contains(k)).ToHashSet();
        if (unused.Count == 0) return;
        for (var i = 0; i < session.Part.Presets.Count; i++)
        {
            // An unreadable preset could refer to anything: keep everything.
            var model = TryPreset(session, i);
            if (model == null) return;
            foreach (var t in model.Textures) unused.Remove(t.Key);
        }
        foreach (var tgi in session.Part.TGIBlocks) unused.Remove(ResourceKey.Of(tgi));
        foreach (var key in unused)
            if (session.Read(key) is { } bytes && Enumerable.Range(0, MaxSalt).Any(s => Candidate(session.Project.Id, bytes, key.Group, s) == key))
                session.Remove(key);
    }

    static Cas.PresetModel? TryPreset(ProjectSession session, int index)
    {
        if (index < 0 || index >= session.Part.Presets.Count) return null;
        try { return session.Preset(index); }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException) { return null; }
    }
}
