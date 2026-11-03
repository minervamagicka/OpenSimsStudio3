# Materials: provenance and open questions

`GeomMaterial` is an editable model of a GEOM's material: the shader hash and the MTNF parameter list stored inside
the GEOM chunk. `ShaderNames` turns shader and parameter hashes into names. Both were written for OSS3 (2026-11-03).
Reading and writing go through s3pi's `GEOM`, `MTNF` and `ShaderData` classes, which are called as a library and
not copied. No EA data is in this folder or in its tests.

## How names are matched
The game stores only hashes. Each one is the 32-bit FNV-1 hash (offset basis `0x811C9DC5`, prime `0x01000193`,
multiply then xor, per byte) of the **lower-cased** ASCII name. `ShaderNames` lists names only. Every hash is computed
from its name at start-up and never typed in, so a name is shown only when it really hashes to the stored value.
Anything else shows as `0xXXXXXXXX`.

## Sources of the names
- **s3pi** (`third_party/s3pi/s3pi.RCOLChunks/ShaderData.cs`, s3pi 17-0520-1823, GPL-3.0-or-later, listed in
  `PROVENANCE.md`): its public `ShaderType` and `FieldType` enums. The enums give names, hashes, and a comment with the
  value type of each parameter (Float, Float2, Float3, Float4, Int, Texture, TextureKey). OSS3 takes the names and
  the value types as data. The hashes are recomputed, not copied. Checked on 2026-11-03: all 190 of s3pi's names
  hash to s3pi's values under the rule above, except one.
  - `PhongAlpha`: s3pi's value `0xFC5FC212` does not match the hash of that name (`0xD9429C93`). It is left out, so
    that hash shows as hex.
  - The test `ShaderNamesTests.Table_agrees_with_s3pi_enums` checks that every name both tables know agrees.
- **OSS3's earlier shader table** (it was in `EditorWindow`, citing CAS research notes that are not in this repo):
  `CasSimHair`, `SimHorse`, `SimPetFur` and `SimPetFace`. These are not in s3pi's enum. The test
  `ShaderNamesTests.Fnv32_matches_known_values` checks them against the hashes that table used, and
  `MeshExchange.SimHorse` (`0x05F75EF1`) agrees.
- The Sims 3 modding wikis (Mod The Sims / SimsWiki, GEOM and MATD pages) describe the same MTNF layout. They could
  not be opened from the session that wrote this code (the host was blocked), so nothing here relies on them.

## Format as handled here
- In a GEOM, the shader is a `uint` before the MTNF block. Shader 0 means no MTNF (morph GEOMs) and no material.
  `GeomMaterial.Read` returns null for those, and the model refuses to set shader 0.
- Each MTNF entry has a field hash, a data type (1 = float, 2 = int, 4 = texture) and a count, with its data stored
  after all the headers:
  - float with count 1–4: `Float`…`Float4`;
  - int with count 1: `Int`;
  - texture with count 4: `Texture`, an int index into the GEOM's own TGI list, then 12 zero bytes;
  - texture with count 5: `TextureKey`, an inline ITG key, then 4 zero bytes.
- Writing rebuilds the whole parameter list from the model, in its order, through s3pi. The MTNF's unknown header
  word, the mesh, the bones and the key list come from the original. An unedited material therefore writes the original
  bytes. This is checked in the tests on GEOMs built by the tests, and on real items with the probe
  `materials <package>...`.
- A texture key set by an edit is looked up in the GEOM's key list and appended if it is missing. Keys no longer used
  are kept, because the skin index points into the same list.

## Open questions (need the game)
- Which parameters each shader actually reads, and their defaults. OSS3 lists every known name in Add…, not a
  per-shader set.
- Whether the game accepts a new parameter at the end of the list, or needs the order EA's tools write.
- Whether `TextureKey` parameters ever occur in CAS GEOMs (s3pi's names suggest they are for imposters).
