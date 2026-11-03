using CASPartResource;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using s3pi.GenericRCOLResource;
using GEOM = meshExpImp.ModelBlocks.GEOM;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>One GEOM of a LOD: a mesh group.</summary>
public sealed record MeshGroup(ResourceKey Key, int Vertices, int Triangles, uint Shader, bool InProject, bool HasVertexColour, bool VertexColourAllZero)
{
    /// <summary>The group's UV problems (<see cref="UvAnalysis" />); <see cref="UvReport.None" /> when it couldn't be read.</summary>
    public UvReport UvCheck { get; init; } = UvReport.None;
}

/// <summary>One VPXY LOD entry (EA numbers them from 1) and its mesh groups.</summary>
public sealed record MeshLod(int Id, IReadOnlyList<MeshGroup> Groups)
{
    public int Vertices => Groups.Sum(g => g.Vertices);
}

/// <summary>
/// An open project: its package's resources in memory, the parsed CASP to edit, and the mesh LODs. Resources the
/// package lacks are read from <see cref="Game" /> (a clone references EA textures; meshes are normally in the package).
/// <see cref="Save" /> rewrites the package from memory with the edited CASP, keeping the previous file as .bak.
/// Edits other than the CASP go through <see cref="Replace" />.
/// </summary>
public sealed class ProjectSession : IResourceSource
{
    const uint Casp = 0x034AEECB, Vpxy = 0x736884F1, Geom = 0x015A1849;

    readonly Dictionary<ResourceKey, byte[]> _resources = new();

    ProjectSession(string folder, Project project, IResourceSource game)
    {
        Folder = folder;
        Project = project;
        Game = game;
    }

    public string Folder { get; }
    public Project Project { get; private set; }
    public IResourceSource Game { get; }
    public string PackagePath => Path.Combine(Folder, Project.Package);
    public ResourceKey CaspKey { get; private set; }
    public CASPartResource.CASPartResource Part { get; private set; } = null!;
    public IReadOnlyList<MeshLod> Lods { get; private set; } = [];
    /// <summary>True when the CASP differs from the saved one, or another resource was replaced since the last save.</summary>
    public bool Dirty => _replaced || !Bytes(Part).AsSpan().SequenceEqual(_savedCasp);

    bool _replaced;
    byte[] _savedCasp = [];
    public IReadOnlyCollection<ResourceKey> PackageKeys => _resources.Keys;

    public static ProjectSession Open(string folder, IResourceSource game)
    {
        var project = Project.Read(folder);
        var session = new ProjectSession(folder, project, game);
        var package = s3pi.Package.Package.OpenPackage(0, session.PackagePath, false);
        try
        {
            foreach (var e in package.GetResourceList.Where(e => !e.IsDeleted))
            {
                using var s = ((s3pi.Package.Package)package).GetResource(e);
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                session._resources[ResourceKey.Of(e)] = copy.ToArray();
            }
        }
        finally { s3pi.Package.Package.ClosePackage(0, package); }
        session.CaspKey = ParseKey(project.Casp);
        session.Reload();
        return session;
    }

    /// <summary>Raw bytes of a resource: the project's copy, else the game's; null when neither has it.</summary>
    public byte[]? Read(ResourceKey key) => _resources.TryGetValue(key, out var b) ? b : Game.Read(key);

    public bool Contains(ResourceKey key) => _resources.ContainsKey(key) || Game.Contains(key);

    /// <summary>Kept for callers that edit <see cref="Part" />; <see cref="Dirty" /> compares the CASP itself.</summary>
    public void MarkChanged() { }

    /// <summary>Puts new bytes for a resource into the project (a mesh re-import, say). Written on <see cref="Save" />.</summary>
    public void Replace(ResourceKey key, byte[] bytes)
    {
        _resources[key] = bytes;
        _replaced = true;
        if (key.Type is Vpxy or Geom) ReadLods();
    }

    /// <summary>
    /// The project's settings that only apply to OpenTS3, by name. OSS3 writes no OpenTS3-only data yet (see
    /// <see cref="ProjectExport" />), so the list is empty; each such setting adds itself here and to
    /// <see cref="ChangeTarget" />'s removal.
    /// </summary>
    public IReadOnlyList<string> OpenTS3OnlySettings() => [];

    /// <summary>The settings changing the target to <paramref name="target" /> would remove; empty when nothing goes.</summary>
    public IReadOnlyList<string> SettingsRemovedBy(ProjectTarget target) =>
        ProjectTargets.RemovedSettings(Project.ParsedTarget, target, OpenTS3OnlySettings());

    /// <summary>
    /// Changes the project's target and writes project.json at once (the package is untouched). Callers ask first
    /// when <see cref="SettingsRemovedBy" /> is not empty; removing those settings belongs here once any exist.
    /// </summary>
    public void ChangeTarget(ProjectTarget target)
    {
        if (target == Project.ParsedTarget && Project.Target == target.Id()) return;
        Project = Project.WriteTarget(Folder, target);
    }

    /// <summary>The recolour's preset XML as an editable model.</summary>
    public Cas.PresetModel Preset(int index) => Cas.PresetModel.Parse(Part.Presets[index].XmlFile.ReadToEnd());

    /// <summary>Writes an edited preset model back into the CASP (saved with the project).</summary>
    public void SetPreset(int index, Cas.PresetModel model) => Part.Presets[index].XmlFile = new StringReader(model.ToXml());

    /// <summary>Copies a recolour to the end of the list with a new preset ID (one above the highest); returns its index.</summary>
    public int DuplicatePreset(int index)
    {
        var source = Part.Presets[index];
        var id = Part.Presets.Max(p => p.Unknown1) + 1;
        Part.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, source.XmlFile.ReadToEnd(), id));
        return Part.Presets.Count - 1;
    }

    /// <summary>Removes a recolour; the last one cannot be removed.</summary>
    public bool DeletePreset(int index)
    {
        if (Part.Presets.Count <= 1) return false;
        Part.Presets.RemoveAt(index);
        return true;
    }

    /// <summary>The LOD's mesh groups merged, for the viewport; null when none can be read.</summary>
    public GeomMesh? Mesh(MeshLod lod)
    {
        var meshes = new List<GeomMesh>();
        foreach (var g in lod.Groups)
            if (Read(g.Key) is { } bytes)
                try { meshes.Add(GeomLoader.Read(new MemoryStream(bytes), g.Key.ToString())); } catch (InvalidDataException) { }
        return meshes.Count == 0 ? null : GeomMesh.Merge(Project.Name, meshes);
    }

    /// <summary>
    /// Writes the package from memory (the edited CASP included) to a temporary file, then swaps it in, keeping the
    /// previous package as .bak, and updates project.json's Modified time. A failed write leaves the old package as is.
    /// </summary>
    public void Save()
    {
        _resources[CaspKey] = Bytes(Part);
        var temp = PackagePath + ".saving";
        var package = s3pi.Package.Package.NewPackage(0);
        try
        {
            foreach (var (key, bytes) in _resources)
                package.AddResource(new s3pi.Interfaces.TGIBlock(0, null, key.Type, key.Group, key.Instance), new MemoryStream(bytes, false), true)!.Compressed = 0xFFFF;
            package.SaveAs(temp);
        }
        finally { s3pi.Package.Package.ClosePackage(0, package); }
        if (File.Exists(PackagePath)) File.Replace(temp, PackagePath, PackagePath + ".bak");
        else File.Move(temp, PackagePath);
        Project = Project with { Modified = DateTime.UtcNow };
        Project.Write(Folder);
        _savedCasp = _resources[CaspKey];
        _replaced = false;
    }

    void Reload()
    {
        Part = new CASPartResource.CASPartResource(0, new MemoryStream(Read(CaspKey) ?? throw new InvalidDataException($"{CaspKey} is not in the project")));
        ReadLods();
        _savedCasp = Bytes(Part);
        _replaced = false;
    }

    void ReadLods()
    {
        var vpxyKey = Part.TGIBlocks.Select(ResourceKey.Of).FirstOrDefault(k => k.Type == Vpxy);
        if (vpxyKey.Type == 0 || Read(vpxyKey) is not { } vpxyBytes) { Lods = []; return; }
        var vpxy = new GenericRCOLResource(0, new MemoryStream(vpxyBytes)).ChunkEntries.Select(c => c.RCOLBlock).OfType<VPXY>().FirstOrDefault();
        if (vpxy == null) { Lods = []; return; }
        Lods = vpxy.Entries.OfType<VPXY.Entry00>().GroupBy(e => (int)e.EntryID).OrderBy(g => g.Key).Select(lod => new MeshLod(lod.Key,
                lod.SelectMany(e => (IEnumerable<int>)e.TGIIndexes)
                    .Select(i => i >= 0 && i < vpxy.TGIBlocks.Count ? ResourceKey.Of(vpxy.TGIBlocks[i]) : default)
                    .Where(k => k.Type == Geom).Distinct().Select(Describe).ToList()))
            .ToList();
    }

    MeshGroup Describe(ResourceKey key)
    {
        var bytes = Read(key);
        if (bytes == null) return new MeshGroup(key, 0, 0, 0, false, false, false);
        try
        {
            var geom = new GenericRCOLResource(0, new MemoryStream(bytes)).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().First();
            var colours = geom.VertexData.SelectMany(v => v.Vertex.OfType<GEOM.ColorElement>()).Select(ColourOf).ToList();
            return new MeshGroup(key, geom.VertexData.Count, geom.Faces.Count, (uint)geom.Shader, _resources.ContainsKey(key),
                colours.Count > 0, colours.Count > 0 && colours.All(c => c == 0)) { UvCheck = CheckUvs(key, bytes) };
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or System.Reflection.TargetInvocationException)
        {
            return new MeshGroup(key, 0, 0, 0, _resources.ContainsKey(key), false, false);
        }
    }

    static UvReport CheckUvs(ResourceKey key, byte[] bytes)
    {
        try { return UvAnalysis.Analyze(GeomLoader.Read(new MemoryStream(bytes), key.ToString())); }
        catch (InvalidDataException) { return UvReport.None; }
    }

    // s3pi's ColorElement has no public getter; the hair shaders read this ARGB (see research/undocumented-fields.md).
    static readonly System.Reflection.FieldInfo ArgbField =
        typeof(GEOM.ColorElement).GetField("argb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    public static uint ColourOf(GEOM.ColorElement c) => (uint)ArgbField.GetValue(c)!;

    static byte[] Bytes(s3pi.Interfaces.IResource resource)
    {
        using var copy = new MemoryStream();
        resource.Stream.CopyTo(copy);
        return copy.ToArray();
    }

    static ResourceKey ParseKey(string text)
    {
        var p = text.Split(':');
        return new ResourceKey(Convert.ToUInt32(p[0], 16), Convert.ToUInt32(p[1], 16), Convert.ToUInt64(p[2], 16));
    }
}
