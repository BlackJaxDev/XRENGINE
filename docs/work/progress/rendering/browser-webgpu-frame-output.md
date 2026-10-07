# Canvas output, visibility and live resource updates

Source implementation on `codex/webgpu-readiness-audit`, September 28, 2026.
Builds, tests, browser execution and device validation were explicitly deferred by
the user. No rendering correctness or performance result is asserted.

## Output and pass

The portable profile now includes the existing `RenderFrameOutputDescription` and
its canonical core scheduling DTOs, preserving their assembly identities. The
browser renderer describes only a ready, drawable canvas. Metadata includes
physical extent, surface generation, one layer/sample/logical frame slot, logical
RGBA8 and depth-component formats, and exact `rgba8unorm` or `bgra8unorm` plus
`depth24plus` encodings. Depth storage precision remains implementation-defined.
The existing positional output-property constructor and deconstruction are unchanged.

This metadata query is not a GPU acquisition or completion fence. SchedulingRequest
remains unspecified: the browser host owns animation-frame scheduling. Split views
are rectangles in one color layer, not separate array layers. The executor acquires
the current canvas texture only while submitting a frame; no GPU handles cross the
managed contract. A configured color encoding cannot change within one target lifetime.

The binary packet is version 2 with a 96-byte header and unchanged 112-byte indexed
draw records. Clear RGBA occupies bytes 64–79, clear depth bytes 80–83, physical
width/height bytes 84–91, and reserved bytes 92–95 remain zero. Only finite normalized
RGB, opaque alpha and depth clear 1 are accepted. Both consumers check output extent
and generation before GPU encoding. The canvas pass clears and stores color, clears
depth and discards it after rendering. This supports one opaque pass, not arbitrary
framebuffer or render-graph lowering. Loader and shader-package manifest require the
same packet version; old packets and manifests are rejected.

## Scene changes and collection

`BrowserSceneSession` collects real scene components using their current render
matrices. Each viewport conservatively rejects local mesh AABBs against the six
homogeneous clip planes, using WebGPU's zero-to-one depth interval. No per-candidate
array is created. Disabling culling retains all eligible components; this remains
CPU-direct indexed rendering, without occlusion or GPU-indirect claims.

`SetCamera` updates cached view/projection matrices. `ReplaceRenderableResources`
acquires replacement immutable descriptors before publishing them to a component.
Reference counts preserve mesh/material sharing and textures shared by materials.
Removal stops collection and releases ownership. Backend handles invalidate
immediately and physical GPU resources retire after submitted work completes.
These are explicit browser-scene APIs, not automatic subscriptions to arbitrary
desktop `XRMesh`, `XRMaterial`, or `XRCamera` mutations.

The page exposes a culling toggle and material recolor action. Explicit counter
capture includes last-frame visibility counts across views, retained resource counts
and canvas output metadata. JSON serialization occurs only on capture, outside the
frame loop. Empty imported scenes reject the recolor action without stopping rendering.

## Remaining work

- Generic engine resource wrappers and render-pass lowering, plus automatic world,
  camera, material and engine render-buffer publication.
- Production shader/material cooking, lighting, transparency, shadows and broader
  cooked content loading beyond the unlit static snapshot subset.
- Build/publish and runtime acceptance, malformed/stale packet exercises, lifecycle
  and resource-retirement evidence, desktop preservation and physical-device budgets.

The [active TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md) checks source
completion separately from runtime acceptance. The preceding source delivery is
[module and snapshot integration](browser-webgpu-module-assets.md).
