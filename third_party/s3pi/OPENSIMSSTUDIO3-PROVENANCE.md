# Where this copy came from

Upstream: s3pi release 17-0520-1823 by Peter L Jones, GPL-3.0-or-later (`gpl-3.0.txt`).
- Archive: `s3pi-Source_17-0520-1823.7z` from https://sourceforge.net/projects/s3pi/files/17-0520-1823/
- SHA-256: `5377d62b386f0b4d6961e2e65ec60b9bda79e11414c1c07e04da29d2a9c5cadd`
- Prepared: 2026-11-01 by OpenSimsStudio³ from the verified archive, not from any other copy.

Attribution kept: `gpl-3.0.txt`, `Acknowledgements.txt` (upstream `s3pi/build/Acknowledgements.txt`), per-file headers,
`s3pi.MeshChunks/OriginalAcknowledgements.txt`, `ViewDDS-Licence.htm` (from the upstream DDSPanel folder; DDSPanel itself
is not included). `s3pi-Version.txt` is ours and just records the release name.

## What is included
One folder per assembly, with upstream's sources unchanged apart from the changes listed below:

| Folder | Upstream folder |
|---|---|
| System.Custom | CS System Classes |
| s3pi.Settings, s3pi.Interfaces, s3pi.Package | s3pi/Settings, Interfaces, Package |
| s3pi.GenericRCOLResource, s3pi.JazzResource, s3pi.MeshChunks, s3pi.CASPartResource, s3pi.CatalogResource, s3pi.ModularResource, s3pi.ObjKeyResource, s3pi.RigResource, s3pi.StblResource, s3pi.TxtcResource | s3pi Wrappers/(same name) |
| s3pi.RCOLChunks | s3pi Wrappers/s3piRCOLChunks |

## What is omitted
- The other upstream projects: WrapperDealer, CreateAssemblyVersion, build, all of `s3pi Extras` (WinForms/UI helpers,
  DDSPanel and its native DLLs), the other wrappers, installers and solution files.
- Upstream `.csproj`/`.sln` files, the `.pfx` key files (upstream does not sign), and upstream's generated
  `Properties/AssemblyVersion.cs`.
- `System.Custom/PortableSettingsProvider.cs` is copied but excluded from the build (`Compile Remove`): it needs
  `System.Configuration.ConfigurationManager` and s3pi does not use it.
- `s3pi.Interfaces/ConstructorParametersAttribute.cs` and `DataGridExpandableAttribute.cs` are copied but excluded, as in
  upstream's compile list.

## SDK conversion done by OpenSimsStudio³
- New SDK-style `.csproj` per assembly (net10.0, upstream's assembly name and root namespace, upstream's project
  references) and a shared `Directory.Build.props`. Source files, including `Package.cs`, are otherwise as upstream.
- `LangVersion` 13: C# 14 makes `field` a keyword inside property accessors, which would change code that uses an
  identifier named `field`.
- `GenerateAssemblyInfo` is off; upstream's `Properties/AssemblyInfo.cs` is used as-is.
- Each `Properties/AssemblyVersion.cs` is a fixed `1705.20.1719.0`. Upstream's generator stamped build time; this is the
  stamp found in upstream's source tree, with the wildcard revision replaced by 0 so builds are reproducible.
- `Properties/Resources.resx` in GenericRCOLResource is embedded as upstream does, with the one file-name case fix listed
  below.

## OpenSimsStudio³ changes
- `s3pi.GenericRCOLResource/GenericRCOLResource.cs`, block registry: `Assembly.LoadFile(path)` became
  `AssemblyLoadContext.Default.LoadFromAssemblyPath(path)`. On .NET Core and later, `LoadFile` loads each DLL into its own
  isolated context, so a registered `GEOM` was a different type from the `GEOM` our code casts to. Loading into the
  default context returns the already-loaded assembly.
- `s3pi.GenericRCOLResource/GenericRCOLResource.cs`, chunk keys: when a single-chunk RCOL stores its chunk type as 0 (every
  EA GEOM does), s3pi fills the type in from the chunk tag so it can pick a wrapper, then used to write that type back. It
  now remembers this and writes 0 again, so unedited GEOMs round-trip byte-exactly.
- `s3pi.MeshChunks/GEOM.cs`, vertex element writing (`ElementList.UnParse` and its copy constructor): each vertex format
  now takes the n-th element of the *exact* matching type. Before, every format took the first element whose type
  matched, so a GEOM with two UV channels was written with the first UV twice. The match is exact because
  `NormalElement` and `TangentNormalElement` derive from `PositionElement`.
- `s3pi.GenericRCOLResource/Properties/Resources.resx`, the `RCOLResources` file reference: `..\rcolresources.txt` became
  `..\RCOLResources.txt`, the file's real name. Windows ignores the case, but builds on case-sensitive file systems
  (Linux) failed with MSB3103 because the file was not found. The embedded resource name and contents are unchanged.
