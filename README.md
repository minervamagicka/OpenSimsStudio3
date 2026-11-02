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
    texture slot with its real size and format; and watch a live preview composed on the mesh.
  - **Meshes:** every LOD and mesh group, **Edit in Blender** with GEOM Tools (Blender 3.x and 4.x; 4.x needs no
    add-on changes) and re-import with vertex colours and tangents preserved.
  - **Viewport:** Vulkan, camera presets, the whole Sim around the part, and the game's own body-shape morphs
    (Thin–Fat, Normal–Fit, Pregnant) applied from its BGEO data.
  - Warnings for missing categories, ages, genders or mesh groups and for broken hair vertex colour.
- **Saving** rewrites the project package safely and keeps a `.bak`.
- **Export** of one or more projects into a single `.package`, with key-collision checks (core only; the window is
  not built yet).

Round trips are exact: every CASP and GEOM in the base game and all packs re-serialises byte for byte, and every
preset XML does too.

## Planned

- The texture window (import, bake, clear, share across recolours), thumbnails and patterns.
- Fit to body, UV checks, shader parameters, bones and LOD generation.
- The export window, `.sims3pack` output and a test-profile copy.
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
| `third_party/s3pi` | s3pi 17-0520-1823 from its official source, as .NET 10 projects |
| `docs/ui` | Design notes and reference boards |

## Contributing

Not open for contributions yet; it will be once the first release is in sight. When it is: code written for this
repository only, with anything else listed in [`PROVENANCE.md`](PROVENANCE.md); no game files or anything derived
from them in the repository; and every PR says what was checked and how. [`CLAUDE.md`](CLAUDE.md) has the working
rules in full.

## Licence and credits

GPL-3.0-or-later ([`LICENSE`](LICENSE)). OSS3 is built on [s3pi](https://sourceforge.net/projects/s3pi/) by
Peter L Jones and contributors, and owes a great deal to the creators who documented these formats over the years.
It is an independent project, not affiliated with or endorsed by Electronic Arts or Maxis. The Sims is a trademark
of Electronic Arts Inc.
