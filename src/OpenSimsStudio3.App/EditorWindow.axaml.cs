using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CASPartResource;
using OpenSimsStudio3.App.Viewport;
using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Rendering;

namespace OpenSimsStudio3.App;

/// <summary>
/// The project editor. Every property edit goes straight to the session's CASP and marks the project unsaved; Save
/// writes the package (keeping a .bak). The outline lists recolours, mesh LODs and groups, morph links and warnings;
/// the inspector shows the selected entry. Recolour and mesh editing come in later slices.
/// </summary>
public partial class EditorWindow : Window
{
    // Outfit category bits by game name (Sims3.SimIFace.CAS.OutfitCategories; see research/undocumented-fields.md).
    static readonly (uint Bit, string Label)[] HumanCategories =
    {
        (0x1, "Naked"), (0x2, "Everyday"), (0x4, "Formalwear"), (0x8, "Sleepwear"), (0x10, "Swimwear"), (0x20, "Athletic"),
        (0x40000, "Outerwear"), (0x100, "Career"), (0x200, "Aux career"), (0x80, "Martial arts"), (0x400, "Makeover"),
        (0x40, "Singed"), (0x800, "Skinny dip towel"), (0x8000, "Massage"), (0x10000, "Child imagination"),
        (0x20000, "Special"), (0x80000, "Supernatural"),
    };
    static readonly (uint Bit, string Label)[] HorseCategories = { (0x1, "Naked"), (0x2, "Everyday"), (0x1000, "Racing"), (0x2000, "Jumping"), (0x4000, "Bridle") };
    static readonly (uint Bit, string Label)[] PetCategories = { (0x1, "Naked"), (0x2, "Everyday") };
    static readonly (uint Bit, string Label)[] FlagBits =
    {
        (0x100000, "Valid for maternity"), (0x200000, "Valid for random"), (0x400000, "Is hat"), (0x800000, "Is revealing"), (0x1000000, "Hidden in CAS"),
    };
    // Shader IDs are FNV-32 of the lower-cased name (research/undocumented-fields.md, "GEOM tangents").
    static readonly Dictionary<uint, string> ShaderNames = new()
    {
        [0x84FD7152] = "SimHair", [0xFCF80CE1] = "CasSimHair", [0x548394B9] = "SimSkin", [0x05F75EF1] = "SimHorse",
        [0xA78F71C4] = "SimPetFur", [0x93B210C6] = "SimPetFace", [0xCF8A70B4] = "SimEyes", [0x5EDA9CDE] = "SimGlass",
    };

    readonly ProjectSession _session;
    RecolourPanel _recolours = null!;
    readonly OffscreenMeshRenderer _renderer = new();
    readonly MeshViewport _viewport;
    bool _loading;
    bool _closingConfirmed;

    /// <summary>Raised after the window closes, so the start screen can come back.</summary>
    public event Action? ProjectClosed;

    public EditorWindow() : this(null!) { }

    public EditorWindow(ProjectSession session)
    {
        InitializeComponent();
        _session = session;
        var logo = new Bitmap(AssetLoader.Open(new Uri("avares://OpenSimsStudio3/Assets/oss3_icon.png")));
        Icon = new WindowIcon(logo);
        Logo.Source = logo;
        _viewport = new MeshViewport(_renderer);
        _viewport.FrameRendered += () => ViewportStatus.Text = $"{_renderer.DeviceName} (Vulkan) · frame {_viewport.LastFrameMs:F1} ms";
        ViewportHost.Content = _viewport;
        if (session == null) return;

        Title = $"OpenSimsStudio³ · {session.Project.Name}";
        ProjectTitle.Text = session.Project.Name;
        TargetText.Text = session.Project.Target switch { "OpenTS3Only" => "OpenTS3 only", var t => t };
        KindText.Text = session.Project.Kind == "CasDefaultReplacement" ? "Default replacement" : "New item";

        SaveButton.Click += (_, _) => Save();
        SaveItem.Click += (_, _) => Save();
        CloseItem.Click += (_, _) => Close();
        ResetViewItem.Click += (_, _) => _viewport.ResetCamera();
        CameraBox.ItemsSource = new[] { "Free", "Front", "Side", "Close-up" };
        CameraBox.SelectedIndex = 0;
        CameraBox.SelectionChanged += (_, _) => { if (CameraBox.SelectedIndex >= 0 && (int)_viewport.View != CameraBox.SelectedIndex) _viewport.View = (CameraView)CameraBox.SelectedIndex; };
        _viewport.ViewChanged += view => CameraBox.SelectedIndex = (int)view;
        FrontViewItem.Click += (_, _) => CameraBox.SelectedIndex = (int)CameraView.Front;
        SideViewItem.Click += (_, _) => CameraBox.SelectedIndex = (int)CameraView.Side;
        CloseUpViewItem.Click += (_, _) => CameraBox.SelectedIndex = (int)CameraView.CloseUp;
        WholeSimBox.IsCheckedChanged += (_, _) => ShowLod();
        WeightSlider.ValueChanged += (_, _) => { _shape = _shape with { Weight = (float)WeightSlider.Value }; Reshape(); };
        FitSlider.ValueChanged += (_, _) => { _shape = _shape with { Fitness = (float)FitSlider.Value }; Reshape(); };
        PregnantSlider.ValueChanged += (_, _) => { _shape = _shape with { Pregnancy = (float)PregnantSlider.Value }; Reshape(); };
        ShapeReset.Click += (_, _) => (WeightSlider.Value, FitSlider.Value, PregnantSlider.Value) = (0, 0, 0);
        KeyDown += (_, e) => { if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) Save(); };
        LodBox.SelectionChanged += (_, _) => ShowLod();
        Closing += OnClosing;
        Closed += (_, _) => { _renderer.Dispose(); ProjectClosed?.Invoke(); };

        BuildProperties();
        _recolours = new RecolourPanel(session);
        _recolours.Changed += () => { UpdateSaveState(); BuildOutline(); ComposePreview(_recolours.SelectedIndex); };
        _recolours.Shown += ComposePreview;
        RecolourHost.Content = _recolours;
        BuildOutline();
        LodBox.ItemsSource = session.Lods.Select(l => $"LOD {l.Id} · {l.Vertices:N0} vertices · {l.Groups.Count} group{(l.Groups.Count == 1 ? "" : "s")}").ToList();
        LodBox.SelectedIndex = session.Lods.Count > 0 ? 0 : -1;
        LodBox.IsVisible = session.Lods.Count > 0;
        if (session.Lods.Count == 0) ViewportStatus.Text = "This part has no mesh (overlay or texture only)";
        if (session.Lods.Count > 0) ShowLodInfo(session.Lods[0]);
        ComposePreview(_recolours.SelectedIndex);
        UpdateSaveState();
    }

    CASPartResource.CASPartResource Part => _session.Part;

    int _composeGeneration;

    /// <summary>
    /// Composes the recolour's diffuse with OSS3's XML compositor (Formats/Compositing; a preview, not the
    /// game's own compiler) on a worker thread, then shows it on the mesh and in the recolour block. A newer request
    /// supersedes an older one still running. EA-derived pixels stay in memory.
    /// </summary>
    async void ComposePreview(int index)
    {
        if (index < 0 || index >= _session.Part.Presets.Count) return;
        var generation = ++_composeGeneration;
        var xml = _session.Part.Presets[index].XmlFile.ReadToEnd();
        var (texture, reason) = await Task.Run(() =>
        {
            try
            {
                var ok = OpenSimsStudio3.Formats.Compositing.CasCompositor.TryComposeDiffuse(_session, xml, out var t, out var r, 1024);
                return (ok ? t : null, r);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or System.Xml.XmlException or IndexOutOfRangeException)
            {
                return (null, error.Message);
            }
        });
        if (generation != _composeGeneration) return;
        if (texture == null)
        {
            _viewport.ClearTexture();
            _recolours.SetPreview(null, "No preview: " + (reason ?? "this recolour can't be composed yet"));
            return;
        }
        _viewport.SetTexture(texture.Width, texture.Height, texture.Rgba);
        _recolours.SetPreview(ToBitmap(texture), $"Preview ({texture.Complate}, {texture.Width}×{texture.Height}, {texture.Elapsed.TotalMilliseconds:F0} ms). " +
                                                 "OSS3's preview compositor; the game's own may differ.");
    }

    static Avalonia.Media.Imaging.WriteableBitmap ToBitmap(OpenSimsStudio3.Formats.Compositing.CompositedTexture t)
    {
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(t.Width, t.Height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul);
        using var target = bitmap.Lock();
        for (var y = 0; y < t.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(t.Rgba, y * t.Width * 4, target.Address + y * target.RowBytes, t.Width * 4);
        return bitmap;
    }

    // EA's nude body for the part's age and gender, read once on a worker thread (see SimBody), and the shown LOD with
    // its morphs. The body-shape sliders re-shape both.
    Task<IReadOnlyList<MorphedMesh>>? _body;
    MorphedMesh? _lodMesh;
    BodyShape _shape;

    async void ShowLod()
    {
        _lodMesh = null;
        if (LodBox.SelectedIndex < 0 || LodBox.SelectedIndex >= _session.Lods.Count) return;
        if (_session.Mesh(_session.Lods[LodBox.SelectedIndex]) is not { } mesh) return;
        _lodMesh = MorphedMesh.Load(_session, _session.Part, mesh);
        var missing = Enum.GetValues<BlendGeometry.Morph>().Where(m => !_lodMesh.Has(m))
            .Select(m => m == BlendGeometry.Morph.Special ? "pregnancy" : m.ToString().ToLowerInvariant()).ToList();
        ShapeNote.Text = missing.Count == 0 ? "" : $"This LOD has no {string.Join(", ", missing)} morph.";
        ShapeNote.IsVisible = missing.Count > 0;
        if (WholeSimBox.IsChecked == true)
        {
            var part = _session.Part;
            _body ??= Task.Run(() => { try { return SimBody.Around(_session.Game, part); } catch (InvalidDataException) { return (IReadOnlyList<MorphedMesh>)[]; } });
            await _body;
        }
        Reshape();
    }

    /// <summary>Shows the LOD, and the body when Whole Sim is on, at the sliders' body shape.</summary>
    void Reshape()
    {
        if (_lodMesh == null) return;
        GeomMesh? body = null;
        if (WholeSimBox.IsChecked == true && _body is { IsCompletedSuccessfully: true, Result: { Count: > 0 } parts })
            body = GeomMesh.Merge("Sim body", parts.Select(p => p.Apply(_shape)).ToList());
        _viewport.ShowMesh(_lodMesh.Apply(_shape), body);
    }

    // ---- Item properties ----

    void BuildProperties()
    {
        _loading = true;
        InternalName.Text = Part.InternalName();
        InternalName.TextChanged += (_, _) => Edit(() => Part.Unknown1 = InternalName.Text ?? "");

        DataTypeBox.ItemsSource = Enum.GetValues<DataTypeFlags>().Select(v => v.ToString()).ToList();
        DataTypeBox.SelectedItem = Part.DataType.ToString();
        DataTypeBox.SelectionChanged += (_, _) => Edit(() => { if (DataTypeBox.SelectedItem is string s) Part.DataType = Enum.Parse<DataTypeFlags>(s); });

        ClothingBox.ItemsSource = Enum.GetValues<ClothingType>().Select(v => v.ToString()).Distinct().ToList();
        ClothingBox.SelectedItem = Part.Clothing.ToString();
        ClothingBox.SelectionChanged += (_, _) => Edit(() => { if (ClothingBox.SelectedItem is string s) Part.Clothing = Enum.Parse<ClothingType>(s); });

        SpeciesBox.ItemsSource = Enum.GetValues<SpeciesType>().Select(v => v.ToString()).ToList();
        SpeciesBox.SelectedItem = Part.AgeGender.Species.ToString();
        SpeciesBox.SelectionChanged += (_, _) =>
        {
            Edit(() => { if (SpeciesBox.SelectedItem is string s) Part.AgeGender.Species = Enum.Parse<SpeciesType>(s); });
            BuildCategories();
        };

        foreach (var age in Enum.GetValues<AgeFlags>())
            Ages.Children.Add(Check(age switch { AgeFlags.YoungAdult => "Young adult", _ => age.ToString() }, Part.AgeGender.Age.HasFlag(age),
                on => Part.AgeGender.Age = on ? Part.AgeGender.Age | age : Part.AgeGender.Age & ~age));
        foreach (var gender in Enum.GetValues<GenderFlags>())
            Genders.Children.Add(Check(gender.ToString(), Part.AgeGender.Gender.HasFlag(gender),
                on => Part.AgeGender.Gender = on ? Part.AgeGender.Gender | gender : Part.AgeGender.Gender & ~gender));
        foreach (var hand in Enum.GetValues<HandednessFlags>())
            Handedness.Children.Add(Check(hand == HandednessFlags.LeftHanded ? "Left" : "Right", Part.AgeGender.Handedness.HasFlag(hand),
                on => Part.AgeGender.Handedness = on ? Part.AgeGender.Handedness | hand : Part.AgeGender.Handedness & ~hand));

        foreach (var (bit, label) in FlagBits)
            Flags.Children.Add(Check(label, ((uint)Part.ClothingCategory & bit) != 0, on => SetCategoryBit(bit, on)));
        var space = Check("Own texture space", Part.UsesUniqueTextureSpace(), on => Part.SetUsesUniqueTextureSpace(on));
        ToolTip.SetTip(space, "Gives this part its own 0–1 UV space in the Sim's texture atlas instead of sharing the body layout. " +
                              "Costs resolution: the region is split, so each part gets fewer pixels.");
        Flags.Children.Add(space);
        BuildCategories();
        _loading = false;
    }

    void BuildCategories()
    {
        var wasLoading = _loading;
        _loading = true;
        Categories.Children.Clear();
        var list = Part.AgeGender.Species switch
        {
            SpeciesType.Human or SpeciesType.None => HumanCategories,
            SpeciesType.Horse => HorseCategories,
            _ => PetCategories,
        };
        foreach (var (bit, label) in list)
            Categories.Children.Add(Check(label, ((uint)Part.ClothingCategory & bit) != 0, on => SetCategoryBit(bit, on)));
        _loading = wasLoading;
    }

    void SetCategoryBit(uint bit, bool on) =>
        Part.ClothingCategory = (ClothingCategoryFlags)(on ? (uint)Part.ClothingCategory | bit : (uint)Part.ClothingCategory & ~bit);

    CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, FontSize = 13 };
        box.IsCheckedChanged += (_, _) => Edit(() => set(box.IsChecked == true));
        return box;
    }

    void Edit(Action change)
    {
        if (_loading) return;
        try { change(); }
        catch (ArgumentOutOfRangeException error)
        {
            // s3pi's flag setters reject values outside their masks; leave the CASP as it was.
            SaveState.Text = "That value isn't valid for this field";
            Console.Error.WriteLine($"OSS3: edit rejected: {error.Message}");
            return;
        }
        _session.MarkChanged();
        UpdateSaveState();
        BuildOutline();
    }

    // ---- Outline and inspector ----

    // The outline's group entries, so a warning can jump to its group.
    readonly Dictionary<(int Lod, int Group), (Button Entry, Action Show)> _groupEntries = new();

    void BuildOutline()
    {
        Outline.Children.Clear();
        _groupEntries.Clear();
        Section("RECOLOURS");
        for (var i = 0; i < Part.Presets.Count; i++)
        {
            var index = i;
            Entry($"Recolour {i + 1}", () => ShowRecolour(index));
        }
        if (Part.Presets.Count == 0) Note("None");

        Section("MESH");
        foreach (var lod in _session.Lods)
        {
            Entry($"LOD {lod.Id}  ·  {lod.Vertices:N0} v", () => ShowLodInfo(lod));
            for (var g = 0; g < lod.Groups.Count; g++)
            {
                var group = lod.Groups[g];
                var index = g;
                Action show = () => ShowGroup(lod, index, group);
                _groupEntries[(lod.Id, g)] = (Entry($"    Group {g}  ·  {group.Vertices:N0} v", show), show);
            }
        }
        if (_session.Lods.Count == 0) Note("No mesh");

        Section("MORPHS");
        foreach (var (label, index) in new[] { ("Fat", Part.BlendInfoFatIndex), ("Fit", Part.BlendInfoFitIndex), ("Thin", Part.BlendInfoThinIndex), ("Special", Part.BlendInfoSpecialIndex) })
        {
            var key = index < Part.TGIBlocks.Count ? ResourceKey.Of(Part.TGIBlocks[index]) : default;
            var present = key.Type != 0 && _session.Read(key) != null;
            Outline.Children.Add(new CheckBox { Content = label, IsChecked = present, IsEnabled = false, Margin = new Thickness(10, 2), FontSize = 13 });
        }

        var warnings = Warnings();
        Section($"WARNINGS{(warnings.Count > 0 ? $" ({warnings.Count})" : "")}");
        if (warnings.Count == 0) Note("None");
        var warn = (IBrush)Application.Current!.FindResource("Warn")!;
        foreach (var w in warnings)
        {
            if (w.Group is not { } at || !_groupEntries.ContainsKey(at))
            {
                Outline.Children.Add(new TextBlock { Text = "⚠ " + w.Text, Foreground = warn, FontSize = 12, Margin = new Thickness(10, 2), TextWrapping = TextWrapping.Wrap });
                continue;
            }
            // Jumps to the group it applies to.
            var jump = new Button { Content = "⚠ " + w.Text, Classes = { "navigation" }, Foreground = warn, Height = 30, MinHeight = 30, FontSize = 12, Padding = new Thickness(10, 2) };
            if (w.Tip != null) ToolTip.SetTip(jump, w.Tip);
            jump.Click += (_, _) => { if (_groupEntries.TryGetValue(at, out var entry)) Select(entry.Entry, entry.Show); };
            Outline.Children.Add(jump);
        }
    }

    /// <summary>One line in the warnings list; <see cref="Group" /> is the (LOD, group) it jumps to, if any.</summary>
    sealed record Warning(string Text, (int Lod, int Group)? Group = null, string? Tip = null);

    // Hair layers its strands over the same texture on purpose, so its UV overlaps are not warned about.
    static bool IsHairShader(uint shader) => ShaderNames.TryGetValue(shader, out var name) && name.Contains("Hair", StringComparison.Ordinal);

    List<Warning> Warnings()
    {
        var list = new List<Warning>();
        if (((uint)Part.ClothingCategory & 0xFFFFF) == 0) list.Add(new("No outfit categories: the part shows in no CAS category."));
        if (Part.AgeGender.Age == 0) list.Add(new("No ages ticked."));
        if (Part.AgeGender.Gender == 0) list.Add(new("No genders ticked."));
        foreach (var lod in _session.Lods)
            for (var g = 0; g < lod.Groups.Count; g++)
            {
                var group = lod.Groups[g];
                if (group.Vertices == 0) list.Add(new($"LOD {lod.Id} group {g}: mesh missing or unreadable.", (lod.Id, g)));
                if (group.VertexColourAllZero) list.Add(new($"LOD {lod.Id} group {g}: vertex colour is all zero, which renders hair black.", (lod.Id, g)));
                foreach (var issue in group.UvCheck.Warnings(IsHairShader(group.Shader)))
                    list.Add(new($"{UvWording.Label(issue)} · LOD {lod.Id}, Group {g}", (lod.Id, g), UvWording.Tip(issue)));
            }
        return list;
    }

    void Section(string title) =>
        Outline.Children.Add(new TextBlock { Text = title, Classes = { "eyebrow" }, Margin = new Thickness(10, Outline.Children.Count == 0 ? 0 : 14, 0, 4) });

    void Note(string text) =>
        Outline.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Muted, Margin = new Thickness(10, 2) });

    Button Entry(string text, Action select)
    {
        var button = new Button { Content = text, Classes = { "navigation" }, Height = 34, Margin = new Thickness(0), FontSize = 13 };
        button.Click += (_, _) => Select(button, select);
        Outline.Children.Add(button);
        return button;
    }

    void Select(Button entry, Action show)
    {
        foreach (var b in Outline.Children.OfType<Button>()) b.Classes.Remove("selected");
        entry.Classes.Add("selected");
        entry.BringIntoView();
        show();
    }

    void ShowRecolour(int index)
    {
        _recolours.Select(index);
        var preset = Part.Presets[index];
        Inspect($"Recolour {index + 1}",
            ("Preset ID", preset.Unknown1.ToString()),
            ("Preset XML", $"{preset.XmlFile.ReadToEnd().Length:N0} characters"));
        Inspector.Children.Add(Muted12("Pattern, channel and texture editing come in the recolour slice."));
    }

    void ShowLodInfo(MeshLod lod)
    {
        LodBox.SelectedIndex = _session.Lods.ToList().IndexOf(lod);
        Inspect($"LOD {lod.Id}", ("Groups", lod.Groups.Count.ToString()), ("Vertices", lod.Vertices.ToString("N0")),
            ("Triangles", lod.Groups.Sum(g => g.Triangles).ToString("N0")));
    }

    void ShowGroup(MeshLod lod, int index, MeshGroup group)
    {
        LodBox.SelectedIndex = _session.Lods.ToList().IndexOf(lod);
        Inspect($"LOD {lod.Id} · Group {index}",
            ("Key", group.Key.ToString()),
            ("Vertices", group.Vertices.ToString("N0")),
            ("Triangles", group.Triangles.ToString("N0")),
            ("Shader", ShaderNames.TryGetValue(group.Shader, out var n) ? n : $"0x{group.Shader:X8}"),
            ("Vertex colour", !group.HasVertexColour ? "none (engine default)" : group.VertexColourAllZero ? "all zero (renders black)" : "present"),
            ("Stored in", group.InProject ? "this project" : "the game"),
            ("UVs", group.UvCheck.Channels == 0 && group.UvCheck.IsClean ? "none" : group.UvCheck.IsClean
                ? $"{group.UvCheck.Channels} set{(group.UvCheck.Channels == 1 ? "" : "s")}, no problems"
                : string.Join(", ", group.UvCheck.Issues.Select(i => $"{UvWording.Label(i)} ({i.Count:N0})"))));
        var showUvs = new Button { Content = "Show UVs", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        showUvs.Click += (_, _) =>
        {
            GeomMesh mesh;
            try
            {
                if (_session.Read(group.Key) is not { } bytes) return;
                mesh = GeomLoader.Read(new MemoryStream(bytes), group.Key.ToString());
            }
            catch (InvalidDataException) { return; }
            new UvWindow($"LOD {lod.Id} · Group {index}", mesh, group.UvCheck).Show(this);
        };
        showUvs.IsEnabled = group.UvCheck.Channels > 0;
        Inspector.Children.Add(showUvs);

        // Edit in Blender: the group goes to <project>\work as .simgeom; the user exports back over the same file.
        var file = Path.Combine(_session.Folder, "work", $"LOD{lod.Id}_group{index}.simgeom");
        var edit = new Button { Content = "Edit in Blender", Classes = { "primary" }, FontSize = 14, Padding = new Thickness(14, 6) };
        var reimport = new Button { Content = "Re-import", Padding = new Thickness(14, 6) };
        var status = Muted12(File.Exists(file) ? $"Working file: {file}" : "Opens this group in Blender with GEOM Tools. Export back over the same file, then re-import.");
        reimport.IsEnabled = File.Exists(file);
        edit.Click += (_, _) =>
        {
            try
            {
                MeshExchange.Export(_session, group.Key, file);
                _workFiles[file] = File.GetLastWriteTimeUtc(file);
                status.Text = BlenderLauncher.Open(file, AppSettings.Current.BlenderPath) is { } blender
                    ? $"Opened in Blender {blender.Version}. Export with Sims 3 GEOM (.simgeom) over {file}, then press Re-import."
                    : $"Blender with GEOM Tools was not found. Edit {file} in your Blender and export over it, then press Re-import.";
                reimport.IsEnabled = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                status.Text = "Could not export: " + error.Message;
            }
        };
        reimport.Click += async (_, _) =>
        {
            if (_workFiles.TryGetValue(file, out var exported) && File.GetLastWriteTimeUtc(file) <= exported)
            {
                status.Text = "The file hasn't changed since it was exported. Export from Blender over it first.";
                return;
            }
            try
            {
                var result = MeshExchange.Import(_session, group.Key, file);
                _workFiles[file] = File.GetLastWriteTimeUtc(file);
                BuildOutline();
                ShowLod();
                UpdateSaveState();
                var summary = $"Vertices {result.VerticesBefore:N0} → {result.VerticesAfter:N0}, triangles {result.TrianglesBefore:N0} → {result.TrianglesAfter:N0}.";
                await ConfirmDialog.Ask(this, "Mesh re-imported",
                    result.Warnings.Count == 0 ? summary : summary + "\n\n" + string.Join("\n\n", result.Warnings.Select(w => "⚠ " + w)), "OK");
                var updated = _session.Lods.First(l => l.Id == lod.Id);
                if (index < updated.Groups.Count) ShowGroup(updated, index, updated.Groups[index]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                status.Text = "Could not re-import: " + error.Message;
            }
        };
        Inspector.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { edit, reimport } });
        Inspector.Children.Add(status);
        var blender = OpenSimsStudio3.Formats.Install.ToolInstalls.PreferredBlender(AppSettings.Current.BlenderPath);
        Inspector.Children.Add(Muted12(blender == null
            ? "No Blender with GEOM Tools found. Choose one in Settings on the start screen."
            : $"Opens in Blender {blender.Version}. Change the default in Settings on the start screen."));
    }

    readonly Dictionary<string, DateTime> _workFiles = new(StringComparer.OrdinalIgnoreCase);

    void Inspect(string title, params (string Key, string Value)[] rows)
    {
        Inspector.Children.Clear();
        Inspector.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        foreach (var (key, value) in rows)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = key.ToUpperInvariant(), Classes = { "eyebrow" } });
            row.Children.Add(new SelectableTextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            Inspector.Children.Add(row);
        }
    }

    static TextBlock Muted12(string text) => new() { Text = text, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    static IBrush Muted => (IBrush)Application.Current!.FindResource("Muted")!;

    // ---- Save and close ----

    void Save()
    {
        try
        {
            _session.Save();
            SaveState.Text = $"Saved {DateTime.Now:HH:mm}";
            Console.Error.WriteLine($"OSS3: saved {_session.PackagePath}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveState.Text = "Could not save: " + error.Message;
            Console.Error.WriteLine($"OSS3: saving {_session.PackagePath} failed: {error}");
        }
    }

    void UpdateSaveState() => SaveState.Text = _session.Dirty ? "Unsaved changes" : SaveState.Text == "Unsaved changes" ? "" : SaveState.Text;

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !_session.Dirty) return;
        e.Cancel = true;
        var choice = await ConfirmDialog.Ask(this, "Save changes?", $"{_session.Project.Name} has unsaved changes.", "Save", "Don't save", "Cancel");
        if (choice == 2) return;
        if (choice == 0) Save();
        if (choice == 0 && _session.Dirty) return;
        _closingConfirmed = true;
        Close();
    }
}
