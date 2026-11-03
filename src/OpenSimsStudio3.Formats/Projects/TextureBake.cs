using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Textures;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>How a bake leaves the recolour's preset, beyond the new Multiplier.</summary>
/// <param name="Channels">Every enabled pattern channel (Pattern A–D) is turned off.</param>
/// <param name="Stencils">Every texture with an "… Enabled" value that is on (stencils, logo) is turned off.</param>
/// <param name="Overlay">The Overlay slot is cleared.</param>
public sealed record BakeEdits(bool Channels, bool Stencils, bool Overlay)
{
    public override string ToString() =>
        string.Join(", ", new[] { Channels ? "channels off" : null, Stencils ? "stencils off" : null, Overlay ? "overlay cleared" : null }.OfType<string>());
}

/// <summary>
/// A bake worked out but not yet applied: the encoded Multiplier, the preset edits that go with it, and how closely the
/// recolour composes to the bake once both are in place (mean absolute difference in 8-bit levels over RGBA).
/// </summary>
public sealed record BakePlan(int Preset, byte[] Dds, DdsFormat Format, int Width, int Height, BakeEdits Edits, double Difference, bool Matches);

/// <summary>
/// Bake in the texture window (mockup Editor.dc.html: the Bake button is shown for the Multiplier slot only).
/// <para>
/// <b>What Bake means here</b> (interpretation, 2026-11-03; the board shows only the button and where it applies):
/// compose the open recolour's diffuse as the game would — its pattern channels, mask, Multiplier, overlay and
/// stencils, through <see cref="CasCompositor" /> — and write that image into the recolour's Multiplier as a flat
/// texture. A Multiplier that already holds the patterns would be patterned again in game, so Bake also changes the
/// preset so that the recolour composes to the baked image: it turns the pattern channels off and, when they too
/// were drawn on top, the stencils and the overlay.
/// </para>
/// <para>
/// Which of those edits a complate needs depends on its template, which OSS3 only sees at run time (EA data). So Bake
/// does not assume: <see cref="Plan" /> tries the edits from fewest to most (<see cref="Candidates" />), recomposes each
/// with the new Multiplier, and keeps the first whose result matches the bake within the texture format's own
/// encoding error. When none matches, it keeps the closest and <see cref="BakePlan.Matches" /> is false.
/// </para>
/// <para>
/// The composed diffuse has its colour multiplied by alpha (see Compositing/README.md). Where it is not opaque, Bake
/// also tries the colour divided back out (straight alpha, what a Multiplier normally holds) and keeps whichever
/// composes closer. The Multiplier keeps its format (<see cref="TextureImport.FormatFor" />) and takes the composed
/// size, with a full mip chain.
/// </para>
/// <para>
/// Same in all recolours: as with Import, a bake with the box ticked writes the open recolour's bake into every
/// recolour that has a Multiplier and makes the same preset edits in each, so every recolour then shows this one's
/// look. Unticked, only the open recolour changes.
/// </para>
/// </summary>
public static class TextureBake
{
    public const string Slot = "Multiplier";
    public const string Overlay = "Overlay";

    /// <summary>Composition size cap: large enough for any CAS texture TS3 ships.</summary>
    const int MaxSize = 2048;

    /// <summary>
    /// The most encoding error (mean levels) the match test allows for. A lossy encoding (say, a translucent bake in a
    /// DXT1 slot) must not make any result look like a match.
    /// </summary>
    const double MaxEncodingError = 4;

    /// <summary>Bake applies to the Multiplier of a recolour whose complate has pattern channels (CasRgbMask and the like).</summary>
    public static bool Applies(ProjectSession session, int preset, string slot)
    {
        if (slot != Slot || preset < 0 || preset >= session.Part.Presets.Count) return false;
        try
        {
            var model = session.Preset(preset);
            return model.Channels.Count > 0 && model.Textures.Any(t => t.Name == Slot);
        }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException) { return false; }
    }

    /// <summary>The edits tried, fewest first; edits that would change nothing in this preset are left out.</summary>
    public static IReadOnlyList<BakeEdits> Candidates(PresetModel model)
    {
        var stencils = model.Textures.Any(t => t.Name != Slot && Clone(model).DisableTexture(t.Name));
        var overlay = model.Textures.Any(t => t.Name == Overlay && !RecolourTextures.IsEmpty(t.Key));
        var list = new List<BakeEdits> { new(true, false, false) };
        if (stencils) list.Add(new(true, true, false));
        if (overlay) list.Add(new(true, stencils, true));
        return list;
    }

    /// <summary>
    /// Composes the recolour and works out the bake without changing the project (safe on a worker thread while the
    /// project is not being edited).
    /// </summary>
    /// <exception cref="InvalidOperationException">Bake doesn't apply, or the recolour can't be composed.</exception>
    public static BakePlan Plan(ProjectSession session, int preset)
    {
        if (!Applies(session, preset, Slot)) throw new InvalidOperationException($"recolour {preset + 1} has nothing to bake");
        var xml = session.Preset(preset).ToXml();
        if (!CasCompositor.TryComposeDiffuse(session, xml, out var composed, out var reason, MaxSize))
            throw new InvalidOperationException(reason ?? "this recolour can't be composed");
        var target = composed!.Rgba;

        var old = RecolourTextures.Slot(session, preset, Slot)!.Value;
        var existing = RecolourTextures.IsEmpty(old) ? null : TextureInfo.Read(session.Read(old));
        var group = RecolourTextures.IsEmpty(old) ? 0 : old.Group;

        var images = new List<DdsImage> { new(composed.Width, composed.Height, Unpremultiply(target)) };
        if (!IsOpaque(target)) images.Add(new DdsImage(composed.Width, composed.Height, target));

        BakePlan? best = null;
        foreach (var image in images)
        {
            var format = TextureImport.FormatFor(existing, image);
            var dds = DdsEncoder.Encode(image, format);
            var tolerance = Math.Min(Difference(DdsDecoder.Decode(dds).Rgba, image.Rgba), MaxEncodingError) * 1.5 + 1.0;
            var key = RecolourTextures.KeyFor(session, dds, group);
            var source = new WithResource(session, key, dds);
            foreach (var edits in Candidates(PresetModel.Parse(xml)))
            {
                var model = PresetModel.Parse(xml);
                model.SetTexture(Slot, key);
                Neutralise(model, edits);
                var difference = CasCompositor.TryComposeDiffuse(source, model.ToXml(), out var again, out _, MaxSize)
                                 && again!.Width == composed.Width && again.Height == composed.Height
                    ? Difference(again.Rgba, target)
                    : double.PositiveInfinity;
                var plan = new BakePlan(preset, dds, format, image.Width, image.Height, edits, difference, difference <= tolerance);
                if (plan.Matches) return plan;
                if (best == null || difference < best.Difference) best = plan;
            }
        }
        return best!;
    }

    /// <summary>
    /// Stores the bake in the Multiplier (via <see cref="RecolourTextures.Replace" />) and makes the plan's preset edits,
    /// in the planned recolour or in every recolour that has a Multiplier. Returns the Multiplier's key.
    /// </summary>
    public static ResourceKey Apply(ProjectSession session, BakePlan plan, bool allRecolours)
    {
        var key = RecolourTextures.Replace(session, plan.Preset, Slot, plan.Dds, allRecolours);
        IEnumerable<int> targets = allRecolours ? Enumerable.Range(0, session.Part.Presets.Count) : [plan.Preset];
        foreach (var i in targets)
        {
            if (RecolourTextures.Slot(session, i, Slot) != key) continue; // no Multiplier, or an unreadable preset
            var model = session.Preset(i);
            Neutralise(model, plan.Edits with { Overlay = false });
            session.SetPreset(i, model);
            if (plan.Edits.Overlay && RecolourTextures.Slot(session, i, Overlay) is { } overlay && !RecolourTextures.IsEmpty(overlay))
                RecolourTextures.Clear(session, i, Overlay, allRecolours: false);
        }
        return key;
    }

    /// <summary>Plans and applies in one go; the plan says how closely the result composes to the bake.</summary>
    public static BakePlan Bake(ProjectSession session, int preset, bool allRecolours)
    {
        var plan = Plan(session, preset);
        Apply(session, plan, allRecolours);
        return plan;
    }

    static void Neutralise(PresetModel model, BakeEdits edits)
    {
        if (edits.Channels)
            foreach (var c in model.Channels.Where(c => c.Enabled)) model.SetChannelEnabled(c.Variable, false);
        if (edits.Stencils)
            foreach (var t in model.Textures.Where(t => t.Name != Slot)) model.DisableTexture(t.Name);
        if (edits.Overlay && model.Textures.Any(t => t.Name == Overlay)) model.SetTexture(Overlay, RecolourTextures.EmptyKey);
    }

    static PresetModel Clone(PresetModel model) => PresetModel.Parse(model.ToXml());

    /// <summary>Colour divided by alpha (the compositor's output is premultiplied); fully transparent pixels stay black.</summary>
    static byte[] Unpremultiply(byte[] rgba)
    {
        var straight = (byte[])rgba.Clone();
        for (var p = 0; p < straight.Length; p += 4)
        {
            int a = straight[p + 3];
            if (a is 0 or 255) continue;
            for (var c = 0; c < 3; c++) straight[p + c] = (byte)Math.Min(255, (straight[p + c] * 255 + a / 2) / a);
        }
        return straight;
    }

    static bool IsOpaque(byte[] rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return false;
        return true;
    }

    /// <summary>Mean absolute difference over every byte, in 8-bit levels.</summary>
    public static double Difference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.PositiveInfinity;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return a.Length == 0 ? 0 : (double)sum / a.Length;
    }

    /// <summary>The project as the compositor reads it, plus one resource not yet stored.</summary>
    sealed class WithResource(IResourceSource inner, ResourceKey key, byte[] bytes) : IResourceSource
    {
        public bool Contains(ResourceKey k) => k == key || inner.Contains(k);
        public byte[]? Read(ResourceKey k) => k == key ? bytes : inner.Read(k);
    }
}
