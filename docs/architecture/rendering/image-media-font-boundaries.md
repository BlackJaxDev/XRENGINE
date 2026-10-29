# Image, Media, And Font Backends

Runtime image pixels use `RuntimeImage`: width, height, mip level, pixel format, row stride, and tightly packed pixel memory. A caller may lend memory or pass an `IMemoryOwner<byte>` that the image releases on disposal. Cooked texture data can be read without starting an image decoder. Source image decoding and PNG encoding use the installed `IRuntimeImageCodec`; the desktop composition installs the ImageMagick implementation. A missing codec reports its registration requirement.

The shared `XRTexture2D` cooked carrier preserves imported color space, pixel
usage, normal-map green inversion, anisotropy, and depth-comparison settings.
Non-default values use an optional versioned sampling header after the asset
base and before the existing sampler fields. Header version 1 occupies 26 bytes.
Full object loads, YAML envelopes,
and streaming mip readers use the same settings parser. Mip offsets remain
relative to the mip section. This format does not add an image decoder or a
desktop service dependency.

Default sampling retains the previous byte layout. Current readers accept old
payloads with linear/data interpretation, anisotropy 1, no green inversion, and
disabled less-equal comparison. Values omitted by an old writer cannot be
recovered from those bytes. Old readers reject the new non-default header;
publish the matching runtime with newly cooked assets. Unknown header versions
fail explicitly. This carrier support does not select browser texture formats,
generate compressed variants, or replace authored sampler admission checks.

When the texture cache cooker receives an authored `XRTexture2D`, both CPU and
GPU mip-generation routes retain its sampler name, filters, wrapping, LOD clamps and bias,
anisotropy, comparison settings, imported pixel interpretation, and alpha policy.
The generated cache owns its mip range, format, and storage flags. Source-only
image cooking keeps its existing default settings. The cache variant key is
`TextureStreaming_v4_preview64_rgba8_uncompressed_binary`; older cache variants
are rebuilt because their generated textures can lack authored sampling values.
Explicit import variant keys remain part of the cache path.

Texture asset copies use the same sampling copier, including comparison and
sampler binding settings. The resident-data preview path still applies its own
filter and mip-range policy and does not restore all authored sampling metadata.
This cache change does not establish preview or live streaming sampling parity.

Streamed media playback uses `HlsReferenceRuntime` as a renderer-facing entry point. The desktop composition installs the FFmpeg backend, which owns decoder/session implementation, native binary loading, and the existing HLS reference binary supply. The renderer's video component keeps the GPU upload and presentation logic. A missing media backend reports its registration requirement.

Source font import asks the installed FreeType character enumerator for supported Unicode codepoints. Bitmap atlas creation asks the installed Skia rasterizer for a cooked PNG and glyph metrics. `FontGlyphSet` stores the resulting glyphs and texture without a SharpFont or Skia object in those public service contracts. The desktop authoring composition installs both services. Existing serialized `FontGlyphSet` names and fields remain unchanged.

`UISvgComponent` lives in `XREngine.Runtime.UI.Skia` with its original namespace and type redirect. That project owns `SkiaSharp` and `Svg.Skia`; `XREngine.Runtime.Text.FreeType` owns `SharpFont` and the selected `freetype6.dll`; `XREngine.Runtime.Media.FFmpeg` owns its managed package and HLS native DLL copy target. `XREngine.Runtime.Imaging.Magick` owns ImageMagick. Bootstrap installs these backends explicitly during engine initialization; the runtime kernel does not install them by default.

Texture import, mip handling, GPU readback capture, editor screenshots, and browser cooked texture publishing now exchange `RuntimeImage` or encoded bytes through neutral contracts. Rendering, Core, the model asset pipeline, and editor source no longer reference `MagickImage`; the codec implementation stays in the imaging leaf. Browser cooked pixels can be consumed without initializing the desktop decoder. The source split still needs runtime checks for both GPU readback backends, editor screenshots, import/mips, and browser cooked texture loading.
