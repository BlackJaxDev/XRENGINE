# Focused browser forward pipeline

**Date:** 2026-09-28. **Status:** Source implementation. Builds, shader execution,
browser/GPU rendering and performance acceptance are deferred at the user's request.

The browser scene now submits through `BrowserRenderPipeline` and a dedicated
WebGPU executor. It selects an explicit CPU-direct forward profile before rendering.
It does not traverse the desktop advanced pipeline. Existing module, resource,
frame-output and scene ownership contracts remain in use.

## Selected rendering behavior

| Surface | Ordering | Depth and alpha |
| --- | --- | --- |
| Opaque | Per-view material/mesh grouping | Depth test/write; opaque output |
| Masked | Per-view material/mesh grouping after opaque | Texture alpha times tint compared to authored cutoff; depth test/write |
| Transparent | Stable back-to-front camera-depth order | Straight-alpha blending; depth test without writes |

Consecutive compatible mesh/material/index-range/viewport records are submitted as
indexed instances. Material grouping never reorders transparent surfaces across
depth order. Transparency sorts object bounds centers; intersecting transparent
triangles and order-independent transparency are outside this profile. Cull mode
is explicitly none, back or front; it is part of the pipeline variant.

The selected shading subset is unlit or flat Lambert, using the existing
position/UV geometry and world-space derivatives for flat normals. One directional
light supplies diffuse illumination, with authored constant ambient light and a
sky gradient. This does not claim smooth-normal PBR, image-based lighting or baked
lightmap support. Negative-scale and two-sided content must use an appropriate
authored culling policy.

One orthographic directional shadow map supports opaque and masked casters.
Transparent shadow casting is excluded explicitly. Camera-invisible casters remain
in the packet as shadow-only records; split view does not duplicate shadow work.
The scene owns the shadow matrix/volume through `SetEnvironment`; imported content
must provide an appropriate volume rather than assume the demo's bounds fit it.
Shadow resolution and update cadence are quality settings. The sampled matrix stays
paired with the last rendered map when an update is skipped.

Rendering proceeds through shadow, linear scene, exposure/optional Reinhard
tonemapping into linear SDR, engine UI composition, and final sRGB encoding into a
fresh canvas output. HDR scene color uses `rgba16float`; the low preset uses an
explicit linear `rgba8unorm` scene target. Depth uses standard zero-to-one depth,
clear 1 and less comparison. Reversed Z is not enabled. Frame output now reports
sRGB presentation, with exact canvas encoding still supplied by the browser.

## Material and binding ownership

`BrowserMaterialData` explicitly carries alpha mode, cutoff, shading, culling and
shadow participation. `WithTint` preserves those policies. The `XRMaterial` browser
recipe accepts the same authored subset. Scene snapshot version 2 persists it;
version 1 remains readable with its original opaque/unlit defaults. The legacy
unlit shader generator rejects richer recipe requests instead of emitting a shader
that ignores their semantics.

No desktop bindless texture handle enters the shader. The bounded layouts are:

| Group / stream | Contents |
| --- | --- |
| Frame group 0 | Light matrix, lighting/environment/output parameters and shadow sampling for scene rendering |
| Material group 1 | Existing linear tint buffer, one sampled texture and sampler |
| Policy group 2 | Alpha cutoff/mode, lighting and shadow reception |
| Vertex stream 0 | Position.xyz and UV.xy, stride 20 |
| Instance stream 1 | World and model/view/projection matrices, stride 128 |

Shadow rendering uses a uniform-only frame group to avoid sampling the active
shadow attachment. Pipeline variants are warmed before readiness; no shader
compilation occurs inside a frame. The fixed family has bounded alpha/culling/target
variants, and material residency is capped. Material creation/destruction acquires
and retires its backend policy buffer explicitly. Existing tint upload buffers are
shared with this path.

The focused raster/compose WGSL files are module-owned built-in shader sources.
They are fetched with bounded reads and compiled by the browser during bounded
startup. They are separate from the earlier hash-verified cooked unlit bootstrap;
`?shader=` selects that compatibility bootstrap artifact, not an arbitrary shader
for these forward passes. General cooked user-material expansion remains separate.

## Packet and frame lifetime

The focused packet has its own magic and version, preserving the earlier mesh
packet ABI. It contains a 256-byte header, 176-byte mesh records and 80-byte UI
records. Header data includes owner, surface generation, frame sequence, output
extent, lighting and shadow matrices. Each draw carries world and projected
transforms, resource handles, viewport, index range and shadow flags.

Managed collection uses retained arrays and a stable merge sort. Its bounded arena
grows only at idle scene mutation boundaries. The synchronous import copies bytes
into backend-owned storage, validates the whole packet, and writes retained vertex
instance/UI buffers. No borrowed managed memory survives submission. Per-frame
native WebGPU command encoders, pass encoders, command buffers and acquired canvas
views are necessary; per-draw managed collections and JSON are not allocated.

Failure/loss stops work and rejects late startup publication. Resize replaces
generation-owned targets; old textures retire after previously submitted GPU work.
The scene retains its fixed-step simulation, transform publication, camera culling,
resource reference counts and explicit allocation counters.

## Engine UI and atlas policy

`BrowserPipelineUiQuad` composes painter-ordered colored or textured rectangles
with physical-pixel rectangles, normalized UV origin/extent, tint and integer clip
rectangles. The sample overlay contains a panel, material swatches, a texture image
and a visibility bar. It is rendered by the engine shaders rather than a native UI
dependency. Browser DOM controls continue to own keyboard focus, text entry and
accessible control labels; engine GPU rectangles do not claim DOM accessibility.

Textures are independent material identities by default. UI atlas use is explicit:
the caller supplies a texture and UV region. Only homogeneous RGBA8, single-sample
2D images with compatible color-space/sampling intent are admitted. Authors must
provide filtering gutters for packed regions; the renderer does not silently pack
arbitrary material textures or create arrays. Font shaping, text layout and a
general interactive UI widget framework remain outside this composition primitive.

## Mobile quality

| Preset | Resolution scale / DPR cap | Backing dimension cap | Shadows | Texture dimension cap | Scene output |
| --- | --- | --- | --- | --- | --- |
| Low | 0.75 / 1 | 1024 | 512, every second frame | 1024 | Linear RGBA8, no tonemap |
| Balanced | 1 / 1.5 | 1280 | 1024, every frame | 2048 | RGBA16F, Reinhard |
| High | 1 / 2 | 1920 | 2048, every frame | 4096 | RGBA16F, Reinhard |

Presets allow one directional light, unlit/flat Lambert materials, up to 256 live
materials and optional UI. Lower-level settings explicitly bound light count,
material complexity/residency, RGBA8 texture tier, HDR and tonemapping. Device limits
are checked before startup and quality selection. A setting incompatible with live
materials or required limits fails by name; no silent material replacement occurs.
Disabled effects do not allocate a desktop effect chain. The host applies resolution
and DPR to backing size; custom hosts must apply those host-side quality fields too.

## Deferred acceptance and remaining scope

The demo includes asymmetric red/green/blue/yellow texture corners, masked and
translucent surfaces, a lit shadow receiver, split cameras and a GPU overlay for
later inspection. No build, test, shader cook/compiler run, browser render,
screenshot, benchmark or Python execution was performed for this delivery.

Known-value acceptance remains open for orientation, matrix layout, depth, color
encoding, masks, transparency order, shadows, HDR exposure/tonemapping and UI
blending. Device loss, repeated resize/quality changes, packet rejection and
allocation budgets also need runtime evidence. Code-completion bullets therefore
do not close G2 or the validation task.

The full browser baseline still needs GPU-driven scene submission, richer content
and materials, animation/UI interaction and the later runtime/platform work. Earlier
Slang original-source mapping and toolchain qualification gaps are unchanged.

Primary references: [WebGPU](https://www.w3.org/TR/webgpu/) and
[WGSL](https://www.w3.org/TR/WGSL/).
