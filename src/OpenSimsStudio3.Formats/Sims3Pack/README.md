# Sims3Pack: provenance and open questions

`Sims3PackFormat` writes and reads the game launcher's `.sims3pack` container. Written for OSS3 (clean room,
2026-11-03) from the public format notes below. No code was taken, ported or paraphrased from another tool (S3PE,
Delphy's tools, TSRW, Sims3Pack generators and so on), and no EA data or real `.sims3pack` is in this repository.

**Status: unverified in game.** Nothing this writes has been installed by the launcher yet. The export window leaves
`.sims3pack` unchecked by default until it has been.

## Sources
- **Mod The Sims wiki, "Sims_3:SIMS3PACK"** (also mirrored on SimsWiki). The pages could not be opened from the
  session that wrote this code (the hosts were blocked); what is used here comes from search-engine extracts of them:
  - three sections: file header, XML document, archive;
  - header: a DWORD signature length, the signature `TS3Pack`, a WORD marked "version??" (`0x0101`) and a DWORD
    giving the XML section's size in bytes; the XML follows immediately;
  - root element `Sims3Package` with `Type`/`SubType` attributes (examples `Type="world" SubType="0x00000000"` and
    `Type="CasPart" SubType="0x00000004"`);
  - `ArchiveVersion` (example `1.4`), `DisplayName`, `Description`, `PackageId` (the package's name without its
    extension), `MinReqVersion` (`1.0.0.0`, meaning unknown), `LocalizedNames`;
  - `PackagedFile`, one per file in the archive, with `Offset` (from the start of the archive section), `Length`,
    `Crc` ("CRC value for the archive"), `Guid` and `ContentType`.
- **s3pi** `third_party/s3pi/System.Custom/Sims3PackCRC.cs`: the 64-bit CRC used for data in a Sims3Pack. OSS3 calls
  it as a library; it is not copied.

## What OSS3 writes, and what is assumed
| Field | Value | Basis |
|---|---|---|
| Header | `07 00 00 00`, `TS3Pack`, `01 01`, XML size (little-endian) | Notes |
| `Sims3Package@Type` | `CasPart` | Notes (example) |
| `Sims3Package@SubType` | `0x00000000` | **Assumed**: the notes give `0x00000004` for one CasPart example without saying what it means |
| `ArchiveVersion` | `1.4` | Notes (example) |
| `DisplayName`, `Description` | the project name, empty | Notes |
| `PackageId` | the package's file name without `.package` | Notes |
| `MinReqVersion` | `1.0.0.0` | Notes |
| `PackagedFile/Name` | the package's file name | **Assumed**: the extracts don't list a name element |
| `PackagedFile/Length`, `Offset` | decimal bytes; offset from the archive start | Notes (decimal is **assumed**) |
| `PackagedFile/Crc` | s3pi's Sims3PackCRC of the file, `0x` + 16 hex digits | CRC from s3pi; the text form is **assumed** |
| `PackagedFile/Guid` | same as `PackageId` | **Assumed** |
| `PackagedFile/ContentType` | `CasPart` | **Assumed** (same as Type) |
| XML | UTF-8 without BOM, `<?xml version="1.0" encoding="utf-8"?>` | **Assumed** |

Not written: `LocalizedNames`/`LocalizedDescriptions`, thumbnails (PNG files in the archive), dependency lists, dates
and any other element the extracts don't mention. If the launcher refuses the file, compare its manifest with a
real CAS `.sims3pack` (`OpenSimsStudio3.Probe sims3pack <file>` prints the header and manifest) and fill in what's
missing.

## Test-profile caches
`Projects/TestProfile.cs` deletes `CASPartCache.package` and `simCompositorCache.package` from the chosen user folder
(and nothing else). Sources, checked 2026-11-03 through search extracts: SimsWiki/MTS "Game Help:Sims 3 Delete Cache
Files", The Sims Wiki "Game guide:Multiple ways to improve The Sims 3's performance", and TheSimsTree "How to add mods
to The Sims 3 and clear cache". They agree on five cache files in the user folder that the game rebuilds; they map
CASPartCache to CAS parts and simCompositorCache to Sims and their textures. compositorCache (Build/Buy),
scriptCache (script mods) and socialCache are left alone.
