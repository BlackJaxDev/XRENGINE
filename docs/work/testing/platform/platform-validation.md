# Platform Validation

[Work docs index](../../README.md) · [Testing docs](../README.md)

Scope: integration acceptance for the native subsystem leaf projects, the portable `net10.0` projects, and the desktop and browser composition.

Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md), [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md), [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md).

Code todos: [Unified desktop and browser runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md).

## Setup

- Use the [editor and tooling workflow](../../../developer-guides/ai/agent-editor-workflows.md) for isolated live sessions, camera views, captures, teardown, and logs.
- Use absolute isolated build-output paths and normal assembly attribute generation. A relative output override can put generated files in source trees.
- View a screenshot before you use it as visual evidence.
- Missing hardware or vendor binaries make a check untested. They do not make it pass.
- For each result, record the source revision, configuration, host and backend, commands or launch settings, result, and limits in a progress or investigation doc. Put disposable logs and captures under `Build/_AgentValidation/<run>/`.
- Checkpoint evidence: [native subsystem progress](../../progress/platform/native-subsystem-project-split.md), [build stabilization](../../progress/platform/unified-runtime-build-stabilization.md), [type identity audit](../../progress/platform/native-subsystem-type-identities.md), [reference harness investigation](../../investigations/rendering/desktop-browser-reference-harness.md). Earlier checkpoint results do not prove that the final integrated source is correct.

## Imported Checks

### From native-subsystem-project-split-todo.md

#### Build, Dependency, And Publish Boundaries

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Dependency and source contract tests | Run the dependency/source contract tests and the native subsystem inventory. Inspect evaluated references, restored packages, generated compile items, and native copy/publish items. | Lower projects do not depend on native modules. Backend modules do not reference each other. Removed packages are not in the active closure. | Open | 2026-09-30: 61 of 233 failed before the build fixes (stale source paths, moved types, Server/VRClient/Bootstrap references to the model asset pipeline). |
| Whole-project `browser-wasm` compile lane | Run the lane for every entry in `Build/Portable/PortableProjects.tsv`, locally and in CI. | Full source inclusion, reviewed package versions, exact generated command registration, and rejection of native assets and unreviewed APIs. The removed subset profiles and build switch are not necessary. This does not qualify browser execution or browser trim/AOT. | Open (Linux CI) | 2026-09-30: local lane passed for all 14 projects. |
| Application build and publish layouts | Inspect Editor, Server, VRClient, and cooked-launcher layouts. | `joltc`, MagicPhysX, audio, XR, image, media, UI, font, storage, mesh, and renderer assets and notices are present for the installed modules. No duplicate or stale files. Jitter is excluded by default. Submodule and SDK supply paths do not change. | Open | |
| Dependency and license inventory | Review the regenerated inventory. | The OpenVR binary, the optional Audio2X bridge output, and the Steam Audio binary notices are resolved or have an explicit disposition before distribution. Dependency or supply-path changes have owner review. | Open | |

#### Physics And Collider Authoring

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Backend-neutral physics contracts | Run the neutral contract fixtures for PhysX and Jolt, plus the Jolt query, geometry, controller, and production-hardening suites. | Static catalog registration, saved enum values, unregistered-backend diagnostics, and explicit Jitter opt-in work. | Open | |
| PhysX suites and editor world | Run the PhysX shape mutation, lifetime, serialization, boundary, and debug-frame suites. Take the editor physics world through initialization, play, mutation, stop, and reload. | All pass. GPU/CUDA capability diagnostics show when enabled. | Open | |
| Jolt and PhysX live scenes | Repeat the live scene checks with viewed captures and logs. | Both backends simulate and present. | Open | The earlier PhysX run created bodies, but Vulkan presentation stopped at a prepared-mesh ingress error. Isolate or fix it first. The earlier Jolt/OpenGL scene is checkpoint evidence only. |
| Cooked collider geometry | Generate colliders through the Authoring module in the editor and cook. Load the cooked convex, triangle-mesh, and height-field geometry in a shipping host without authoring services. | Geometry loads. A request for uncooked generation without a backend gives an explicit diagnostic. | Open | |

#### Jolt Browser Proof

The [native supply proposal](../../design/platform/jolt-browser-native-supply.md) and `Tools/Dependencies/JoltBrowser/` hold the pinned build definition, the single-threaded C ABI wrapper, and the WebAssembly harness. PhysX stays the desktop default until these checks pass and the owner decides on promotion.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Browser `joltc` link and execution | Build the pinned `joltc` static archive. Link the browser app through `NativeFileReference`. Through JoltPhysicsSharp, create a world, drop a box, run 120 checked steps, raycast, and destroy. Repeat creation and partial-failure cleanup. | Browser output records each step. Cleanup leaks nothing. | Open | |
| Callback and marshalling compatibility | Audit the bindings for static unmanaged-callable targets, pointer-sized signatures, and no unsupported delegate marshalling. | Record whether upstream bindings work unchanged, or which approved upstream fix or owned thin binding is necessary. | Open | |
| Single-threaded job system | Run the supplied wrapper without pthreads, then tear down. | Execution is single-threaded and teardown is safe. Zero `JobSystemThreadPool` workers alone is not proof. | Open | |
| Body and geometry parity | Compare static, kinematic, and dynamic bodies, triggers, enabled and disabled simulation, enabled compound colliders, and runtime mutations. Use spheres, boxes, capsules, cylinders, cones, planes, convex hulls, triangle meshes, and height fields. | Jolt matches the desktop behavior. | Open | |
| Query, joint, and controller parity | Compare ray, sweep, and overlap filtering and hit data. Compare fixed, distance, hinge, prismatic, spherical, and 6-DOF joint limits, motors, and breaking. Run the [character controller correctness gates](../../todo/physics/jolt-character-controller-correctness-todo.md). | Jolt matches the desktop behavior. | Open | |
| Contacts, debug, serialization, replication | Compare contact events, debug frames, serialization and reload, and replication authority fields. | Results match the neutral contracts and desktop behavior. | Open | |

#### Audio, Windowing, And XR

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Audio backends | Exercise legacy OpenAL/EFX and V2 playback and spatialization, NAudio/SDL2 output, Steam Audio scene geometry and probes, device changes, teardown, microphone capture, and live network voice. | All work. Hardware and optional OVRLipSync, Audio2Face, and voice-conversion availability are recorded. | Open | |
| Windows and input | On the affected OpenGL and Vulkan paths, exercise main editor windows, detached ImGui viewports, resize, minimize, restore, focus, keyboard, mouse, gamepad, close and reopen, and VRClient windows. | Event-pump and context ownership are correct. UI input replays in order. No new per-frame allocations. | Open | |
| Secondary GPU contexts | Exercise the secondary GPU-context callers. | OpenGL hidden-context ownership works. Vulkan reports unsupported secondary-context ownership explicitly, and each caller accepts it without a silent bypass. | Open | |
| OpenXR and OpenVR smoke | Run the OpenXR no-HMD/Monado lane and the OpenVR smoke path. | Startup, shutdown, restart, actions, pose, render-model loading, and presentation work. Physical-headset checks are recorded separately as manual acceptance. | Open | |
| XR swapchain and dispatch lifetime | Exercise normal retirement, cancellation, retry, partial failure, and device loss with acquired images. | Children release before native parents. Vulkan post-detachment recovery or explicit abandonment has no leaks, stale calls, or unsafe unpinning. | Open | |

#### Images, Media, Text, And UI

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Images | Exercise image import and encoding, format conversion, mip generation, OpenGL and Vulkan screenshots, MCP captures, and cooked raw texture loading without ImageMagick. | Stride, origin, and ownership are correct. | Open | |
| Media | Exercise FFmpeg video components, streamed audio, and HLS reference playback with seek, stop, restart, and disposal. Check optional yt-dlp resolution. | Playback works. A missing tool gives a diagnostic. The yt-dlp supply path does not change. | Open | |
| UI and text | Exercise ImGui editor and debug UI, Ultralight, Rive, Skia/SVG components, FreeType character enumeration and MSDF generation, and in-world text and font atlases. | Missing backends give diagnostics. Requested acceleration uses a fallback only when it is explicitly allowed. | Open | |
| meshoptimizer and ReSTIR | Exercise the optional native meshoptimizer service and the OpenGL ReSTIR backend registration and execution. | Native failures give diagnostics. The fallback policy is explicit. | Open | |

#### Assets, Networking, And Runtime Services

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| DirectStorage | Exercise asset load and range reads, scene streaming, and texture streaming with DirectStorage on and off. Exercise GDeflate and native hardware-compression capability failures. | Managed fallback occurs only when it is explicitly requested. | Open | |
| Desktop services | Exercise discovery, watching, mapping, platform paths, clipboard, process launch, optional development assembly loading, and diagnostics inventory through their registered capabilities. | Teardown is clean. An unavailable capability gives an error. | Open | |
| Networking | Exercise server, client, and VRClient networking and control-plane join flows: UDP, TCP, TLS, portable framing and WebSocket paths, profiler telemetry, OSC/VMC, and native capture components where enabled. | Reconnect, cancellation, and shutdown work. A missing transport registration gives a diagnostic. | Open | |

#### Serialization, AOT, And Application Acceptance

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Moved-type loading | Load sample and test worlds, prefabs, and settings that contain moved types. Use the [type identity audit](../../progress/platform/native-subsystem-type-identities.md) for public and nested names, old assembly-qualified inputs, and saved backend values. | All load. Any necessary migration is recorded. A source search is not a runtime loading test. | Open | |
| Generated factories and cooked metadata | Qualify factories and metadata for moved components, transforms, render commands and legacy aliases, and replication properties. | Published lookup uses registered metadata. A missing entry fails by name. Development authoring discovery still works. | Open | |
| Cooked NativeAOT game | Publish and launch a cooked game through the [final-game workflow](../../../developer-guides/runtime/aot-final-game-builds.md). Inspect native assets and notices. | Startup, config, and content loading work. Browser trim/AOT stays in the unified runtime TODO. | Open | |
| Application performance baseline | Launch final Editor, Server, and VRClient outputs and representative cooked worlds. Compare smoke-scene frame times and hot-path allocations to a recorded baseline with matched settings. | No material regression, or an investigation for each regression. | Open | |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Dependency and source contract tests | 61 of 233 failed on 2026-09-30. | [Build stabilization](../../progress/platform/unified-runtime-build-stabilization.md) |
| Jolt and PhysX live scenes | Vulkan presentation stopped at a prepared-mesh ingress error in the PhysX run. | [Native subsystem progress](../../progress/platform/native-subsystem-project-split.md) |
