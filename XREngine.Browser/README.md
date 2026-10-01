# Browser runtime entry points

## Authored engine player

The editor's `BrowserWebGPU` target installs `engine-player.html` as the site
entry point. It loads the canonical cooked `XRWorld` and statically linked game
assembly through the shared runtime. This path is still under qualification;
the production `DefaultRenderPipeline` WebGPU output remains explicitly gated
until its required lit material and pass routes are available. See the
[current checkpoint](../docs/work/progress/platform/unified-browser-checkpoint-2026-10-01.md)
for exact build/runtime evidence and remaining work.

Browser composition installs its built-in material target before deserializing
the world. Material semantics and parameters are retained without loading
desktop GLSL. Hash-owned `materialVariants` metadata is validated before play;
it does not make an absent renderer variant available.

A rendered local player owns the same `XRViewport` used for camera and UI
coordinates. `XRViewport.BindInputSource` accepts the externally owned browser
snapshot source, and `BindLocalPlayer` maintains player/view ownership.
Unbound desktop viewports still read their window's input. Browser pointer
publication converts CSS coordinates to canvas backing pixels.

Startup failures include their managed stage and full exception. An initial
resource-generation failure is reported immediately. If a visible canvas
otherwise cannot present its first frame after 45 seconds of active frame time,
the shell reports the renderer, pipeline-decline, and resource-failure state
instead of waiting indefinitely. Restart creates a fresh owner; unsupported
passes do not select a substitute renderer or simplified game runtime.

## Frozen reference harness

This standalone application composes the engine's shared scene/component/transform
runtime with a browser-owned WebGPU canvas. A ticking `XRComponent` rotates and
moves a mesh group, and the canvas can render two camera views of the same scene.
Indexed geometry, opaque/masked/transparent materials and RGBA8 textures use a
versioned binary packet bridge with one managed-to-JavaScript submission per frame.
The focused CPU-direct pipeline adds unlit/flat Lambert shading, directional
shadows, sky/ambient lighting, HDR tonemapping and GPU-composed UI rectangles.

**Status:** Canvas, registered WebGPU module, static engine snapshot bridge, frame-output/pass contracts, culling, live resource updates, batched uploads, cooked-shader loading and focused forward-pipeline code are implemented. The host compiles and publishes as of 2026-09-30 ([build record](../docs/work/progress/platform/unified-runtime-build-stabilization.md)). Local browser smokes render the reference fixture and an editor-published minimal authored world; captures and limitations are recorded in the [harness investigation](../docs/work/investigations/rendering/desktop-browser-reference-harness.md). Physical-device, recovery and performance qualification remain open.

## Build and run

Install .NET SDK **10.0.401** and its `wasm-tools` workload, then run from the repository
root. `global.json` requires that exact SDK and workload set **10.0.401.1**;
the set supplies WebAssembly runtime pack 10.0.12 and Emscripten 3.1.56. Run
workload installation from the repository so it uses the pin. Checked-in shaders
and registrations are ready for publishing. The portable
source guard runs as a C# MSBuild task using the SDK; no Python process is invoked.

```sh
dotnet workload install wasm-tools
dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -m:1
```

`Directory.Build.props` marks the browser and its shared project closure with
`XREnginePortableProject=true`; they compile their complete source sets. No
separate portable build property or source profile is needed on restore or build.
The browser project remains outside the default desktop solution, so desktop
builds do not require the WASM workload. Trimming and AOT remain unqualified and
are rejected by the portable build guard. See [portable project rules](../docs/developer-guides/runtime/portable-projects.md).

Serve the `wwwroot` directory in the publish output reported by the SDK through HTTPS
or localhost HTTP, with `.wasm` served as `application/wasm` and `.wgsl` as text.
This direct browser publish is the developer harness (`index.html` and `main.js`),
including the interactive demo and diagnostic controls. An editor **Build Project**
publish with the `BrowserWebGPU` target instead installs `engine-player.html` as
`index.html` and removes `main.js` from its staged site. The player requires the
generated `browser-publish.json` to name a cooked startup world and reports an
error if the world is absent. It shows load progress and failures, supports gesture
audio activation and explicit restart, and hides the current fixture overlay.
No demo scene or diagnostic controls are included in that published entrypoint.
Back/forward-cache page suspension keeps the authored world, stops frame
submission, resets input and elapsed timing, and resumes through the same surface
lifecycle on `pageshow`. A discarded page instead requests engine teardown.
The focused page-state probe passes; real browser history/cache behavior remains
part of lifecycle qualification.
The separate browser scene, animation, collision and focused pipeline are a
frozen reference harness. Changes may fix harness defects; new engine features
follow the [unified runtime design](../docs/work/design/platform/unified-desktop-browser-runtime-design.md).
Publish the entire `wwwroot/webgpu` directory with the application. Revalidate
`webgpu/shaders/manifest.json`; hash-named `.shader.json` and `.wgsl` files may be cached
immutably. The loader rejects missing, stale, oversized or incompatible artifacts
before device creation. Open the server URL, not a `file://` URL. The host requires `navigator.gpu` and a
usable adapter. `?renderer=WebGPU` and `?renderer=Auto` select the packaged WebGPU
path; `WebGL2` and unknown renderer names produce a diagnostic. No fallback
renderer is packaged. The shader package declares the initial profile
requirements; adapter limits are checked before requesting the device.

An editor-published engine-asset manifest may carry `materialVariants` entries
for exact semantic, target, pass, vertex-profile, and output-profile keys. Each
entry refers to one hash-verified shader descriptor containing the same explicit
declaration. Browser startup rejects duplicate keys, missing hashes, and
mismatched descriptors before activating the world. A missing catalog entry
does not trigger a shader-name or authored-source fallback.

Keep hash-addressed cooked shader payloads under `Assets/shaders/` as LF bytes.
Line-ending conversion changes their hashes and causes the loader to reject them.

- In the demo, drag the left canvas half to move and the right half to look. Focus
  the canvas for WASD/arrows, Space or an enabled standard gamepad. **Jump** is also
  touch accessible. Text/IME input stays with DOM fields.
- **Enable audio** activates Web Audio from a gesture and plays a sample pulse.
  Load a bounded **Audio clip**, optionally loop it, adjust volume or pause it.
  Browser codec/permission failures remain visible. **Apply label** changes the
  engine scene-node name from committed text.
- **Import scene** loads static scene JSON exported with `BrowserSceneSnapshot.ToJson()` (versions 1 and 2) or a stable-ID `xre.browser.scene.v1` envelope (16 MiB maximum). Version 2 preserves authored alpha, shading, culling and shadow policies. The captured camera fills the canvas; its aspect can stretch on a differently shaped canvas. **Demo scene** returns to the interactive fixture. Imported scenes disable demo density and split-view controls.
- Toggle **Cull offscreen meshes** to control conservative per-view AABB rejection. **Recolor first mesh** replaces one material while retaining its texture. Empty imported scenes reject that action explicitly. Counter snapshots include last-frame candidates/culled/drawn counts, live resource counts and output metadata.
- Toggle **Split view** to switch between one view and two camera projections.
- **Update checker texture** streams retained demo pixels through one upload batch. This updates GPU contents; restart restores the immutable descriptor. Imported scenes disable this fixture control.
- Select **16**, **64**, or **256** mesh instances. Consecutive compatible records
  are batched into indexed instanced draws. **Capture counters** snapshots packet/draw counts, copied and
  uploaded bytes, storage growth, rejected packets and executor call counts. Snapshots also include upload command counts, submission calls, packet failure context, capability limits and managed current-thread frame allocation counters; these are instrumentation, not measured performance evidence.
- **Stop** cancels frames and startup, removes session listeners/observers, and
  releases the scene and GPU resources. **Restart** constructs a fresh session.
- Startup, shader, device-loss and render errors appear in the status area and
  browser console. Device loss requires an explicit restart.
- Select **Low**, **Balanced**, or **High** quality to set backing resolution/DPR,
  shadow resolution/cadence, texture limits and HDR output. Toggle **Show overlay**
  for engine-composed UI. DOM controls retain accessible labels and keyboard focus.

## Ownership and scheduling

Use **Cooked world** or `?world=./content/manifest.json` to load a same-origin
package produced by `Tools/BrowserContentCooker`. Essential dependency closures
load first; later scene chunks integrate one bounded asset per animation frame.
The loader checks schema/profile, byte budgets and SHA-256 before passing bytes
to the engine. Stop/restart cancels pending requests and drops stale session work.

The offline packager accepts preconverted mesh/material/scene JSON and full raw
texture mip chains. Supported device-enabled ASTC 4×4 or ETC2 RGBA8 variants can
be selected with a matching RGBA8 fallback. Conversion, native imports, collision
cooking and font generation stay offline; no runtime Basis transcoder is included.
The fixed material sampler is linear with clamp-to-edge addressing. Payloads use
immutable hash URLs; the bootstrap is revalidated. Persistent browser storage is
not required.

See [cooked content delivery](../docs/work/progress/rendering/browser-cooked-content.md)
and the [cooker README](../Tools/BrowserContentCooker/README.md) for contracts,
limits, deployment, memory accounting and remaining acceptance work. Runtime and
cache qualification remain deferred.

The interactive demo adds explicit static-box character collision and a two-bone
CPU-skinned reference whose idle/walk blend follows movement. Static imported worlds
do not inherit the demo collision/animation data. Manifest schema 2 can declare
required/optional services; schema 3 supplies baked animation and static collision
payloads. Animated instances share immutable inputs but own separate output meshes
and playback state. The player uses engine transforms and authored cadence; CPU
and compute deformation consume the same packed skin/morph records. Collision uses
the existing fixed-step character and explicit cooked boxes. Unsupported required services fail, and required audio
waits for a running user-activated context. See [interaction and runtime services](../docs/work/progress/rendering/browser-interactive-services.md)
for selected profiles, limits, source inventory and remaining acceptance.

`BrowserRendererComposition` registers `WebGpuRendererBackendModule` in the existing
`RendererBackendCatalog`. Each canvas creates a pending managed renderer through
that catalog and uses its `IBrowserRendererHost` capability for resources and
packets. `BrowserCanvasHost` owns one supplied canvas and one module-owned WebGPU
executor; it acknowledges managed readiness after asynchronous device startup. It passes
surface/input snapshots to `BrowserSceneSession` through generated .NET interop.
The shared runtime routes imports by monotonically increasing session IDs;
canvas contexts and devices are instance-owned. One runtime factory can create
additional hosts without replacing an existing device.

`BrowserCanvasRenderTarget` implements the portable presentation and surface
contracts. It requests `BrowserCanvasPresentation`, never native WSI. Resize
publishes physical dimensions and a generation; each submitted frame must match
that generation and extent. `TryDescribeFrameOutput` publishes the existing output contract only while the configured surface is drawable; it does not acquire a GPU texture. The logical RGBA8/depth formats include exact backend encoding metadata, one layer/sample and logical slot zero. The executor alone acquires the canvas texture at submission. Packet v2 supplies an opaque clear color and standard depth clear; color is stored and depth discarded. DPR is capped at 1.5 and the longest backing edge at 1280 pixels, bounded further by the device limit. Zero-sized/detached canvases are unconfigured and suspended;
reattachment resumes with a new generation. Hidden pages pause scheduling and
reset simulation time. Focus loss releases held input. Page restoration from the
back/forward cache starts a fresh device and scene.

The JavaScript frame callback enters managed code once per animation frame.
Managed code performs up to four 1/60-second simulation steps, drains deferred scene destruction, publishes the engine transform buffers once, freezes a reusable renderable snapshot and renders it at display cadence. Long time gaps reset timing and increment history generation; ordinary variable render delta is available in diagnostics. Structural/resource changes during a frame are rejected. The existing one-argument `RuntimeSceneHost.Advance` still publishes transforms for other callers. There are no task waits, native event loops,
worker dispatches, or per-frame GPU completion waits.

The renderer loads and hashes cooked shader metadata/source during startup,
checks the binding contract, creates its shader and pipeline asynchronously, caches
bind groups, descriptors and typed upload storage, and uses aligned per-draw
uniform slices with a single matrix upload per frame. WebGPU necessarily creates a fresh canvas view, command encoder,
render pass encoder and command buffer for each submitted frame. No .NET memory
view is retained across callbacks. Matrix fields use the System.Numerics row-vector
to WGSL column-vector convention and a 0..1 depth projection.

## Scope

`BrowserMeshComponent` attaches immutable mesh/material descriptions to real engine
scene nodes. `BrowserSceneSession.AddRenderable` accepts indexed position/UV
geometry and optional textured materials, deduplicates resources by description
identity, and collects the component transforms into one frame packet for all
views. The default fixture shares geometry and materials across cube and quad
nodes. Resource handles validate kind, owner and generation; CPU invalidation is
immediate, while GPU retirement waits asynchronously for submitted work.

The executor and shaders belong to `XREngine.Runtime.Rendering.WebGPU`. Static
`XRMesh` and standard `XRCamera` export adapters plus explicit `XRMaterial` browser
recipes now feed the same shared resource descriptors. This supports a bounded
static snapshot and explicit live browser-scene changes. `SetCamera` replaces cached camera matrices; `ReplaceRenderableResources` acquires new shared resources before changing a component; `RemoveRenderable` stops submission and releases references. Mesh/material/texture descriptions remain immutable. Conservative culling uses each mesh AABB and current render transform independently for each viewport. Generic resource/pass wrappers are now implemented for the selected core profile; automatic arbitrary desktop-asset binding and production shading remain open.
The original `SceneBoot` exports remain available as a separate lifecycle fixture.
The default page runs the continuous mesh scene. The packet is explicitly copied
from transient .NET memory into reusable JavaScript storage; it is not zero-copy.

The portable project allowlists retain engine assembly/type identities while the
browser publish uses the SDK's normal output directories. Desktop bootstrap/native
integrations remain outside this graph. The existing portability guard continues
to check project/package/native-asset boundaries; it is not a forbidden-API proof.

See [mesh and packet implementation notes](../docs/work/progress/rendering/browser-mesh-packet-bridge.md)
for remaining work and the explicit validation deferral.

See [batched upload bridge](../docs/work/progress/rendering/browser-upload-bridge.md)
for `BrowserUploadBatch`, texture-region integration, vertex/index/tint commands,
bounded arenas, ownership, copy accounting and deferred acceptance evidence.

See [cooked shader artifact notes](../docs/work/progress/rendering/browser-shader-artifacts.md)
for recipe limits, identities, compiler-contract compatibility and deferred validation.

Use the [C# shader cooker](../Tools/ShaderCooker/README.md) for new shader packages;
no Python is required. [Shader cooking and material generation](../docs/work/progress/rendering/browser-shader-cooking.md)
documents the typed unlit generator, optional Slang 2026.8 route and schema 2
contracts. After cooking and publishing a named variant, `?shader=<artifact-name>`
selects it; only that pipeline is compiled. The existing checked-in default remains
schema 1. Shader compilation and pipeline creation have separate cancellable
45-second deadlines, with startup timings included in counter snapshots. The new
cooker and shader routes have not been executed or qualified for this delivery.

See [module and snapshot integration](../docs/work/progress/rendering/browser-webgpu-module-assets.md)
for the export API, supported subset, ownership and remaining integration work.

See [canvas output and live updates](../docs/work/progress/rendering/browser-webgpu-frame-output.md) for the output/pass ABI and remaining integration work.

See [GPU resources and ordered submission](../docs/work/progress/rendering/browser-gpu-resources-submission.md)
for the buffer/texture/view/sampler API, immutable selected-device capabilities,
framebuffer attachment lowering, bounded pipeline cache, reusable render/compute/copy
commands and cancellable asynchronous readback. Builds and browser/GPU acceptance
remain deferred; this core resource profile is not full baseline qualification.

See [compute and indirect commands](../docs/work/progress/rendering/browser-compute-indirect.md)
for storage-to-render ordering, device-limited compute, indirect draw argument and
usage rules, and the opt-in **Renderer counters → Run GPU reference cases** action.
The action pauses scene frames, performs bounded offscreen work and asynchronous
readback, and displays session-specific results. It has not been run for this
delivery. `?strategy=Auto` permits only `CpuDirect`; unsupported scene strategies
fail with a reason. No source addition establishes baseline qualification.

The [compute reuse audit and integration](../docs/work/progress/rendering/browser-compute-reuse-audit.md)
records reuse of existing palette/bounds contracts and canonical skinning/Hi-Z
algorithms. **Renderer counters** now offers explicit experimental `GpuIndirect`,
`ComputeCulling`, `HiZ`, `BvhCulling` and `BvhHiZ` submission plus `Compute` deformation, with a restart
action. Equivalent parameters are `?strategy=HiZ&skinning=Compute` (built-in
animated sample), `?world=./content/manifest.json&strategy=BvhHiZ&skinning=Compute`
(an animated schema-3 package), or `?strategy=ComputeCulling` (static/imported content). Required
compute deformation on a scene without admitted animation data fails visibly.
Automatic choices remain CPU-direct and CPU deformation. Hi-Z accepts only
explicitly designated opaque occluders; uncertain deformation bounds stay visible.

See [focused browser forward pipeline](../docs/work/progress/rendering/browser-focused-pipeline.md)
for material sorting, directional shadows, linear/HDR composition, binding/packet
contracts, UI atlas constraints and mobile quality presets. The module-owned forward
shaders are compiled during bounded startup; `?shader=` still selects the earlier
cooked compatibility bootstrap. The optional BVH uses canonical LBVH topology and
node layout with WebGPU construction/refit/traversal passes. PBR, full desktop GPUScene ownership and meshlets remain
outside this profile. Compute deformation and bounded indirect visibility are
explicit experimental backend paths described by the audit above.

See [portable host completion](../docs/work/progress/rendering/browser-portable-host-completion.md)
for generated registrations, source/API guards, frame publication and the remaining
acceptance work. Builds regenerate the browser registry under intermediate output
from `browser-registration-manifest.json` using the shared PowerShell factory
generator; neither the registry
nor scene JSON serialization performs runtime assembly scanning. The portable source
guard documents its lexical limits and reviewed exceptions; an evaluated inventory
still needs to be captured from the restored build graph.
