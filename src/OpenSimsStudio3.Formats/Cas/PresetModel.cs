using System.Globalization;
using System.Xml.Linq;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Cas;

/// <summary>One pattern channel of a recolour (Pattern A–D in a CasRgbMask complate).</summary>
public sealed record PresetChannel(string Variable, bool Enabled, string PatternName, string PatternFile, float[]? Color, string Tiling);

/// <summary>One texture a recolour names, by its complate value key (Multiplier, Mask, Clothing Specular…).</summary>
public sealed record PresetTexture(string Name, ResourceKey Key);

/// <summary>
/// A CASP preset's XML as an editable model: the complate's values (textures, pattern flags) and the nested pattern
/// elements (pattern file and colour). Unchanged values are written back exactly as read: the XML is kept as an
/// element tree and only the edited attributes change; <see cref="ToXml" /> writes EA's compact form.
/// </summary>
public sealed class PresetModel
{
    static readonly string[] ChannelNames = ["Pattern A", "Pattern B", "Pattern C", "Pattern D"];

    readonly XElement _root;
    readonly XElement _complate;
    readonly string _prefix;

    PresetModel(XElement root, string prefix)
    {
        _root = root;
        _prefix = prefix;
        _complate = root.Element("complate") ?? throw new InvalidDataException("preset has no complate");
    }

    /// <summary>Parses a preset; anything before the root element (some packs carry an XML declaration) is kept verbatim.</summary>
    public static PresetModel Parse(string xml)
    {
        var start = xml.IndexOf("<preset", StringComparison.Ordinal);
        return new PresetModel(XElement.Parse(start > 0 ? xml[start..] : xml, LoadOptions.PreserveWhitespace), start > 0 ? xml[..start] : "");
    }

    public string Complate => (string?)_complate.Attribute("name") ?? "";

    /// <summary>EA's compact form: no declaration, no indentation.</summary>
    public string ToXml() => _prefix + _root.ToString(SaveOptions.DisableFormatting);

    public IReadOnlyList<PresetChannel> Channels =>
        ChannelNames.Where(n => Value(n) != null).Select(n =>
        {
            var pattern = PatternElement(n);
            return new PresetChannel(n, string.Equals(Value(n + " Enabled"), "True", StringComparison.OrdinalIgnoreCase),
                (string?)pattern?.Attribute("name") ?? "", Value(n) ?? "", ParseColor(PatternValue(pattern, "Color")), Value(n + " Tiling") ?? "");
        }).ToList();

    /// <summary>Every complate value that names an _IMG texture (CasRgbMask: Multiplier, Mask…; HairUniversal: Diffuse Map, Control Map…).</summary>
    public IReadOnlyList<PresetTexture> Textures =>
        _complate.Elements("value").Select(v => ((string?)v.Attribute("key") ?? "", (string?)v.Attribute("value") ?? ""))
            .Where(t => t.Item2.StartsWith("key:00B2D882:", StringComparison.OrdinalIgnoreCase))
            .Select(t => new PresetTexture(t.Item1, ParseKey(t.Item2))).ToList();

    /// <summary>Top-level colour values ("… Color" with 3–4 numbers): HairUniversal's Root, Diffuse, Highlight and Tip Color, say.</summary>
    public IReadOnlyList<(string Name, float[] Rgba)> Colors =>
        _complate.Elements("value").Select(v => ((string?)v.Attribute("key") ?? "", ParseColor((string?)v.Attribute("value"))))
            .Where(t => t.Item1.EndsWith("Color", StringComparison.Ordinal) && t.Item2 != null).Select(t => (t.Item1, t.Item2!)).ToList();

    public void SetColor(string name, float[] rgba) => SetValue(name, FormatColor(rgba));

    /// <summary>False for a stencil or logo slot whose "… Enabled" value is off; true for every other texture.</summary>
    public bool IsTextureUsed(string name)
    {
        var enabled = Value(name + " Enabled");
        return enabled == null || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns off a stencil or logo slot's "… Enabled" value; false when the slot has no such value or it is already off.</summary>
    public bool DisableTexture(string name)
    {
        if (Value(name + " Enabled") == null || !IsTextureUsed(name)) return false;
        SetValue(name + " Enabled", "False");
        return true;
    }

    public void SetChannelEnabled(string channel, bool on) => SetValue(channel + " Enabled", on ? "True" : "False");

    /// <summary>Sets a solid-colour channel's colour (RGBA 0–1). Only patterns that carry a Color value can be set.</summary>
    public bool SetChannelColor(string channel, float[] rgba)
    {
        var pattern = PatternElement(channel);
        var value = pattern?.Elements("value").FirstOrDefault(v => (string?)v.Attribute("key") == "Color");
        if (value == null) return false;
        value.SetAttributeValue("value", FormatColor(rgba));
        return true;
    }

    /// <summary>Points a texture value (Multiplier, Mask…) at another resource.</summary>
    public void SetTexture(string name, ResourceKey key) => SetValue(name, $"key:{key.Type:X8}:{key.Group:X8}:{key.Instance:X16}");

    static string FormatColor(float[] rgba) => string.Join(",", rgba.Select(c => Math.Clamp(c, 0f, 1f).ToString("0.0000", CultureInfo.InvariantCulture)));

    string? Value(string key) => (string?)_complate.Elements("value").FirstOrDefault(v => (string?)v.Attribute("key") == key)?.Attribute("value");

    void SetValue(string key, string value) =>
        (_complate.Elements("value").FirstOrDefault(v => (string?)v.Attribute("key") == key) ?? throw new KeyNotFoundException(key))
        .SetAttributeValue("value", value);

    XElement? PatternElement(string channel) => _complate.Elements("pattern").FirstOrDefault(p => (string?)p.Attribute("variable") == channel);

    static string? PatternValue(XElement? pattern, string key) =>
        (string?)pattern?.Elements("value").FirstOrDefault(v => (string?)v.Attribute("key") == key)?.Attribute("value");

    static float[]? ParseColor(string? text)
    {
        if (text == null) return null;
        var parts = text.Split(',');
        var values = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return null;
        return values.Length >= 3 ? values : null;
    }

    static ResourceKey ParseKey(string text)
    {
        var p = text[4..].Split(':');
        return new ResourceKey(Convert.ToUInt32(p[0], 16), Convert.ToUInt32(p[1], 16), Convert.ToUInt64(p[2], 16));
    }
}
