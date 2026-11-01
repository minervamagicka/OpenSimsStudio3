using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenSimsStudio3.Formats.Compositing;

/// <summary>
/// One complate (a CAS part's template or a pattern's) bound to the values a preset gives it: template param defaults,
/// overridden by the preset's &lt;value&gt; items. Resolves "($Name)" references and "{...}" arithmetic in step attributes.
/// </summary>
sealed partial class Complate
{
    public const uint TemplateType = 0x0333406C;

    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Complate(XElement template, IEnumerable<XElement> presetValues)
    {
        Template = template;
        foreach (var p in template.Element("variables")?.Elements("param") ?? [])
            if ((string?)p.Attribute("name") is { } n && (string?)p.Attribute("default") is { } d) _values[n] = d;
        foreach (var v in presetValues)
            if ((string?)v.Attribute("key") is { } k && (string?)v.Attribute("value") is { } val) _values[k] = val;
        // Pattern references are written "($daeFileName)Pattern A" (the tool's per-asset prefix); the preset's
        // <pattern variable="Pattern A"> is what they name, so the prefix resolves to nothing here.
        _values["daeFileName"] = "";
    }

    public XElement Template { get; }

    public string Name => (string?)Template.Attribute("name") ?? "";

    /// <summary>The XML of a template resource (UTF-8, possibly with a BOM).</summary>
    public static XElement ParseTemplate(byte[] bytes) => XElement.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿'));

    /// <summary>An attribute with references and arithmetic resolved, or null when absent. Attribute names match case-insensitively.</summary>
    public string? Attr(XElement step, string name)
    {
        var raw = step.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
        return raw == null ? null : Resolve(raw);
    }

    public string Resolve(string text)
    {
        for (var depth = 0; depth < 8 && text.Contains("($"); depth++)
            text = Reference().Replace(text, m => _values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
        return Arithmetic().Replace(text, m => Evaluate(m.Groups[1].Value).ToString("R", CultureInfo.InvariantCulture));
    }

    public bool Bool(XElement step, string name, bool fallback) =>
        Attr(step, name) is { } s && bool.TryParse(s.Trim(), out var b) ? b : fallback;

    public float Float(XElement step, string name, float fallback) => Attr(step, name) is { } s ? Floats(s) is [var f, ..] ? f : fallback : fallback;

    /// <summary>A comma-separated number list; unparsable items read as 0.</summary>
    public static float[] Floats(string text) =>
        text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f).ToArray();

    [GeneratedRegex(@"\(\$([^()]*)\)")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Arithmetic();

    /// <summary>+ - * / and parentheses over numbers: what the templates' "{0.0174532925 * (...)}" rotations need.</summary>
    static double Evaluate(string expr)
    {
        var pos = 0;
        var value = Sum();
        return value;

        double Sum()
        {
            var v = Product();
            while (Peek() is '+' or '-') v = Next() == '+' ? v + Product() : v - Product();
            return v;
        }
        double Product()
        {
            var v = Unary();
            while (Peek() is '*' or '/') v = Next() == '*' ? v * Unary() : v / Unary();
            return v;
        }
        double Unary()
        {
            if (Peek() == '-') { pos++; return -Unary(); }
            if (Peek() == '+') { pos++; return Unary(); }
            if (Peek() == '(') { pos++; var v = Sum(); if (Peek() == ')') pos++; return v; }
            var start = pos;
            while (pos < expr.Length && (char.IsDigit(expr[pos]) || expr[pos] is '.' or 'e' or 'E')) pos++;
            return double.TryParse(expr.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        char Peek()
        {
            while (pos < expr.Length && char.IsWhiteSpace(expr[pos])) pos++;
            return pos < expr.Length ? expr[pos] : '\0';
        }
        char Next() { var c = Peek(); pos++; return c; }
    }
}
