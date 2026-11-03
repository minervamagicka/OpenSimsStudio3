# OpenSimsStudio³

A GPL-3.0-or-later CAS-content studio for The Sims 3 (C#/.NET 10, Avalonia 12, Vulkan via Silk.NET, s3pi).
Layout and build: `README.md`. Where code comes from: `PROVENANCE.md`. UI target look, decisions and reference
boards: `docs/ui/DESIGN.md` (read it before any UI change; keep it current when a UI decision changes).

## Rules
- **Only code written for this repo, or listed in `PROVENANCE.md`.** Never paste, port or paraphrase code from
  another project without a PROVENANCE entry agreed by the maintainers. This includes OpenTS3: it is a separate
  project, and OSS3 support for it (the OpenTS3 export target) is written here, not copied from it.
- **No EA data in the repo:** no textures, meshes, XML, packages or images taken from the game, and nothing
  derived from them. Tests use data made for the test.
- **s3pi** (`third_party/s3pi`) stays upstream source plus the changes listed in its `OPENSIMSSTUDIO3-PROVENANCE.md`;
  add any new change there.
- Change only what the task needs. Every PR states what was checked and how.

## Building and checking
- `dotnet build OpenSimsStudio3.sln -c Release` (.NET 10 SDK).
- `dotnet test tests/OpenSimsStudio3.Tests -c Release`: pure-code tests, no install needed. Add tests with every
  change to `Formats`, using data the test builds (see `TestDds`, `TestPackages`).
- The app and the `OpenSimsStudio3.Probe` checks need a Windows install of The Sims 3. Without one (e.g. in a cloud
  session), build and run pure-code checks only, and say in the PR which in-game or install checks are still owed.
- The maintainer pulls branches and runs the app and probes on Windows, then reports back. When you hand over a branch,
  say exactly what to look at and which probe commands to run (e.g. `compose <part> <outDir>`, `roundtrip geom <package>`).
  Their screenshots may show EA content: use them in the conversation, never commit them.
