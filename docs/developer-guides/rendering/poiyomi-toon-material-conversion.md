# Poiyomi Toon Material Conversion

XRENGINE provides versioned conversion support for **Poiyomi Toon 9.2.66,
9.2.79, 9.3.64, and verified 10.0.25 sources**. The 9.3.64 catalog comes from commit
`c5aaeeb3a67782b7e8a26e184d5e0a1970792294`. Other explicit versions produce
`POI0001` and preserve inspectable source data.

`SourceToon92Catalog` records the two verified 9.2 source identities and hashes.
Both releases use GUID `104003ab15a9f7a47b6ef0597225e771`; the matcher also
requires the exact shader version marker. Locked materials supply the original
GUID through their override tag. A missing marker on this shared GUID cannot
select the 9.3 signature fallback. This is exact-version support, not support
for the full 7.3.50-to-9.2.66 bundle range.

`SourceToon81Catalog` also admits a locked Poiyomi 8.1.167 shader. Admission
requires its shader GUID, original shader GUID, version, and complete source
SHA-256. It does not admit other 8.1 shaders. Its glitter branch preserves the
verified single-layer jitter and green global-mask multiplication. Unsupported
changes to optimizer-fixed inputs remain diagnostic. The conversion report
version is `1.3.0`.

`SourceToon10Catalog` admits the verified 10.0.25 unlocked shader and a bounded
set of verified locked shader contents. It checks the exact version, original
shader identity, and complete source hash after line-ending normalization.
Other locked contents and the separate Grab Pass shader remain unsupported.
The selected source supplies property types, defaults, and enum declarations.
Duplicate declarations remain in the descriptor. The converter fills only its
listed render, lighting, and AO defaults; conflicting duplicate defaults are
not used. It does not apply the 9.3 color declarations to 10.0 materials.

Optimizer-renamed animated values override their canonical values in a private
conversion copy after source color normalization. Parsed source metadata stays
unchanged. Both authored Uber and native Advanced shading combine weighted AO
channels with the source minimum operation. The converter binds all four
channel strengths and leaves material AO disabled when all four are zero.

The glitter color map retains its source color space, UV selection, transform,
and pan. Its RGB multiplies the source flake color in both authored Uber and
native Advanced shading. Source 10.0 distance LOD is not implemented; active
glitter reports this difference with `POI0006`.

Matcap layers retain enabled hue shift, shift amount, speed, and color space.
The supported selection mode applies the shift after source tint and lighting
and before layer composition. Other active selection modes fail explicitly.
Parallax activation uses the source enable controls. A dormant depth value
does not enable parallax when those controls are off.

Shader identity acceptance does not establish complete Advanced support.
The native compute path still rejects active flipbook operations. Unity
serialized texture arrays also need a supported decode and binding path.
The authored material path and its diagnostics remain separate from native
compute admission. The Pro downgrade below remains lossy.

Source glitter supports the verified 9.2 shape texture. Each nearest flake samples
the texture with its authored transform and pan. Shape color multiplies flake
color; shape alpha multiplies coverage. The sampler uses linear filtering, the
nearest mip level, and clamp addressing. Shape gradients use source UV0. Shared
world-space glitter rotation accounts for the engine's reflected Z axis.
The fragment path uses derivatives; the compute path uses explicit footprints.

The 9.2 sources use the existing shared Uber property mappings. Serialized
values remain in a descriptor with their source version. Color transfer flags
come from each source shader's declarations. The 9.3 property catalog remains
the editor authoring inventory; it is not a generated 9.2 property catalog.
Nonzero `_PoiInternalParallax`, `_AudioLinkDelay`, and `_AudioLinkCCStripY`, or
a bound `_AnisoNoiseMap`, receive `POI0006` because these 9.2 controls have no
native mapping. Their source values remain available for inspection.

## Missing Material Textures

Dependency edges retain the material texture slot, GUID, file ID, and original
serialized property. Reports and manifests identify the named slot. Prefab
modification references retain their source-object ownership.

Strict import can retain a missing AO reference as an unused input only for
the verified locked 10.0.25 calculation. All four saved strengths must be zero
and nonanimated. Every AO sample in the selected shader must reduce to one
through the verified zero-strength operation. Zero saved strength alone is
not sufficient. Active or unknown use remains a required visual dependency.

`UNITYDEP0004` records this proof. The unresolved GUID and transform remain in
source metadata, and the manifest records an optional dependency. This does
not mark the import as partial. Texture conversion checks the exact GUID and
file ID and verifies both source hashes. The manifest fingerprints invalidate
the import when either source changes. A new import must prove use again; activation with
the texture still missing fails strict import.

## Support Contract

Every source feature and property has one of three outcomes:

| Outcome | Meaning |
| --- | --- |
| Exact | Authored values, render state, bindings, and shader behavior are represented directly. |
| Native equivalent | The material intent is implemented through an XRENGINE subsystem with a documented semantic difference. |
| Preserved inactive | The value remains in versioned source metadata and the report explains why no runtime path was enabled. |

There is no best-effort mode for recognized 9.3.64 materials. The conversion
report is the authority for the outcome of each enabled feature. Unsupported or
unavailable integrations are visible and never sampled from fabricated data.

## Lossy Poiyomi Pro Downgrade

Poiyomi Pro-authored material ingestion is an explicitly lossy migration path,
separate from the pinned Toon parity contract above. It does not make Poiyomi
Pro a supported shader family and it does not claim visual or behavioral
parity.

The matcher recognizes unlocked Pro shader evidence, optimizer-locked Pro
signatures, source version markers, and locked Grab Pass variants. A recognized
Pro material is normalized to properties shared with the Toon converter and
receives conversion outcome `Downgraded` plus diagnostic `POIPRO0001`.
Common portable state is retained, including base color/texture, normal data,
emission, alpha/render state, culling, texture transforms, and other fields
already understood by the Toon descriptor.

These Pro-only property groups are removed from runtime conversion:

- Grab Pass;
- refraction;
- blur;
- touch effects;
- Pro-only integrations;
- Pro-only vertex effects;
- Pro authoring and locking metadata.

Each active discarded group emits `POIPRO0002` with its group and source
property. Dormant serialized values remain available through the preserved raw
Unity material document and import diagnostics, but they do not enable shader
features, samplers, or descriptors. The repository contains only synthetic
locked-style tests; no Poiyomi Pro package asset or parity corpus is
redistributed.

## Runtime Parity

| Feature set | XRENGINE outcome | Important semantic difference |
| --- | --- | --- |
| Base color, normal, alpha, masks, detail, decals, emissions, flipbook, dissolve, parallax | Exact portable shader modules | Unity texture metadata is normalized into explicit engine sampler and color-space metadata. |
| Toon ramps and shading modes | Native equivalent | Evaluated with XRENGINE Forward+ light records rather than Unity ForwardBase/ForwardAdd macros. |
| PBR, specular, clear coat, reflections | Native equivalent | Uses engine BRDF, reflection probes, and environment services. |
| Matcaps, rim, backlight, subsurface, glitter | Native equivalent | Uses stereo-safe engine view vectors and portable derivative/noise behavior. |
| Outlines | Native equivalent | An inverse-hull engine material pass shares the same authored state and pass identity. |
| Render presets, blend/depth/stencil/fog/queue state | Exact where representable; otherwise reported native difference | Unity ForwardAdd is folded into Forward+ lighting rather than emitted as an additive light pass. |
| Static and animated properties | Exact intent | Static values specialize shader source; animated values remain live uniforms and bindings. |
| Locked and unlocked materials | Exact descriptor equivalence | XRENGINE prepares variants without destructive Unity shader locking. |
| AudioLink | Native adapter | Requires `IAudioLinkProvider`; absent providers preserve state and emit one material diagnostic. |
| LTCGI and Light Volumes | Native adapter | Requires `IMaterialEnvironmentProvider`; no provider means preserved inactive. |
| Mirror and camera visibility | Native view-context adapter | Uses `MaterialViewContextScope` and stereo-safe engine view flags. |
| VRChat, Udon, and game-specific inputs | Preserved inactive unless an explicit service exists | No Unity/VRChat runtime code is executed inside the engine. |

The exhaustive property, annotation, and workflow inventory is embedded as
`Importers/Poiyomi/Catalogs/poiyomi-toon-9.3.64.json`. The generated human-
readable table is [Poiyomi Toon 9.3.64 Parity](../../reference/rendering/poiyomi-toon-9.3.64-parity.md).

## Static, Animated, And Renamed Properties

Properties default to `Static`. Their current values become deterministic GLSL
literals and participate in the variant hash. Changing a static value requests a
new variant and prewarm entry.

`Animated` properties stay as uniforms. Animation import remaps Unity property
names to stable semantic bindings. Renamed animated properties keep the source
alias and the semantic destination so locked and unlocked materials resolve the
same binding. Moving a property back to static captures its last runtime value.

Feature membership remains compile-time removable. A disabled feature retains
its authored values but contributes no live code, samplers, descriptors, or
animation uniforms.

## Sampler And Descriptor Limits

The guaranteed baseline for both OpenGL 4.6 and Vulkan 1.0 is 16 fragment
samplers, 16 sampled images, and 16 KiB of uniform data. Vulkan additionally
guarantees 128 bytes of push constants.

`UberMaterialBindingPlanner` uses the first faithful rung:

1. direct samplers;
2. compatible texture arrays;
3. the material texture table;
4. bindless descriptors;
5. explicit unsupported result.

Arrays are used only when texture role, dimensionality, color space, and sampler
state remain compatible. The converter never drops textures to meet a limit. An
over-limit material receives a deterministic failure reason before rendering.

## Conversion Diagnostics

| Code | Meaning | Remediation |
| --- | --- | --- |
| `POI0001` | Source version or identity is unsupported. | Supply verified 9.2.66, 9.2.79, or 9.3.64 source evidence, or add reviewed support for the new version. |
| `POI0002` | Locked shader matched only by an ambiguous signature. | Restore the original shader GUID/version metadata and reimport. |
| `POI0003` | Runtime-visible property lacks classification. | Update the catalog and parity fixture; conversion must not ship in this state. |
| `POI0004` | Catalog identity/hash mismatch. | Regenerate from the pinned commit and review the source audit. |
| `POI0005` | Runtime integration is unavailable. | Register the required provider or accept preserved-inactive behavior. |
| `POI0006` | Recognized runtime mapping is unavailable. | Inspect preserved source metadata and add a semantic mapping if runtime behavior is required. |
| `POI0007` | Source value could not be preserved. | Correct malformed YAML/metadata; this is a conversion error. |
| `POI0008` | Render state has an intentional native difference. | Review the pass-state section of the report and visual references. |
| `POI0009` | Animation binding was not mapped. | Restore the source property/rename metadata or add a catalog alias. |
| `POI0010` | Intentional native equivalent. | Review the documented semantic difference; no action is normally required. |
| `POI0011` | Asset reference is missing. | Restore the referenced Unity asset or replace the material slot. |
| `POI0012` | Texture asset is incompatible with its role. | Correct dimensionality/import classification and reimport. |
| `POI0013` | Requested UV channel is absent. | Import a mesh containing that UV channel or change the material selector. |
| `POI0014` | Enum value is outside the pinned schema. | Correct the source value or add a reviewed version-specific mapping. |
| `POI0015` | Pass prewarm failed. | Inspect shader diagnostics; the last-known-good variant remains active. |
| `POIPRO0001` | A Pro-authored material was accepted through the lossy Toon downgrade. | Review the downgrade report; use an authored Toon material when exact conversion is required. |
| `POIPRO0002` | An active Pro-only feature group was discarded. | Replace the feature with an engine-native equivalent or accept the documented visual difference. |

## Authoring And Security

The ImGui material inspector compiles the pinned UI catalog into a stable-ID
schema tree. It supports nested sections, raw/localized search, conditions,
typed actions, specialized controls, presets, versioned clipboard data, texture
packing/arrays, gradients/curves, decal positioning, multi-material editing,
semantic links, notes, animation modes, and variant preparation.

Imported labels, expressions, actions, URLs, paths, locale data, presets, and
clipboard payloads are untrusted. Parsing is bounded. Shader metadata cannot
instantiate types, invoke reflection, execute code, fetch remote content, or
write outside approved asset roots. URL, remote-content, editor-command, and
generated-output operations require explicit allowlisted policy. Mutations use
one undo transaction and generated assets are committed atomically.

Third-party shaders extend the editor by registering an engine-owned widget or
tool ID with `ShaderAuthoringWidgetRegistry` during editor startup. The
registration supplies a typed capability and engine delegate. Shader source can
reference the ID but cannot provide executable code. Unknown IDs render a visible
unsupported node and remain inert.

## Reimport And Overrides

Imported values and local overrides are separate layers. Reimport refreshes the
source descriptor, report, and untouched imported values while preserving local
notes, links, recipes, generated assets, and explicit overrides. Preview state
is transient and never persists across restart. Schema migration uses stable
semantic IDs; missing nodes are reported instead of rebound by display label.

## Multilayer Lighting And Tile Coverage

Source lighting mode 1 uses three shadow colors and color textures. It retains
each layer's border, blur, and shadow receive value. It also retains the blur
map, strength mask, border color and range, base-color blend, environment blend,
and nonlinear edge option. Missing source texture keywords use white samples.
Source color normalization runs before parameter binding. Native and authored
Uber shading share the layer composition. The first band also controls source
matcap and rim lighting. `_FirstShadeMap` and `_SecondShadeMap` belong to source
mode 4; they do not supply mode 1 layers.

Border maps use separate RGB remap ranges and can modify the lighting input or
the unclamped bands. The final clamp follows the post-edge mask. Equal remap
limits use the source 0.001 upper-limit adjustment; reversed limits remain valid.
Conversion enables the border operation only when the source texture is assigned.
Direct edits to the converted enable value can activate its white fallback.
The source texture uses two fixed repeat sampler roles: bilinear at LOD zero and
trilinear for positive LOD. Positive LOD floors the UV gradients by `pow(lod, 4)`.
Both roles use the same source UV selector, scale, offset, and pan.

Flat-mask mode blends a fixed-direction band with the surface bands through the
strength texture's red channel. Strength-mask mode instead multiplies shadow
strength. The imported fixed axis reflects source Z. Each draw transforms this
axis through its retained skin-root or component basis, independently of the
geometry matrix. Admission rejects mask types other than zero and one. The first band
used by matcap, rim, and glitter receives these same mask operations.
Engine local-light attenuation and reconstructed shader derivatives can differ
from Unity.

Native Advanced tile discard supports UV0 and UV1. Mode 0 rejects a whole
triangle when any original corner selects an enabled tile. Mode 1 tests the
interpolated pixel UV. Mode 0 uses tile IDs computed before half precision UV
packing. Camera visibility and native directional shadows use the same triangle
predicate. Authored late passes and authored Vulkan shadow passes reject active
mode 0 because they do not bind the canonical triangle data. This includes the
current point and spot shadow atlas paths.

## Source Texture Arrays

Unity YAML `Texture2DArray` assets can supply inline DXT1, DXT3, or DXT5
image data in sRGB or UNorm format. Conversion decodes each layer and its
authored mip chain to RGBA8. It preserves the array dimensions, layer order,
color space, filtering, wrapping, anisotropy, and mip bias. It does not generate
replacement mip levels. Unsupported formats, streamed payloads, inconsistent
metadata, and invalid byte counts produce explicit texture diagnostics.

The source flipbook path uses one shared calculation in authored Uber and
Advanced native Uber. It supports timed and manual frames, start/end bounds,
crossfade, array UV selection and transforms, placement, rotation, aspect ratio,
edge coverage, symmetry, independent masks, color and alpha controls, hue, and
emission. The source operation disables the separate legacy atlas overlay.
Mirrored-UV controls, a second source array, global-mask mixing, theme colors,
and active AudioLink inputs still require a separate mapping and receive an
explicit diagnostic. Native admission requires the source playback contract.
Negative frame values use the source signed remainder rule. In shadow and
outline passes, only alpha mode 1 changes coverage. Color replacement in alpha
mode 2 applies to the color pass.

## Glitter Distance Filtering

Verified Toon 10 reflection and light glitter modes use the source distance-LOD
cell reduction and the unmodified UV footprint for flake coverage. Fragment
shading uses derivatives; compute shading uses the reconstructed pixel UV
gradients. The source four-layer limit applies. Linear glitter response,
full-coverage particles, and sprite-sheet particles remain unsupported.

## Validation

Do not accept a completed frame as proof that imported textures are ready.
During loading, color textures can contain an 8x8 red/blue procedural preview.
Before a parity capture, inspect `get_texture_streaming_summary` and
`list_texture_streaming_textures`. Wait for import scopes, decode queues, GPU
uploads, and pending residency transitions to settle. Require ready visible
texture previews and no texture validation failures. Preserve the readiness
snapshot with the image.

Run the complete automated and live-backend acceptance path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Validate-PoiyomiParity.ps1
```

The runner tests the corpus and contracts, compiles OpenGL/Vulkan and VR shader
permutations, waits for non-empty final targets, captures multiple camera views,
records profiler and streaming telemetry, and rejects shader, validation, or
resource-lifetime errors.
