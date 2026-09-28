# Browser canvas host

This standalone application composes the engine's shared scene/component/transform
runtime with a browser-owned WebGPU canvas. A ticking `XRComponent` rotates and
moves a mesh group, and the canvas can render two camera views of the same scene.
Indexed geometry, opaque unlit materials and RGBA8 textures use a versioned binary
packet bridge with one managed-to-JavaScript submission per rendered frame.

**Status:** Canvas, registered WebGPU module, static engine snapshot bridge, frame-output/pass contracts, culling, live resource updates, batched uploads, packet and cooked-shader loading code are implemented but have not been built or run.
Validation was explicitly deferred for this change. Earlier scene-only results
in [portable scene boot](../docs/work/progress/rendering/portable-browser-scene-boot.md)
do not qualify this renderer.

## Build and run

Install the .NET 10 SDK and its `wasm-tools` workload, then run from the repository
root. Checked-in shaders and registrations are ready for publishing. The portable
source guard runs as a C# MSBuild task using the SDK; no Python process is invoked.

```sh
dotnet workload install wasm-tools
dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -p:XREnginePortableRuntime=true -m:1
```

Use the portable property on restore and build commands as well. It selects the
same source profile throughout the graph. The browser project remains outside
the default desktop solution, so desktop builds do not require the WASM workload.
Trimming and AOT remain unqualified and are rejected by the portable build guard.

Serve `XREngine.Browser/bin/portable/Release/net10.0/publish/wwwroot` through HTTPS
or localhost HTTP, with `.wasm` served as `application/wasm` and `.wgsl` as text.
Publish the entire `wwwroot/webgpu` directory with the application. Revalidate
`webgpu/shaders/manifest.json`; hash-named `.shader.json` and `.wgsl` files may be cached
immutably. The loader rejects missing, stale, oversized or incompatible artifacts
before device creation. Open the server URL, not a `file://` URL. The host requires `navigator.gpu` and a
usable adapter. `?renderer=WebGPU` and `?renderer=Auto` select the packaged WebGPU
path; `WebGL2` and unknown renderer names produce a diagnostic. No fallback
renderer is packaged. The shader package declares the initial profile
requirements; adapter limits are checked before requesting the device.

- Drag on the canvas or focus it and use arrow keys to move the scene.
- **Import scene** loads flat v1 static scene JSON exported with `BrowserSceneSnapshot.ToJson()` or a stable-ID `xre.browser.scene.v1` envelope (16 MiB maximum). The captured camera fills the canvas; its aspect can stretch on a differently shaped canvas. **Demo scene** returns to the interactive fixture. Imported scenes disable demo density and split-view controls.
- Toggle **Cull offscreen meshes** to control conservative per-view AABB rejection. **Recolor first mesh** replaces one material while retaining its texture. Empty imported scenes reject that action explicitly. Counter snapshots include last-frame candidates/culled/drawn counts, live resource counts and output metadata.
- Toggle **Split view** to switch between one view and two camera projections.
- **Update checker texture** streams retained demo pixels through one upload batch. This updates GPU contents; restart restores the immutable descriptor. Imported scenes disable this fixture control.
- Select **16**, **64**, or **256** mesh instances. These are separate indexed draws,
  not GPU instancing. **Capture counters** snapshots packet/draw counts, copied and
  uploaded bytes, storage growth, rejected packets and executor call counts. Snapshots also include upload command counts, submission calls, packet failure context, capability limits and managed current-thread frame allocation counters; these are instrumentation, not measured performance evidence.
- **Stop** cancels frames and startup, removes session listeners/observers, and
  releases the scene and GPU resources. **Restart** constructs a fresh session.
- Startup, shader, device-loss and render errors appear in the status area and
  browser console. Device loss requires an explicit restart.

## Ownership and scheduling

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
static snapshot and explicit live browser-scene changes. `SetCamera` replaces cached camera matrices; `ReplaceRenderableResources` acquires new shared resources before changing a component; `RemoveRenderable` stops submission and releases references. Mesh/material/texture descriptions remain immutable. Conservative culling uses each mesh AABB and current render transform independently for each viewport. Full engine visibility/render-buffer publication, generic resource/pass wrappers, automatic live desktop-asset binding and production shading remain open.
The original `SceneBoot` exports remain available as a separate lifecycle fixture.
The default page runs the continuous mesh scene. The packet is explicitly copied
from transient .NET memory into reusable JavaScript storage; it is not zero-copy.

The portable source allowlists retain engine assembly/type identities and isolate
outputs under `bin/portable` and `obj/portable`. Desktop bootstrap/native
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

See [portable host completion](../docs/work/progress/rendering/browser-portable-host-completion.md)
for generated registrations, source/API guards, frame publication and the remaining
acceptance work. Regenerate the checked-in browser registry after changing its
manifest with `python3 Tools/Generate-BrowserRegistrations.py`; neither the registry
nor scene JSON serialization performs runtime assembly scanning. The portable source
guard documents its lexical limits and reviewed exceptions; an evaluated inventory
still needs to be captured from the restored build graph.
