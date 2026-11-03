using System.Reflection;
using meshExpImp.ModelBlocks;
using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Materials;

/// <summary>How a shader parameter's value is stored (MTNF data type and count).</summary>
public enum ShaderParameterKind
{
    /// <summary>One float.</summary>
    Float,
    /// <summary>Two floats (UV scales and the like).</summary>
    Float2,
    /// <summary>Three floats (colours, directions).</summary>
    Float3,
    /// <summary>Four floats.</summary>
    Float4,
    /// <summary>One 32-bit integer.</summary>
    Int,
    /// <summary>A texture, stored in a GEOM as an index into the GEOM's own resource key list.</summary>
    Texture,
    /// <summary>A texture stored as an inline resource key.</summary>
    TextureKey,
}

/// <summary>One shader parameter: its hash, kind and value. Values are edited in place on the owning material.</summary>
public sealed class ShaderParameter
{
    float[] _floats;
    ResourceKey? _texture;

    internal ShaderParameter(uint field, ShaderParameterKind kind, float[] floats, int intValue, ResourceKey? texture, int textureIndex)
    {
        Field = field;
        Kind = kind;
        _floats = floats;
        IntValue = intValue;
        _texture = texture;
        TextureIndex = textureIndex;
    }

    /// <summary>The parameter's hash (FNV-32 of its lower-cased name).</summary>
    public uint Field { get; }

    public ShaderParameterKind Kind { get; }

    /// <summary>The known name, else null (see <see cref="ShaderNames" />).</summary>
    public string? Name => ShaderNames.ParameterName(Field);

    /// <summary>The name when known, else the hash in hex.</summary>
    public string Label => ShaderNames.ParameterLabel(Field);

    /// <summary>Number of floats for the float kinds (1–4); 0 for the others.</summary>
    public static int FloatCount(ShaderParameterKind kind) => kind switch
    {
        ShaderParameterKind.Float => 1,
        ShaderParameterKind.Float2 => 2,
        ShaderParameterKind.Float3 => 3,
        ShaderParameterKind.Float4 => 4,
        _ => 0,
    };

    public bool IsFloat => FloatCount(Kind) > 0;
    public bool IsTexture => Kind is ShaderParameterKind.Texture or ShaderParameterKind.TextureKey;

    /// <summary>The float values (a copy); empty for the non-float kinds.</summary>
    public float[] Floats
    {
        get => (float[])_floats.Clone();
        set
        {
            if (!IsFloat) throw new InvalidOperationException($"{Label} is {Kind}, not a float parameter");
            if (value.Length != FloatCount(Kind)) throw new ArgumentException($"{Label} takes {FloatCount(Kind)} values, got {value.Length}");
            _floats = (float[])value.Clone();
        }
    }

    /// <summary>Sets one float of a float parameter.</summary>
    public void SetFloat(int index, float value)
    {
        if (!IsFloat) throw new InvalidOperationException($"{Label} is {Kind}, not a float parameter");
        _floats[index] = value;
    }

    /// <summary>The value of an <see cref="ShaderParameterKind.Int" /> parameter (0 for the other kinds).</summary>
    public int IntValue { get; set; }

    /// <summary>
    /// The texture's key for the texture kinds. For <see cref="ShaderParameterKind.Texture" /> it is read through the
    /// GEOM's key list, and is null when the stored index is out of range (the index is then kept as it was).
    /// </summary>
    public ResourceKey? Texture
    {
        get => _texture;
        set
        {
            if (!IsTexture) throw new InvalidOperationException($"{Label} is {Kind}, not a texture parameter");
            if (value == null && Kind == ShaderParameterKind.TextureKey) throw new ArgumentNullException(nameof(value), "A texture key parameter always has a key");
            if (_texture == value) return;
            _texture = value;
            TextureIndex = -1;
        }
    }

    /// <summary>
    /// For <see cref="ShaderParameterKind.Texture" />: the stored index into the GEOM's key list, or -1 once
    /// <see cref="Texture" /> has been set (the index is then found or added when the material is written).
    /// </summary>
    public int TextureIndex { get; private set; }

    /// <summary>The value as short text (for lists and probes).</summary>
    public string ValueText => Kind switch
    {
        ShaderParameterKind.Int => IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ShaderParameterKind.Texture or ShaderParameterKind.TextureKey => _texture?.ToString() ?? $"index {TextureIndex} (missing)",
        _ => string.Join(", ", _floats.Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
    };
}

/// <summary>
/// The material of one GEOM: its shader and its MTNF parameter list, as an editable model. Read a GEOM resource's bytes
/// with <see cref="Read" />, edit, then <see cref="Write" /> the same bytes back through the model. Writing rebuilds the
/// parameter list from the model and keeps everything else (mesh, bones, the MTNF header) from the original, so an
/// unedited material writes the original bytes exactly. Texture keys added by an edit are appended to the GEOM's key
/// list; keys an edit no longer uses stay in the list (other fields, e.g. the skin index, point into it too).
/// </summary>
public sealed class GeomMaterial
{
    readonly List<ShaderParameter> _parameters;
    uint _shader;

    GeomMaterial(uint shader, List<ShaderParameter> parameters, IReadOnlyList<ResourceKey> keys)
    {
        _shader = shader;
        _parameters = parameters;
        Keys = keys;
    }

    /// <summary>The shader hash. Never 0: a GEOM with shader 0 (a morph) has no material.</summary>
    public uint Shader
    {
        get => _shader;
        set => _shader = value != 0 ? value : throw new ArgumentException("Shader 0 means no material (morph GEOMs); pick a shader");
    }

    /// <summary>The shader's name when known, else the hash in hex.</summary>
    public string ShaderLabel => ShaderNames.ShaderLabel(_shader);

    public IReadOnlyList<ShaderParameter> Parameters => _parameters;

    /// <summary>The GEOM's resource key list as read (texture parameters index into it).</summary>
    public IReadOnlyList<ResourceKey> Keys { get; }

    public ShaderParameter? Find(uint field) => _parameters.FirstOrDefault(p => p.Field == field);

    /// <summary>
    /// Adds a parameter with a zero value (floats 0, int 0; textures take <paramref name="texture" />, or the all-zero key).
    /// Throws when the material already has that parameter: the game reads one value per name.
    /// </summary>
    public ShaderParameter Add(uint field, ShaderParameterKind kind, ResourceKey? texture = null)
    {
        if (Find(field) != null) throw new InvalidOperationException($"{ShaderNames.ParameterLabel(field)} is already in the material");
        var isTexture = kind is ShaderParameterKind.Texture or ShaderParameterKind.TextureKey;
        var parameter = new ShaderParameter(field, kind, new float[ShaderParameter.FloatCount(kind)], 0,
            isTexture ? texture ?? default(ResourceKey) : null, -1);
        _parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Removes a parameter; false when the material doesn't have it.</summary>
    public bool Remove(uint field)
    {
        var parameter = Find(field);
        return parameter != null && _parameters.Remove(parameter);
    }

    /// <summary>Reads the material of a GEOM resource (an RCOL with one GEOM chunk); null for shader 0 (no material).</summary>
    public static GeomMaterial? Read(byte[] geomResource)
    {
        var (_, geom) = Parse(geomResource);
        if ((uint)geom.Shader == 0 || geom.Mtnf == null) return null;
        var keys = geom.TGIBlocks.Select(ResourceKey.Of).ToList();
        var parameters = geom.Mtnf.SData.Select(d => FromShaderData(d, keys)).ToList();
        return new GeomMaterial((uint)geom.Shader, parameters, keys);
    }

    /// <summary>
    /// Writes this material into <paramref name="geomResource" /> (normally the bytes it was read from) and returns the
    /// new resource bytes. The GEOM must have a material (shader not 0).
    /// </summary>
    public byte[] Write(byte[] geomResource)
    {
        var (rcol, geom) = Parse(geomResource);
        if ((uint)geom.Shader == 0 || geom.Mtnf == null) throw new InvalidDataException("This GEOM has no material (shader 0)");

        if ((uint)geom.Shader != _shader) geom.Shader = (ShaderType)_shader;
        var list = geom.Mtnf.SData;
        var built = _parameters.Select(p => ToShaderData(p, geom)).ToList();
        list.Clear();
        foreach (var data in built) list.Add(data);

        // Write the chunk from its fields, hand the bytes back to it (which marks it changed), then force the container
        // write, so nothing depends on s3pi's change tracking reaching the RCOL.
        using (var chunk = (MemoryStream)geom.UnParse())
            geom.AsBytes = chunk.ToArray();
        using var written = (Stream)ResourceUnParse.Invoke(rcol, null)!;
        written.Position = 0;
        using var copy = new MemoryStream();
        written.CopyTo(copy);
        return copy.ToArray();
    }

    static readonly MethodInfo ResourceUnParse =
        typeof(AResource).GetMethod("UnParse", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("AResource.UnParse not found; s3pi changed?");

    static (GenericRCOLResource Rcol, GEOM Geom) Parse(byte[] bytes)
    {
        var rcol = new GenericRCOLResource(0, new MemoryStream(bytes, writable: false));
        var geom = rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().FirstOrDefault()
            ?? throw new InvalidDataException("No GEOM chunk in this RCOL resource");
        return (rcol, geom);
    }

    static ShaderParameter FromShaderData(ShaderData data, IReadOnlyList<ResourceKey> keys)
    {
        var field = (uint)data.Field;
        return data switch
        {
            ElementFloat f => new(field, ShaderParameterKind.Float, [f.Data], 0, null, 0),
            ElementFloat2 f => new(field, ShaderParameterKind.Float2, [f.Data0, f.Data1], 0, null, 0),
            ElementFloat3 f => new(field, ShaderParameterKind.Float3, [f.Data0, f.Data1, f.Data2], 0, null, 0),
            ElementFloat4 f => new(field, ShaderParameterKind.Float4, [f.Data0, f.Data1, f.Data2, f.Data3], 0, null, 0),
            ElementInt i => new(field, ShaderParameterKind.Int, [], i.Data, null, 0),
            ElementTextureRef t => new(field, ShaderParameterKind.Texture, [], 0,
                t.Index >= 0 && t.Index < keys.Count ? keys[t.Index] : null, t.Index),
            ElementTextureKey k => new(field, ShaderParameterKind.TextureKey, [], 0, ResourceKey.Of(k.Data), 0),
            _ => throw new InvalidDataException($"Unsupported shader parameter type {data.GetType().Name}"),
        };
    }

    static ShaderData ToShaderData(ShaderParameter p, GEOM geom)
    {
        var field = (FieldType)p.Field;
        var f = p.Floats;
        return p.Kind switch
        {
            ShaderParameterKind.Float => new ElementFloat(0, null, field, f[0]),
            ShaderParameterKind.Float2 => new ElementFloat2(0, null, field, f[0], f[1]),
            ShaderParameterKind.Float3 => new ElementFloat3(0, null, field, f[0], f[1], f[2]),
            ShaderParameterKind.Float4 => new ElementFloat4(0, null, field, f[0], f[1], f[2], f[3]),
            ShaderParameterKind.Int => new ElementInt(0, null, field, p.IntValue),
            ShaderParameterKind.Texture => new ElementTextureRef(0, null, field, p.TextureIndex >= 0 ? p.TextureIndex : IndexOf(geom, p.Texture ?? default), geom.TGIBlocks),
            ShaderParameterKind.TextureKey => new ElementTextureKey(0, null, field, (p.Texture ?? default).Type, (p.Texture ?? default).Group, (p.Texture ?? default).Instance),
            _ => throw new InvalidOperationException($"Unknown kind {p.Kind}"),
        };
    }

    /// <summary>The key's index in the GEOM's key list, appending it when it isn't there yet.</summary>
    static int IndexOf(GEOM geom, ResourceKey key)
    {
        for (var i = 0; i < geom.TGIBlocks.Count; i++)
            if (ResourceKey.Of(geom.TGIBlocks[i]) == key) return i;
        geom.TGIBlocks.Add(new TGIBlock(0, null, key.Type, key.Group, key.Instance));
        return geom.TGIBlocks.Count - 1;
    }
}
