# Compositing: provenance and inferred rules

`CasCompositor` composes a CAS recolour's diffuse from its preset XML; `Complate` binds a complate template to the
preset's values; `DdsDecoder` decodes the top mip of DDS textures. Written for OSS3 (clean room, 2026-11-01). No code
was taken from another project, and no EA data is in this folder.

## Sources
- **EA complate templates** (resource type 0x0333406C, read from the player's install at run time): step names
  (`SetTarget`, `ColorFill`, `ChannelSelect`, `DrawImage`, `DrawFabric`, `HSVShift`, `CASPickData`), their attributes
  (`texture`, `select`, `color`, `colorWrite`, `enableBlending`, `srcBlend`/`dstBlend`, `sourceRect`, `destRect`,
  `rotation`, `mask`/`maskSelect`/`maskBias`, `enabled`), `($Name)` references and `{...}` arithmetic.
- **s3pi** `third_party/s3pi/s3pi.TxtcResource`: the TXTC model of the same operations: blend factors (XNA `Blend`
  names), colour-write bits, the step-type and render-target lists.
- **Public format documentation**: Microsoft's DDS header/pixel-format and BC1-BC3 (DXT1/3/5) block layouts; Direct3D 9
  fixed-function blending (`out = src * srcFactor + dst * dstFactor`, clamped).
- **Black-box comparison** with the previous OSS3 build's `compose` probe output (PNG only). Nothing of its code, binaries
  or history was read.

## Rules inferred from black-box comparison
- Target size: the largest texture the enabled steps draw, halved until it fits `maxSize`.
- Render target A starts cleared (0,0,0,0); B is created cleared on first use. Targets keep float values; only the
  final image and each composed pattern are rounded to 8 bits.
- Textures are sampled bilinearly with wrapping at pixel centres (`enableFiltering="false"`: nearest).
- A mask step skips pixels where `dot(mask, maskSelect) + maskBias < 0`.
- `DrawFabric`: the pattern complate is composed at the target's size, rounded to RGBA8, then sampled with the step's
  `sourceRect` tiling. (A pattern composed at its own 256 px size and tiled 1:1 did not match; the oracle shows the
  slight blur of this double resampling.)
- `HSVShift`: each channel first moves 8/255 away from 0.5 (up above, down below; also for a zero shift), then
  standard HSV: hue added and wrapped, saturation multiplied by `1 + s`, value added; clamped.
- The diffuse is the first texturePart's `diffuse` destination (or the one named by `part`). The final RGBA is target A
  as is; for most complates the last step draws B over the cleared A with SrcAlpha/InvSrcAlpha, so the colour is
  already multiplied by alpha.
- Missing texture keys are listed and their steps skipped. Texture values that are file paths (`.tga` defaults) fail.

## Known differences from the oracle
- `HSVShift` with a strong saturation cut (about -0.85 or more) can differ by a few levels; up to 4-19 on some parts.
- `AnimalHair` (masked steps on the diffuse alpha) and `AnimalBody` (masked multiply, alpha-0 pixels only) differ
  at some edge pixels.
- Not supported (as before): `RemappedChannelSelect` (PeltMarking), `SkinTone` (scalp), DDS FourCC 0x71 (A16B16G16R16F).
