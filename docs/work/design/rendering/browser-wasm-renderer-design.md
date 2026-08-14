# Browser WebGL2 And WebGPU Renderer Modules

[<- Work docs index](../../README.md)

[Rendering architecture](../../../architecture/rendering/README.md)

Status: proposed implementation design.

Date: 2026-08-13.

Primary target: .NET 10 WebAssembly running in a browser and presenting to an
`HTMLCanvasElement` or `OffscreenCanvas`.

## Decision Summary

XRENGINE should add two renderer leaf modules, not one renderer containing two
large conditional implementations:

- `XREngine.Runtime.Rendering.WebGL2`
- `XREngine.Runtime.Rendering.WebGPU`

Both modules should share a portable browser presentation and JavaScript
interop layer. A browser application host should expose a Three.js-like
selection policy:

```text
Auto       -> try WebGPU, then visibly fall back to WebGL2
WebGPU     -> require WebGPU; fail with an actionable diagnostic
WebGL2     -> require WebGL2; fail with an actionable diagnostic
```

The public experience can therefore feel like one adaptive browser renderer,
while the engine retains two honest backend identities, factories, capability
sets, resource wrappers, shader targets, and recovery paths. Automatic fallback
is a composition-root decision made before world rendering starts. It is not a
silent mid-frame backend substitution.

WebGL 1 is not part of the v1 browser renderer. WebGL2 is the compatibility
baseline. WebGPU is the preferred advanced backend when available. HTML5 canvas
is the presentation surface; it is not a third graphics API or renderer.

This design takes inspiration from Three.js's separation of `WebGLRenderer`,
`WebGPURenderer`, and a canvas-owned animation loop. It does not embed Three.js
or translate XRENGINE scenes into Three.js objects.

## Why This Is More Than A Renderer Class

The current renderer-module boundary is a good starting point:

- `IRendererBackendModule` publishes metadata and lifecycle.
- `IRendererBackendFactory` creates an `IRuntimeRendererHost` from a
  `RendererBackendCreateContext`.
- `IRendererBackendCatalog` resolves a concrete backend and validates required
  capabilities.
- `GenericRenderObject` and `AbstractRenderAPIObject` separate engine resources
  from backend-owned handles.

However, the current application and rendering projects target
`net10.0-windows7.0` and depend on Windows/native libraries, Silk.NET desktop
windowing, OpenXR/OpenVR, Ultralight, FFmpeg, ImageMagick, and other APIs that
cannot simply be carried into `browser-wasm`. The desktop loop also owns an OS
window and can block a render thread, while a browser owns the page, canvas,
event dispatch, and animation scheduling.

The implementation therefore needs three coordinated changes:

1. Extract a browser-compatible runtime/rendering kernel from desktop-only
   hosting and native integrations.
2. Add browser canvas presentation and input/lifecycle services.
3. Add the WebGL2 and WebGPU renderer leaf modules.

Blindly multi-targeting the current Windows-heavy projects is not the intended
solution. Portable contracts and implementations should move into portable
projects; Windows-specific code should remain in desktop leaf projects.

## Goals

- Run an XRENGINE world in a browser-hosted .NET WebAssembly application.
- Present to a supplied `HTMLCanvasElement` or `OffscreenCanvas`.
- Provide WebGL2 as a production compatibility path.
- Provide WebGPU as the advanced render-and-compute path.
- Reuse XRENGINE scene, asset, material, render-pipeline, and generic render
  resource models where they are portable.
- Preserve backend isolation through the existing renderer-module catalog.
- Batch managed-to-JavaScript graphics work so interop calls are not made per
  vertex, uniform, or ordinary draw-state mutation.
- Make unsupported features and backend fallback visible in diagnostics.
- Support device/context loss, canvas resize, device-pixel ratio changes,
  page visibility changes, and browser-owned frame pacing.
- Produce statically deployable browser output with deterministic cooked assets
  and shader artifacts.

## Non-Goals

- WebGL 1 or OpenGL ES 2.0 support.
- Porting the full ImGui or native desktop editor in the first release.
- Running Silk.NET desktop windowing, OpenVR, native OpenXR, Ultralight, native
  FFmpeg, PhysX, DirectStorage, CUDA, or vendor upscalers in the browser.
- Claiming initial feature parity with the OpenGL 4.6 or Vulkan 1.3 backends.
- Reusing desktop GLSL unchanged in browsers.
- Calling one JavaScript graphics function for every existing
  `AbstractRenderer` method invocation.
- Silently replacing an explicitly requested compute, indirect, mesh-shader,
  sparse-texture, or XR path with a CPU implementation.
- Making arbitrary local filesystem paths available to browser code.
- Adopting Three.js as an engine dependency.

## Proposed Project Layout

The exact extraction sequence will be established by the portability audit, but
the target dependency direction should be:

```text
portable scene/data/runtime kernel
  -> portable rendering kernel and renderer-module contracts
       -> XREngine.Runtime.Rendering.Browser
            -> XREngine.Runtime.Rendering.WebGL2
            -> XREngine.Runtime.Rendering.WebGPU
       -> existing desktop rendering host
            -> XREngine.Runtime.Rendering.OpenGL
            -> XREngine.Runtime.Rendering.Vulkan

XREngine.Browser
  -> portable runtime composition
  -> browser presentation/input/assets
  -> statically selected browser renderer modules
```

Recommended responsibilities:

| Project | Responsibility |
| --- | --- |
| Portable rendering kernel | Renderer catalog, generic resources, render-pipeline contracts, capability contracts, backend-neutral frame data. |
| `XREngine.Runtime.Rendering.Browser` | Canvas presentation target, browser frame host, JS bridge contracts, handle tables, command packet ABI, resize/loss events, browser diagnostics. |
| `XREngine.Runtime.Rendering.WebGL2` | WebGL2 wrappers, state cache, GLSL ES programs, framebuffer and texture mapping, WebGL context loss/recovery. |
| `XREngine.Runtime.Rendering.WebGPU` | WebGPU wrappers, bind groups, pipelines, command encoding, WGSL programs, device loss/recovery. |
| `XREngine.Browser` | `browser-wasm` executable, HTML/JS bootstrap, backend policy, DOM input, asset fetch, service installation, and application startup. |

Each concrete renderer leaf owns and publishes its matching ES-module packet
executor as a static web asset. The shared browser project owns only the
backend-neutral ABI/bootstrap utilities. This keeps WebGL2 constants and state
logic out of the WebGPU module and allows a size-sensitive publish to omit a
backend intentionally.

The browser app should be a plain WebAssembly Browser App unless a product
specifically needs Blazor UI. Rendering must not depend on Blazor's component
model. Use source-generated `[JSImport]`/`[JSExport]` interop for browser control
operations and a versioned binary packet for hot-path graphics operations.

## Backend Identity And Registration

Add explicit identities:

```csharp
public enum RuntimeGraphicsApiKind
{
    Unknown = 0,
    OpenGL,
    Vulkan,
    WebGL2,
    WebGPU,
}

public static RendererBackendId WebGL2 { get; } = new("webgl2");
public static RendererBackendId WebGPU { get; } = new("webgpu");
```

Add `RenderExecutionMode.BrowserCanvas`. `BrowserCanvasRenderTarget` requires
that mode and `BrowserCanvasPresentation`; it must not reuse `DesktopWsi`,
`HeadlessWsi`, or `Presentationless`. Every exhaustive execution-mode switch,
including target-driver factories, profiling labels, frame submission, and
validation, requires a corresponding audit.

`RendererBackendId.FromGraphicsApi`, settings serialization, backend catalog
lookups, static module registration, build mapping, hot-reload mapping, logs,
and every OpenGL/Vulkan-only switch require an audit. Do not classify WebGL2 as
desktop OpenGL: the APIs and allowed shader/resource behavior differ.

Add coarse module capabilities where selection needs them before device
creation:

```text
BrowserCanvasPresentation
BrowserWorkerPresentation
GpuCompute
AsyncGpuReadback
ExternalImageSource
WebXrPresentation       // reserved until a later phase
```

Fine-grained limits and optional features remain renderer-instance capabilities
after a context, adapter, and device exist.

### Landed Portable Prerequisites (2026-08-13)

The Vulkan presentation-independent refactor landed the shared contracts that
the browser modules should reuse:

- `RuntimeGraphicsApiKind.WebGL2` and `.WebGPU`, matching
  `RendererBackendId.WebGL2` and `.WebGPU`;
- `RenderExecutionMode.BrowserCanvas`;
- the coarse browser, worker, async-readback, external-image, and reserved
  WebXR module capabilities listed above; and
- `RenderFrameOutputDescription`, an immutable backend-neutral acquired-output
  value exposed by `IRuntimeRenderPipelineFrameContext.FinalOutput`.

The AOT-safe texture-streaming provider registry accepts both browser backend
identities, and generic GPU-profiler fallback labels distinguish WebGL2 from
WebGPU. Neither identity is treated as desktop OpenGL or Vulkan.

`RenderFrameOutputDescription` carries dimensions, layers, formats, samples,
target generation, frame slot, view index, execution mode, and portable output
capabilities. It intentionally carries no Vulkan handles and must likewise
carry no `JSObject`, WebGL object, WebGPU object, or JavaScript handle-table
entry. A future `BrowserCanvasRenderTarget` should project its acquired canvas
output into this value while its concrete renderer keeps API objects and packet
executor handles private.

Render-pipeline resource keys now include the output target class and
color/depth formats as well as dimensions, views, and samples. Canvas resize,
device/context recovery, swapchain reconfiguration, and format changes can
therefore publish a new target generation without adding browser conditions to
the generic render graph. Browser implementation, portable project extraction,
canvas hosting, JavaScript interop, and command packets remain future work.

Browser static composition should register only the browser modules included in
the published app. It should not use collectible assembly loading or reflection
discovery. Trimming and AOT roots should be generated or explicit.

## Browser Presentation Target And Host

Add `BrowserCanvasRenderTarget : IRendererPresentationTarget`. It should carry
stable configuration, not raw JavaScript objects:

```csharp
public sealed record BrowserCanvasOptions(
    string CanvasBindingId,
    BrowserRendererPreference RendererPreference,
    BrowserPowerPreference PowerPreference,
    BrowserCanvasHostMode HostMode,
    bool Alpha,
    bool Antialias,
    bool PremultipliedAlpha,
    float MaximumDevicePixelRatio);
```

The JavaScript bootstrap registers an `HTMLCanvasElement` or `OffscreenCanvas`
under `CanvasBindingId`; resolving a CSS selector is one bootstrap option, not a
renderer contract. The bridge validates the registered object and returns a
generation-checked integer handle. `BrowserCanvasHostMode` is explicitly
`MainThread` or `OffscreenWorker`; transferring a canvas to a worker is not a
reversible preference or automatic fallback. JavaScript objects such as
`WebGL2RenderingContext`, `GPUDevice`, and `GPUTexture` remain in JavaScript
handle tables and never leak into generic engine resources.

V1 should support one presentation canvas with multiple XRENGINE viewports.
The contracts should keep renderer and handle-table ownership per canvas so a
later multi-canvas host does not require global JavaScript state, but shipping
multiple independently paced canvases is not a v1 requirement.

The browser host replaces `XRWindow` ownership with a canvas-oriented host that
provides:

- logical CSS size and physical drawing-buffer size;
- current device-pixel ratio and a configurable upper bound;
- focus, pointer capture, keyboard, text, wheel, touch, and gamepad snapshots;
- resize and page-visibility notifications;
- `requestAnimationFrame` scheduling;
- async renderer initialization and loss recovery;
- canvas presentation metadata and browser diagnostics.

The browser host must not emulate a Silk.NET `IWindow` or claim
`IRendererDesktopWindowServices`. Introduce a portable render-surface host
contract for the size, lifecycle, input, focus, and scheduling capabilities
that truly apply to both desktop windows and browser canvases. Keep
`IRuntimeRenderWindowHost` as a compatibility adapter during migration if
needed; native-window escape hatches remain desktop-only. The
`BrowserCanvasRenderTarget` should contain the portable browser surface host,
just as `DesktopWindowRenderTarget` contains the desktop host.

### Frame Lifecycle

The browser owns the outer loop:

```text
JavaScript requestAnimationFrame(timestamp)
  -> write the latest DOM/input/resize snapshot into the shared input view
  -> exported .NET BuildBrowserFrame(timestamp)
       -> latch the shared browser snapshot
       -> update fixed/variable simulation according to host policy
       -> collect visible and swap engine buffers
       -> build a packet in the current shared output-arena slot
       -> return packet slot, length, frame ID, and flags
  -> JavaScript validates and executes that packet
  -> queue next requestAnimationFrame
```

The callback must return promptly. It must never run the desktop
`BlockForRendering()` loop. A first implementation may run .NET and graphics
submission on the browser main thread. A later worker mode may transfer an
`OffscreenCanvas` and run .NET in a Web Worker, but it is a distinct host mode
with explicit deployment/header requirements, not an automatic optimization.

When the page is hidden, the host should stop submitting ordinary frames and
apply a configured simulation policy. Returning to visible state should reset
frame-time accumulation and invalidate temporal history rather than simulating
one enormous delta.

## Managed/JavaScript Bridge

Fine-grained JS interop in the render hot path would dominate small draw calls.
The bridge should use two lanes:

1. **Control lane:** source-generated `[JSImport]`/`[JSExport]` calls for
   initialization, capability snapshots, diagnostics, loss callbacks, and
   infrequent browser operations.
2. **Packet lane:** JavaScript calls one exported .NET frame builder per
   `requestAnimationFrame`, then reads the completed packet from a persistent
   runtime-created `MemoryView` and executes it against JavaScript object handle
   tables. Ordinary draw count does not increase the interop call count.

Use the supported .NET WebAssembly `ArraySegment<byte>`-to-`MemoryView` mapping
to expose fixed packet and snapshot arenas at bootstrap. Unlike an array, this
does not copy the underlying memory, and unlike a `Span<byte>` view it may live
beyond one interop call. Creating an `ArraySegment` view also creates a proxy and
`GCHandle`, so the bridge should create a small fixed set once, retain them for
the session, and explicitly call `dispose()` during shutdown. Do not create a
new view per frame.

Use at least double-buffered packet slots so .NET never overwrites a slot while
JavaScript is executing it. An undersized slot fails the current frame with the
required byte count; arena growth occurs only between frames and republishes a
new view generation. JavaScript must not retain typed subviews after that
generation is disposed. Operations that settle asynchronously must copy or
enqueue their source data into browser/GPU-owned storage before the slot is
released; a Promise must never retain a transient packet view.

The packet ABI should be versioned and little-endian, with a fixed header:

```text
magic | ABI version | backend | frame ID | byte length | command count
resource generation | flags | checksum in validation builds
```

Commands use fixed headers plus aligned payloads. Large vertex, index, texture,
and uniform uploads reference upload-arena ranges instead of embedding repeated
copies. Handles contain an index and generation so stale resources fail with a
named diagnostic instead of targeting a newly reused JavaScript object.

The executor validates packet bounds, arena generation, and handle generations even for trusted
engine output. Development builds should optionally annotate commands with
debug-label IDs and report the last completed command when JavaScript throws or
a device is lost.

The first benchmark gate is not maximum visual complexity. It is proof that a
scene with many small objects does not produce interop calls proportional to
draw count.

## Resource Model

Continue using `GenericRenderObject` as the engine-owned resource and create
backend-specific `AbstractRenderAPIObject` wrappers:

| Engine resource | WebGL2 wrapper | WebGPU wrapper |
| --- | --- | --- |
| `XRDataBuffer` | `WebGl2DataBuffer` | `WebGpuBuffer` |
| `XRTexture*` | `WebGl2Texture*` | `WebGpuTexture*` |
| `XRRenderProgram` | `WebGl2Program` | `WebGpuRenderPipeline` / `WebGpuComputePipeline` |
| `XRFrameBuffer` | `WebGl2FrameBuffer` | attachment/view plan lowered into a render-pass descriptor |
| `XRMeshRenderer` | `WebGl2MeshRenderer` | `WebGpuMeshRenderer` |
| fence/readback | WebGL sync/query ticket | queue/map completion ticket |

The wrappers own integer JS handles, readiness, generation, size, usage, and
debug metadata. They do not own `JSObject` instances in per-frame C# state.

Explicit wrapper disposal emits an ordered destroy command after the last frame
that references the handle. JavaScript then deletes/releases the API object and
increments the handle generation. Managed or JavaScript finalizers are leak
diagnostics only, never the normal GPU lifetime mechanism. Context/device loss
invalidates the entire table generation without walking stale objects through
ordinary destroy commands.

Resource creation may be asynchronous. Callers should request a resource and
observe `Pending`, `Ready`, `Failed`, or `Lost`, with a material/pipeline policy
that either skips a draw with a counted diagnostic or renders an explicit error
material. It must not block the browser UI thread waiting for shader compilation
or GPU completion.

Readback APIs must be ticket-based. Same-frame query results and synchronous
GPU waits are incompatible with browser scheduling and, in WebGL2, query and
sync results are deliberately unavailable in the issuing frame. Methods such
as `WaitForGpu` need an async/browser-safe contract or must be rejected by the
browser backend outside controlled teardown.

## WebGL2 Backend

WebGL2 is an OpenGL ES 3.0-derived browser API, not OpenGL 4.6. The backend can
reuse conceptual state-machine behavior from the OpenGL renderer, but should
not reference or subclass its Silk.NET implementation.

Initial supported feature set:

- vertex and index buffers;
- vertex array objects;
- uniform buffers with `std140` layouts;
- 2D, cube, 2D-array, and selected 3D textures;
- framebuffer objects and multiple render targets within reported limits;
- instanced raster draws;
- shadow maps;
- forward rendering and a reduced deferred/post-process profile;
- asynchronous occlusion/timer queries when extensions permit;
- context loss and complete resource re-creation.

Important limitations:

- no compute shaders or shader storage buffers;
- no desktop geometry, tessellation, task, or mesh shader stages;
- no persistent buffer mapping, sparse textures, bindless textures, or native
  indirect-count path;
- `mapBufferRange` is unavailable;
- texture formats, MRT counts, float filtering/renderability, timer queries,
  anisotropy, and compression depend on extensions and reported limits;
- only WebGL-valid GLSL ES 3.00 may be submitted.

The default mesh-submission strategy is `CpuDirect`, still backed by GPU
rasterization. If an application explicitly forces a compute/indirect/meshlet
strategy, startup or pipeline selection must fail with the missing capability;
it must not silently choose CPU direct.

The backend should maintain a managed state shadow and emit only changed state
to the packet. This both reduces packet size and avoids repeated JavaScript
calls.

## WebGPU Backend

WebGPU initialization is asynchronous:

```text
navigator.gpu
  -> requestAdapter(options)
  -> inspect adapter features and limits
  -> requestDevice(exact required features/limits)
  -> configure GPUCanvasContext with preferred format
  -> publish ready capability snapshot
```

The module should request only the features and limits required by the selected
browser render profile. Optional features must be enabled deliberately after
inspection. Requesting unsupported features should never be used as feature
detection.

The WebGPU backend should lower engine work into WebGPU-native concepts:

- immutable render/compute pipeline descriptors and caches;
- bind group layouts and bind groups;
- explicit buffer/texture usage masks;
- render and compute pass encoders;
- queue writes and command-buffer submission;
- asynchronous pipeline compilation where beneficial;
- asynchronous buffer mapping and queue completion;
- device error scopes and uncaptured-error diagnostics.

WebGPU should not be implemented as a field-for-field emulation of the current
GL-shaped `AbstractRenderer`. Before implementation, carve a narrower set of
resource, pass, command, copy, query, and presentation capabilities out of the
base class. Legacy stateful methods may remain as compatibility adapters for
the OpenGL/WebGL2 path, but new pipelines should depend on typed capabilities.

WebGPU enables compute but does not guarantee Vulkan's descriptor indexing,
mesh shaders, indirect-count drawing, sparse residency, or vendor extensions.
The initial GPU-driven profile should use storage buffers, compute culling, and
supported indirect draws, with texture arrays/atlases or bounded bind-group
tiers. Each higher rung requires an explicit device capability.

`GPUDevice.lost` is terminal for every object created by that device. Recovery
must request a new adapter and device, increment the backend generation,
reconfigure the canvas, recreate generic-resource wrappers from CPU/cooked
sources, invalidate temporal history, and resume only after the minimum world
resource set is ready. Intentional `destroy()` during shutdown does not trigger
recovery.

## Renderer Capability Profiles

Pipelines should resolve a named profile after device creation:

| Profile | Minimum behavior |
| --- | --- |
| `BrowserWebGL2Baseline` | CPU-direct mesh submission, forward opaque/masked/transparent rendering, basic shadows, UI, LDR output. |
| `BrowserWebGL2Extended` | Supported float/MRT extensions, reduced deferred path, selected post effects, async queries. |
| `BrowserWebGPUBaseline` | Render pipelines, storage buffers, compute, indirect draws, HDR intermediate targets with SDR canvas output. |
| `BrowserWebGPUExtended` | Optional compression/timestamp/features and higher limits; never assumed from API name alone. |

The render pipeline should declare its required and optional capabilities. The
resolver chooses a compatible pipeline variant and records every disabled
optional feature. A user-selected required feature causes a visible error when
unavailable.

The first shipping browser pipeline should be a focused `BrowserRenderPipeline`
or a capability-defined variant of `DefaultRenderPipeline`, not an attempt to
run every desktop pass and discover failures dynamically. It should include:

- depth and opaque/masked/transparent scene passes;
- one practical shadow path;
- sky/environment;
- tonemapping and a small post-processing set;
- engine UI composition;
- canvas presentation.

Advanced GI, meshlets, GPU BVHs, vendor upscale, sparse streaming, VR, and
editor overlays are promoted individually after validation.

## Shader Architecture

The current shader corpus is desktop GLSL with OpenGL/Vulkan fixups. Browser
support requires explicit shader targets:

```text
engine shader compile request + reflection/layout contract
  -> GLSL ES 3.00 for WebGL2
  -> WGSL for WebGPU
```

Do not build a long-term regex translator from desktop GLSL to WGSL. Complete
the language-neutral `ShaderCompileRequest`/`ShaderCompileResult` work described
in the existing Slang cross-compile plan, then evaluate a pinned, license-
approved toolchain that can produce both browser targets with source-mapped
diagnostics. Adding that dependency requires the repository's dependency and
license approval workflow.

The shader contract must define:

- source language, stage, entry point, macros, includes, and specialization;
- vertex input and fragment output locations;
- bind groups/descriptor sets and binding numbers;
- uniform/storage layout and alignment;
- texture/sampler pairing rules;
- clip-space Y direction and depth range;
- matrix layout and handedness;
- render-target formats and sample counts;
- required backend features;
- compiler/toolchain version in cache keys.

WebGL2 output must remove unsupported stages and use GLSL ES precision
qualifiers and WebGL-valid resource layouts. WebGPU output must be WGSL and use
explicit address spaces, bindings, and layouts. Shader variants that cannot be
represented on a target fail at cook time with the originating material/pass
named.

The browser publish should contain cooked shader artifacts and reflection
metadata. Runtime source compilation remains useful for development but must
not be the only production path.

## Assets, Networking, And Storage

Browser assets are URL-addressed cooked artifacts. The browser composition root
should provide implementations for:

- `HttpClient`/Fetch streaming of manifests and asset chunks;
- content-hash URLs and immutable caching;
- optional IndexedDB cache for large reusable payloads;
- image decode through browser-native sources where useful;
- explicit cross-origin and credential policy;
- cancellation and bounded concurrent downloads.

No engine code may assume arbitrary filesystem enumeration, memory-mapped
files, Windows paths, registry access, or synchronous file IO. Asset cooks
should prefer browser-supported texture payloads with a negotiated compressed
variant plus an uncompressed fallback. Large scenes need a bootstrap manifest
that separates first-frame essentials from streamed content.

## Input, Audio, And Browser Integration

DOM events should be normalized into the existing input snapshot model:

- pointer events for mouse, pen, and touch;
- wheel with browser delta-mode normalization;
- keyboard by physical code plus text input for characters;
- focus/blur and pointer lock;
- Gamepad API polling;
- resize observation and device-pixel ratio changes.

Prevent browser defaults only while the canvas owns the relevant interaction.
Text fields and accessibility overlays may require DOM cooperation rather than
raw key handling.

Audio requires a separate Web Audio-backed module and browser user-gesture
resume policy. It is not part of these renderer libraries, but the browser host
must not claim a world is fully started while an explicitly required audio path
silently failed.

## Browser Security And Deployment

The publish output is static, but production hosting must provide:

- HTTPS, which WebGPU requires through secure-context exposure;
- correct WebAssembly and asset MIME types;
- Brotli/gzip precompressed artifact handling;
- immutable caching for fingerprinted assets and no-cache for the bootstrap
  manifest/HTML;
- an explicit Content Security Policy and `connect-src` allowlist;
- cross-origin resource rules for fetched assets;
- SPA fallback only where the chosen host needs it;
- service-worker scope/versioning rules if offline/PWA behavior is enabled.

COOP/COEP headers are required only by the selected shared-memory/worker design
and have material embedding/cross-origin consequences. Do not enable them
without validating the hosting model.

The JS packet executor is part of the trusted application, but it still
validates bounds, enum ranges, resource generations, upload sizes, and shader
diagnostics. URLs, imported world data, and network messages remain untrusted.

## Diagnostics And Recovery

Expose a browser renderer diagnostics snapshot containing:

- selected/requested backend and fallback reason;
- browser bridge and packet ABI versions;
- adapter/context features, limits, and selected profile;
- logical/physical canvas size and device-pixel ratio;
- resource counts and estimated resident bytes;
- packet bytes, command count, upload bytes, and interop calls per frame;
- draw/pass/triangle counts;
- shader and pipeline pending/failed counts;
- context/device loss count and recovery state;
- last JavaScript exception or WebGPU error scope;
- disabled optional pipeline features with reasons.

For `Auto`, one clear startup message should state that WebGPU failed and WebGL2
was selected. For `WebGPU`, the same failure must stop renderer startup. A
WebGPU device loss attempts WebGPU recovery; it does not change APIs to WebGL2
mid-session unless the application restarts rendering under an explicit
fallback policy.

WebGL's `webglcontextlost` handler must call `preventDefault()` when recovery is
supported, stop frame submission, and rebuild only after
`webglcontextrestored`. Resource wrappers from the old context generation are
invalid.

## Performance Rules

- No heap allocations in the steady-state managed render hot path.
- No per-draw managed-to-JavaScript call pattern.
- Reuse packet writers, upload arenas, typed-array views, encoder scratch, and
  state caches.
- Bound resource creation, compilation, and uploads per frame so startup work
  does not freeze the page.
- Prefer browser-side bulk buffer/texture uploads from WebAssembly memory.
- Device-pixel ratio is a quality setting, not an unconditional multiplier;
  cap it and support dynamic resolution.
- Keep CPU visibility and render-command production useful for WebGL2; enable
  WebGPU compute paths only when they beat the CPU path under browser profiling.
- Treat AOT as a measured publish option. It can improve CPU-heavy code while
  increasing download size; publish size and first-interaction time are release
  gates.

## Implementation Phases

### Phase 0: Portability And API Audit

- Inventory the transitive project graph required to load and render one world.
- Classify every dependency as portable, desktop-only, replaceable, or excluded.
- Extract portable renderer/module/resource contracts from Windows/native code.
- Audit reflection, `Reflection.Emit`, collectible loading, dynamic expression
  compilation, P/Invoke, and serializers for trimming/AOT.
- Define focused renderer capabilities so WebGPU is not forced through the
  complete GL-shaped base surface.

Exit: a browser-compatible kernel project builds without native desktop assets
or packages.

### Phase 1: Browser Host And Canvas Triangle

- Create the `browser-wasm` app, JS bootstrap, canvas target, rAF loop, resize,
  focus, and input snapshots.
- Implement the control lane, persistent `MemoryView` arenas, and versioned
  packet decoder; prove arena lifetime, resize, disposal, and copy behavior with
  the exact .NET 10 runtime before expanding the command ABI.
- Register a temporary minimal backend that clears and draws a hard-coded
  triangle through the canvas.
- Add backend selection and diagnostics UI/log output.

Exit: published static output renders, resizes, pauses/resumes, and reports
failures in supported browsers.

### Phase 2: WebGL2 Baseline Module

- Implement generic resource wrappers and the state/packet encoder.
- Add GLSL ES shader cooking for a minimal material set.
- Render engine meshes, cameras, basic materials, textures, depth, transparency,
  one shadow path, and UI through the browser pipeline.
- Implement context-loss recovery.

Exit: a representative cooked scene runs through
`BrowserWebGL2Baseline` with no per-draw JS interop.

### Phase 3: WebGPU Baseline Module

- Implement async adapter/device creation and exact capability negotiation.
- Implement buffer, texture, sampler, bind-group, render-pipeline, and pass
  wrappers plus WGSL cooking.
- Add device loss, async readbacks, compute, and supported indirect submission.
- Match the Phase 2 representative scene.

Exit: the same scene runs through `BrowserWebGPUBaseline`, and `Auto` selects it
where available.

### Phase 4: Pipeline And Asset Expansion

- Add selected post-processing, HDR intermediates, compressed textures,
  streaming, skinning, particles, and performance profiles.
- Port features only through declared capability contracts.
- Add worker/OffscreenCanvas mode after main-thread profiling justifies it.

Exit: one real sample/game scene meets agreed visual, memory, startup, and frame
time budgets.

### Phase 5: WebXR And Tooling

- Design WebXR presentation as a separate target/capability.
- Add browser capture, diagnostics export, material/shader compatibility reports,
  and editor-side browser cook previews.

WebXR is not required for the initial desktop-browser canvas renderer.

## Validation Matrix

Validation starts with live feature paths; test additions follow after each
feature slice is functionally validated and explicitly cleared.

Minimum manual/runtime matrix:

| Area | Cases |
| --- | --- |
| Browsers | Current stable Edge/Chrome, Firefox, and Safari; backend availability recorded rather than assumed. |
| APIs | Forced WebGL2, forced WebGPU, Auto WebGPU success, Auto WebGPU-to-WebGL2 fallback, required-WebGPU failure. |
| Canvas | CSS resize, DPR change, fullscreen, hidden/visible page, detached/reinserted canvas. |
| Loss | WebGL context loss/restore and WebGPU device loss/recreation. |
| Content | static/skinned meshes, opaque/masked/transparent materials, textures, shadows, UI, post process. |
| Assets | cold cache, warm HTTP cache, throttled network, missing/corrupt asset, cross-origin rejection. |
| Publish | interpreted and AOT builds, compressed/uncompressed host behavior, cache upgrade, CSP. |
| Performance | startup bytes/time, first useful frame, frame CPU/GPU time, packet bytes, interop calls, upload bandwidth, memory. |

GPU output comparisons should use stable reference scenes with tolerant image
metrics plus inspected captures. WebGPU validation/error scopes and browser
developer-tool errors must remain clean.

## Principal Risks

| Risk | Mitigation |
| --- | --- |
| Windows/native dependencies prevent browser compilation. | Extract a portable composition root; do not reference the desktop aggregate project. |
| `AbstractRenderer` is too GL-shaped for WebGPU. | Introduce focused resource/pass/command capabilities before the WebGPU implementation. |
| JS interop overwhelms small draws. | Versioned batched packet ABI and upload arenas; enforce interop-call metrics. |
| Desktop GLSL cannot target both browser APIs reliably. | Unified shader compile contract and cooked GLSL ES/WGSL outputs; fail unsupported variants during cook. |
| WebGL2 lacks compute and modern binding features. | Ship a deliberate baseline pipeline and visible capability resolver; never imply parity. |
| WebGPU availability/features vary. | Inspect adapter/device capabilities and keep WebGL2 as explicit host-level fallback. |
| Browser device/context loss invalidates all handles. | Generation-checked wrappers and full resource reconstruction from generic/cooked sources. |
| WASM payload and startup time are excessive. | Minimal browser composition, trimming audit, split bootstrap assets, compression, measured AOT policy. |
| Worker mode complicates deployment and input. | Main-thread first; add OffscreenCanvas only as a separately validated mode. |

## Definition Of Done For V1

- A static `XREngine.Browser` publish can load and run a representative world.
- Two independently registered modules identify themselves as `webgl2` and
  `webgpu`.
- `Auto`, `WebGL2`, and `WebGPU` selection policies behave exactly as documented.
- WebGL2 renders the baseline browser pipeline without WebGL errors.
- WebGPU renders the same baseline with clean validation/error diagnostics.
- The steady-state frame uses a bounded number of JS interop calls independent
  of draw count.
- Canvas resize/DPR, page hide/show, WebGL context loss, and WebGPU device loss
  are handled without stale resource reuse.
- Unsupported required features fail visibly; optional feature exclusions are
  listed in diagnostics.
- Shaders are cooked to GLSL ES 3.00 and WGSL with source-mapped errors.
- Published assets are fingerprinted, compressible, cacheable, and URL-based.
- Startup, first-frame, frame-time, upload, packet, memory, and publish-size
  budgets are measured and recorded on the validation matrix.
- Browser architecture and user-facing build/hosting instructions are promoted
  into stable docs after implementation.

## References

- [.NET WebAssembly JavaScript interop](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)
- [.NET WebAssembly Browser App interop project](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [W3C WebGPU specification](https://www.w3.org/TR/webgpu/)
- [Khronos WebGL2 specification](https://registry.khronos.org/webgl/specs/latest/2.0/)
- [WHATWG canvas and OffscreenCanvas](https://html.spec.whatwg.org/multipage/canvas.html)
- [Three.js WebGPURenderer guide](https://threejs.org/manual/en/webgpurenderer)
- [Three.js WebGLRenderer reference](https://threejs.org/docs/pages/WebGLRenderer.html)
- [XRENGINE Slang shader cross-compile plan](../scripting/slang-shader-cross-compile-plan.md)
- [XRENGINE renderer backend hot reload](../../../architecture/rendering/renderer-backend-hot-reload.md)
- [XRENGINE runtime rendering host capability inventory](runtime-rendering-host-capability-inventory.md)
