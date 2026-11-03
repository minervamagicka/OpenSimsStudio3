<p align="center">
  <img src="src/OpenSimsStudio3.App/Assets/oss3_icon.png" alt="" width="128" height="128">
</p>

<h1 align="center">OpenSimsStudio³</h1>

<p align="center">
  A modern, open-source studio for making custom content for <em>The Sims 3</em>.<br>
  Clone a game item, edit everything about it on one screen, and export it.
</p>

<p align="center">
  <a href="LICENSE"><img alt="GPL-3.0-or-later" src="https://img.shields.io/badge/licence-GPL--3.0--or--later-blue"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Windows" src="https://img.shields.io/badge/platform-Windows-0078D4">
  <img alt="Status: early development" src="https://img.shields.io/badge/status-early%20development-orange">
</p>

> **Early development.** OSS3 already clones, edits and saves CAS parts, but it is not ready for everyday use, and
> nothing it writes has been tested in the game yet. Screenshots will follow once a sample project made from
> original assets exists: the app shows the player's own game files, which stay out of this repository.

## Why

The Sims 3 Resource Workshop has carried creators for over a decade, but it is closed source and shows its age. OSS3 aims at the same job with today's tools: a clean, dark interface, a real-time 3D
viewport, one editor screen instead of a wizard, and source anyone can read and improve.

## What works today

- **Start a project** by cloning a game item, cloning an existing custom-content package, or making a default
  replacement. The browser lists every CAS part in your install (about 4,800 with all packs), grouped by type, with
  thumbnails rendered from the meshes.
- **One editor screen** for the item:
  - **Item properties** edit the part directly: type, clothing type, species, ages, genders, outfit categories, flags
    and own texture space.
  - **Recolours:** pick, duplicate and delete recolours; edit each channel's colour and enabled state; see every
    texture slot with its real size and format; watch a live preview composed on the mesh; and render or replace each
    recolour's CAS thumbnail (not yet checked in game).
  - **Meshes:** every LOD and mesh group, **Edit in Blender** with GEOM Tools (Blender 3.x and 4.x; 4.x needs no
    add-on changes) and re-import with vertex colours and tangents preserved.
  - **Viewport:** Vulkan, camera presets, the whole Sim around the part, and the game's own body-shape morphs
    (Thin–Fat, Normal–Fit, Pregnant) applied from its BGEO data.
  - Warnings for missing categories, ages, genders or mesh groups and for broken hair vertex colour.
- **Saving** rewrites the project package safely and keeps a `.bak`.
- **Export** (File › Export…): checks with a Show link on each problem, `.package` and `.sims3pack` output, and a copy
  to a test profile's `Mods\Packages` that clears its CAS caches. The core merges several projects into one package;
  the window exports the open project. The `.sims3pack` format is not verified in game yet.

Round trips are exact: every CASP and GEOM in the base game and all packs re-serialises byte for byte, and every
preset XML does too.

## Planned

- The texture window (import, bake, clear, share across recolours) and patterns.
- Fit to body, UV checks, shader parameters, bones and LOD generation.
- Merged export of several projects from the window, more export checks, and `.sims3pack` verified in game.
- A CAS camera, poses and outfit preview.
- Build/Buy objects and patterns after CAS.

The target look and every UI decision live in [`docs/ui/DESIGN.md`](docs/ui/DESIGN.md), with reference boards in
[`docs/ui/mockups`](docs/ui/mockups).

## Building

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download), a Vulkan-capable GPU and driver, and for
the app and install checks, a Windows install of The Sims 3. Blender with GEOM Tools is optional (Edit in Blender).

```
dotnet build OpenSimsStudio3.sln -c Release
```

Run the app:

```
artifacts\bin\OpenSimsStudio3.App\release\OpenSimsStudio3.exe
```

OSS3 finds the game through the registry, the usual EA/Origin folders or Steam; **Change…** on the start screen
picks another folder. Settings live in `%APPDATA%\OpenSimsStudio3`, projects in `Documents\OpenSimsStudio3\Projects`.
`OSS3_SETTINGS_DIR` and `OSS3_PROJECTS_ROOT` move both for test runs.

## Checking

The pure-code tests need no game install and run on any OS (CI runs them on Linux for every push):

```
dotnet test tests/OpenSimsStudio3.Tests -c Release
```

`OpenSimsStudio3.Probe` runs read-only checks against a real install. The most useful:

| Command | What it checks |
|---|---|
| `install` | Which game, version and packs were found |
| `cascatalog` | The CAS part catalog loads and groups correctly |
| `roundtrip casp\|geom <package>…` | Every resource re-serialises byte for byte |
| `presetrt`, `vpxyrt` | Preset XML and VPXY round trips across the install |
| `clone <part> <out.package>` | A clone re-parses and every reference resolves |
| `bgeo <part>…` | Morph decoding against the game's own morph meshes |
| `compose --survey <n>` | The compositor on every complate in the install |
| `sims3pack <file>` | A `.sims3pack`'s header, manifest and CRCs |

```
dotnet artifacts/bin/OpenSimsStudio3.Probe/release/OpenSimsStudio3.Probe.dll install
```

## Layout

| Path | What |
|---|---|
| `src/OpenSimsStudio3.App` | Avalonia 12 desktop app |
| `src/OpenSimsStudio3.Formats` | Reading and writing TS3 resources, projects, cloning, export and the compositor |
| `src/OpenSimsStudio3.Rendering` | The Vulkan viewport renderer (Silk.NET) |
| `src/OpenSimsStudio3.Probe` | Command-line checks |
| `tests/OpenSimsStudio3.Tests` | Pure-code tests (xUnit) on data made for the test |
| `third_party/s3pi` | s3pi 17-0520-1823 from its official source, as .NET 10 projects |
| `docs/ui` | Design notes and reference boards |

## Contributing

Contributions are welcome. For anything larger than a fix, open an issue first so the work fits the plan.

**In scope**
- **CAS content creation:** the items under [Planned](#planned), and fixes or polish to what already works.
- **UI:** changes that follow [`docs/ui/DESIGN.md`](docs/ui/DESIGN.md). Update that file when a design decision changes.
- **Formats:** reading and writing TS3 resources, with byte-for-byte round trips kept intact.
- **Checks:** probe commands, tests and test data made for the purpose.
- **Docs:** format notes, building on other setups, and fixes to these docs.
- **Bug reports:** what you did, what happened, and your game version and packs.

**Out of scope for now**
- Build/Buy objects and patterns, until CAS is complete.
- Integrations with other tools or games. OpenTS3 export support will be written here by the maintainers, and its
  code must not be copied in.
- Gameplay or script mods. OSS3 makes content; it does not change the game.

**Rules for every PR**
- **Your own code only.** Code from another project needs an entry in [`PROVENANCE.md`](PROVENANCE.md) agreed
  before it lands.
- **No game data in the repository.** That covers textures, meshes, XML, packages and screenshots taken from the
  game, and anything derived from them.
- **s3pi changes are recorded** in `third_party/s3pi/OPENSIMSSTUDIO3-PROVENANCE.md`.
- **Say what you checked and how.** That includes the build, probe runs and in-game tests. Name any check you
  couldn't run, and the maintainers will run it.

The full working rules are in [`CLAUDE.md`](CLAUDE.md).

## Licence and credits

GPL-3.0-or-later ([`LICENSE`](LICENSE)). OSS3 is built on [s3pi](https://sourceforge.net/projects/s3pi/) by
Peter L Jones and contributors, and owes a great deal to the creators who documented these formats over the years.
It is an independent project, not affiliated with or endorsed by Electronic Arts or Maxis. The Sims is a trademark
of Electronic Arts Inc.
