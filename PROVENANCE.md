# Where OpenSimsStudio³'s code comes from

OpenSimsStudio³ (OSS3) is GPL-3.0-or-later. Its own code is written for this repository. Code taken from anywhere
else is listed here, with its source, version and licence, before it is merged.

| Path | Source | Licence |
|---|---|---|
| `third_party/s3pi` | s3pi 17-0520-1823 by Peter L Jones and contributors, from the official source archive; OSS3 converted it to .NET 10 SDK projects and fixed three bugs. Details: `third_party/s3pi/OPENSIMSSTUDIO3-PROVENANCE.md` | GPL-3.0-or-later |
| `src/OpenSimsStudio3.Formats/Compositing` | OSS3's own compositor, written from The Sims 3's compositor templates, s3pi's TXTC code and public modding documentation. Sources: `src/OpenSimsStudio3.Formats/Compositing/README.md` | GPL-3.0-or-later (OSS3) |

## EA data
The Sims 3's own files (textures, meshes, XML, loading screens, pack icons) are read from the player's install,
in memory. They are never written to disk, a cache, this repository, an exported package or a bug report, except
where the user's own project needs a copy of a resource it changes. Tests and samples must use data made for this
repository.
