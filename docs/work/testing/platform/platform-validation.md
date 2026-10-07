# Platform Validation

Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md), [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md), [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md), [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md), [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md)  Code todos: [Unified desktop and browser runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md)

## Scope

This document owns manual, runtime, visual, hardware, profiler, benchmark, soak, and publish checks for the platform runtime slice. It covers native subsystem extraction, the portable engine host, the unified desktop and browser runtime, the browser WebGPU runtime, and the current test-suite layout checks that must not live in code todos.

## Setup

Use these tasks from `.vscode/tasks.json` when a check names them: `Build-Editor`, `Build-Editor-Release`, `Build-Server`, `Build-VRClient`, `Generate-UnitTestingWorldSettings`, `Start-Editor-NoDebug`, `Start-Editor-RendererDevelopment-NoDebug`, `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`, `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`, `Start-Server-NoDebug`, `Start-Client-NoDebug`, `Start-DedicatedServer-NoDebug`, `Test-OpenXR-Monado-Smoke`, `Test-OpenXR-SteamVR-Smoke`, `Test-OpenXR-SceneOnlyVR-Smoke`, `Measurement-GameLoopRenderPipeline-Release-All`, and `Report-Dependencies`.

Use these launch profiles from `.vscode/launch.json` when a check names them: `Editor (Default World)`, `Editor (Renderer Development)`, `Editor (Unit Testing World)`, `Editor (Unit Testing OpenXR SteamVR)`, `Editor (Unit Testing World, Validation Layers)`, `Debug Server (Server only)`, `Debug Server (Clients runs separately)`, `Debug Client (Server & other client run separately)`, and `Debug VRClient (Editor runs separately)`.

Use these settings and environment variables as needed: `XRE_WORLD_MODE`, `XRE_UNIT_TEST_WORLD_KIND`, `XRE_UNIT_TEST_VR_MODE`, `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS`, `XRE_WINDOW_TITLE`, `XRE_NET_MODE`, `XRE_UDP_CLIENT_RECEIVE_PORT`, `XRE_VULKAN_VALIDATION`, `XRE_GL_DEBUG`, `XRE_PROFILER_ENABLED`, and `XRE_FORCE_MESH_SUBMISSION_STRATEGY`. Use `global.json` for the .NET SDK and workload set. Install `wasm-tools` before browser compile or publish checks.

Record the source revision, configuration, host, backend, command, result, and limits in a progress or investigation doc. Summarize disposable logs and captures. Do not put machine-local paths in this file. View every screenshot before you use it as evidence. Missing hardware or vendor binaries mean a check is untested.

## Checks

### Portable Project Closure

Architecture: [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md).

<a id="build-dependency-and-publish-boundaries"></a>

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Dependency and source contracts | Run dependency/source contract tests and inspect evaluated references, restored packages, generated compile items, and native copy or publish items. | Lower projects do not depend on native modules. Backend modules do not reference each other. Removed packages are absent from the active closure. | Open | 2026-09-30: 61 of 233 failed before build fixes. Failures covered stale paths, moved types, and application model-pipeline references. |
| Whole-project `browser-wasm` compile lane | Run `Tools/Test-PortableBrowserCompile.ps1 -Configuration Release` locally and in CI for each project in `Build/Portable/PortableProjects.tsv`. | Full source inclusion, reviewed package versions, generated command registration, and rejection of native assets and unreviewed APIs. This does not qualify browser execution or browser trim/AOT. | Open | 2026-09-30: local lane passed for all 14 projects. Linux CI remained unqualified. |
| Application build and publish layouts | Inspect Editor, Server, VRClient, cooked-launcher, and browser publish outputs. | Required native assets and notices are present for installed modules. Duplicate or stale files are absent. Jitter is excluded by default. Supply paths do not change. | Open | Last evidence: none. |
| Dependency and license inventory | Run the dependency report and review distribution notices. | OpenVR, optional Audio2X, and Steam Audio binary notices have an explicit disposition. Dependency or supply changes have owner review. | Open | Last evidence: none. |

### Portable Engine Host

Architecture: [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Desktop host startup | Launch `Editor (Unit Testing World)`, `Debug Server (Server only)`, and `Debug VRClient (Editor runs separately)`. | The editor enters and leaves play mode. Server runs the unit world. VRClient starts the unit world and shuts down without a queue stall. | Open | 2026-09-30: editor, server, and VRClient startup ran. VRClient shutdown remained open. 2026-10-05: the collapsed window host fixed the root cause for Vulkan editor close only. |
| Frame pacing preservation | Run editor and VRClient frame-pacing smokes after host-service or timer changes. | `EngineTimer` behavior matches the baseline for fixed and variable updates. | Open | 2026-09-30: VRClient reported 89.808 Hz variable updates across 30 ready samples. This did not prove physical FPS. |
| Runtime host service availability | Exercise startup policy, world host, settings, rendering host, physics, input, audio, animation, networking, and debug service slots. | Missing required services fail by name. Optional unavailable services report a clear diagnostic. | Open | Last evidence: none. |

### Physics And Collider Authoring

Architecture: [Physics overview](../../../architecture/physics/overview.md), [Runtime Project Organization](../../../architecture/runtime/project-organization.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Backend-neutral physics contracts | Run neutral contract fixtures for PhysX and Jolt. Include Jolt query, geometry, controller, and hardening suites. | Static catalog registration, saved enum values, unregistered-backend diagnostics, and explicit Jitter opt-in work. | Open | Last evidence: none. |
| PhysX suites and editor world | Run shape mutation, lifetime, serialization, boundary, and debug-frame suites. Run editor physics world initialization, play, mutation, stop, and reload. | All pass. GPU/CUDA capability diagnostics show when enabled. | Open | Last evidence: none. |
| Jolt and PhysX live scenes | Repeat live scene checks with viewed captures and logs. | Both backends simulate and present. | Open | Earlier PhysX run created bodies, but Vulkan presentation stopped at a prepared-mesh ingress error. Earlier Jolt/OpenGL scene is checkpoint evidence only. |
| Cooked collider geometry | Generate colliders through the authoring module in the editor and cook. Load cooked convex, triangle-mesh, and height-field geometry in a shipping host without authoring services. | Geometry loads. A request for uncooked generation without a backend gives an explicit diagnostic. | Open | Last evidence: none. |

### Jolt Browser Proof

Architecture: [Jolt browser native supply](../../design/platform/jolt-browser-native-supply.md), [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Browser `joltc` link and execution | Build the pinned static archive. Link the browser app through `NativeFileReference`. Through JoltPhysicsSharp, create a world, drop a box, run 120 checked steps, raycast, destroy, and repeat cleanup paths. | Browser output records each step. Cleanup leaks nothing. | Open | Last evidence: none. |
| Callback and marshalling compatibility | Audit bindings for static unmanaged-callable targets, pointer-sized signatures, and unsupported delegate marshalling. | Record whether upstream bindings work unchanged, or which approved fix or owned thin binding is necessary. | Open | Last evidence: none. |
| Single-threaded job system | Run the supplied wrapper without pthreads, then tear down. | Execution is single-threaded and teardown is safe. Zero worker threads alone is not proof. | Open | Last evidence: none. |
| Body and geometry parity | Compare static, kinematic, and dynamic bodies, triggers, disabled simulation, compound colliders, runtime mutations, and primitive and mesh shapes. | Jolt matches desktop behavior within documented tolerances. | Open | Last evidence: none. |
| Query, joint, and controller parity | Compare ray, sweep, overlap, joints, motors, breaking, and character controller gates. | Jolt matches neutral contracts and desktop behavior. | Open | Last evidence: none. |
| Contacts, debug, serialization, replication | Compare contact events, debug frames, serialization and reload, and replication authority fields. | Results match neutral contracts and desktop behavior. | Open | Last evidence: none. |

### Desktop Native Services

Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Audio backends | Exercise OpenAL/EFX, V2 playback, NAudio/SDL2 output, Steam Audio geometry and probes, device changes, teardown, microphone capture, and live network voice. | Hardware and optional OVRLipSync, Audio2Face, and voice-conversion availability are recorded. | Open | Last evidence: none. |
| Windows and input | On OpenGL and Vulkan, exercise main editor windows, detached ImGui viewports, resize, minimize, restore, focus, keyboard, mouse, gamepad, close, reopen, and VRClient windows. | Event-pump and context ownership are correct. UI input replays in order. No new per-frame allocations occur. | Open | Last evidence: none. |
| Secondary GPU contexts | Exercise secondary GPU-context callers. | OpenGL hidden-context ownership works. Vulkan reports unsupported secondary-context ownership explicitly, and each caller accepts it without a silent bypass. | Open | Last evidence: none. |
| OpenXR and OpenVR smoke | Run the OpenXR no-HMD/Monado lane and the OpenVR smoke path. | Startup, shutdown, restart, actions, pose, render-model loading, and presentation work. Physical-headset checks are recorded separately. | Open | Last evidence: none. |
| XR swapchain and dispatch lifetime | Exercise normal retirement, cancellation, retry, partial failure, and device loss with acquired images. | Children release before native parents. Recovery or explicit abandonment has no leaks, stale calls, or unsafe unpinning. | Open | Last evidence: none. |
| Images | Exercise image import and encoding, format conversion, mip generation, OpenGL and Vulkan screenshots, MCP captures, and cooked raw texture loading without ImageMagick. | Stride, origin, and ownership are correct. | Open | Last evidence: none. |
| Media | Exercise FFmpeg video components, streamed audio, HLS playback with seek, stop, restart, and disposal. Check optional yt-dlp resolution. | Playback works. A missing tool gives a diagnostic. The yt-dlp supply path does not change. | Open | Last evidence: none. |
| UI and text | Exercise ImGui editor/debug UI, Ultralight, Rive, Skia/SVG, FreeType enumeration, MSDF generation, in-world text, and font atlases. | Missing backends give diagnostics. Requested acceleration uses fallback only when fallback is explicitly allowed. | Open | Last evidence: none. |
| Meshoptimizer and ReSTIR | Exercise the optional native meshoptimizer service and OpenGL ReSTIR backend registration and execution. | Native failures give diagnostics. The fallback policy is explicit. | Open | Last evidence: none. |
| DirectStorage | Exercise asset load and range reads, scene streaming, and texture streaming with DirectStorage on and off. Exercise GDeflate and native hardware-compression failures. | Managed fallback occurs only when explicitly requested. | Open | Last evidence: none. |
| Desktop services | Exercise discovery, watching, mapping, platform paths, clipboard, process launch, optional development assembly loading, and diagnostics inventory. | Teardown is clean. An unavailable capability gives an error. | Open | Last evidence: none. |
| Networking services | Exercise server, client, and VRClient networking and control-plane join flows: UDP, TCP, TLS, portable framing, WebSocket paths, profiler telemetry, OSC/VMC, and native capture components. | Reconnect, cancellation, and shutdown work. A missing transport registration gives a diagnostic. | Open | Last evidence: none. |

### Serialization, AOT, And Published Applications

Architecture: [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md), [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Moved-type loading | Load sample and test worlds, prefabs, and settings that contain moved types. Use the type identity audit for public and nested names, old assembly-qualified inputs, and saved backend values. | All load. Any necessary migration is recorded. A source search is not a runtime loading test. | Open | Last evidence: none. |
| Generated factories and cooked metadata | Qualify factories and metadata for moved components, transforms, render commands, legacy aliases, and replication properties. | Published lookup uses registered metadata. Missing entries fail by name. Development authoring discovery still works. | Open | Last evidence: none. |
| Cooked NativeAOT game | Publish and launch a cooked game through the final-game workflow. Inspect native assets and notices. | Startup, config, and content loading work. Browser trim/AOT stays separate. | Open | Last evidence: none. |
| Application performance baseline | Launch final Editor, Server, and VRClient outputs and representative cooked worlds. Compare smoke-scene frame times and hot-path allocations to a matched baseline. | No material regression, or an investigation exists for each regression. | Open | Last evidence: none. |

### Unified Browser Runtime

Architecture: [Unified desktop and browser runtime design](../../design/platform/unified-desktop-browser-runtime-design.md), [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md), [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Real browser-world assets | Load YAML and MemoryPack assets in the interpreter. Round-trip representative worlds, prefabs, components, and external asset references. | The browser consumes the same authored data as desktop. Missing external references fail by name. | Open | 2026-09-30: YAML and binary round trips for a limited page passed. A representative world failed on an external shader reference. |
| Desktop and browser frame pacing | Compare editor and VRClient after shared frame-step changes. | Desktop timing is unchanged. Browser uses a caller-thread frame step without blocking. | Open | Last evidence: none. |
| Shared renderer contract on desktop | Validate OpenGL and Vulkan with editor captures from multiple positions after renderer-contract changes. | Desktop behavior is unchanged. Captures are viewed. | Open | Last evidence: none. |
| Shader coordinate conventions | Render known-value scenes for clip depth, texture Y, winding, matrix layout, and reversed-Z where used. | WebGPU data interpretation is deliberate and documented. | Open | Last evidence: none. |
| Desktop/WebGPU visual comparison | Render the same test worlds on OpenGL, Vulkan, and WebGPU. Compare tolerant captures and document deliberate differences. | A representative world renders through engine objects and the web tier. | Open | Last evidence: none. |
| Browser physics parity | Compare desktop and browser Jolt scenes. Use hashes only if determinism is approved; otherwise use documented tolerances. | Jolt-backed components behave the same in desktop and browser within the chosen criteria. | Open | Last evidence: none. |
| Browser audio codecs | Validate cooked audio codecs on the browser matrix, including Safari when it is in support scope. | Required audio works after user activation. Unsupported optional services fail by name. | Open | Last evidence: none. |
| Browser game components | Load serialized game components, game modes, and pawns in the browser from the same assets as desktop. | The same compiled game code runs on desktop and browser. | Open | Last evidence: none. |
| Editor browser publish | Publish the parity target and a lit, textured, animated test world. Play them in the browser and compare with desktop. | The editor produces a playable static site with named unsupported-feature reports. | Open | Last evidence: none. |
| Physical browser devices | Run the declared iOS and Android browser matrix with exact device, OS, browser, adapter, and profile data. | Device support claims have matching evidence. Unsupported devices show expected diagnostics. | Open | Last evidence: none. |
| Browser runtime mode | Measure interpreter and AOT candidates for startup, payload, memory, and CPU. | The shipping mode is chosen from measurements. | Open | Last evidence: none. |
| Browser hot-path allocations | Measure simulation, visibility, recording, and submission after warm-up. | No recurring per-draw managed allocations, or every unavoidable allocation is listed. | Open | Last evidence: none. |
| Browser memory bounds | Measure .NET heap, native Jolt heap, staging, and retained content across repeated world load and unload. | Memory remains bounded. Estimates are marked as estimates. | Open | Last evidence: none. |
| Lifecycle and recovery | Exercise device loss, hide/show, lock/unlock, orientation during loading, canvas removal, repeated load/unload, and explicit restart. | Stale generations are rejected. Recovery does not leak resources. | Open | Last evidence: none. |
| Networked browser client | Join the real server path under throttling, disconnects, app switching, and session expiry. | Reconnect, resync, queues, auth, and required media work or fail explicitly. | Open | Last evidence: none. |
| Browser CI and hosting | Run clean build and publish lanes. Validate HTTPS, MIME types, compression, immutable caching, bootstrap revalidation, CORS, credentials, CSP, and selected isolation. | A clean checkout can reproduce the documented static browser build and host configuration. | Open | Last evidence: none. |

### Browser WebGPU Runtime Details

Architecture: [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md), [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Platform boot | Publish clean output and start the browser host. Test unsupported API/device and initialization cancellation. | Required desktop services are absent. Startup diagnostics are actionable. | Open | 2026-09-30: reference harness and a published-world smoke ran for a separate limited path. Unified path remains unqualified. |
| Canvas behavior | Exercise CSS resize, DPR cap/change, orientation, safe area, soft keyboard, zero size, detach/reattach, and multiple viewports. | Target generations and lifecycle logs match each change. | Open | Last evidence: none. |
| Frame loop responsiveness | Exercise input, bounded catch-up, duplicate-loop prevention, and suspension/resume. | No desktop blocking loop runs inside a browser callback. | Open | Last evidence: none. |
| Content rendering | Exercise static and skinned meshes, opaque/masked/transparent materials, textures, mips, shadows, environment, UI, and tonemapping. | Inspected captures and tolerant reference comparisons pass. | Open | Last evidence: none. |
| Shader and data ABI | Check matrix/layout known values, binding compatibility, offscreen Y/depth, invalid shader diagnostics, and stale artifact/schema rejection. | Data layout is deterministic. Unsupported shader features fail with source context. | Open | Last evidence: none. |
| Bridge robustness | Run increasing draw counts, malformed packets, stale handles, upload-arena growth, cancellation, disposal, and memory-pressure cases. | Interop crossings stay bounded. Failure diagnostics identify the command and resource. | Open | Last evidence: none. |
| Compute and indirect baseline | Check compute output, compute-to-render, empty visibility, indirect arguments, and instance addressing. | Correctness passes without synchronous readback in the hot path. | Open | Last evidence: none. |
| Asset delivery | Exercise cold and warm cache, throttled downloads, corrupt or missing chunks, cancellation, cross-origin rejection, and cache upgrade. | Asset diagnostics, startup time, and peak memory stay within the selected profile. | Open | Last evidence: none. |
| Gameplay services | Exercise multi-touch controls, UI focus, required animation, physics, audio, user-gesture activation, pause, denied access, and context suspension. | Required services work. Optional unsupported services give visible reasons. | Open | Last evidence: none. |
| Deployment and performance | Validate static hosting and measure startup, first interaction, frame-time distribution, allocations, uploads, memory peaks, and sustained sessions. | The filled budget worksheet meets targets or records a failed gate. | Open | Last evidence: none. |

## Hardware Matrix

| Area | Required hardware or runtime | Status | Last evidence |
|---|---|---|---|
| Desktop OpenGL | Windows desktop GPU and OpenGL driver | Open | Last evidence: none. |
| Desktop Vulkan | Windows desktop GPU and Vulkan driver | Open | Last evidence: none. |
| VRClient OpenVR | SteamVR/OpenVR runtime and headset or proxy runtime | Open | Last evidence: none. |
| OpenXR Monado | Monado runtime for no-HMD smoke | Open | Last evidence: none. |
| OpenXR SteamVR | SteamVR OpenXR runtime and headset for hardware acceptance | Open | Last evidence: none. |
| Browser WebGPU desktop | WebGPU-capable desktop browser | Open | 2026-09-30: reference harness evidence only. |
| Browser WebGPU iOS | Supported iPhone or iPad with Safari version recorded | Open | Last evidence: none. |
| Browser WebGPU Android | Supported Android device with Chrome version recorded | Open | Last evidence: none. |
| Native physics supply | PhysX GPU/CUDA where enabled and pinned browser Jolt archive | Open | Last evidence: none. |
| Audio and media | OpenAL, NAudio/SDL2, Steam Audio, microphone, and optional media tools | Open | Last evidence: none. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Dependency and source contract tests | 61 of 233 failed on 2026-09-30 before the later build fixes. | [Build stabilization](../../progress/platform/unified-runtime-build-stabilization.md) |
| Jolt and PhysX live scenes | Vulkan presentation stopped at a prepared-mesh ingress error in the PhysX run. | [Native subsystem progress](../../progress/platform/native-subsystem-project-split.md) |
| Representative browser world | Browser fetched limited authored content but failed by name on `Shaders/Common/UnlitColoredForward.fs`. | [Browser boot qualification](../../progress/platform/portable-browser-engine-boot.md) |
| Jolt browser native supply | Archive naming was repaired, then a managed signature conflict blocked linkage. | [Jolt browser native supply](../../design/platform/jolt-browser-native-supply.md) |
| VRClient shutdown | The inherited queue stall is fixed for Vulkan editor close only. OpenGL close and fresh VRClient shutdown remain unqualified. | [Portable engine host validation](../../investigations/platform/portable-engine-host-validation.md) |
