# CAS thumbnails

How OSS3 stores a recolour's CAS thumbnails (`CasThumbnails.cs`) and frames rendered ones (`ThumbnailFraming.cs`).
Written 2026-11-03 without access to a game install, so **nothing here has been checked in game yet**.

## What OSS3 writes

| Size | Type | Pixels |
|---|---|---|
| Small | `0x626F60CC` | 32 × 32 |
| Medium | `0x626F60CD` | 128 × 128 |
| Large | `0x626F60CE` | 256 × 256 |

- **Format:** PNG, 8-bit RGBA, transparent background.
- **Instance:** the CASP's instance.
- **Group:** the recolour's preset ID (the CASP preset's `Unknown1` in s3pi). If the CASP's group has a top byte (EP hair carries its pack there), the thumbnail group keeps it: `(caspGroup & 0xFF000000) | (presetId & 0x00FFFFFF)`.
- **Item thumbnail:** the first recolour is also written under the CASP's own group. This is skipped when another recolour's preset ID already maps to that key.
- **Deleted recolours:** deleting a recolour removes its thumbnails. The item thumbnail stays.

Every key comes from `CasThumbnails.KeyFor` and `ItemKeyFor`, the clearly marked mapping. If in-game checks disagree,
change those two functions and this file.

## Sources and confidence

The sources are community modding tutorials. The modthesims.info wiki and threads could not be fetched from the
session that wrote this, so the claims below come from search excerpts of these pages:

- TAMO, "TS3 – How to Add a Thumbnail to Custom Makeup" (tamosim.blogspot.com, 2011): CAS thumbnails are THUM
  `0x626F60CE`, 256 × 256 PNG; the group "must be 0x00000000" for makeup.
- "Sims 3 tutorials: Creating Custom Thumbnails for CAS and launcher" (simtuts.blogspot.com, 2014): the game
  generates CAS thumbnails at 32 × 32, 128 × 128 and 256 × 256 as `0x626F60CC/CD/CE`; only the 256 one matters for
  custom content.
- Mod The Sims, "[Tutorial] Creating Custom Thumbnails" (thread 391244) and "Custom thumbnails for eyebrows?"
  (thread 413578):
  - The instance matches the CASP.
  - For multi-preset items, the group matches the preset's `Unknown1`.
  - The default preset's thumbnail group ends in 00.
  - For hair, the group is the CASP's group, which can carry an expansion ID in its first byte.
- sweetdevil-sims (tumblr), on thumbnails that don't update: give each preset its own thumbnail copy with its own
  group number. CASThumbnails.package is the game's cache and has to be cleared to see changes.
- Mod The Sims wiki, "Sims 3:Catalog Resource": THUM small/medium/large are consecutive types, and the instance
  matches the item.

| Claim | Confidence |
|---|---|
| Types and sizes `0x626F60CC/CD/CE` = 32/128/256 PNG | Good: several sources agree |
| Instance = CASP instance | Good: several sources agree |
| Group = preset ID | Moderate: one source family, consistent with the "default ends in 00" note |
| Top byte kept for pack groups | Low: an extrapolation from the hair note |
| Writing the item thumbnail as well | Low: harmless if the game ignores it |

OSS3's item browser reads a different type, `0x0580A2B5`, from the game's `Thumbnails/AllThumbnails.package`
(`CasCatalog`). Several sources give `0x0580A2B4/5/6` as object (Buy/Build) thumbnail types. Whether EA's own CAS
thumbnails also use them is not settled, and OSS3 does not write them.

## Rendered thumbnails

Render draws the recolour composited by `CasCompositor` on the project's LOD 0 mesh:

- **Camera:** from the front (+Z), 0.08 rad above, through a 20° lens.
- **Framing:** fitted so the bounding box's projection fills the square, less 4% on each side of the longer
  dimension, and centred.
- **Size:** rendered at 512 × 512, cropped to the visible pixels (`ImageResize.CropToContent`, the same margin), then
  area-averaged down to each size. The crop matters because the mesh's bounds include cut-out areas.
- **Transparency:** texels under 50% alpha are cut out (hair strands, a top's bare-skin areas), and the background
  is transparent.

This look is OSS3's choice. It is not measured from EA's thumbnails.
