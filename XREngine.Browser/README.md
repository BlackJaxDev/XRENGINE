# Browser canvas host

This standalone application composes the engine's shared scene/component/transform
runtime with a browser-owned WebGPU canvas. A ticking `XRComponent` rotates and
moves a mesh group, and the canvas can render two camera views of the same scene.
Indexed geometry, opaque unlit materials and RGBA8 textures use a versioned binary
packet bridge with one managed-to-JavaScript submission per rendered frame.

**Status:** Canvas, mesh/material, packet and cooked-shader loading code is implemented but has not been built or run.
Validation was explicitly deferred for this change. Earlier scene-only results
in [portable scene boot](../docs/work/progress/rendering/portable-browser-scene-boot.md)
do not qualify this renderer.

## Build and run

Install the .NET 10 SDK and its `wasm-tools` workload, then run from the repository
root. The checked-in shader package is ready for publishing. After editing WGSL
or its recipe, regenerate it first using Python 3.10 or later:

```sh
python3 Tools/Shaders/cook_browser_shaders.py
dotnet workload install wasm-tools
dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -p:XREnginePortableRuntime=true -m:1
```

Use the portable property on restore and build commands as well. It selects the
same source profile throughout the graph. The browser project remains outside
the default desktop solution, so desktop builds do not require the WASM workload.
Trimming and AOT remain disabled and unqualified.

Serve `XREngine.Browser/bin/portable/Release/net10.0/publish/wwwroot` through HTTPS
or localhost HTTP, with `.wasm` served as `application/wasm` and `.wgsl` as text.
Publish the entire `wwwroot/shaders` directory with the application. Revalidate
`shaders/manifest.json`; hash-named `.shader.json` and `.wgsl` files may be cached
immutably. The loader rejects missing, stale, oversized or incompatible artifacts
before device creation. Open the server URL, not a `file://` URL. The host requires `navigator.gpu` and a
usable adapter. `?renderer=WebGPU` and `?renderer=Auto` select the packaged WebGPU
path; `WebGL2` and unknown renderer names produce a diagnostic. No fallback
renderer is packaged. The shader package declares the initial profile
requirements; adapter limits are checked before requesting the device.

- Drag on the canvas or focus it and use arrow keys to move the scene.
- Toggle **Split view** to switch between one view and two camera projections.
- Select **16**, **64**, or **256** mesh instances. These are separate indexed draws,
  not GPU instancing. **Capture counters** snapshots packet/draw counts, copied and
  uploaded bytes, storage growth, rejected packets and executor call counts.
- **Stop** cancels frames and startup, removes session listeners/observers, and
  releases the scene and GPU resources. **Restart** constructs a fresh session.
- Startup, shader, device-loss and render errors appear in the status area and
  browser console. Device loss requires an explicit restart.

## Ownership and scheduling

`BrowserCanvasHost` owns one supplied canvas and one WebGPU executor. It passes
surface/input snapshots to `BrowserSceneSession` through generated .NET interop.
The shared runtime routes imports by monotonically increasing session IDs;
canvas contexts and devices are instance-owned. One runtime factory can create
additional hosts without replacing an existing device.

`BrowserCanvasRenderTarget` implements the portable presentation and surface
contracts. It requests `BrowserCanvasPresentation`, never native WSI. Resize
publishes physical dimensions and a generation; each submitted frame must match
that generation. DPR is capped at 2 and backing dimensions are bounded by the
device limit. Zero-sized/detached canvases are unconfigured and suspended;
reattachment resumes with a new generation. Hidden pages pause scheduling and
reset simulation time. Focus loss releases held input. Page restoration from the
back/forward cache starts a fresh device and scene.

The JavaScript frame callback enters managed code once per animation frame.
Managed code performs up to four 1/60-second simulation steps, drains deferred
scene destruction, and renders the latest transform at the display cadence.
Excess elapsed time is dropped. There are no task waits, native event loops,
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

The executor remains a focused browser-app leaf. Existing `XRMesh`/`XRMaterial`
adapters, full engine camera/visibility and render-buffer publication, renderer
module registration, cooked resources and production shading are still required.
The original `SceneBoot` exports remain available as a separate lifecycle fixture.
The default page runs the continuous mesh scene. The packet is explicitly copied
from transient .NET memory into reusable JavaScript storage; it is not zero-copy.

The portable source allowlists retain engine assembly/type identities and isolate
outputs under `bin/portable` and `obj/portable`. Desktop bootstrap/native
integrations remain outside this graph. The existing portability guard continues
to check project/package/native-asset boundaries; it is not a forbidden-API proof.

See [mesh and packet implementation notes](../docs/work/progress/rendering/browser-mesh-packet-bridge.md)
for remaining work and the explicit validation deferral.

See [cooked shader artifact notes](../docs/work/progress/rendering/browser-shader-artifacts.md)
for recipe limits, identities, compiler-contract compatibility and deferred validation.
