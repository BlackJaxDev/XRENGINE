# Unified Desktop And Browser Runtime TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md)  Design: [Unified desktop and browser runtime](../../design/platform/unified-desktop-browser-runtime-design.md)
Validation: [Platform Validation](../../testing/platform/platform-validation.md)

## Current State

`XREngine.Runtime.Host` exists as a portable `net10.0` host for the `Engine` facade, `EngineTimer`, settings, snapshots, and world hosting. `XREngine.Browser` references Host, Core, Rendering, WebGPU, and Animation, and uses browser manifest registration. The browser still contains reference runtime types such as `BrowserSceneSession`, `BrowserMeshComponent`, and `BrowserRenderPipeline`. Real browser-world startup, browser Jolt linkage, full WebGPU renderer parity, editor browser publishing, physical device validation, and separate-runtime retirement remain open.

## Open Code Items

<a id="ur00--stabilize-the-branch-as-a-reference-harness"></a>

### Browser Composition And World Startup

- [ ] Make `XREngine.Browser` install the full portable assembly closure and each integration adapter needed for real-world boot. Files/types: `XREngine.Browser/XREngine.Browser.csproj`, `XREngine.Browser/Program.cs`, browser composition services. Done when: the evaluated closure includes the needed portable projects and startup reports each installed or unavailable service by name.
- [ ] Replace the reference `BrowserSceneSession` startup path with `RuntimeWorld` and `RuntimeWorldHost`. Files/types: `BrowserSceneSession`, `RuntimeWorldHost`, browser startup. Done when: a fetched cooked `XRWorld` constructs scenes, game mode, pawns, and components, then runs fixed and variable updates.
- [ ] Generate browser component, transform, serializer, resource, module, and game-assembly registrations through the approved manifest route. Files/types: `Build/Registration/FactoryRegistrations.targets`, `XREngine.Browser/browser-registration-manifest.json`, generated browser registrations. Done when: missing metadata fails by name and the old browser Python generator is not needed.
- [ ] Audit browser-reachable static constructors, module initializers, reflection scans, dynamic loading, and service boundaries. Files/types: browser closure projects in `Build/Portable/PortableProjects.tsv`. Done when: desktop initialization is moved to leaves or blocked with named diagnostics.
- [ ] Implement supported external asset loading for fetched YAML, MemoryPack, and cooked-binary payloads. Files/types: asset source contracts, browser asset source, `RuntimeWorld` loading. Done when: representative worlds, prefabs, components, and external references load without native filesystem assumptions.

### Frame Step And Platform Host

- [ ] Extend `EngineTimer.BeginExplicitFrame` into the shared host step for desktop and browser. Files/types: `XREngine.Runtime.Host/Core/Time/EngineTimer.ExplicitFrames.cs`, frame scheduling services. Done when: fixed-step simulation, variable update, visibility, render recording, and submission can run on the calling thread.
- [ ] Support a caller-thread render path. Files/types: `EngineTimer`, `RuntimeRenderThreadHost`, render scheduling services. Done when: update, swap, collect, render, and submit run in sequence without a dedicated render thread on browser hosts.
- [ ] Add a caller-thread executor for shared jobs and remove browser-reachable blocking waits. Files/types: `JobManager`, Core, Rendering, Data, Extensions, Animation, AudioIntegration, InputIntegration, Browser. Done when: browser-reachable code has no required `.Wait()`, `.Result`, thread join, sleep, or unbounded `Task.Run` path.
- [ ] Create the browser platform host from the current canvas contracts. Files/types: `BrowserCanvasRenderTarget`, `IRuntimeSurfaceHost`, browser canvas services, JavaScript canvas host. Done when: CSS size, backing size, DPR caps, orientation, safe area, detach, reattach, and output generations are owned by a browser platform service.
- [ ] Handle page visibility, freeze/resume, `pagehide`, and `pageshow` in the host. Files/types: browser lifecycle services, frame scheduler. Done when: timing resets and temporal history invalidates after suspension or large elapsed time.

### Asset I/O And Cooking

- [ ] Route runtime asset loading through asynchronous asset-source APIs. Files/types: `IRuntimeAssetSource`, `IAssetReadBatch`, `DirectStorageIO.Source`, browser asset source. Done when: browser reads use same-origin, credential-free, hash-verified fetches and required synchronous calls fail by name.
- [ ] Remove sync-over-async and direct filesystem loading from browser-reachable runtime paths. Files/types: `AssetManager`, Core, Rendering. Done when: browser runtime code does not require native file enumeration, watchers, or blocking fetch wrappers.
- [ ] Add a browser platform target to content cooking. Files/types: `XREngine.Editor/ProjectBuilder.cs`, shader cooker, texture cooker, audio cooker. Done when: web cooking creates WGSL shader artifacts, mobile texture variants with fallbacks, web-decodable audio, and capability requirements.
- [ ] Generalize browser content package rules to the real asset graph. Files/types: content packager, manifest builder, launch descriptor. Done when: payload URLs, hashes, dependency closures, essential and streamed splits, and strict limits come from authored assets.
- [ ] Implement bounded browser delivery budgets. Files/types: browser content loader, asset integration scheduler. Done when: download concurrency, cancellation, retry, progress, retained bytes, staging bytes, and estimated GPU bytes are bounded and reported.

### WebGPU Renderer And Shader Path

- [ ] Implement `AbstractRenderer` coverage in `XREngine.Runtime.Rendering.WebGPU`. Files/types: `WebGpuRendererBackendModule`, `WebGpuRendererHost`, WebGPU wrappers. Done when: every abstract member is implemented or fails with a named unsupported diagnostic and the renderer can clear and present.
- [ ] Add WebGPU data buffers, views, programs, mesh renderers, and vertex layouts. Files/types: `WebGpuRendererHost.Resources.cs`, `WebGpuRendererHost.Pipeline.cs`, mesh renderers. Done when: an unlit `ModelComponent` renders through an engine camera.
- [ ] Add WebGPU textures, samplers, framebuffers, render buffers, materials, uniforms, and compute dispatch. Files/types: WebGPU resource and command files. Done when: textured and lit materials, offscreen targets, resolves, and compute dispatch work through engine contracts.
- [ ] Track GL-shaped state in C# and lower it to WebGPU pipeline, layout, and bind-group caches. Files/types: WebGPU pipeline cache, renderer state. Done when: complete cache keys bound cache growth and incompatible entries fail.
- [ ] Flush one prepared packet per frame to JavaScript. Files/types: WebGPU command packet builder and executor. Done when: ordinary draws do not create per-draw managed-to-JavaScript crossings.
- [ ] Make non-blocking renderer APIs honest. Files/types: runtime image readback, screenshot, pixel read, luminance, `WaitForGpu`, capability probes. Done when: non-blocking hosts use async readbacks or reject sync waits by name.
- [ ] Present through `RenderFrameOutputDescription` and surface generations. Files/types: canvas target, WebGPU presentation. Done when: resize rejects obsolete plans and reacquires output for each frame.
- [ ] Implement WebGPU pending, ready, failed, lost, and recovery states. Files/types: WebGPU device/session state. Done when: device loss stops invalid work and rebuilds resources from CPU or cooked sources before resume.
- [ ] Add WebGPU error scopes, debug labels, counters, and allocation guards. Files/types: WebGPU diagnostics. Done when: recording and submission avoid per-frame heap allocations in the hot path.
- [ ] Inventory web-tier shaders for `DefaultRenderPipeline`. Files/types: shader inventory doc, `Build/CommonAssets/Shaders`, pipeline pass list. Done when: each shader is classed as Slang-portable, WGSL rewrite, or desktop-only.
- [ ] Cook engine shaders and material generation to WGSL. Files/types: `Tools/ShaderCooker`, `ShaderCompileTarget.WebGPUWgsl`, material shader generator. Done when: artifacts carry source, dependency, compiler, schema, layout, and capability identity.
- [ ] Port the required web-tier shaders. Files/types: depth, shadow, forward, sky, tonemap, UI, and text shader sources. Done when: each pass group cooks without errors and has a known-value render check in validation.
- [ ] Define the WebGPU capability profile for `DefaultRenderPipeline` and `AdvancedRenderPipeline`. Files/types: pipeline capability policy, pass selection. Done when: unsupported required passes fail by name and optional exclusions are listed before runtime.
- [ ] Resolve WebGPU mesh submission, skinning, blendshapes, UI, and quality tiers. Files/types: mesh submission strategy, WebGPU compute, UI renderer, quality settings. Done when: CPU-direct works first, GPU paths are opt-in until measured, and disabled effects allocate no resources.

### Browser Physics, Audio, Input, UI, And Game Code

- [ ] Productize the approved browser `joltc` native asset supply. Files/types: Jolt browser build scripts, `.props`, `NativeFileReference`, Jolt package policy. Done when: browser publishes link the approved archive without changing desktop supply.
- [ ] Retarget the Jolt leaf for browser admission. Files/types: `XREngine.Runtime.Physics.Jolt`, `Build/Portable/PortableRuntime.targets`. Done when: the Jolt project builds for browser and native asset allowances are narrow and documented.
- [ ] Install Jolt in browser composition with single-threaded job execution. Files/types: browser composition, Jolt runtime services. Done when: required Jolt features report truthful capability and unsupported PhysX-only worlds fail by component path.
- [ ] Implement Web Audio, browser input, browser UI, text, and accessibility leaves. Files/types: audio service, input service, UI/text services, browser JavaScript modules. Done when: touch, keyboard, IME, wheel, gamepad, audio activation, spatial audio, and UI hit testing use engine contracts.
- [ ] Make project templates and game projects target portable engine assemblies for browser publishing. Files/types: `XREngine.Editor/CodeManager.cs`, `XREngine.Editor/EditorProjectInitializer.cs`, browser publish checks. Done when: blocked references list type and member names.
- [ ] Make the selected parity target portable or choose another target. Files/types: `Samples/MonkeyBallVR`, physics calls, VR rig, OpenVR manifest, cooked-world serializer. Done when: gameplay code references portable projects and the desktop build adds desktop-only VR parts.

### Editor Publish, CI, Hosting, And Runtime Retirement

- [ ] Replace `BrowserWorldPublishExporter` with the unified publish flow. Files/types: editor publish pipeline, content cook, browser host publish, launch descriptor. Done when: Build Project cooks the startup world's web closure, builds game assemblies, publishes the browser host, writes the descriptor, and activates output atomically.
- [ ] Report web-unsupported components and features before publish. Files/types: publish diagnostics, world scan. Done when: unsupported required features block publish with scene paths and reasons.
- [ ] Ship a player shell page separate from the developer harness. Files/types: browser shell HTML/JS/CSS. Done when: loading, progress, errors, and audio unlock are production-oriented.
- [ ] Keep the editor CLI and Build Project action stable. Files/types: editor build command, packaged editor publish. Done when: `--build-project <project> --build-platform BrowserWebGPU` works from source and packaged editor.
- [ ] Add browser build, publish, smoke, and physical-device lanes. Files/types: CI workflows, test scripts, device evidence templates. Done when: clean publish and smoke results are reproducible and device evidence names exact versions.
- [ ] Document production hosting and troubleshooting after validation. Files/types: user guide, hosting guide, support matrix. Done when: HTTPS, MIME, compression, caching, CSP, permissions, limitations, and diagnostics are covered.
- [ ] Retire the separate browser runtime after the unified path covers its cases. Files/types: `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent`, browser DTOs, `BrowserSceneSession`, `BrowserRenderPipeline`, `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, `BrowserKinematicCharacter`, `BrowserWorldPublishExporter`, browser-only cook recipes, browser Python scripts. Done when: removed types have no remaining required consumers and the developer harness either uses the unified runtime or is removed.

### Native Subsystem Follow-Up

- [ ] Promote Jolt to the default physics backend for new projects and the unit-testing world after owner approval and browser proof. Files/types: physics architecture docs, user docs, editor labels, unit-testing settings, generated settings schema, `EPhysicsLibrary` persistence. Done when: new projects and the unit-testing world select Jolt, and saved PhysX projects still load with PhysX.

### Browser Runtime Carry-Over

- [ ] Carry the canvas host, bridge, resource lifetime, content delivery, recovery, and performance policies into engine-owned services. Files/types: browser platform host, WebGPU renderer, content loader, recovery controller, diagnostics counters. Done when: the reference harness contains no unique production policy.
- [ ] Keep WebGL2, worker/offscreen canvas, PWA/offline packaging, WebXR, native mobile applications, and advanced desktop feature promotion separate. Files/types: future design docs and backend modules. Done when: the WebGPU package reports unavailable fallback or unsupported features honestly.

## Decisions Needed

- [ ] Choose the parity target if MonkeyBall remains blocked by PhysX, VR, OpenVR, or cooked-world serializer dependencies. Owner: product/runtime owner.
- [ ] Decide Jolt default-promotion criteria for desktop and browser. Owner: runtime/physics owner.
- [ ] Decide the reviewed browser managed Jolt binding supply after the signature conflict. Owner: runtime/physics owner.
- [ ] Choose the web-tier shader authoring route: Slang sources cooked to every target, or hand-written WGSL beside GLSL. Owner: rendering owner.
- [ ] Decide headless-browser smoke tooling after license review. Owner: tooling owner.
- [ ] Choose the browser shipping runtime mode from measurements. Owner: product/runtime owner.
- [ ] Decide whether to retire the separate browser runtime fully or keep a small standalone demo. Owner: product owner.
- [ ] Decide whether to run the optional Box3D comparison or defer it. Owner: physics owner.
- [ ] Decide the remaining Bootstrap modeling-integration scan question. Owner: runtime owner.

## Out Of Scope

- Browser-hosted editor.
- Immersive WebXR runtime.
- WebGL2 backend and automatic backend selection.
- Native Android or iOS applications.
- PWA/offline packaging.
- Multithreaded browser runtime.
