# Unified Desktop And Browser Runtime

[<- Work docs index](../../README.md) · [Runtime modularization plan](../runtime-modularization-plan.md) · [Browser renderer module design](../rendering/browser-wasm-renderer-design.md) · [Physics architecture](../../../architecture/physics/overview.md)

Execution trackers:

- [Native subsystem integration debugging and validation](../../todo/platform/native-subsystem-project-split-todo.md): qualify the implemented native boundaries and portable projects, resolve integration defects, and complete the physics browser/default-promotion decisions.
- [Unified desktop and browser runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md): run the same engine, worlds, and game code on desktop and in the browser.

Status: unified browser gameplay architecture proposed. Native extraction and whole-project `net10.0` source changes are implemented; their integration qualification remains open. [Runtime Project Organization](../../../architecture/runtime/project-organization.md) describes the current layout.

Date: 2026-09-29.

Scope: the XRENGINE runtime (not the editor) on Windows desktop and in mobile and desktop browsers through .NET 10 WebAssembly and WebGPU.

## Decision Summary

XRENGINE will use the engine model that Unity, Godot, and formerly Unreal use for web builds: **the same engine code, worlds, and game code run on every platform**. Only the platform layer is swapped, and only GPU-facing and format-facing data is cooked per platform.

- The runtime kernel (`Data`, `Extensions`, `Runtime.Core`, `Runtime.Rendering`, the feature libraries, and the integration adapters) now targets `net10.0` with whole-project portability checks. The shared assemblies still require browser compilation and runtime qualification before claiming the same gameplay on desktop CoreCLR and WebAssembly.
- Native and OS-specific subsystem implementations are organized into leaf projects behind an engine contract. Examples are PhysX, Jolt, OpenAL, Steam Audio, FFmpeg, ImageMagick, FreeType, GLFW/SDL, XInput, OpenXR, OpenVR, DirectStorage, Ultralight, Rive, and CUDA. This continues the pattern the runtime modularization already used for the OpenGL and Vulkan backends.
- Each application's composition root installs its leaves explicitly: desktop bootstrap, server, editor, or browser host. Required services that a platform cannot provide fail with named diagnostics and never fall back silently.
- The browser renderer is a WebGPU **backend** for the engine's existing renderer contract and pipelines, not a separate browser pipeline. Command recording happens in C#; a thin JavaScript executor replays one batched packet per frame into WebGPU calls.
- **Jolt becomes the primary physics backend** on both desktop and web: Jolt compiled to WebAssembly and statically linked into the .NET WebAssembly runtime. Box3D remains a tracked candidate to re-evaluate when it leaves alpha. PhysX becomes an optional desktop-only backend. Jitter2 is sidelined as a reference/experimental backend.
- The editor stays a desktop application. Its Build Project action gains a browser target that cooks web variants of the project's real assets and publishes the real engine plus the project's game assemblies. It no longer flattens worlds into a separate browser scene format.
- The separate browser runtime on the `codex/webgpu-readiness-audit` branch becomes a reference harness. Its reusable pieces (canvas host, packet bridge, WebGPU resource/command executor, content delivery, shader artifacts, texture variants, compute kernels) migrate into the unified path; the parallel scene, component, animation, and pipeline types retire.

## Background

### Existing Boundaries

The completed runtime modularization established Core, backend-neutral Rendering, renderer modules, integration adapters, and desktop Bootstrap. Native subsystem extraction extends that organization to physics, audio, input/windowing, XR, image/media/font/UI, storage, diagnostics, sockets/OSC, and mesh processing. The authoritative current map is [Runtime Project Organization](../../../architecture/runtime/project-organization.md).

Shared projects now target `net10.0` with their full source set. Native package/API ownership sits in the modules, unused packages have been removed, and whole-project package/source/native-asset checks replace the former source-subset profiles. See [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md). This is implemented source structure; final builds, publish layouts, and live regression acceptance remain deferred.

`AbstractRenderer` still has a state-oriented surface (blend, stencil, depth, VAOs, indirect draws, and GPU waits), which the unified browser renderer must adapt. Screenshot/readback data is now neutral `RuntimeImage` data; ImageMagick encoding belongs to its own module. Physics creation already uses a static backend catalog. PhysX remains the desktop default, Jolt is an installed alternative, and Jitter is experimental/opt-in. Browser Jolt linkage and default promotion are unproven and gated.

### WebGPU Branch Audit (2026-09-29)

This is the pre-extraction audit snapshot, not the current build result. The drawing-value policy and source-profile removal have since been implemented, but no replacement build evidence is claimed here.

The `codex/webgpu-readiness-audit` branch (19 commits, about 26,000 added lines) implemented a browser application, a WebGPU module, cooked content delivery, and an editor publish target. The audit found:

| Finding | Evidence |
| --- | --- |
| The branch does not build. The portable source guard rejects `System.Drawing` in `XREngine.Extensions/Numbers/ColorExtensions.cs` and `XREngine.Extensions/RectangleF.cs`. Deny rules cannot be allowlisted, and those files predate the branch, so the guard has failed since it became a C# MSBuild task. | `dotnet build XREngine.Browser/XREngine.Browser.csproj -c Release -p:XREnginePortableRuntime=true` → 3 × `XREPORTABLE001`. |
| The browser runs a separate runtime that shares only the scene graph kernel with the engine. | `Build/Portable/*.items` compile 46 of about 785 Core files and 102 of about 2,380 Rendering files (most of them new `Browser*` types). `XRWorld`, `XRScene`, `GameMode`, `XRMesh`, `XRMaterial`, `XRCamera`, `CameraComponent`, `ModelComponent`, lights, physics, animation, UI, audio, and input components are absent. |
| The browser registers three browser-only components. | `SceneBootComponent`, `BrowserMeshComponent`, `BrowserSpinComponent` in `XREngine.Browser/browser-registration-manifest.json`. |
| Browser rendering bypasses the engine renderer. | `WebGpuRendererHost` implements only `IBrowserRendererHost`; drawing goes through a new `BrowserRenderPipeline` with `unlit` and `lambert` shading. The WebGPU leaf is about 4,400 lines of JavaScript against about 790 lines of C#. |
| Editor publishing converts worlds into a flat instance list. | `BrowserWorldPublishExporter` admits only exact `ModelComponent`, `CameraComponent`, and `AnimationClipComponent`. Any other active component (including any light) or any `DefaultGameMode` aborts the publish. Materials must be the canonical opaque unlit color/texture shaders with shadows forced off. Skinned export rejects every clip that has a `SourceImportManifest`, which all importer-produced clips have. |
| A published world is not playable. | No collision is exported, so no character controller is created and the camera stays at the exported snapshot. The published page is the developer demo harness, including demo controls and a sample UI overlay drawn over the world by default. |

The branch's shared pieces remain valuable (see [Relationship To The Browser Branch](#relationship-to-the-browser-branch)). The problem is the architectural boundary: extending that runtime would mean re-implementing every engine subsystem a second time and converting every content type into a second format.

## Platform Facts That Shape The Design

### C# Runs In The Browser

The browser runs .NET code through the .NET WebAssembly runtime (the runtime behind Blazor WebAssembly), selected with `Microsoft.NET.Sdk.WebAssembly` and the `browser-wasm` runtime identifier. It offers two execution modes:

- **Interpreter:** the IL is interpreted. Startup payload is smaller and builds are fast, but CPU-heavy code runs much slower than desktop CoreCLR.
- **Ahead-of-time (AOT):** the IL is compiled to WebAssembly through Emscripten. It is faster at runtime, but downloads are larger and builds are slower. It needs the `wasm-tools` workload and trimming-safe metadata.

Game code written in C# can therefore run in the browser unchanged, as long as the engine API it calls is present in the browser build.

### WebAssembly Reaches Web APIs Through JavaScript

WebAssembly cannot call WebGPU, WebGL, DOM, Web Audio, or fetch directly; each call goes through a JavaScript binding. Every engine's web build works this way, including Unity, Godot, and Unreal's former HTML5 target, whose GL and WebGPU calls go through generated JavaScript glue. The cost is the managed-to-JavaScript crossing. The rule is therefore: **record in C#, cross once per frame** with a binary command packet, and keep the JavaScript side a thin executor.

### Native Libraries Must Be Compiled To WebAssembly

Windows DLLs cannot load in a browser. Portable C and C++ can be compiled with Emscripten and statically linked into the .NET WebAssembly runtime as `NativeFileReference` items, then called through ordinary P/Invoke ([Microsoft: WebAssembly native dependencies](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-native-dependencies?view=aspnetcore-10.0)). Constraints:

- **Toolchain:** requires the `wasm-tools` workload and native relinking of `dotnet.wasm`.
- **Emscripten version:** prebuilt archives must use the same Emscripten version as the .NET runtime pack.
- **Callbacks:** native-to-managed callbacks must target `static` `[UnmanagedCallersOnly]` methods and use `IntPtr` in `DllImport` signatures rather than function-pointer types.
- **NuGet packages:** WebAssembly archives in a package's `browser-wasm` folder are not referenced automatically; a `.props` file or the app must add them as `NativeFileReference` items.

Some existing dependencies already ship browser builds, notably SkiaSharp. Others do not: `JoltPhysics.Native` 1.1.0 ships `joltc` for Windows, Linux, macOS, and Android only.

### Threads Are Not Available For The First Release

.NET WebAssembly multithreading (`WasmEnableThreads`) is still disabled by default in .NET 10, requires cross-origin isolation headers (COOP/COEP), and has open issues. These include a runtime startup failure when combined with native relinking (`WasmBuildNative`), which statically linked physics requires ([dotnet/runtime#112926](https://github.com/dotnet/runtime/issues/112926), [tracking issue #68162](https://github.com/dotnet/runtime/issues/68162)). The browser runtime is therefore designed as a **single-threaded, frame-stepped** runtime:

- **Frame step:** simulation, visibility, and render recording run in sequence inside one `requestAnimationFrame` callback.
- **Jobs:** the job system executes on the caller.
- **Physics:** native solvers run with one worker.
- **No blocking:** any code that blocks waiting for another thread or for I/O deadlocks and is not permitted on this path.

Threads can be re-evaluated as a measured optimization later.

### WebGPU Is Not Desktop GPU Parity

WebGPU has no bindless textures, bounded bind groups and binding counts, no geometry shaders, no multi-draw-indirect-count, no 64-bit shader integers in the baseline, and WGSL as its only shader language. Mobile devices add memory, bandwidth, and texture-format limits (ASTC/ETC2 instead of BC). Desktop render passes therefore need capability-selected WebGPU variants. This does not require a second renderer: the engine's material contract already lets each backend choose its own texture encoding (arrays, bindless handles, descriptor indexing, or heaps) without changing logical references ([pipeline notes](../../../architecture/rendering/default-render-pipeline-notes.md#shared-gpu-scene-and-material-contract)).

### Memory, Download, And Lifecycle

- **Memory:** WebAssembly32 caps the linear heap at 4 GiB and mobile browsers reclaim far earlier. The .NET heap, native heaps (Jolt), and staging buffers share one linear memory.
- **Download:** first load downloads the runtime, the engine assemblies, and the content. Assembly size, trimming, and content streaming matter.
- **Lifecycle:** pages can be hidden, frozen, or discarded, and GPU devices can be lost. Suspension and device loss are normal events.

## How Other Engines Approach The Web

| Approach | Example | What runs in the browser | Content path |
| --- | --- | --- | --- |
| Web-native renderer library | three.js | A JavaScript scene graph and renderer with a deliberately narrow feature set: forward rendering, a standard PBR material, shadow maps, optional post-processing. Shaders are generated at runtime from material templates or its node shading language, which emits WGSL or GLSL. | Artists export glTF from DCC tools; KTX2/Basis textures are transcoded by WebAssembly decoders in workers. The conversion happens in the authoring tool. |
| Engine compiled to WebAssembly | Unity (C# through IL2CPP to C++ to Emscripten), Godot (C++ engine through Emscripten), Unreal's former HTML5 target | The same engine code, scene format, and gameplay code as desktop. The platform layer (windowing, input, audio, GPU backend, file I/O) is swapped. | The editor cooks each platform's variants of the same assets: shader targets, texture formats, compressed meshes. |

XRENGINE is a full engine with an editor, physics, animation, networking, and C# gameplay, so the engine model is the right one. XRENGINE has one advantage here. Godot 4 still has no official C# web export, largely because it must host the .NET runtime next to its own C++ engine module ([Godot C# web export discussion](https://github.com/godotengine/godot-proposals/discussions/13076)). XRENGINE's engine is itself C#, so the .NET WebAssembly runtime is the only runtime, and native pieces are linked into it.

### What Gets Converted

| Kind | Converted per platform? | Why |
| --- | --- | --- |
| Shaders | Yes, cooked | Desktop GLSL 4.6 with bindless and 64-bit extensions cannot run on WebGPU. Shaders are authored or ported to Slang (or explicit WGSL) and cooked to SPIR-V, GLSL, and WGSL targets. |
| Textures | Yes, cooked | BC formats are not available on mobile. Cook ASTC/ETC2 (later KTX2/Basis) variants with an uncompressed fallback. |
| Meshes | Optional | The same vertex data; optional compression, quantization, or meshlet variants. |
| Audio | Yes, cooked | Decode formats must match the browser matrix; native codecs such as LAME and FFmpeg are not available at runtime. |
| Model/image/font imports | Editor only | Assimp, FBX, ImageMagick, FreeType, and CoACD run in the editor or cook tools; the runtime consumes their output assets. |
| Worlds, scenes, prefabs, components, animation graphs, game code | **No** | These are the same serialized assets and the same C# assemblies on every platform. |

The branch's flattening exporter falls in the last row: it converts worlds and components into a separate format. That conversion is not inherent to the web and is removed.

## Principles

1. **One engine code path.** Desktop and browser load the same kernel, feature, and adapter assemblies and the same game assemblies. There is no parallel browser component library, scene format, or render pipeline.
2. **Portable means the whole assembly.** A portable project targets `net10.0`, references only reviewed portable packages, and contains no Windows-only APIs. The same binary runs on both platforms. Per-platform source subsets that compile under the same assembly identity (the branch's `Build/Portable/*.items`) are not used. A game assembly compiled against the desktop build would see a different API surface in the browser and fail with `MissingMethodException`.
3. **Leaves own native code.** Each native or OS-specific API lives in a leaf project that depends downward on contracts. Kernels and feature libraries never reference leaves. Leaves never reference each other.
4. **Explicit composition.** Each application installs leaves in its composition root using static registration. Shipping and browser builds do not scan assemblies.
5. **Capabilities are declared and required services fail loudly.** A world or project declares the services and features it requires. A missing backend, an unsupported render pass, or an unavailable physics feature produces a named diagnostic. There is no silent CPU fallback for a requested GPU path, no WebGPU-to-WebGL2 switch, and no dropped component.
6. **Conversion is limited to platform data.** Cooking produces shader, texture, audio, and optional mesh variants. World, prefab, and component data are identical.
7. **No blocking in shared runtime code.** Asset I/O is asynchronous. The engine exposes a frame-step API that both the desktop loop and the browser `requestAnimationFrame` callback drive. Synchronous waits move to desktop-only leaves, editor code, or cook time.
8. **Hot paths stay bounded.** One managed-to-JavaScript crossing per frame for rendering, with no per-draw interop and no per-frame heap allocation, following the repository hot-path rules.
9. **Desktop is not regressed.** Every extraction keeps desktop behavior and performance. Splitting native APIs into leaves also benefits desktop: smaller dependency closures, headless servers without GPU/audio libraries, and Linux/macOS readiness for the [Apple platform design](apple-platform-moltenvk-support-design.md).

## Target Architecture

### Layers

```text
Applications / composition roots
  XREngine.Editor (desktop)   XREngine.Server   XREngine.VRClient   XREngine.Browser (browser-wasm)
        |                          |                  |                    |
        +------------- install leaves explicitly per application ---------+

Leaves (one native or platform API each; never reference each other)
  Desktop-only:  Rendering.OpenGL, Rendering.Vulkan, Physics.PhysX, Audio.OpenAL,
                 Audio.SteamAudio, Media.FFmpeg, Imaging.Magick (editor/cook),
                 Platform.Desktop (GLFW/SDL windowing), Input.Silk, Input.XInput,
                 XR.OpenXR, XR.OpenVR, IO.DirectStorage, UI.Ultralight, UI.Rive, ...
  Cross-platform native: Physics.Jolt (native on desktop, Emscripten archive in browser)
  Portable managed:      Physics.Jitter (reference), UI.Skia candidate
  Browser-only:  Rendering.WebGPU, Platform.Browser (canvas/input/lifecycle),
                 Audio.WebAudio, Media.Html, Net.WebSocket

Portable kernel and feature libraries (net10.0, identical binaries everywhere)
  Extensions, Data, Runtime.Core, Runtime.Rendering, Animation, Audio (core),
  Input (core), Modeling (authoring), Runtime.*Integration adapters
```

Project names follow the existing `XREngine.Runtime.*` convention (for example `XREngine.Runtime.Physics.Jolt`). [Runtime Project Organization](../../../architecture/runtime/project-organization.md) owns the current project map, composition, and serialized-identity rules. The diagram above includes proposed browser modules; Jolt browser support and Skia browser use are candidates, not supported configurations inferred from the current desktop projects.

### Composition Roots

- **Desktop:** `XREngine.Runtime.Bootstrap` remains the desktop composition root. It installs the desktop leaves and keeps its explicit static registration for AOT launchers.
- **Browser:** `XREngine.Browser` becomes a real browser composition root. It references the portable kernel, the project's game assemblies, and browser/cross-platform leaves, and installs them explicitly.
- **Server:** installs only headless leaves, such as physics and networking transports, with no GPU or audio.
- **Editor:** stays desktop and additionally installs authoring and cook leaves (imaging, model import, font atlas generation, convex decomposition).

Backend selection generalizes the existing renderer backend catalog. Each replaceable subsystem (renderer, physics, audio, media, transport) has a small module catalog with stable IDs, capability reports, supported platforms, and factories. `Engine.RuntimeRenderingHostServices` already selects physics through the module catalog with stable saved backend values and named missing-module diagnostics.

### Platform Host Contract

The branch's portable host contracts (`IRuntimeSurfaceHost`, `RuntimeSurfaceState`, `RuntimeInputState`, `BrowserCanvasRenderTarget`, `RenderExecutionMode.BrowserCanvas`) are the starting point. The unified host contract covers:

- **Surface:** logical/physical size, device-pixel ratio, orientation, safe area, attach/detach, and output generations.
- **Input:** pointers, touch, keyboard, IME/text, gamepad, and wheel, delivered through `XREngine.Input` device abstractions.
- **Lifecycle:** start, suspend, resume, stop, visibility, and device loss.
- **Scheduling:** a single-frame step API (`Step(elapsed)`: fixed-step simulation with bounded catch-up, variable update, visibility, render recording, submission). The desktop render-thread host calls it from its loop; the browser host calls it from `requestAnimationFrame`.
- **Jobs:** a job scheduler interface with threaded (desktop) and caller-thread (browser) executors.

## Subsystem Strategies

### Rendering

- **Backend:** `XREngine.Runtime.Rendering.WebGPU` implements the engine renderer backend contract (`AbstractRenderer` and API-object wrappers for buffers, textures, samplers, framebuffers, programs, materials, mesh renderers, and compute), as the OpenGL and Vulkan leaves do.
- **State and pipelines:** GL-shaped state calls are tracked in C# and resolved into cached immutable WebGPU pipelines and bind groups, as the Vulkan backend does.
- **Recording:** commands are recorded into reusable C# arenas and flushed once per frame to a JavaScript executor that owns WebGPU objects by generation-checked handles. The branch's resource, command, readback, usage-scope, and limits executors are reused; pipeline-policy logic currently in `browser-render-pipeline.js` moves into C#.
- **Contract cleanup:** remove `MagickImage` from `AbstractRenderer`; make screenshots, pixel reads, and luminance reads asynchronous readbacks with neutral image data; make `WaitForGpu` illegal on non-blocking hosts. Add capability probes WebGPU answers truthfully (no indirect-count, no mesh shaders, no bindless, bounded bind groups). Contract evolution toward explicit pass and pipeline descriptions happens in the shared kernel, benefiting all backends, rather than in a browser-only pipeline.
- **Pipelines:** `DefaultRenderPipeline` gains a capability-selected web tier (forward shading with the engine's lit material model, shadows, sky/environment, HDR, tonemapping, a bounded post-process set, UI). `AdvancedRenderPipeline` reports unsupported on WebGPU through its existing `Available` policy: the output stays explicitly unbound with a rejection reason until its requirements are met.
- **Mesh submission:** starts with `CpuDirect`. GPU culling can later write fixed indexed slots with zero-instance culled draws, avoiding indirect-count and readback. The branch's WGSL skinning, culling, BVH, and Hi-Z kernels are ports of the canonical kernels and can be connected to the engine pipeline.
- **Materials:** a WebGPU encoding of the logical material and texture contract uses bounded bind groups and, where content qualifies, texture arrays. No desktop bindless handle is passed to WGSL.
- **Shaders:** engine shaders are authored or ported to Slang (or explicit WGSL) and cooked per target. Shaders without a WGSL variant are flagged web-unsupported, so materials that need them fail by name at cook time. The branch's WGSL artifact format, `ShaderCompileTarget` WGSL target, pinned Slang route, and warm-up logic are reused.
- **Deferred:** WebGL2 remains a separate backend under the [browser renderer design](../rendering/browser-wasm-renderer-design.md).

### Physics

The engine keeps its backend-neutral contracts in `Runtime.Core`: `AbstractPhysicsScene`, `IPhysicsBackendService`, the rigid-body, controller, and joint interfaces, `IPhysicsGeometry`, and the physics components. Each backend moves into its own leaf.

| Criterion | Jolt | Box3D | PhysX (MagicPhysX) | Jitter2 |
| --- | --- | --- | --- | --- |
| License | MIT | MIT | BSD-3 (PhysX 5) | MIT |
| Maturity | Mature, widely shipped | v0.1.0, alpha, announced 2026-06-30 | Mature | Mature library; XRENGINE backend is a 197-line prototype |
| Web build | Emscripten builds exist (JoltPhysics.js, with a cross-platform deterministic option) | Official Emscripten build; community WebAssembly ports | None practical | Pure C#, runs anywhere |
| Features | Rigid bodies, 10+ shapes including mesh and height field, 6-DOF and other constraints, `Character`/`CharacterVirtual`, vehicles, ragdolls, soft bodies, double precision | Rigid bodies, hulls, static mesh/height field, joints without a D6 equivalent, experimental character mover | Full feature set plus GPU dynamics | Basic |
| .NET binding | JoltPhysicsSharp 2.22.0 (already used; `LibraryImport` plus `UnmanagedCallersOnly`) | None; C17 API | MagicPhysX | Native C# API |
| XRENGINE backend today | 12 files, about 4,500 lines; controller correctness work in progress | [Integration TODO](../../todo/physics/box3d-backend-integration-todo.md), blocked on dependency approval | 43 files, about 9,300 lines; documented primary | Throws `NotImplementedException` for actors and queries |

Decision:

- **Jolt is the primary backend on desktop and web.** It is the only candidate that is mature, feature-complete for the engine's contracts, proven on WebAssembly, and already integrated. Its C wrapper (`joltc`) is compiled with the .NET-pinned Emscripten into a static archive and linked into the browser runtime. The same managed binding runs on both platforms if it passes the WebAssembly callback and marshalling spike; otherwise the project owns a thin binding over `joltc`. Browser physics runs with one worker.
- **Box3D is re-evaluated when it reaches a stable release.** Its C17 API is simpler to bind and to compile for the web, and it claims cross-platform determinism. Its gaps today are alpha status, no D6 equivalent, and an experimental character mover. It stays behind the same contracts so adopting it later does not affect gameplay code. Its integration TODO's file map moves to a leaf project.
- **PhysX stays as an optional desktop-only leaf** for projects that need its features (GPU dynamics, PhysX-specific extensions). A world whose required features exist only in PhysX fails browser publishing with a named capability error.
- **Jitter2 is sidelined** as an experimental reference backend. It is excluded from default application composition and is not a shipping target.
- **The default changes to Jolt only after parity gates pass** (rigid bodies, compound shapes, queries and filtering, joints including 6-DOF, character controller correctness, events, debug draw, serialization). Existing serialized `EPhysicsLibrary` values stay stable.
- **Cross-platform determinism is an open decision.** It requires building `joltc` with Jolt's cross-platform deterministic option for both desktop and web, which replaces the prebuilt `JoltPhysics.Native` binaries with repository-built ones. It matters for rollback or lockstep networking; server-authoritative replication does not need it.

### Audio

- **Split:** `XREngine.Audio` keeps the contracts (`IAudioScene`, `IAudioTransport`, effects/capture abstractions) and managed logic. OpenAL, Steam Audio, NAudio device output, and FFmpeg decoding move to desktop leaves. The Google Cloud speech packages and the MathNet CUDA provider have no source references and are removed rather than moved; if speech services return, they belong in a server or tool leaf that the browser client never requires.
- **Browser:** `XREngine.Runtime.Audio.WebAudio` implements the same contracts over Web Audio. It reuses the branch's `browser-audio.js`: gesture-driven activation, suspension, positional sources, listener.
- **Codecs:** audio assets cook to formats the declared browser matrix decodes. Safari support is verified before choosing Opus-only delivery.
- **Components:** `AudioSourceComponent`, listener components, and their serialized data are unchanged.

### Input And Windowing

- **Core:** `XREngine.Input` keeps device abstractions.
- **Desktop:** GLFW/SDL input (`Input.Silk`), XInput, and OpenVR input become leaves. Window creation and event pumping move from `Runtime.Rendering` into a desktop platform leaf, coordinated with the [render-thread window ownership plan](../rendering/dedicated-render-thread-window-ownership-plan.md).
- **Browser:** the browser platform leaf feeds DOM pointer, touch, keyboard, IME, wheel, and Gamepad API events into the same abstractions. The branch's `browser-input.js` and pointer-snapshot logic are reused.
- **Behavior:** player controllers, pawns, and input mappings are unchanged.

### Assets, Content, And Cooking

- **Loading:** `AssetManager` loads through an asynchronous asset-source contract. Desktop uses files, archives, and optionally DirectStorage (desktop leaf). The browser uses same-origin fetch of content-addressed payloads with optional HTTP caching. Synchronous load wrappers leave the runtime path.
- **Format:** the serialized asset format is the same on every platform.
- **Cook:** the cook step (already `CookContent` in the desktop build) gains a platform target. Web cooking selects WGSL shader artifacts, ASTC/ETC2 texture variants with fallbacks, web audio codecs, and optional mesh encodings, and records required capabilities per asset.
- **Delivery:** the branch's content manifest, SHA-256 immutable payload URLs, bounded concurrent delivery, cancellation, dependency closures, and per-frame integration budget are reused for the real asset graph.
- **Imports:** model, image, font, and collision imports stay in the editor and cook tools (`Runtime.ModelAssetPipeline`, imaging leaf, CoACD leaf). They are never reachable from the browser runtime.

### Imaging, Media, Text, And UI

- **Images:** ImageMagick moves to an editor/cook leaf. Runtime texture code consumes cooked pixel data through a neutral image contract; `Mipmap2D.GetImage()` and other `MagickImage` surfaces leave the kernel. The browser decodes user-supplied images with `createImageBitmap` through the platform leaf when a feature needs it.
- **Video:** FFmpeg becomes a desktop media leaf behind a media-source contract. The browser media leaf uses `<video>` elements imported as WebGPU external textures.
- **Fonts:** FreeType (SharpFont) runs at cook time to generate glyph atlases. The runtime renders cooked fonts. The browser uses DOM text only for text entry, IME, and accessibility.
- **UI backends:** ImGui becomes an editor/debug leaf and is not part of shipping browser builds. Ultralight and Rive become desktop UI leaves. SkiaSharp has an official browser build and can become a cross-platform UI leaf after review. The engine's own UI renders through the renderer on every platform.

### Networking

Replication and session logic depend on a transport contract. Desktop and server keep UDP/TLS transports in a leaf. The browser uses a WebSocket transport leaf first, with WebTransport or WebRTC as capability-gated later options, plus a matching server gateway. The [mobile TODO's MW11](../../todo/rendering/mobile-webgpu-runtime-todo.md#mw11--browser-multiplayer-and-optional-voicemedia) requirements (bounded queues, reconnect/resync, suspension, origin and credential policy) apply unchanged.

### XR

OpenXR and OpenVR move out of `Runtime.Rendering` into XR leaves. Renderer-specific XR bridges stay in the OpenGL and Vulkan leaves, as today. WebXR is a separate future browser leaf and presentation target.

### Game Code, Components, And Serialization

- **Targets:** project game assemblies target `net10.0` and reference the portable engine assemblies. A build-time check reports references to desktop-only leaves, so a project knows which features block browser publishing.
- **One compilation:** the editor compiles game code once. Desktop builds load it as today (including collectible hot reload in the editor). Browser publishing links the same game assemblies into the browser application.
- **Registration:** component, transform, serializer, and module registrations are generated at build time by a C# source generator, replacing the branch's Python generator and assembly scanning. The interpreter tolerates reflection-based YAML and MemoryPack serialization. Trimming and AOT require the generated metadata and are separately qualified.
- **Persisted identities:** serialized type identities stay stable. The Phase 6 loader rule (matching the stable full type name even when the assembly qualifier changes) means moving a type between assemblies without renaming its namespace or type does not break assets. Moves that rename types need explicit migrations.

### Editor

The editor remains a desktop application; the ImGui editor depends on native ImGui, desktop windowing, MSBuild/`dotnet` child processes, native importers, and the file system. A browser-hosted editor is out of scope. It would need most of this design plus ImGui compiled for WebAssembly with a WebGPU backend, an in-browser or server-side compilation service, server-side import, and a virtual file system. It can be designed separately after the runtime ships.

The editor's publish flow for the browser target becomes:

1. Validate the project's declared platforms and required capabilities.
2. Cook web variants of the startup world and its dependency closure.
3. Compile game assemblies.
4. Publish `XREngine.Browser` with the engine assemblies, the game assemblies, the selected leaves, and a player shell page. The developer harness page stays a separate development page.
5. Write the launch descriptor and activate the output atomically. The branch's staging and rollback logic is reused.

## Build, Publish, And Runtime Modes

- **Toolchain:** pin the .NET SDK and the `wasm-tools` workload. Native relinking is required once Jolt is statically linked. Record the Emscripten version the runtime pack expects; native archives are rebuilt when it changes.
- **Execution mode:** start with the untrimmed interpreter for correctness. Measure AOT and trimming for startup, download, memory, and CPU cost before choosing the shipping mode.
- **Hosting:** HTTPS with correct WebAssembly/WGSL MIME types, compression, revalidated bootstrap manifests, and immutable hashed payloads. Cross-origin isolation headers are added only if a threaded configuration is adopted.
- **CI:** a browser build and publish lane plus a headless-browser smoke test, alongside the existing Windows desktop lane.

## Relationship To Existing Work

| Document | Relationship |
| --- | --- |
| [Runtime modularization plan](../runtime-modularization-plan.md) | This design continues it: native subsystem leaves and the deferred physics split. |
| [Browser renderer module design](../rendering/browser-wasm-renderer-design.md) | Backend-level content (bridge, resource model, WebGPU and WebGL2 modules, security, diagnostics) remains valid. Its portable-runtime extraction approach is superseded by genuinely portable assemblies. |
| [Mobile WebGPU runtime TODO](../../todo/rendering/mobile-webgpu-runtime-todo.md) | Device, bridge, recovery, budget, hosting, and physical-device validation items (MW03, MW05, MW07, MW10–MW12) still apply to the WebGPU backend and delivery path. Its portable-profile (MW01), focused pipeline (MW06), and bounded service profiles (MW08) are superseded by the unified TODO. |
| [Box3D integration TODO](../../todo/physics/box3d-backend-integration-todo.md) | Box3D stays an optional backend candidate; its file map moves to a leaf project. |
| [Jolt character controller correctness TODO](../../todo/physics/jolt-character-controller-correctness-todo.md) | Becomes a prerequisite for making Jolt the default. |
| [Physics architecture](../../../architecture/physics/overview.md) | Updated when the default changes. Some Jolt notes there are already stale: the Jolt scene now iterates `ColliderShapes`. |

### Relationship To The Browser Branch

| Keep and migrate | Retire after the unified path reaches parity |
| --- | --- |
| Canvas host, surface/input/lifecycle contracts, `browser-input.js`, `browser-audio.js`, browser services | `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent` and the generated browser registrations |
| Frame/upload packet ABI, reusable arenas, generation-checked handle tables, bridge counters | `BrowserCooked*` scene/instance DTOs and `BrowserSceneSession` content/motion/animation paths |
| WebGPU resource, command, readback, usage-scope, limits, and pipeline-cache executors; device-loss detection | `BrowserRenderPipeline`, `browser-render-pipeline.js`, and the focused-pipeline packet |
| WGSL artifact format, WGSL compile target, pinned Slang WGSL route, shader warm-up | `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, `BrowserKinematicCharacter` |
| Content manifest, immutable payloads, bounded delivery, texture variants, cooked texture upload | `BrowserWorldPublishExporter` and its flattening rules |
| WGSL skinning, culling, BVH, and Hi-Z kernels (as engine pipeline ports) | `Build/Portable/*.items`, the same-identity source profiles, and the Python generators/audits |
| Editor build target, staging, and rollback | The developer demo page as published output |

## Migration Strategy

The two TODOs sequence the work:

1. **Stabilize the branch:** fix the source guard, build, run, and record evidence. Freeze feature growth in the separate browser runtime.
2. **Split native subsystems into leaves, one subsystem at a time, keeping every desktop application building and passing its targeted tests.** Physics goes first because its contracts already exist.
3. **Retarget the kernel and feature libraries to `net10.0`** once their native references are gone, and enforce portability over whole projects.
4. **Boot a real `XRWorld` in the browser** from the real assemblies, with no rendering.
5. **Implement the WebGPU backend for the engine renderer** and the web tier of `DefaultRenderPipeline`.
6. **Add Jolt, Web Audio, input, and UI** browser leaves, plus the real-asset web cook.
7. **Switch editor browser publishing to the unified path, then retire the separate runtime.**
8. **Qualify performance, AOT, devices, networking, and hosting.**

## Risks

| Risk | Mitigation |
| --- | --- |
| Moving types between assemblies breaks persisted assets or game code. | Keep namespaces and type names stable; rely on the full-type-name loader rule; audit serialized identities before each move; add explicit migrations for renames. |
| Native leaves leak back into kernels through convenience APIs. | Whole-project portability guard, package allowlists, and dependency contract tests like the Phase 5 suite. |
| Jolt's managed binding does not work under .NET WebAssembly. | Early spike; fall back to a repository-owned thin binding over `joltc`; keep backend-neutral contracts so Box3D remains possible. |
| Emscripten or .NET runtime-pack updates break statically linked archives. | Pin versions; rebuild archives in the dependency lane; check versions at startup. |
| Interpreter performance is too slow for engine-scale simulation. | Measure early with representative worlds; plan AOT with generated metadata; keep hot paths allocation-free. |
| Single-threaded browser frames exceed budget. | Capability-tiered pipelines, bounded per-frame work, and measured threads later. |
| Renderer contract changes regress OpenGL/Vulkan. | Change the contract in small steps with desktop visual and performance validation per the rendering invariants. |
| Download size of the full engine grows beyond mobile budgets. | Trimming, lazy assembly loading, and content streaming, measured against the [readiness budgets](../../progress/rendering/mobile-browser-readiness.md#devices-and-measurable-budgets). |
| Physics behavior differs between backends. | One primary backend (Jolt) on every platform; parity tests; determinism decision recorded. |

## Open Decisions

1. Confirm Jolt as the primary cross-platform physics backend and the parity gates for changing the default from PhysX.
2. Choose the Jolt binding strategy for WebAssembly: upstream JoltPhysicsSharp or a repository-owned binding over `joltc`.
3. Decide whether to build `joltc` in-repository (required for cross-platform determinism) instead of using `JoltPhysics.Native` binaries.
4. Approve pinning the .NET SDK, the `wasm-tools` workload, and Emscripten for browser builds.
5. Choose the initial shipping runtime mode (interpreter, or AOT after measurement).
6. Decide the WebGL2 scope relative to the WebGPU-first delivery.
7. Decide whether to retire the branch's separate runtime or keep it as a small standalone demo.

## References

- [Microsoft: Blazor WebAssembly native dependencies (.NET 10)](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-native-dependencies?view=aspnetcore-10.0)
- [dotnet/runtime: experimental WebAssembly multithreading tracking](https://github.com/dotnet/runtime/issues/68162)
- [dotnet/runtime: `WasmBuildNative` with threading fails to start](https://github.com/dotnet/runtime/issues/112926)
- [Jolt Physics](https://github.com/jrouwe/JoltPhysics) and [JoltPhysics.js](https://github.com/jrouwe/JoltPhysics.js)
- [Box3D](https://github.com/erincatto/box3d) and [release notes](https://github.com/erincatto/box3d/releases)
- [Godot proposals: .NET 10 and C# web support](https://github.com/godotengine/godot-proposals/discussions/13076)
