# Unified Desktop And Browser Runtime TODO

[<- Work docs index](../../README.md) · Design: [Unified desktop and browser runtime](../../design/platform/unified-desktop-browser-runtime-design.md) · Prerequisite: [Native subsystem integration debugging and validation](native-subsystem-project-split-todo.md) · Backend detail: [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md) · Device and delivery validation: [Mobile WebGPU runtime TODO](../rendering/mobile-webgpu-runtime-todo.md)

Status: browser gameplay integration proposed. The shared project retargeting and native extraction are implemented; their deferred qualification is tracked in the prerequisite checklist. Current ownership and portable build rules are documented in [Runtime Project Organization](../../../architecture/runtime/project-organization.md).

Created: 2026-09-29.

Owner: Runtime architecture / rendering / platform.

## Goal

Run the same engine, worlds, and C# game code in the browser (WebGPU, .NET 10 WebAssembly) as on desktop, following the engine model used by Unity and Godot web builds:

- **Same assets:** one serialized world, prefab, and component format.
- **Same code:** one set of engine and game assemblies.
- **Per-platform differences are limited to** platform leaves (renderer backend, audio, input, windowing, physics native build, transports) and cooked GPU/format variants (shaders, textures, audio codecs).

The editor stays a desktop application and gains an honest browser publish target on this path. The separate browser runtime on the `codex/webgpu-readiness-audit` branch is stabilized as a reference harness and retired once this path reaches parity.

## Rules

- **No second runtime.** New browser functionality goes through engine components, the engine renderer contract, and engine pipelines. Do not add features to the separate browser scene, component, animation, or pipeline types.
- **Named failures.** A required service, pass, or physics feature that a platform lacks fails with a named diagnostic. No silent CPU fallback for a requested GPU path, no WebGPU-to-WebGL2 switch, no dropped components.
- **Hot paths.** One managed-to-JavaScript crossing per frame for rendering; no per-draw interop; no per-frame heap allocations in recording, visibility, simulation, or submission.
- **No blocking on the browser path.** Asynchronous I/O and readback only; the browser runs frame-stepped on one thread.
- **Desktop preservation.** Validate every shared-contract change on OpenGL and Vulkan per the [pipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md) and [mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md).
- **Validation order.** Validate each feature through its live path (desktop editor, browser page) before adding regression tests, following repository policy. Record browser evidence under `Build/_AgentValidation/<run>/` and durable findings in `docs/work/investigations/<subsystem>/`.
- **Approvals.** Toolchain pins, new dependencies (repository-built `joltc`, headless-browser test tooling), and supply-path changes need owner approval and license review.
- **No todo IDs in code.** Keep task IDs out of code, comments, type names, and diagnostics.

## Gates

| Gate | Result | Main workstreams |
| --- | --- | --- |
| U0 — Reference harness | The branch's browser app and editor browser target build, run, and have recorded evidence. The separate runtime is frozen. | UR00 |
| U1 — Engine boots in the browser | The real portable assemblies load a real `XRWorld` asset through fetch, construct scenes, the game mode, and components, and tick fixed/variable updates without rendering. | Native subsystem integration acceptance; UR01–UR03 |
| U2 — Engine renders in the browser | Engine cameras, `ModelComponent`, and engine materials render through the WebGPU backend and the web tier of `DefaultRenderPipeline`, with resize and device-loss reporting. | UR04–UR06 |
| U3 — Published project plays | Editor Build Project with the browser target produces a site that runs the startup world with the project's game code, Jolt physics, audio, input, and UI. The MonkeyBall sample game is the parity target. | UR07–UR11 |
| U4 — Production qualification | Physical mobile devices, budgets, runtime mode (interpreter or AOT), recovery, hosting, and CI pass. The separate runtime is retired. | UR13–UR16 |
| U5 — Networked browser client | The browser client joins the real server path. | UR12 |

## UR00 — Stabilize The Branch As A Reference Harness

- [ ] **UR00.01** Validate the implemented portable source policy: permit `System.Drawing.Primitives` value types and deny Windows bitmap APIs. Resolve any remaining guard/compile failures in Data, Core, Rendering, WebGPU, and Browser; record fresh build evidence.
- [ ] **UR00.02** Pin the .NET SDK and install the `wasm-tools` workload per [the browser README](../../../../XREngine.Browser/README.md). Build and publish `XREngine.Browser` using its normal project configuration; the old portable-profile switch has been removed. Record exact SDK, workload, and runtime-pack versions.
- [ ] **UR00.03** Serve the published output locally. Run the demo in a WebGPU-capable desktop browser, capture and view screenshots, capture counters, and record results and failures in `docs/work/investigations/rendering/`.
- [ ] **UR00.04** Build the editor. Run Build Project with the `BrowserWebGPU` platform on a minimal world that meets the current exporter rules. Serve and open the output, and record the result.
- [ ] **UR00.05** Freeze feature work in the separate runtime (`Browser*` scene, component, animation, collision, and pipeline types, and `BrowserWorldPublishExporter`); allow fixes only to keep the harness running.
- [ ] **UR00.06** Reconcile the [mobile TODO](../rendering/mobile-webgpu-runtime-todo.md) code-completion rows with UR00.02–UR00.04 build evidence. Mark rows that never compiled.
- [ ] **UR00.07** If the branch's publish target is used before UR11 lands, stop shipping the developer harness as published output: no demo controls, no sample UI overlay, no "Demo scene" switch in the published page.

**Acceptance (U0):** reproducible build, publish, and run evidence for the branch as it stands, with its limits recorded.

## UR01 — Portable Engine Assemblies In The Browser Host

Shared projects already target `net10.0` with whole-project checks. Depends on their [integration acceptance](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries); source completion alone does not establish browser startup.

- [ ] **UR01.01** Verify the browser host references the full portable assemblies and include the integration adapters needed for real-world boot. Source-subset profiles and the old portable build property are already removed; qualify the evaluated closure and full API surface.
- [ ] **UR01.02** Make `XREngine.Browser` a composition root that installs browser leaves explicitly as they land: WebGPU renderer, browser platform, Jolt, Web Audio, browser input, fetch asset source, WebSocket transport. Unavailable required services fail by name.
- [ ] **UR01.03** Audit static constructors, module initializers, and reflection scans reachable at browser startup. Qualify the implemented published-metadata lookup and desktop service boundaries, then move any remaining browser-reachable desktop initialization into leaves.
- [ ] **UR01.04** Reuse the existing Bootstrap-owned static factory generation for browser component, transform, serializer, and module registration. Retire the branch's Python registration generator.
- [ ] **UR01.05** Load real YAML/MemoryPack assets in the interpreter and round-trip representative worlds, prefabs, and components.
- [ ] **UR01.06** Boot a real `XRWorld` fetched from a cooked bundle. Construct scenes, the game mode, pawns, and components; run fixed and variable updates; report lifecycle state to the page.

**Acceptance:** the browser runs the engine's own world, scene, and component lifecycle from the same binaries desktop uses.

## UR02 — Platform Host, Frame Stepping, And Scheduling

- [ ] **UR02.01** Extract a single-frame engine step (fixed-step simulation with bounded catch-up, variable update, visibility, render recording, submission) from the desktop render-thread host loop. Desktop and browser hosts both call it.
- [ ] **UR02.02** Support rendering on the calling thread: update, swap, collect, and render run in sequence within one step. Verify the engine's double-buffered render state works without a dedicated render thread.
- [ ] **UR02.03** Add a caller-thread executor for the job system. Audit the synchronous waits (16 Core and 19 Rendering files in the baseline) and `new Thread` sites (4 Core and 6 Rendering files). Make them asynchronous, move them to desktop leaves, or confine them to cook and editor code.
- [ ] **UR02.04** Create `XREngine.Runtime.Platform.Browser` from the branch's canvas host and surface contracts: `browser-canvas-host.js`, `IRuntimeSurfaceHost`, `RuntimeSurfaceState`, `BrowserCanvasRenderTarget`. It covers CSS/backing size, DPR caps, orientation, safe area, detach/reattach, and output generations.
- [ ] **UR02.05** Handle page visibility, freeze/resume, and `pagehide`/`pageshow`; reset timing and invalidate temporal history after suspension or large gaps.
- [ ] **UR02.06** Confirm desktop editor and VRClient frame pacing is unchanged.

**Acceptance:** one engine frame step drives desktop and browser, and no browser-reachable code blocks.

## UR03 — Asset I/O And Per-Platform Cooking

- [ ] **UR03.01** Route `AssetManager` loading through the asynchronous asset-source contract (see the asset-source boundary in [project organization](../../../architecture/runtime/project-organization.md)). Implement the browser source with same-origin, credential-free, hash-verified fetches, reusing `content-loader.js` and `content-manifest.js`.
- [ ] **UR03.02** Remove synchronous load wrappers from runtime-reachable paths.
- [ ] **UR03.03** Add a platform target to cooking (`CookContent`). Web cooking produces:
  - WGSL shader artifacts;
  - ASTC 4×4 and ETC2 texture variants with RGBA8 fallbacks, reconciled with the [texture compression TODO](../texturing/texture-compression-and-cooked-cache-todo.md);
  - web-decodable audio;
  - per-asset capability requirements.
- [ ] **UR03.04** Generalize the branch's `BrowserContentPackageBuilder` rules (immutable SHA-256 payload URLs, revalidated manifest, dependency closures, essential and streamed splits, strict limits) to the real asset graph instead of browser-only DTOs.
- [ ] **UR03.05** Keep bounded download concurrency, cancellation, retry, progress, and per-frame integration budgets, reusing the branch's delivery code. Track retained, staging, and estimated GPU bytes.
- [ ] **UR03.06** Validate cold and warm cache, throttled, failed, corrupt, and missing payloads, and cancel/restart. This carries over the mobile TODO's MW07 acceptance.

**Acceptance:** the browser loads the same world assets as desktop, with only cooked platform variants differing.

## UR04 — WebGPU Renderer Backend For The Engine

- [ ] **UR04.01** Implement `AbstractRenderer` and its API-object wrappers in `XREngine.Runtime.Rendering.WebGPU`: data buffers and views, 2D/array/cube textures, samplers, framebuffers and render buffers, programs, materials, mesh renderers and vertex layouts, and compute dispatch.
- [ ] **UR04.02** Track GL-shaped state in C# and resolve it into cached immutable render pipelines, layouts, and bind groups with complete keys and bounded caches, following the Vulkan backend's approach.
- [ ] **UR04.03** Record commands into reusable C# arenas and flush one packet per frame to the JavaScript executor. Reuse the branch's resource, command, readback, usage-scope, limits, and pipeline-cache executors. Move the policy logic in `browser-render-pipeline.js` into C#.
- [ ] **UR04.04** Clean up the renderer contract for non-blocking backends:
  - neutral `RuntimeImage` readbacks (implemented shared contract; browser renderer support still required);
  - asynchronous-only screenshots, pixel reads, and luminance;
  - `WaitForGpu` rejected with a named error on non-blocking hosts;
  - truthful capability probes: no indirect-count draw, no mesh shaders, no bindless textures, bounded bind groups.
- [ ] **UR04.05** Present to the canvas through `RenderFrameOutputDescription` with surface generations, re-acquiring the output each frame and rejecting obsolete plans after resize.
- [ ] **UR04.06** Handle pending, ready, failed, and lost states. Recovery reconstructs device resources from CPU-side and cooked sources, carrying over the mobile TODO's MW10.01–MW10.05.
- [ ] **UR04.07** Add error scopes, debug labels, and bridge counters. Recording and submission must not allocate per frame.
- [ ] **UR04.08** Validate every shared renderer-contract change on OpenGL and Vulkan with editor captures from multiple positions.

**Acceptance:** the engine's renderer runs on WebGPU through the same contract as OpenGL and Vulkan, with one JavaScript crossing per frame.

## UR05 — Shaders And Materials

- [ ] **UR05.01** Inventory the shaders the web tier of `DefaultRenderPipeline` needs. Classify each as portable through Slang, needing a WGSL rewrite, or desktop-only.
- [ ] **UR05.02** Cook engine shaders to WGSL with the pinned Slang route and the shader artifact format (reusing `Tools/ShaderCooker`, the `ShaderCompileTarget` WGSL target, and artifact schema checks). Follow the [Slang cross-compile plan](../../design/scripting/slang-shader-cross-compile-plan.md).
- [ ] **UR05.03** Extend the engine's material shader generation with a WGSL target, replacing the separate browser material generator. Support the engine's lit material model, not only unlit and Lambert.
- [ ] **UR05.04** Implement the WebGPU encoding of logical material and texture references with bounded bind groups, texture arrays for qualifying content, and material batching. No desktop bindless handle reaches WGSL.
- [ ] **UR05.05** Report web-unsupported shaders and material features at cook time with material, pass, source location, and reason.
- [ ] **UR05.06** Verify coordinate conventions (clip depth range, texture Y, winding, matrix layout, reversed-Z where the engine uses it) with known-value renders.

**Acceptance:** engine materials cook to WGSL and render with correct interpretation; unsupported ones fail by name.

## UR06 — Render Pipeline Web Tier

- [ ] **UR06.01** Define the WebGPU capability profile of `DefaultRenderPipeline`: depth, forward lighting with the engine material model, directional/spot/point shadows within limits, sky and environment, alpha-masked and sorted transparency, HDR with tonemapping, a bounded post-process set, and engine UI.
- [ ] **UR06.02** Select passes from capabilities and report excluded passes explicitly; never discover unsupported passes by failing at runtime.
- [ ] **UR06.03** Make `AdvancedRenderPipeline` report unsupported for WebGPU outputs through its existing `Available` policy (explicitly unbound, with a reason) and fail under `Required`.
- [ ] **UR06.04** Resolve `CpuDirect` mesh submission for WebGPU. Later, and only after measurement, add GPU culling that writes fixed indexed slots with zero-instance culled draws, porting the branch's WGSL culling, BVH, and Hi-Z kernels into the engine's GPU scene path.
- [ ] **UR06.05** Run engine skinning and blendshapes through WebGPU compute (reusing the branch's WGSL skinning kernel as a canonical port) or CPU, with CPU/GPU parity checks.
- [ ] **UR06.06** Add mobile quality tiers to engine settings: backing resolution and DPR caps, shadow sizes and cadence, light counts, texture tiers, and post effects. Disabled effects must not allocate resources.
- [ ] **UR06.07** Render the same test worlds on OpenGL, Vulkan, and WebGPU; compare tolerant captures and document deliberate differences.

**Acceptance (U2):** a representative engine world renders in the browser through engine objects and the web tier, with documented differences from desktop.

## UR07 — Jolt Physics In The Browser

Depends on the [Jolt browser proof and default-promotion gates](native-subsystem-project-split-todo.md#jolt-browser-proof-and-default-promotion-gates). The desktop Jolt module already exists.

- [ ] **UR07.01** Productize the `joltc` Emscripten archive build, pinned to the runtime pack's Emscripten version. Ship it as the Jolt leaf's `browser-wasm` native asset, with a `.props` file that adds the `NativeFileReference`.
- [ ] **UR07.02** Install the Jolt module in the browser composition with single-threaded job execution and a truthful capability report.
- [ ] **UR07.03** Compare desktop and browser results on matched scenes. If the approved native supply enables cross-platform determinism, match state hashes; otherwise match within documented tolerances.
- [ ] **UR07.04** Fail browser publishing of worlds that require PhysX-only features with the component path and feature name.
- [ ] **UR07.05** Measure physics step time on the reference devices and set budgets.

**Acceptance:** engine physics components behave the same on desktop and web through Jolt.

## UR08 — Audio In The Browser

- [ ] **UR08.01** Implement `XREngine.Audio.WebAudio` against the audio contracts (sources, listener, spatialization, gain, looping, streaming), reusing `browser-audio.js`. Engine audio components are unchanged.
- [ ] **UR08.02** Add gesture-driven activation, suspension, and resume. Gate simulation only when a world declares audio as required.
- [ ] **UR08.03** Validate cooked audio codecs on the browser matrix, including Safari.
- [ ] **UR08.04** Report Steam Audio features as unsupported on web unless a WebAssembly build is separately evaluated and approved.

**Acceptance:** engine audio components play in the browser with the same authored data.

## UR09 — Input, UI, And Text

- [ ] **UR09.01** Implement a browser input leaf that feeds `XREngine.Input` devices from pointer, touch, keyboard, IME, wheel, and Gamepad API events, reusing `browser-input.js`. Player controllers and input mappings are unchanged.
- [ ] **UR09.02** Render engine UI through WebGPU with hit testing in the same coordinate convention as rendering.
- [ ] **UR09.03** Cook glyph atlases with FreeType at cook time and render cooked fonts at runtime. Desktop shipping builds may use the same path.
- [ ] **UR09.04** Bridge text entry, IME, and accessibility to DOM elements.
- [ ] **UR09.05** Express mobile touch controls (virtual sticks, buttons) as engine input mappings and UI, not page-specific script.

**Acceptance:** a user can play with touch, keyboard/mouse, or gamepad through the engine's own input and UI.

## UR10 — Game Code And Components

- [ ] **UR10.01** Make project templates and game projects target `net10.0` and reference only portable engine assemblies (plus leaf contracts where needed).
- [ ] **UR10.02** Report at build time which game-assembly references block browser publishing (desktop-only leaves or APIs), with type and member names.
- [ ] **UR10.03** Link the project's game assemblies into the browser publish, with generated static registrations. Editor hot reload remains desktop-only.
- [ ] **UR10.04** Keep editor-only code out of game builds, as today.
- [ ] **UR10.05** Load serialized game components, game modes, and pawns in the browser from the same assets as desktop.

**Acceptance:** the same compiled game code runs on desktop and in the browser.

## UR11 — Editor Browser Publishing On The Unified Path

- [ ] **UR11.01** Replace `BrowserWorldPublishExporter` with this flow:
  1. Cook the startup world's web closure.
  2. Build the game assemblies.
  3. Publish the browser host with the engine assemblies, game assemblies, and selected leaves.
  4. Write the launch descriptor.
  5. Activate the output atomically, reusing the branch's staging and rollback.
- [ ] **UR11.02** Before publishing, report web-unsupported components and features per world with scene paths and reasons. Unsupported required features block the publish; optional ones are listed.
- [ ] **UR11.03** Ship a player shell page (canvas, loading progress, errors, audio unlock) separate from the developer harness page.
- [ ] **UR11.04** Keep the CLI entry point (`--build-project <project> --build-platform BrowserWebGPU`) and the editor Build Project action stable. Browser publishing must also work from a packaged editor, not only a source checkout.
- [ ] **UR11.05** Publish the MonkeyBall sample game and a lit, textured, animated test world; play them in the browser and compare with desktop.

**Acceptance (U3):** a published project plays in the browser from the editor's normal build action.

## UR12 — Networked Browser Client

- [ ] **UR12.01** Implement a WebSocket transport leaf over the transport contract (already separated from the desktop socket implementation), plus the matching server gateway.
- [ ] **UR12.02** Carry over the mobile TODO's MW11 requirements: bounded queues, backpressure, reconnect/resync, suspension, authentication, origin and credential policy, and optional voice.
- [ ] **UR12.03** Validate against the real server path under throttling, disconnects, and app switching.

**Acceptance (U5):** the browser client joins and plays through the production server path.

## UR13 — Performance, Runtime Mode, And Size

- [ ] **UR13.01** Measure the interpreter with representative worlds (CPU per frame, startup, memory) early. Do not wait until feature completion.
- [ ] **UR13.02** Qualify AOT: generated serialization and registration metadata, trimming roots, build time, download size, runtime cost. Choose the shipping mode from measurements.
- [ ] **UR13.03** Reduce download size through trimming, lazy assembly loading, and streamed content, against the [readiness budgets](../../progress/rendering/mobile-browser-readiness.md#devices-and-measurable-budgets).
- [ ] **UR13.04** Demonstrate steady-state frames with no recurring managed allocations in simulation, visibility, recording, and submission; record unavoidable browser/API allocations separately.
- [ ] **UR13.05** Bound linear-memory use across the .NET heap, native Jolt heap, staging, and retained content; test repeated world load and unload.

## UR14 — Lifecycle And Recovery

- [ ] **UR14.01** Carry over the mobile TODO's MW10 recovery and lifecycle matrix to the unified runtime: device loss and reconstruction, hide/show, lock/unlock, orientation during loading, canvas removal, repeated load/unload, and explicit restart.
- [ ] **UR14.02** Reject stale asynchronous completions after teardown or device replacement.

## UR15 — CI, Hosting, And Evidence

- [ ] **UR15.01** Add a browser build and publish lane to CI next to the Windows desktop lane.
- [ ] **UR15.02** Propose a headless-browser smoke harness (tool choice needs owner approval) that boots a cooked world and checks startup, rendering, and diagnostics.
- [ ] **UR15.03** Document production hosting: HTTPS, MIME types, compression, immutable caching, bootstrap revalidation, and CSP. Add cross-origin isolation only if threads are adopted.
- [ ] **UR15.04** Run the physical-device matrix from the mobile TODO (MW00.06, MW12.07) with the evidence template in its completion section.
- [ ] **UR15.05** Publish user-facing build, publish, hosting, support-matrix, and troubleshooting docs after validation.

**Acceptance (U4, together with UR13, UR14, and UR16):** reproducible clean publish and recorded device evidence.

## UR16 — Retire The Separate Browser Runtime

After U3:

- [ ] **UR16.01** Remove `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent`, and the browser registration manifest and generator.
- [ ] **UR16.02** Remove `BrowserCooked*` scene and instance DTOs and the `BrowserSceneSession` content, motion, collision, and animation paths.
- [ ] **UR16.03** Remove `BrowserRenderPipeline`, its packet types, and `browser-render-pipeline.js` once the engine pipeline covers their cases.
- [ ] **UR16.04** Remove `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, and `BrowserKinematicCharacter`.
- [ ] **UR16.05** Remove `BrowserWorldPublishExporter`, `Tools/BrowserContentCooker`'s browser-only recipe format (keeping the generalized packager), and the Python scripts (`Tools/Generate-BrowserRegistrations.py`, `Tools/Reports/audit_browser_dependencies.py`, `Tools/Shaders/cook_browser_shaders.py`).
- [ ] **UR16.06** Keep the developer harness page only if it still exercises the unified runtime; otherwise remove it.
- [ ] **UR16.07** Close or rewrite superseded mobile TODO rows and progress docs; move durable content into stable docs.

## Mobile TODO Carry-Over

| Mobile TODO item | Disposition on the unified path |
| --- | --- |
| MW00 (scope, budgets, devices) | Still applies; budgets and device matrix are reused. |
| MW01 (same-identity portable profiles) | Superseded by the implemented whole-project layout; remaining qualification is in the native subsystem integration checklist and UR01. |
| MW02 (browser host) | Host contracts and canvas code carry into UR02. |
| MW03 (batched bridge) | Carries into UR04; its acceptance evidence still applies. |
| MW04 (WGSL artifacts) | Artifact format and Slang route carry into UR05; the separate browser material generator is superseded. |
| MW05 (WebGPU resources and submission) | Carries into UR04; acceptance still applies. |
| MW06 (focused browser pipeline) | Superseded by UR06; the branch pipeline is reference only. |
| MW07 (cooked content) | Delivery code carries into UR03; browser-only DTOs are superseded. |
| MW08 (interaction and services) | JavaScript services carry into UR08 and UR09; bounded animation and collision profiles are superseded by engine components. |
| MW09 (compute/indirect) | WGSL kernels become engine pipeline ports in UR06. |
| MW10 (recovery, budgets) | Carries into UR13 and UR14. |
| MW11 (multiplayer) | Carries into UR12. |
| MW12 (publish, hosting, validation) | Carries into UR11 and UR15. |
| MW-D01–MW-D06 (deferred) | Still deferred. |

## Deferred

- **Browser-hosted editor:** a separate design after the runtime ships. It needs ImGui for WebAssembly with a WebGPU backend, a compilation service, server-side import, and a virtual file system.
- **WebXR:** a separate presentation and input leaf.
- **WebGL2:** a separate backend under the [browser renderer design](../../design/rendering/browser-wasm-renderer-design.md).
- **Multithreaded browser runtime:** after .NET WebAssembly threading with native relinking is reliable and measurements justify it.
- **PWA/offline packaging and native mobile applications.**

## Validation Matrix

| Area | Required evidence |
| --- | --- |
| Boot | Clean publish; engine world lifecycle in the browser; absent desktop services; named failures for missing required services |
| Rendering | OpenGL/Vulkan/WebGPU captures of the same worlds; orientation, depth, linear/sRGB, alpha, transparency, shadows, tonemapping; resize and device loss |
| Content | Same world assets; cooked variant selection; cold/warm cache; failure and cancellation cases |
| Physics | Desktop/browser Jolt parity on matched scenes; PhysX-only rejection |
| Audio/input/UI | Gesture activation, spatial audio, touch/keyboard/gamepad, IME, UI hit testing |
| Game code | Same game assemblies on desktop and web; blocked-reference report |
| Performance | Interpreter/AOT comparison; frame-time percentiles; allocations; download size; memory peaks on reference devices |
| Desktop preservation | OpenGL/Vulkan editor smokes and targeted tests after each shared-contract change |

## Definition Of Done

- The browser runs the engine's own worlds, components, renderer, pipelines, physics, audio, input, and UI from the same assemblies as desktop.
- Editor browser publishing produces a playable site from the project's real assets and game code, with named reports for unsupported features.
- Only platform leaves and cooked GPU/format variants differ between desktop and browser.
- The separate browser runtime is removed, and superseded mobile TODO rows are closed.
- Device, performance, recovery, hosting, and CI evidence is recorded.
