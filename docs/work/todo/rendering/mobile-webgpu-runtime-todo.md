# Mobile WebGPU Runtime — Implementation TODO

[Companion renderer design](../../design/rendering/browser-wasm-renderer-design.md) · [Work docs index](../../README.md) · [Rendering architecture](../../../architecture/rendering/README.md)

**Status:** Proposed implementation checklist; browser/device validation has not been performed for this document.  
**Created:** September 23, 2026.  
**Repository:** `BlackJaxDev/XRENGINE`.  
**Source-review baseline:** [`4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63`][baseline] — “More Vulkan work,” September 23, 2026.  
**Intended repository location:** `docs/work/todo/rendering/mobile-webgpu-runtime-todo.md`.  
**Companion:** [`docs/work/design/rendering/browser-wasm-renderer-design.md`][design-snapshot], “Browser WebGL2 And WebGPU Renderer Modules.”

## 1. Purpose and scope

Get a real XRENGINE world running in mobile browsers through .NET 10 WebAssembly and WebGPU. This requires a portable runtime, a browser application host, a WebGPU renderer, cooked shaders/assets, mobile interaction, and validated lifecycle behavior. Adding a backend enum or rendering an unrelated JavaScript triangle is not completion.

This document turns the source review into implementation tasks. The companion document remains the architecture reference. Repository findings below refer to the pinned review baseline, not an assertion that later commits are unchanged. Reconcile the checklist with the implementation branch before starting work.

### Delivery scope

The initial target is a **mobile-browser canvas application**, optionally packaged as a PWA later. It is not a native Android/iOS application using a native WebGPU implementation, and it is not immersive WebXR. Native mobile packaging and immersive XR need separate host/target workstreams.

Deliver WebGPU first without discarding the companion design's two-module architecture. Its complete browser v1 includes both WebGPU and WebGL2, with `Auto` selection and explicit fallback. A WebGPU-only mobile milestone does **not** satisfy that broader definition of done. This checklist changes implementation order for the mobile goal; it does not redefine WebGL2 as implemented or unnecessary for the full design.

The first local rendering milestone does not require multiplayer, voice chat, the desktop editor, advanced GI, desktop bindless parity, mesh shaders, sparse textures, vendor upscalers, or worker rendering. Required gameplay services become mandatory for the playable milestone; browser transport becomes mandatory for a networked social client.

### Checklist rules

- Every unchecked item is work to implement, reconcile, or validate. Existing code is recorded separately rather than marked browser-tested.
- Use stable task IDs in changes, issues, and validation evidence. Check an item only after its deliverable and applicable acceptance criteria are satisfied.
- A working stub, enum, interface, successful desktop build, or dependency declaration does not establish browser functionality.
- Keep implementation status and validation status separate. Record untested environments explicitly.
- Preserve desktop OpenGL/Vulkan behavior while extracting shared code. Do not force desktop renderers through browser-specific abstractions.
- Validate a live feature slice before adding its regression coverage, following the repository's applicable contribution/test policy.

## 2. Source baseline: reuse versus missing work

| Area | Observed at the review baseline | Implementation consequence |
| --- | --- | --- |
| Presentation and renderer modules | `RendererBackendCreateContext` accepts an `IRendererPresentationTarget`; `AbstractRenderer` accepts `RendererHostContext`. Browser backend identities and acquired-output prerequisites are documented as landed. [Create context][create-context], [renderer base][abstract-renderer], [design][design-snapshot]. | Reuse the existing catalog, target, wrapper, generation, and output contracts. Do not recreate backend identities. |
| Browser application and backend | The checked solution contains OpenGL/Vulkan projects but no browser/WebGPU projects. Bootstrap accepts only `All`, `OpenGL`, `Vulkan`, or `None`. [Solution][solution], [bootstrap project][bootstrap-project]. | Implement browser composition, publishing, registration, and the actual renderer leaf. |
| Shared runtime dependencies | Core and Rendering target `net10.0-windows7.0` and include native/desktop integrations. [Core project][core-project], [Rendering project][rendering-project]. | Extract a portable dependency graph rather than changing framework strings alone. |
| Data layer | `XREngine.Data` targets `net10.0` but references DirectStorage, System.Drawing, and audio-related packages. [Data project][data-project]. | Audit reachable implementations and transitive assets; a neutral framework target is not proof of portability. |
| Frame host | `RuntimeRenderThreadHost` owns native event pumping, blocking loops, and optional dedicated-thread creation/join. [Render-thread host][render-thread-host]. | Introduce a browser-owned single-frame host; do not enter the desktop loop. |
| Shader infrastructure | `ShaderCompileRequest` exists, but the only compile target is `Vulkan14Spirv16`; `ShaderCompileResult` contains `byte[] SpirV`. [Request][shader-request], [target][shader-target], [result][shader-result]. | Extend existing contracts to represent WGSL artifacts. The companion's request/result work is partially landed, not absent. |
| Shader production | The generator emits desktop GLSL; Slang compilation launches a local executable and writes files. [Generator][shader-generator], [Slang compiler][slang-compiler]. | Add a WGSL cooking path and browser artifact loader; keep native compilation in development/cook tooling. |
| Desktop material table | The shared GLSL material table requires bindless-texture and shader-int64 extensions. [Material table][material-table]. | Add bounded WebGPU binding layouts rather than passing desktop handles to WGSL. |
| Remote assets | Remote loading includes synchronous wrappers and file-path-based downloaded-asset loading. [Remote asset API][remote-assets]. | Add async byte/stream-oriented consumption suitable for the browser host. |
| Audio and networking | Audio has native integrations; the realtime TLS client tunnel uses local UDP. A generic WebSocket component exists separately. [Audio project][audio-project], [TLS tunnel][tunnel], [WebSocket component][websocket-component]. | Implement/select browser services and integrate production replication explicitly. |
| Validation | The inspected Windows CI workflow builds/tests the desktop solution. [Windows CI][windows-ci]. | Add a browser publish and browser/device validation lane. Do not treat Windows CI as mobile evidence. |

Source paths are navigation starting points, not instructions to move entire files unchanged. Preserve exact path casing, and verify actual project inclusion before relocating code.

## 3. Delivery gates and dependencies

| Gate | Required result | Main workstreams |
| --- | --- | --- |
| G0 — Portable scene boot | A browser publish constructs an engine scene and advances component/transform lifecycle without desktop native services. | MW00–MW02; initial MW12 publish lane. |
| G1 — First engine-rendered frame | An engine camera, mesh, material, and resource wrappers render through the WebGPU module, with resize and basic failure reporting. | MW03, MW04 seed shaders, MW05 minimum raster slice. |
| G2 — Representative cooked world | The browser loads a real cooked scene with textures, lighting, transparency, shadows, tonemapping, and interaction. | Full MW04, MW06, MW07, relevant MW08. |
| G3 — Playable mobile WebGPU runtime | Required gameplay services, WebGPU baseline compute/indirect capability validation, recovery, and measured mobile budgets pass on physical devices. | MW08–MW10, MW12; G0–G2. |
| G4 — Networked mobile client | The real server path supports browser sessions and production replication, plus required voice/media services. | MW11; G3. |
| Full companion-design v1 | Independent WebGL2/WebGPU modules and documented `Auto`/forced selection work across the broader matrix. | Separate WebGL2 lane plus the companion design's remaining requirements. |

```text
MW00 scope + dependency audit
  -> MW01 portable runtime
  -> MW02 browser host
  -> MW03 batched bridge
  -> MW05 WebGPU resources and submission
  -> MW06 browser pipeline + MW07 cooked content
  -> MW08 playable services
  -> MW09 validated compute/indirect capabilities
  -> MW10 recovery and mobile performance
  -> G3 release validation

MW04 seed WGSL unblocks MW05; full shader cooking proceeds alongside MW05.
MW12 starts with the first browser project and continues through every gate.
MW11 can proceed after portable services exist; it is required for G4, not G1.
```

These are dependency gates, not a requirement to finish every optional feature in one workstream before beginning another. In particular, advanced GPU visibility is not a prerequisite for a CPU-direct first frame, and shader-toolchain generalization must not prevent validating the backend with a small explicit WGSL shader set.

## 4. Proposed project boundaries

Use the companion's names unless the repository audit establishes a better extraction with the same responsibilities. Entries below are intended boundaries, not claims that these projects already exist.

| Boundary | Responsibility |
| --- | --- |
| Portable scene/runtime kernel | Scene nodes, transforms, component lifecycle, portable scheduling, data contracts, asset identities, and service interfaces. |
| Portable rendering kernel | Generic resources, frame data, renderer modules/catalog, capabilities, pipeline contracts, and presentation metadata. |
| `XREngine.Runtime.Rendering.Browser` | Canvas host/target contracts, shared JS bootstrap utilities, packet ABI, input snapshots, handles, and lifecycle reporting. |
| `XREngine.Runtime.Rendering.WebGPU` | WebGPU wrappers, capability negotiation, binding/pipeline caches, pass encoding, JS packet executor, and device recovery. |
| `XREngine.Browser` | Browser executable and composition root, app bootstrap, browser services, static module registration, assets, and deployment output. |
| Desktop/native leaves | Existing native rendering, windowing, editor, imports, native physics/audio/media, XR, and other platform integrations. |

The browser shared layer must not import the desktop bootstrap or a concrete renderer. JavaScript GPU objects stay backend-private; the generic engine sees portable descriptions and generation-aware resource identities.

## MW00 — Reconcile scope, dependencies, and acceptance budgets

**Priority:** P0. **Depends on:** None. **Gate:** G0 planning inputs.

- [ ] **MW00.01** Compare the implementation branch with the review baseline. Record new browser, shader, platform, or asset work and link its evidence before removing or completing tasks here.
- [ ] **MW00.02** Inventory the transitive project/package graph required to construct, update, load, and render one minimal world. Include build targets, native assets, static initializers, module initializers, and implicit service registration.
- [ ] **MW00.03** Classify every reachable dependency as portable, browser-replaceable, offline-cook-only, desktop-only, or excluded from the selected milestone. Assign an extraction or substitution owner.
- [ ] **MW00.04** Confirm the first delivery is browser canvas/WebGPU. Record the WebGPU-only packaging policy and which broader WebGL2/`Auto` behavior remains pending.
- [ ] **MW00.05** Choose a deterministic sample world: one camera, static and skinned geometry, textured opaque/masked/transparent materials, a light/shadow receiver, environment, and basic UI. Keep a smaller static subset for G1.
- [ ] **MW00.06** Select physical iOS and Android reference devices and record exact OS/browser versions during testing. Define expected behavior for unsupported devices without asserting support from a user-agent string.
- [ ] **MW00.07** Set numeric startup, first-interaction, frame-time, resolution, memory, texture, upload, and content budgets before performance sign-off. Use the budget worksheet below; do not substitute unmeasured performance promises.
- [ ] **MW00.08** Record required versus optional gameplay services for G3 and G4. An explicitly required service must work or produce a blocking diagnostic, not silently become a no-op.

**Acceptance:** A reviewed dependency inventory, sample-world manifest, device matrix, and measurable gate definitions exist. A package's presence is not used as proof that its implementation works in WASM.

## MW01 — Extract and validate the portable runtime/rendering kernel

**Priority:** P0. **Depends on:** MW00. **Gate:** G0.

**Starting points:** [Core project][core-project], [Rendering project][rendering-project], [Data project][data-project], [Bootstrap project][bootstrap-project], [AbstractRenderer][abstract-renderer].

- [ ] **MW01.01** Extract the minimum scene, transform, component, world-update, data, and rendering contracts into browser-compatible project boundaries. Preserve public identities/serialization compatibility where needed.
- [ ] **MW01.02** Remove desktop/native dependencies from the browser's reachable graph. Isolate GLFW/SDL desktop hosting, OpenVR/OpenXR, DirectStorage, native ImageMagick/FFmpeg, Ultralight, native font processing, CUDA, and native physics/audio integrations according to MW00's inventory.
- [ ] **MW01.03** Audit neutral-target projects such as Data and Audio independently. Move unsupported implementations and runtime assets out of the browser graph rather than assuming `net10.0` makes them portable.
- [ ] **MW01.04** Separate the useful generic renderer/resource contracts from desktop, ImGui, native-window, image-processing, and native API dependencies in the current rendering assembly.
- [ ] **MW01.05** Introduce focused resource/pass/command/copy/presentation/completion capabilities. Keep legacy stateful renderer adapters where necessary without requiring WebGPU to emulate the entire GL-shaped API.
- [ ] **MW01.06** Create a browser composition root with explicit service installation. Do not reference `XREngine.Runtime.Bootstrap` unchanged or transitively register all desktop integrations.
- [ ] **MW01.07** Reuse the static factory/provider mechanisms, but generate registrations only for browser-included components, resources, serializers, and modules. Preserve stable type IDs needed by cooked assets.
- [ ] **MW01.08** Audit reflection discovery, dynamic code generation, dynamic expressions, runtime assembly loading, serializers, and generic construction for the selected WASM/trimming/AOT modes. Treat AOT as a separately tested publish option, not a blanket ban on all reflection.
- [ ] **MW01.09** Split build-time generators/cook tools from runtime dependencies. Ensure the browser build cannot invoke unrelated native submodule preparation, native DLL copy targets, or desktop-only tooling by accident.
- [ ] **MW01.10** Add a portability guard for the browser graph and applicable forbidden APIs/dependencies. Record justified exceptions rather than suppressing all analyzer warnings.
- [ ] **MW01.11** Validate the extracted scene kernel in an actual browser publish, then retain desktop build/runtime coverage for moved code.

**Acceptance:** G0 constructs a real engine scene, advances lifecycle and transforms, and reports startup success without loading desktop services. Portable changes preserve the existing desktop composition.

## MW02 — Browser application host, canvas target, and frame scheduling

**Priority:** P0. **Depends on:** MW01. **Gate:** G0, then G1.

**Starting points:** [RendererBackendCreateContext][create-context], [RuntimeRenderThreadHost][render-thread-host], [companion host design][design-snapshot].

- [ ] **MW02.01** Add the browser executable, HTML/ES-module bootstrap, and a supplied canvas binding. Keep rendering independent of a UI framework unless the product explicitly needs one.
- [ ] **MW02.02** Implement `BrowserCanvasRenderTarget` using `RenderExecutionMode.BrowserCanvas` and the corresponding presentation capability. Do not disguise the canvas as a desktop WSI target or Silk.NET `IWindow`.
- [ ] **MW02.03** Extract a portable surface-host contract for logical/physical dimensions, focus, lifecycle, input, and scheduling. Keep desktop-native escape hatches desktop-only.
- [ ] **MW02.04** Define asynchronous startup states and failure transitions. Reconcile the currently synchronous factory contract through a pending renderer/host initialization state or a reviewed async extension; never block on adapter/device creation.
- [ ] **MW02.05** Add a single-frame engine entry point driven by `requestAnimationFrame`, including simulation, visibility collection, engine-buffer swaps, frame packet production, and prompt return to JavaScript.
- [ ] **MW02.06** Remove dependence on the desktop `BlockForRendering()` path, native event pumping, thread joins, sleep/spin pacing, and synchronous waits in the browser frame path. Provide a single-thread scheduling policy for jobs needed by the initial host.
- [ ] **MW02.07** Implement bounded fixed-step catch-up and variable-step policy. Handle hidden/visible pages by policy, reset excessive elapsed time, and invalidate temporal history on resume as needed.
- [ ] **MW02.08** Handle CSS resize, physical backing size, device-pixel-ratio changes/caps, orientation, zero-sized or detached canvases, and reattachment. Publish target-generation changes when output resources are reconfigured.
- [ ] **MW02.09** Support one canvas with multiple engine viewports without accidental process-global canvas/device ownership. Preserve a future multi-canvas boundary without making multi-canvas delivery a G1 requirement.
- [ ] **MW02.10** Make startup, stop, and teardown idempotent. Remove browser listeners, cancel pending work, release owned resources, and prevent overlapping animation loops on restart.

**Acceptance:** The browser updates an engine scene, remains responsive, handles resize and page suspension, and reports startup failures. No desktop loop runs inside a browser callback.

## MW03 — Batched .NET/JavaScript command and upload bridge

**Priority:** P0. **Depends on:** MW01–MW02. **Gate:** G1; maintained through G3.

- [ ] **MW03.01** Implement a low-frequency control lane using supported generated .NET JavaScript interop for bootstrap, capability snapshots, lifecycle, and diagnostics.
- [ ] **MW03.02** Prove persistent packet/input/upload memory-view lifetime, ownership, disposal, and copy behavior with the exact selected .NET runtime before expanding the graphics ABI. Do not assume a view proves zero-copy GPU upload.
- [ ] **MW03.03** Define a versioned little-endian packet header and typed command layout: ABI/backend identity, frame ID, byte/command counts, arena generation, resource generation, and development validation metadata.
- [ ] **MW03.04** Use reusable packet slots and upload arenas. Implement an explicit producer/consumer ownership handshake so memory cannot be overwritten while the executor is reading it.
- [ ] **MW03.05** Define arena overflow and growth behavior: reject the affected frame with the required capacity, grow only at a safe boundary, publish a new view generation, and dispose obsolete views after consumers release them.
- [ ] **MW03.06** Add backend-owned generation-checked handle tables. Validate object type, owner, generation, command enums, offsets, alignment, counts, lengths, and integer overflow before executing a packet.
- [ ] **MW03.07** Distinguish packet-consumption lifetime from GPU-resource lifetime. Release packet storage only after referenced source bytes have been consumed/copied as required by the called API; do not keep transient arena views alive in unresolved promises.
- [ ] **MW03.08** Ensure asynchronous callbacks capture and validate the relevant device/resource generation before publishing a result. Discard cancelled, obsolete, or post-teardown completions.
- [ ] **MW03.09** Package the WebGPU executor with the WebGPU module. Keep API-specific GPU constants/object logic out of the shared browser ABI layer.
- [ ] **MW03.10** Add packet, upload, arena-growth, and interop-call counters plus command labels for development failures. Benchmark increasing draw counts to verify managed/JS crossings remain bounded independently of draw count.
- [ ] **MW03.11** Validate malformed/truncated packets, stale handles, growth during queued work, cancellation, disposal, and memory pressure. Record allocation/copy evidence rather than assuming all paths are allocation-free.

**Acceptance:** G1 uses the real packet path. Many ordinary draws may produce many JavaScript WebGPU API calls, but they do not produce one managed-to-JavaScript transition per draw or state mutation. No stale arena access or implicit per-frame GPU wait is needed for correctness.

## MW04 — WGSL shader artifacts and material/layout contracts

**Priority:** P0. **Depends on:** MW01; progresses alongside MW03–MW05. **Gates:** G1 seed shaders, G2 cooked materials.

**Starting points:** [ShaderCompileRequest][shader-request], [ShaderCompileTarget][shader-target], [ShaderCompileResult][shader-result], [ShaderGenerator][shader-generator], [Slang compiler][slang-compiler], [Slang plan](../../design/scripting/slang-shader-cross-compile-plan.md).

### First-frame shader subset

- [ ] **MW04.01** Add a small explicit WGSL set for unlit color/texturing and the first depth-tested engine mesh. Keep bindings and layouts explicit so this validates backend integration, not a separate rendering system.
- [ ] **MW04.02** Define the initial portable vertex, frame, object, and material data layouts with matching C# packing and shader declarations. Add known-value visual/readback checks for offsets and transforms.

### Production artifact path

- [ ] **MW04.03** Extend the existing compile-target contract with WGSL. Generalize the result's SPIR-V-specific payload into a target-tagged artifact representation while preserving existing Vulkan behavior and provenance.
- [ ] **MW04.04** Define source language, entry point, stage, includes, defines, specialization, required capabilities, compiler identity, and reflection/schema identity in artifact/cache keys.
- [ ] **MW04.05** Evaluate and pin an approved WGSL-producing toolchain through the existing dependency/license workflow. Reuse the Slang investment where validated; do not assume every existing GLSL/Slang construct lowers correctly.
- [ ] **MW04.06** Keep process-based/native compilation in offline cook/development tooling. Load cooked WGSL and metadata at browser runtime without requiring the Vulkan compiler provider, temporary desktop files, or a local executable.
- [ ] **MW04.07** Extend material/shader generation through an explicit target-aware or language-neutral boundary. Do not add a long-term regex translator from desktop GLSL to WGSL.
- [ ] **MW04.08** Specify uniform/storage alignment, padding, array/matrix strides, scalar widths, address spaces, binding groups, texture/sampler pairing, vertex locations, and render-target outputs. Validate against the selected WGSL target, not assumed GLSL layout equivalence.
- [ ] **MW04.09** Define the engine-to-WebGPU coordinate contract: matrix multiplication/storage convention, clip-space depth, viewport/texture Y handling, front-face winding, render-to-texture sampling, and reversed-Z variants when included.
- [ ] **MW04.10** Produce named cook-time errors for unsupported shader stages, capabilities, bindings, or layouts. Include the material/pass, source location, entry point, target, and compiler diagnostic.
- [ ] **MW04.11** Ship source-mapped WGSL diagnostics, deterministic artifacts, dependency hashes, and runtime schema validation. Reject incompatible cached artifacts rather than binding them with an old layout.
- [ ] **MW04.12** Implement browser shader-module/pipeline readiness and warm-up budgets. Cooked WGSL still requires browser-side shader/pipeline compilation; do not block the page waiting for all variants.

**Acceptance:** A cooked material renders with correct data interpretation and coordinates, and an intentionally unsupported shader fails with an actionable source-linked diagnostic. Vulkan shader compilation remains intact.

## MW05 — WebGPU renderer module, resources, and submission

**Priority:** P0. **Depends on:** MW02–MW03 and MW04 seed shaders. **Gates:** G1 and WebGPU baseline readiness.

- [ ] **MW05.01** Implement the WebGPU module metadata, factory, lifecycle, static registration, and backend build/publish selection. Reuse existing WebGPU IDs/capability contracts rather than introducing aliases.
- [ ] **MW05.02** Implement async adapter/device acquisition, capability inspection, exact required-feature/limit requests, and canvas configuration. Distinguish API absence, no adapter, insufficient capabilities, and initialization errors.
- [ ] **MW05.03** Publish the selected instance capabilities/limits and profile only after validation. Do not treat the WebGPU backend name as proof that every desktop feature or requested limit exists.
- [ ] **MW05.04** Implement generic-resource wrappers for buffers, textures/views, samplers, shader modules, binding layouts/groups, render/compute pipelines, and mesh submission. Track owner, generation, readiness, usage, size, and debug name.
- [ ] **MW05.05** Implement buffer creation, bounded writes/copies, vertex/index/uniform/storage/indirect usages, dynamic offsets, and target-appropriate alignment. Define a deliberate replacement for persistent native mapping.
- [ ] **MW05.06** Implement the initial texture dimensions/formats, mip upload, view creation, sampling, color-space interpretation, and format-dependent usage validation. Expand optional formats only when capability checks and tests exist.
- [ ] **MW05.07** Lower `XRFrameBuffer` attachment plans into render-pass descriptors. Handle load/store/clear policy, depth/stencil aspects, resolve targets, and sample-count compatibility without inventing a native framebuffer object.
- [ ] **MW05.08** Cache immutable pipelines and layouts using complete shader/binding/vertex/raster/depth/blend/attachment/sample keys. Reject incompatible cache entries and bound cache growth.
- [ ] **MW05.09** Implement frame command encoding, render/compute/copy ordering, queue submission, and presentation. Reacquire the canvas output for the appropriate frame; do not retain a prior frame's acquired output as a persistent render target.
- [ ] **MW05.10** Publish `RenderFrameOutputDescription` with dimensions, formats, samples, layers/views, frame slot, and target generation while keeping GPU/JS handles private to the backend.
- [ ] **MW05.11** Implement asynchronous readback/completion tickets, request cancellation, and error scopes. Reject synchronous `WaitForGpu`-style assumptions in ordinary browser execution and teardown.
- [ ] **MW05.12** Define explicit resource retirement after the last permitted use. Distinguish resource destruction from releasing references to objects that do not expose a destroy operation; finalizers are leak diagnostics, not lifetime management.
- [ ] **MW05.13** Add initial `Pending`, `Ready`, `Failed`, and `Lost` handling, plus a terminal device-loss transition. Full reconstruction is MW10, but invalid work must stop immediately from the first implementation.
- [ ] **MW05.14** Render an engine-owned indexed mesh through its camera, material, wrappers, pass, packet, and presentation target. Keep a JS-only triangle as a bootstrap diagnostic, not G1 acceptance.

**Acceptance:** G1 renders through the real engine path with clean WebGPU diagnostics, stable resource ownership, working resize, and no desktop renderer dependency. Remaining baseline compute/indirect behavior is tracked in MW09 rather than silently claimed complete.

## MW06 — Focused browser pipeline and portable material binding

**Priority:** P0. **Depends on:** MW04–MW05. **Gate:** G2.

**Starting points:** [Desktop material table][material-table], [material binding policy][material-policy], and the companion's `BrowserWebGPUBaseline` profile.

- [ ] **MW06.01** Implement a focused `BrowserRenderPipeline` or an explicitly capability-defined existing-pipeline variant. Do not execute the complete desktop advanced pipeline and discover unsupported passes at runtime.
- [ ] **MW06.02** Start with explicit CPU-direct submission of engine meshes to validate raster correctness. Label this as the initial raster slice, not completion of the companion's entire WebGPU baseline.
- [ ] **MW06.03** Implement opaque, alpha-masked, and sorted transparent rendering; depth testing/writes; culling; indexed/instanced draws; and the selected material-shading subset.
- [ ] **MW06.04** Implement one practical directional-light shadow path, environment/sky rendering, the chosen ambient/baked-lighting path, and SDR presentation. Add HDR intermediates and tonemapping for full baseline acceptance.
- [ ] **MW06.05** Replace desktop bindless-handle material lookup with bounded bind-group layouts and explicit material batching. Keep engine material identities independent of GPU texture handles.
- [ ] **MW06.06** Use texture arrays/atlases only for content that satisfies documented packing/sampling constraints. Preserve the existing distinction between arbitrary material textures and opted-in homogeneous texture classes.
- [ ] **MW06.07** Define stable frame/pass, material, and object/instance binding conventions. Bound layout variants, material-group churn, pipeline compilation, and cache residency.
- [ ] **MW06.08** Resolve required/optional capabilities at pipeline/material selection. Fail unsupported required features by name; list optional exclusions or an explicitly permitted fallback in diagnostics.
- [ ] **MW06.09** Add engine UI composition using portable assets/shaders. Keep browser DOM cooperation for text entry/accessibility where appropriate without making native ImGui or Ultralight a runtime prerequisite.
- [ ] **MW06.10** Add mobile quality settings for backing resolution/DPR, shadow size/update rate, light count, texture tier, material complexity, and selected post effects. Avoid allocating resources for disabled desktop effects.
- [ ] **MW06.11** Validate asymmetric test patterns for orientation, offscreen composition, depth/reversed-Z if enabled, linear/sRGB sampling, alpha conventions, transparency order, shadows, and tonemapping.

**Acceptance:** G2 displays the representative world through engine objects and a declared browser profile. Visual differences from desktop are deliberate, documented profile choices, not missing passes hidden by catch-all fallback.

## MW07 — Cooked content, asynchronous assets, and texture variants

**Priority:** P0. **Depends on:** MW01 and MW04; integrate with MW05–MW06. **Gate:** G2.

**Starting points:** [Remote asset API][remote-assets], [texture compression TODO](../texturing/texture-compression-and-cooked-cache-todo.md), [texture design](../../design/texturing/texture-compression-and-cooked-cache-design.md).

- [ ] **MW07.01** Add a browser-targeted cook manifest containing asset IDs, hashes, dependencies, schema/toolchain versions, required capabilities, and compatible payload variants.
- [ ] **MW07.02** Add async asset consumption from bytes/streams or a portable asset-source abstraction. Do not require downloaded content to become a native filesystem path before the engine can deserialize it.
- [ ] **MW07.03** Route browser loading through asynchronous APIs end to end. Remove browser reachability of synchronous `.GetAwaiter().GetResult()` wrappers and arbitrary filesystem enumeration/watching assumptions.
- [ ] **MW07.04** Keep model imports, expensive image conversion, shader-tool invocation, collision cooking, and font-atlas generation offline unless a specific browser implementation is separately approved and validated.
- [ ] **MW07.05** Split first-use essentials from streamed content. Load the minimal world/material/shader set first; budget subsequent decode, deserialization, upload, and resource creation across frames.
- [ ] **MW07.06** Implement bounded download concurrency, cancellation, progress, retry/backoff policy, hash/schema checks, and named missing/corrupt-asset diagnostics.
- [ ] **MW07.07** Publish content-hash URLs and immutable payload caching with an upgrade-safe bootstrap manifest. Never combine a new shader layout with an old cached material payload.
- [ ] **MW07.08** Implement the selected compressed-texture variants after capability negotiation, with a defined uncompressed fallback. Reconcile ASTC, ETC2/EAC, and KTX2/Basis work with the existing texture roadmap rather than duplicating it.
- [ ] **MW07.09** Preserve mip chains, color-space intent, normal-map conventions, alpha behavior, and material sampling semantics through cooking and upload. Validate format-specific copy/upload requirements.
- [ ] **MW07.10** Track downloaded/compressed, decoded, managed, staging, and GPU-resident bytes separately. Avoid retaining unnecessary full-resolution source copies after upload, while preserving an explicit reload/recovery recipe.
- [ ] **MW07.11** Apply strict size/count/dependency-depth limits to untrusted world manifests and payloads. Restrict asset URLs and credentials by policy; reject path traversal, incompatible types, and uncontrolled allocation requests.
- [ ] **MW07.12** Keep persistent browser caching optional initially. When added, handle quota/eviction, partial downloads, version changes, cancellation, and unavailable storage without corrupting the world cache.

**Acceptance:** A static browser deployment loads a cooked world on cold and warm cache paths, handles throttled or failed downloads, and never requires desktop importers or local path-based downloads at runtime.

## MW08 — Mobile interaction, animation, physics, audio, and UI

**Priority:** P1; minimum touch interaction is needed earlier for G2. **Depends on:** MW01–MW02 and the relevant G2 content/rendering work. **Gate:** G3.

- [ ] **MW08.01** Normalize pointer/touch events into the engine input snapshot model, preserving pointer identity, capture, release/cancel, focus loss, and simultaneous touches.
- [ ] **MW08.02** Implement mobile camera/movement controls with independent gestures or virtual controls. Ensure UI interaction cannot accidentally move the camera/player and browser scrolling is suppressed only for owned gestures.
- [ ] **MW08.03** Add keyboard/text-input separation, mobile text entry/IME cooperation, wheel normalization, and gamepad polling where included. Do not require desktop pointer lock or hover to perform essential actions.
- [ ] **MW08.04** Handle orientation, safe-area insets, browser UI changes, and on-screen keyboard resize. Keep UI hit testing in the same logical/physical coordinate convention as rendering.
- [ ] **MW08.05** Port/validate the required CPU animation state, hierarchy evaluation, clips, and skinning data path. Establish an animated reference scene before promoting optional compute animation optimizations.
- [ ] **MW08.06** Select and validate a browser-compatible physics path from the inventory. Evaluate existing candidates rather than assuming a package reference proves WASM support; isolate excluded native physics backends.
- [ ] **MW08.07** Implement the sample's required character collision/movement and fixed-step integration. Validate pause/resume, large deltas, scene unloading, and animation/physics ordering.
- [ ] **MW08.08** Add a Web Audio-backed service for required playback, listener/source updates, gain, looping, and supported asset decoding. Keep native OpenAL/FFmpeg/NAudio integrations out of the browser runtime path.
- [ ] **MW08.09** Handle audio start/resume in an appropriate user-gesture flow, plus pause, denied access, and context suspension. Do not declare required audio ready before it actually is.
- [ ] **MW08.10** Use cooked font/UI assets or a deliberately selected browser implementation. Complete essential navigation, loading/error state, interaction, and text-entry behavior without importing the desktop editor.
- [ ] **MW08.11** Install explicit unsupported-service implementations only for excluded optional services, with visible reasons. Never silently disable a service required by the loaded world's manifest/profile.

**Acceptance:** A user can load the sample, move and interact by touch, see required animation, and use its declared physics/audio/UI features. No essential control requires desktop-only interaction.

## MW09 — WebGPU compute, indirect draws, and measured GPU-resident expansion

**Priority:** P1 for baseline capability validation; P2 for advanced optimization. **Depends on:** MW04–MW07. **Gate:** G3 baseline; later expansion for advanced paths.

The companion's `BrowserWebGPUBaseline` includes storage buffers, compute, supported indirect draws, and HDR intermediates. An initial CPU-direct raster slice is useful but does not satisfy that profile by itself. Conversely, supporting those capabilities does not require turning every scene into the desktop GPU-driven pipeline.

### Baseline capability implementation

- [ ] **MW09.01** Implement and validate storage-buffer binding, compute-pipeline creation, dispatch, and compute-to-render consumption through the same packet and resource-lifetime system.
- [ ] **MW09.02** Query and honor actual workgroup, buffer-size, binding, and dispatch limits. Use validated shader/layout variants rather than desktop constants copied without an adapter check.
- [ ] **MW09.03** Implement supported indirect draw submission with initialized argument buffers, bounds/alignment validation, correct index formats/offsets, and tested instance addressing.
- [ ] **MW09.04** Validate optional indirect-instance behavior before relying on it. Do not assume desktop draw-ID, indirect-count, descriptor-indexing, or multi-draw semantics are available through the selected browser API.
- [ ] **MW09.05** Respect render/compute pass resource-usage rules, subresource aliasing constraints, and ordering. Verify that the same resource is not illegally read/written within a usage scope.
- [ ] **MW09.06** Add deterministic compute/readback and indirect-render reference cases. Verify empty draws, zero visible instances, maximum supported arguments, and error reporting without synchronous readback in the frame loop.

### Optional optimizations after correctness

- [ ] **MW09.07** Port compute skinning/blendshapes where measured beneficial. Validate CPU/GPU results, normal/tangent handling, bone palettes, active morph bounds, and visible-avatar workload limits.
- [ ] **MW09.08** Add GPU scene buffers and compute culling with a capability-compatible submission plan. A bounded draw list with zero-count culled draws is an acceptable intermediate strategy; avoid requiring GPU draw-count readback every frame.
- [ ] **MW09.09** Add Hi-Z or hierarchical visibility only after depth construction, reductions, resize invalidation, and conservative visibility rules are validated on the selected profile.
- [ ] **MW09.10** Keep material-aware batching compatible with bounded bind groups. Do not make arbitrary desktop bindless textures a prerequisite for GPU visibility or skinning.
- [ ] **MW09.11** Compare CPU-direct, CPU-culling, compute-culling, and skinning variants on physical devices under sustained load. Select defaults using measured total frame cost, not fewer draw calls alone.
- [ ] **MW09.12** Keep forced strategy requests honest: unsupported required compute/indirect/meshlet settings fail visibly; automatic choices may select only a documented permitted strategy and must report it.

**Acceptance:** The declared WebGPU baseline's compute/indirect behavior passes correctness checks. Advanced GPU-resident features are enabled individually only when their visual correctness and mobile cost are measured.

## MW10 — Device recovery, mobile lifecycle, memory, and performance

**Priority:** P0 for correctness; P1 for performance qualification. **Depends on:** MW02–MW09 as applicable. **Gate:** G3.

### Recovery and ownership

- [ ] **MW10.01** Implement complete device-loss handling: stop submission, distinguish intentional shutdown, invalidate device-owned wrappers/handles, and transition the renderer out of ready state.
- [ ] **MW10.02** Reacquire/recreate the required device state, renegotiate capabilities, increment generations, reconfigure the canvas, and rebuild the minimal resource set from cooked/CPU sources before resuming.
- [ ] **MW10.03** Reject old-generation compilation, download-to-GPU, readback, map, and completion callbacks. Prevent asynchronous work from resurrecting resources after teardown or replacing newer content.
- [ ] **MW10.04** Recreate pipeline/binding caches and invalidate temporal/shadow/history resources as required. Restore viewport/output metadata without leaking JavaScript handles into the generic render graph.
- [ ] **MW10.05** Bound recovery attempts and preserve useful failure diagnostics. A lost WebGPU device must not silently turn into WebGL2 mid-session; any API change requires the documented application-level restart policy.
- [ ] **MW10.06** Validate hide/show, app switching, screen lock/unlock, orientation changes during loading/recovery, canvas removal, repeated scene load/unload, and explicit renderer restart.

### Budgets and diagnostics

- [ ] **MW10.07** Implement configurable backing-resolution/DPR caps and optional dynamic resolution. Avoid reallocating every frame near a threshold; record resolution changes and their effect on temporal state.
- [ ] **MW10.08** Bound scene residency, texture mips, skinning/morph workloads, particles, shadows, transparent overdraw, and expensive post effects for each profile.
- [ ] **MW10.09** Budget uploads, shader/pipeline creation, and resource reconstruction over frames. Separate packet backpressure from GPU completion; avoid UI-thread stalls and unbounded queued work.
- [ ] **MW10.10** Record managed heap, retained decoded data, arena capacities, estimated GPU residency, and peak startup/recovery memory. Label GPU memory estimates as estimates rather than precise browser allocator telemetry.
- [ ] **MW10.11** Expose CPU simulation/collection/packet time, JS executor time, frame-time percentiles, draw/pass counts, interop calls, allocations, upload bytes, shader readiness, errors, and loss/recovery counters. Use GPU timing only when supported and enabled; report absence honestly.
- [ ] **MW10.12** Run sustained sessions and workload sweeps on reference devices to assess frame pacing and load-related degradation, including warm devices. Do not claim direct thermal telemetry unless an actual measurement source is available.
- [ ] **MW10.13** Compare interpreted and AOT publishes for startup payload, time to interaction, memory, and runtime CPU cost. Choose the shipping configuration from measured results.
- [ ] **MW10.14** Demonstrate a steady-state managed rendering path without recurring per-draw allocations or temporary interop proxies. Record unavoidable browser/API allocations separately rather than asserting zero allocation everywhere.

**Acceptance:** G3 survives the lifecycle matrix without stale resource reuse and meets recorded budgets on the selected physical devices. Recovery, resource counts, and memory remain bounded across repeated cycles.

## MW11 — Browser multiplayer and optional voice/media

**Priority:** P1 for a networked product. **Depends on:** MW01, MW07–MW08; integrate with MW10 lifecycle. **Gate:** G4 only.

**Starting points:** [Realtime TLS client tunnel][tunnel], [generic WebSocket component][websocket-component].

- [ ] **MW11.01** Separate production replication/session logic from native socket ownership. Inventory which transport assumptions leak into packet framing, reliability, ordering, authentication, and scheduling.
- [ ] **MW11.02** Implement a browser-compatible transport and matching server endpoint/gateway. A WebSocket-based first path is acceptable when its delivery semantics fit the selected milestone; do not relabel the existing raw TLS/local-UDP tunnel as browser support.
- [ ] **MW11.03** Integrate the actual production replication protocol, session bootstrap, authentication, entity/asset references, and version negotiation. The generic `WebSocketClientComponent` alone is not G4 completion.
- [ ] **MW11.04** Define send/receive bounds, backpressure, stale-state coalescing, reconnect/resync, and mobile suspension behavior. Validate any browser restrictions on authentication/header/cookie handling in the chosen transport.
- [ ] **MW11.05** Measure latency and head-of-line effects before selecting reliable ordered delivery for high-rate state. Evaluate WebRTC/WebTransport as separate capability-gated options when the product needs different delivery semantics.
- [ ] **MW11.06** Apply explicit origin/credential policy and server-side authorization; never trust the browser client to establish asset ownership or authoritative game state.
- [ ] **MW11.07** When voice is required, implement microphone permission, capture, encoding/transport, playback/mixing, muting, and reconnection using validated browser services. Keep this independent of desktop audio capture assumptions.
- [ ] **MW11.08** Validate production-server compatibility under network throttling, disconnects, reconnects, app switching, session expiry, and scene transitions. Label a local-only G3 build clearly until this passes.

**Acceptance:** G4 connects through the real supported server path and exchanges production state within documented delivery, latency, and memory budgets. Required voice/media paths work or fail explicitly.

## MW12 — Publish pipeline, validation evidence, and repository integration

**Priority:** P0. **Depends on:** Starts with MW01; expanded at every gate. **Gates:** G0–G4.

- [ ] **MW12.01** Add a browser-specific build/publish target with a pinned compatible .NET SDK/workload setup. Ensure it is independently buildable and does not first build every native desktop submodule.
- [ ] **MW12.02** Publish the runtime, app bootstrap, module-owned JS executor, WGSL/metadata, and cooked manifest/assets together. Validate content/schema/ABI version consistency in clean output.
- [ ] **MW12.03** Provide a minimal production-equivalent HTTPS hosting configuration with correct WASM/asset MIME types, content encoding, fingerprinted caching, and bootstrap revalidation.
- [ ] **MW12.04** Validate CORS, credentials, CSP, connect-source restrictions, and compressed/uncompressed delivery against the actual runtime. Document any required permissions rather than disabling browser security globally.
- [ ] **MW12.05** Keep the initial main-thread/non-shared-memory deployment independent of unnecessary isolation requirements. Introduce cross-origin isolation only when a selected shared-memory configuration needs it; do not assume OffscreenCanvas alone implies shared WASM memory.
- [ ] **MW12.06** Add browser smoke checks for scene boot, real engine rendering, errors, asset loading, and supported capability paths. Add regression coverage after live validation according to repository policy.
- [ ] **MW12.07** Validate physical iOS/Android browsers explicitly. Desktop automation or a WebKit-based test harness is useful regression coverage, not a replacement for physical mobile-browser evidence.
- [ ] **MW12.08** Capture tolerant reference-image comparisons plus inspected output for pipeline, orientation, materials, shadows, and animation. Record compute/indirect correctness separately from screenshot similarity.
- [ ] **MW12.09** Exercise all applicable failure/recovery cases in the matrix below. Keep expected unsupported-device errors distinct from regressions and unexpected validation errors.
- [ ] **MW12.10** Record exact build command/configuration, commit, device/OS/browser, capabilities/profile, scene, measurements, diagnostics, and pass/fail evidence for each gate. Preserve failed-run evidence and outstanding limitations.
- [ ] **MW12.11** Retain relevant desktop OpenGL/Vulkan regression checks for extracted code and changed shader contracts. Confirm WebGPU-only publishes omit excluded renderer modules and native assets.
- [ ] **MW12.12** Add this file to `docs/work/todo/rendering/`. Link it from the companion design using `../../todo/rendering/mobile-webgpu-runtime-todo.md` and from `docs/work/README.md` using `todo/rendering/mobile-webgpu-runtime-todo.md`.
- [ ] **MW12.13** Update the companion's landed-work notes for the existing shader request/result contracts and each newly validated browser slice. Preserve the distinction between WebGPU-first delivery and complete dual-backend v1.
- [ ] **MW12.14** Publish user-facing build, cook, host, support, and troubleshooting instructions after validation. Include explicit known limitations, required services, fallback rules, and how to export browser diagnostics.

**Acceptance:** A clean checkout can produce the documented static browser build, deploy it using the documented host, and reproduce the applicable gate results. CI status alone is not a substitute for physical-device qualification.

## 5. Deferred and separate workstreams

These remain visible so they are not mistaken for implemented support or allowed to block the first useful mobile build.

- [ ] **MW-D01 — WebGL2 and full `Auto` behavior:** Implement the separate WebGL2 renderer/GLSL ES cooking/context recovery lane. Then validate forced WebGL2, forced WebGPU, automatic selection/fallback, and parity for the companion's baseline scene. A WebGPU-only package must clearly report an unavailable fallback rather than claiming to use one.
- [ ] **MW-D02 — Worker/OffscreenCanvas host:** Add only after profiling the main-thread path. Define ownership transfer, input delivery, lifecycle, exact .NET threading requirements, hosting/isolation implications, and unsupported-mode behavior. Do not silently switch a transferred canvas between incompatible host modes.
- [ ] **MW-D03 — PWA/offline packaging:** Add manifest/install behavior, service-worker versioning, update consistency, storage/quota policy, and offline-content rules after ordinary HTTPS browser delivery works.
- [ ] **MW-D04 — WebXR:** Design a separate presentation target and capability path with runtime/device validation. Do not reuse native OpenXR/OpenVR integration or assume mobile canvas rendering establishes immersive XR support.
- [ ] **MW-D05 — Native mobile applications:** Design native Android/iOS composition, surfaces, packaging, permissions, and native dependencies independently if requested. The browser JS bridge is not automatically the correct native backend boundary.
- [ ] **MW-D06 — Advanced desktop feature promotion:** Treat GI, meshlet/BVH pipelines, complex post effects, vendor-specific features, and editor functionality as separate capability/performance projects. Cook-time and startup diagnostics must continue rejecting unsupported required features.

## 6. Validation matrix

Run the relevant subset at each gate, then the complete selected product matrix before release. “Not run,” “not supported,” and “failed” are different outcomes.

| Area | Required cases | Evidence |
| --- | --- | --- |
| Platform boot | Clean publish; required desktop services absent; unsupported API/device; initialization cancellation. | Build/publish log, dependency inventory, startup diagnostics. |
| Physical devices | Selected iPhone/iPad Safari and Android Chrome configurations; additional browsers according to declared support. Record actual versions and adapter features. | Device/OS/browser/build/profile record. |
| Canvas | CSS resize, DPR cap/change, orientation, safe area, soft keyboard, zero size, detach/reattach, multiple viewports. | Captures and target-generation/lifecycle logs. |
| Frames | Responsive input; bounded simulation catch-up; no duplicate rAF loops; no desktop blocking loop. | Timing and lifecycle traces. |
| Content | Static/skinned meshes, opaque/masked/transparent materials, textures/mips, shadows, environment, UI, tonemapping. | Inspected captures, tolerant reference comparisons. |
| Shader/data ABI | Matrix/layout known values, binding compatibility, offscreen Y/depth, invalid shader, stale artifact/schema. | Diagnostics and deterministic expected outputs. |
| Bridge | Increasing draw counts, malformed packet, stale handles, upload-arena growth, cancellation, memory-view disposal. | Interop/allocation counters and failure diagnostics. |
| Compute/indirect | Known compute output, compute-to-render, empty visibility, indirect arguments and instance addressing. | Correctness checks/readback evidence outside the hot path. |
| Assets | Cold/warm cache, throttled download, corruption/missing chunk, cancellation, cross-origin rejection, cache upgrade. | Asset diagnostics, startup/peak-memory measurements. |
| Lifecycle/loss | Hide/show, lock/unlock, app switch, forced/test-induced device-loss path, recovery, shutdown during async work. | Recovery generation/resource/memory logs. |
| Gameplay | Multi-touch controls, UI focus, required animation/physics/audio, user-gesture audio activation. | Recorded manual scenarios and diagnostics. |
| Networking, G4 | Real-server session, replication, reconnect/resync, backpressure, auth expiry, suspension, required voice. | Protocol/server/client traces and bounded-queue metrics. |
| Deployment | HTTPS, MIME, compression, CORS/CSP, cache consistency, clean build, selected AOT/runtime mode. | Hosting config plus browser network/error traces. |
| Performance | Startup/interaction, frame-time distribution, steady-state allocations, uploads, memory peaks, sustained sessions. | Filled budget worksheet with run conditions. |
| Desktop preservation | Existing supported OpenGL/Vulkan scenarios affected by extraction or shader-contract changes. | Build/runtime regression evidence. |

## 7. Budget worksheet

Set target values under MW00.07; collect results under MW10/MW12. No performance result is asserted by this initial checklist.

| Metric | Target/limit to define | Measured result | Run conditions |
| --- | --- | --- | --- |
| Initial compressed application payload | Define before G3 sign-off. | Not measured. | Runtime mode, content split, compression. |
| Cold time to useful frame and first interaction | Define on a named connection/device. | Not measured. | Cache/network/device conditions. |
| Warm time to useful frame | Define separately from cold startup. | Not measured. | Cache state and manifest version. |
| Sustained frame-time p50/p95/p99 | Define per device/profile and chosen refresh target. | Not measured. | Scene, resolution, session duration. |
| Managed simulation/collection/packet CPU time | Set per-frame limits. | Not measured. | Workload and runtime configuration. |
| JavaScript executor CPU time | Set per-frame limit. | Not measured. | Draw/pass/command counts. |
| Managed/JS interop crossings per frame | Bounded independently of ordinary draw count. | Not measured. | Low/high draw-count comparison. |
| Steady-state managed render allocations | Target no recurring per-draw allocations; document remaining sources. | Not measured. | Warm-up state and sampling method. |
| Upload bytes/resource creations per frame | Set steady and burst limits. | Not measured. | Scene streaming/startup/recovery. |
| Packet/upload arena capacity and growth | Set resident/peak limits and overflow policy. | Not measured. | Normal and stress workloads. |
| Managed/decoded/staging/GPU residency | Set separate steady/peak budgets; GPU bytes may be estimated. | Not measured. | Startup, scene change, recovery. |
| Recovery time and retained resource count | Define successful-resume and leak thresholds. | Not measured. | Loss/shutdown/resize scenario. |
| G4 transport queue and latency bounds | Define by message class and delivery semantics. | Not measured. | Server/network/session conditions. |

## 8. Completion evidence and release checklist

Use an evidence entry for each checked work item or accepted group of closely related tasks:

```text
Task IDs:
Implementation commit / PR:
Gate and selected profile:
Build / publish configuration:
Device, OS, browser, and versions:
Capabilities / limits relevant to the feature:
Scene / test / reproduction steps:
Observed result and measurements:
Logs / screenshots / capture artifacts:
Remaining limitations and untested configurations:
```

### Mobile WebGPU runtime — G3

- [ ] **MW-R01** A clean static publish runs a real cooked XRENGINE world on the selected physical mobile devices.
- [ ] **MW-R02** The browser runtime graph and startup path exclude unsupported desktop/native services.
- [ ] **MW-R03** The selected WebGPU baseline renders correctly, and its claimed compute/indirect/HDR capabilities have actual validation evidence.
- [ ] **MW-R04** Required material variants, touch controls, animation, physics, audio, and UI work; optional exclusions are documented.
- [ ] **MW-R05** Asset/shader cooking and asynchronous loading are deterministic and compatible with runtime schemas, cache upgrades, and resource recovery.
- [ ] **MW-R06** Managed/JS interop is bounded independently of ordinary draw count; frame-time, allocation, upload, startup, and memory budgets pass.
- [ ] **MW-R07** Resize, page suspension, teardown, and device-loss recovery pass without stale handles, overlapping loops, or unbounded resource retention.
- [ ] **MW-R08** Required-feature failures are actionable, and no implicit desktop or WebGL2 fallback is presented as WebGPU success.
- [ ] **MW-R09** Browser hosting, support matrix, limitations, reproduction commands, and evidence are documented; affected desktop paths remain validated.

### Networked mobile client — G4

- [ ] **MW-R10** The actual server/replication path works through the supported browser transport with bounded queues, reconnect/resync, and required authentication behavior.
- [ ] **MW-R11** Required voice/media and mobile suspension behavior pass the networked-device matrix.

**Do not mark full companion-design v1 complete at G3 or G4 unless its separate WebGL2 module, `Auto` selection/fallback, and broader validation requirements are also satisfied.**

## 9. Repository source references

References below are pinned to the reviewed commit. The relative navigation links at the top are intended to work after placing this file at the proposed repository location. Use the pinned companion link when reading this file outside the repository.

| Source group | Navigation |
| --- | --- |
| Reviewed baseline and architecture | [Commit][baseline]; [companion renderer design][design-snapshot]. |
| Project graph and composition | [Solution][solution]; [Core][core-project]; [Rendering][rendering-project]; [Data][data-project]; [Bootstrap][bootstrap-project]; [Audio][audio-project]. |
| Renderer and host boundaries | [RendererBackendCreateContext.cs][create-context]; [AbstractRenderer.cs][abstract-renderer]; [RuntimeRenderThreadHost.cs][render-thread-host]. |
| Shader contracts and tools | [ShaderCompileRequest.cs][shader-request]; [ShaderCompileTarget.cs][shader-target]; [ShaderCompileResult.cs][shader-result]; [ShaderGenerator.cs][shader-generator]; [SlangVulkanShaderCompiler.cs][slang-compiler]. |
| Material binding | [MaterialTable.glsl][material-table]; [material binding policy][material-policy]. |
| Assets and transport | [AssetManager.Loading.Remote.Api.cs][remote-assets]; [RealtimeTlsClientTunnel.cs][tunnel]; [WebSocketClientComponent.cs][websocket-component]. |
| Existing validation lane | [windows-ci.yml][windows-ci]. |

[baseline]: https://github.com/BlackJaxDev/XRENGINE/commit/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63
[design-snapshot]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/docs/work/design/rendering/browser-wasm-renderer-design.md
[create-context]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Runtime/RendererModules/RendererBackendCreateContext.cs
[abstract-renderer]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs
[solution]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XRENGINE.slnx
[bootstrap-project]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj
[core-project]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Core/XREngine.Runtime.Core.csproj
[rendering-project]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj
[data-project]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Data/XREngine.Data.csproj
[render-thread-host]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Runtime/RuntimeRenderThreadHost.cs
[shader-request]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/Shaders/Compilation/ShaderCompileRequest.cs
[shader-target]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/Shaders/Compilation/ShaderCompileTarget.cs
[shader-result]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/Shaders/Compilation/ShaderCompileResult.cs
[shader-generator]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/Shaders/Generator/ShaderGenerator.cs
[slang-compiler]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/SlangVulkanShaderCompiler.cs
[material-table]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/Build/CommonAssets/Shaders/Common/MaterialTable.glsl
[material-policy]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/docs/architecture/rendering/material-binding-policy.md
[remote-assets]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XRENGINE.Runtime.Core/Assets/Loading/AssetManager.Loading.Remote.Api.cs
[audio-project]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Audio/XREngine.Audio.csproj
[tunnel]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XRENGINE.Runtime.Core/Networking/RealtimeTlsClientTunnel.cs
[websocket-component]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Core/Scene/Components/Networking/WebSocketClientComponent.cs
[windows-ci]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/.github/workflows/windows-ci.yml
