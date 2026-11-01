# OpenSimsStudio³

An open-source, modern replacement for The Sims Resource Workshop (TSRW) for The Sims 3. Status: **viewport prototype**.
Where code comes from: `PROVENANCE.md`. Working rules for contributors and coding agents: `CLAUDE.md`.

Licence: GPL-3.0-or-later (`LICENSE`), required by the bundled s3pi.

## Layout
| Path | What |
|---|---|
| `src/OpenSimsStudio3.Formats` | Reading/writing TS3 resources; wraps s3pi behind our own types (`Meshes/GeomLoader`) |
| `src/OpenSimsStudio3.Rendering` | Vulkan viewport renderer via Silk.NET (`OffscreenMeshRenderer`, `OrbitCamera`, runtime GLSL compile) |
| `src/OpenSimsStudio3.App` | Avalonia 12 desktop app (currently the viewport prototype window) |
| `src/OpenSimsStudio3.Probe` | Command-line checks against a real install (read-only) |
| `third_party/s3pi` | s3pi 17-0520-1823 from its official source archive, converted to .NET 10 SDK projects by OSS3; see `third_party/s3pi/OPENSIMSSTUDIO3-PROVENANCE.md` |

## Build and run
Needs the .NET 10 SDK and a Vulkan driver.

```
dotnet build OpenSimsStudio3.sln -c Release
dotnet artifacts/bin/OpenSimsStudio3.App/release/OpenSimsStudio3.dll [package]
dotnet artifacts/bin/OpenSimsStudio3.Probe/release/OpenSimsStudio3.Probe.dll geoms <package>
dotnet artifacts/bin/OpenSimsStudio3.Probe/release/OpenSimsStudio3.Probe.dll render <package> <instance-hex|first> <out.bmp>
dotnet artifacts/bin/OpenSimsStudio3.Probe/release/OpenSimsStudio3.Probe.dll roundtrip <casp|geom> <package>...
dotnet artifacts/bin/OpenSimsStudio3.Probe/release/OpenSimsStudio3.Probe.dll geomdiff <package> <instance-hex>
```
`roundtrip` parses each resource, writes it back from the parsed fields, and compares bytes. For RCOL resources (GEOM) every chunk is checked against its original bytes sliced from the file, and then the container is checked. Patch-package deletion markers (entries with no data) are counted separately, not as failures.
The app defaults to the base game's `FullBuild0.package`, opened read-only. Left-drag orbits, the wheel zooms, and Previous/Next step through drawable GEOMs (morph GEOMs are skipped).

## Verified (2026-10-29, Windows, RTX 4090)
- `geoms` on FullBuild0: 14,557 GEOMs, all parsed, 0 failures.
- `render`: a base-game nude top (1,680 vertices) drawn correctly offscreen, at about 18 ms per 640×800 frame including readback.
- The app opens, shows the mesh in the Avalonia window, and steps between meshes.
- **Byte-exact round trips** across the base game, the patch and all 35 EP/SP build packages:

  | Type | Checked | Failures |
  |---|---|---|
  | CASP | 5,271 | 0 |
  | GEOM | 44,940 | 0 (96 patch deletion markers skipped) |

  Getting there needed two s3pi fixes (see `third_party/s3pi/OPENSIMSSTUDIO3-PROVENANCE.md`): EA's zero chunk key is now kept, and a GEOM's second UV channel is no longer overwritten by the first.

## Known limits of the prototype
- Pixels go GPU → CPU → Avalonia bitmap every frame, which costs about 18-66 ms per frame depending on size. Fine for a turntable, too slow for smooth interaction at large sizes. The planned fix is Avalonia's GPU interop (zero-copy), behind the same renderer API.
- Prototype lighting only. No textures, retail shading, skinning or morphs yet.
- No tests project yet; the probe commands are the current checks.
