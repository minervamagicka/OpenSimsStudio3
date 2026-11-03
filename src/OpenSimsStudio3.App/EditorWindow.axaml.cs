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
    // Outfit category bits by species are in Formats (OutfitCategories), shared with the checks.
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
        BuildTargetPicker();
        KindText.Text = session.Project.Kind == "CasDefaultReplacement" ? "Default replacement" : "New item";

        SaveButton.Click += (_, _) => Save();
        SaveItem.Click += (_, _) => Save();
        CloseItem.Click += (_, _) => Close();
        ExportItem.Click += (_, _) => OpenExport();
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
        KeyDown += (_, e) => { if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control)) OpenExport(); };
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

    bool _targetReverting;

    /// <summary>
    /// The title bar's target picker (DESIGN.md, "Project target"). A change is written to project.json at once; when it
    /// would remove OpenTS3-only settings, it lists them and asks first. OpenTS3OnlyGate controls follow the target.
    /// </summary>
    void BuildTargetPicker()
    {
        TargetBox.ItemsSource = ProjectTargets.All.Select(t => t.DisplayName()).ToList();
        ShowTarget();
        TargetBox.SelectionChanged += async (_, _) =>
        {
            if (_targetReverting || TargetBox.SelectedIndex < 0) return;
            var next = ProjectTargets.All[TargetBox.SelectedIndex];
            if (next == _session.Project.ParsedTarget) return;
            var removed = _session.SettingsRemovedBy(next);
            if (removed.Count > 0 && await ConfirmDialog.Ask(this, $"Change target to {next.DisplayName()}?",
                    "These OpenTS3-only settings will be removed:\n" + string.Join("\n", removed.Select(r => "• " + r)),
                    "Change and remove", "Cancel") != 0)
            {
                ShowTarget();
                return;
            }
            try { _session.ChangeTarget(next); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"OSS3: changing the target of {_session.Folder} failed: {error}");
                await ConfirmDialog.Ask(this, "Could not change the target", error.Message, "OK");
            }
            ShowTarget();
        };
    }

    void ShowTarget()
    {
        var target = _session.Project.ParsedTarget;
        _targetReverting = true;
        TargetBox.SelectedIndex = ProjectTargets.All.ToList().IndexOf(target);
        _targetReverting = false;
        OpenTS3OnlyGate.SetTarget(this, target);
    }

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
        foreach (var (bit, label) in OutfitCategories.For(Part.AgeGender.Species))
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

    // The outline's entries by what they show (a recolour, a LOD, a mesh group) and its section headings, so a warning
    // or an export check can jump to them (ShowCheck).
    readonly Dictionary<(CheckTargetKind Kind, int A, int B), (Button Entry, Action Show)> _entries = new();
    readonly Dictionary<CheckTargetKind, Control> _sections = new();

    void BuildOutline()
    {
        Outline.Children.Clear();
        _entries.Clear();
        _sections.Clear();
        _sections[CheckTargetKind.Recolour] = Section("RECOLOURS");
        for (var i = 0; i < Part.Presets.Count; i++)
        {
            var index = i;
            Action show = () => ShowRecolour(index);
            _entries[(CheckTargetKind.Recolour, i, -1)] = (Entry($"Recolour {i + 1}", show), show);
        }
        if (Part.Presets.Count == 0) Note("None");

        _sections[CheckTargetKind.Mesh] = Section("MESH");
        foreach (var lod in _session.Lods)
        {
            Action showLod = () => ShowLodInfo(lod);
            _entries[(CheckTargetKind.Lod, lod.Id, -1)] = (Entry($"LOD {lod.Id}  ·  {lod.Vertices:N0} v", showLod), showLod);
            for (var g = 0; g < lod.Groups.Count; g++)
            {
                var group = lod.Groups[g];
                var index = g;
                Action show = () => ShowGroup(lod, index, group);
                _entries[(CheckTargetKind.MeshGroup, lod.Id, g)] = (Entry($"    Group {g}  ·  {group.Vertices:N0} v", show), show);
            }
        }
        if (_session.Lods.Count == 0) Note("No mesh");

        _sections[CheckTargetKind.Morphs] = Section("MORPHS");
        foreach (var morph in Enum.GetValues<BlendGeometry.Morph>())
        {
            var state = PartChecks.Morph(_session, Part, morph);
            var name = PartChecks.MorphName(morph);
            Outline.Children.Add(new CheckBox
            {
                Content = char.ToUpperInvariant(name[0]) + name[1..] + (state == MorphState.Broken ? " (broken link)" : ""),
                IsChecked = state == MorphState.Present, IsEnabled = false, Margin = new Thickness(10, 2), FontSize = 13,
            });
        }

        // The same checks as the export window (PartChecks); each line jumps to where it applies.
        var warnings = PartChecks.Run(_session, gameChecked: true);
        Section($"WARNINGS{(warnings.Count > 0 ? $" ({warnings.Count})" : "")}");
        if (warnings.Count == 0) Note("None");
        foreach (var w in warnings)
        {
            var brush = (IBrush)Application.Current!.FindResource(w.Severity == CheckSeverity.Error ? "Danger" : "Warn")!;
            var jump = new Button
            {
                Content = new TextBlock { Text = "⚠ " + w.Label, TextWrapping = TextWrapping.Wrap }, Classes = { "navigation" }, Foreground = brush,
                MinHeight = 30, FontSize = 12, Padding = new Thickness(10, 2),
            };
            if (w.Tip != null) ToolTip.SetTip(jump, w.Tip);
            var target = w.Target;
            jump.Click += (_, _) => ShowCheck(target);
            Outline.Children.Add(jump);
        }
    }

    /// <summary>
    /// Goes to where a check applies: selects its recolour, LOD or mesh group in the outline, or scrolls to the item
    /// properties, the mesh or the morphs. Used by the Warnings list and by the export window's Show.
    /// </summary>
    public void ShowCheck(CheckTarget target)
    {
        if (!target.IsEditor) return;
        var key = target.Kind switch
        {
            CheckTargetKind.MeshGroup => (target.Kind, target.Lod, target.Group),
            CheckTargetKind.Lod => (target.Kind, target.Lod, -1),
            CheckTargetKind.Recolour => (target.Kind, target.Recolour, -1),
            _ => (CheckTargetKind.None, 0, 0),
        };
        if (_entries.TryGetValue(key, out var entry)) Select(entry.Entry, entry.Show);
        else if (target.Kind == CheckTargetKind.Properties) Categories.BringIntoView();
        else if (_sections.TryGetValue(target.Kind == CheckTargetKind.Lod || target.Kind == CheckTargetKind.MeshGroup ? CheckTargetKind.Mesh : target.Kind, out var section))
            section.BringIntoView();
        Activate();
    }

    TextBlock Section(string title)
    {
        var heading = new TextBlock { Text = title, Classes = { "eyebrow" }, Margin = new Thickness(10, Outline.Children.Count == 0 ? 0 : 14, 0, 4) };
        Outline.Children.Add(heading);
        return heading;
    }

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

    /// <summary>File › Export…: the export reads the saved package, so unsaved changes are saved first (after asking).</summary>
    async void OpenExport()
    {
        if (_session.Dirty)
        {
            if (await ConfirmDialog.Ask(this, "Save changes?", $"{_session.Project.Name} has unsaved changes.", "Save", "Cancel") != 0) return;
            Save();
            if (_session.Dirty) return;
        }
        await new ExportWindow([_session.Folder], _session.Game, target => { if (SamePath(target.Path, _session.Folder)) ShowCheck(target); }).ShowDialog(this);
    }

    static bool SamePath(string a, string b) =>
        a.Length > 0 && string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

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
