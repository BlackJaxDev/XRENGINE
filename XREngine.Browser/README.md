# Browser canvas host

This standalone application composes the engine's shared scene/component/transform
runtime with a browser-owned WebGPU canvas. A ticking `XRComponent` rotates and
moves a triangle, and the canvas can render two camera views of the same scene.

**Status:** Canvas/WebGPU code is implemented but has not been built or run.
Validation was explicitly deferred for this change. Earlier scene-only results
in [portable scene boot](../docs/work/progress/rendering/portable-browser-scene-boot.md)
do not qualify this renderer.

## Build and run

Install the .NET 10 SDK and its `wasm-tools` workload, then run from the repository
root:

```sh
dotnet workload install wasm-tools
dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -p:XREnginePortableRuntime=true -m:1
```

Use the portable property on restore and build commands as well. It selects the
same source profile throughout the graph. The browser project remains outside
the default desktop solution, so desktop builds do not require the WASM workload.
Trimming and AOT remain disabled and unqualified.

Serve `XREngine.Browser/bin/portable/Release/net10.0/publish/wwwroot` through HTTPS
or localhost HTTP, with `.wasm` served as `application/wasm` and `.wgsl` as text.
Open the server URL, not a `file://` URL. The host requires `navigator.gpu` and a
usable adapter. `?renderer=WebGPU` and `?renderer=Auto` select the packaged WebGPU
path; `WebGL2` and unknown renderer names produce a diagnostic. No fallback
renderer is packaged.

- Drag on the canvas or focus it and use arrow keys to move the triangle.
- Toggle **Split view** to switch between one view and two camera projections.
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

The diagnostic renderer creates its shader and pipeline asynchronously, caches
bind groups, descriptors and typed upload storage, and uses aligned per-view
uniform slices. WebGPU necessarily creates a fresh canvas view, command encoder,
render pass encoder and command buffer for each submitted frame. No .NET memory
view is retained across callbacks. Matrix fields use the System.Numerics row-vector
to WGSL column-vector convention and a 0..1 depth projection.

## Scope

This is a fixed diagnostic triangle executor, not a general engine mesh/material
renderer. It intentionally uses a small scalar bridge for frame begin, each view,
and frame end. The versioned batched command/upload ABI, resource handle tables,
visibility collection, full engine render-buffer publication, cooked resources,
and WebGPU renderer-module integration are still required before general scenes.
The original `SceneBoot` exports remain available as a separate lifecycle fixture;
the default page now runs the continuous canvas scene.

The portable source allowlists retain engine assembly/type identities and isolate
outputs under `bin/portable` and `obj/portable`. Desktop bootstrap/native
integrations remain outside this graph. The existing portability guard continues
to check project/package/native-asset boundaries; it is not a forbidden-API proof.

See [canvas implementation notes](../docs/work/progress/rendering/browser-webgpu-canvas-host.md)
for remaining work and the explicit validation deferral.
