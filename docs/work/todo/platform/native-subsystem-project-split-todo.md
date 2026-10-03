# Native Subsystem Integration Debugging And Validation TODO

[Work docs index](../../README.md) · [Current project organization](../../../architecture/runtime/project-organization.md) · [Checkpoint evidence](../../progress/platform/native-subsystem-project-split.md) · [Unified runtime follow-on](unified-desktop-browser-runtime-todo.md)

Status: awaiting the deferred integration debugging and validation pass. Native extraction, neutral contracts, full-project `net10.0` retargeting, and portability checks are implemented in source. Completed implementation work is documented in the project organization and subsystem guides; it is no longer a checklist here.

Updated: 2026-09-29. Owner: Runtime architecture / platform.

This document tracks remaining acceptance, defects discovered while validating, and the decisions required before physics default promotion. Earlier checkpoint results do not establish correctness of the final integrated source. The owner deferred builds, tests, and live debugging until a separate pass; this rewrite does not start that pass. Test source work was already explicitly cleared after the earlier live feature checks.

## How To Record Results

Keep an item open until its required evidence is recorded. Record the source revision, configuration, host/backend, commands or launch settings, result, and any limitation in a durable progress or investigation doc. Disposable logs/captures belong under `Build/_AgentValidation/<run>/`; preserve the actual findings in tracked documentation.

Use the [editor/tooling workflow](../../../developer-guides/ai/agent-editor-workflows.md) for isolated live sessions, camera views, captures, teardown, and logs. Use absolute isolated build-output paths and normal assembly attribute generation: an earlier relative override placed generated files inside source trees. A screenshot must be viewed before it counts as visual evidence. Missing hardware or vendor binaries mean untested acceptance, not a pass.

## Build, Dependency, And Publish Boundaries

- [x] Build the final integrated solution and the Editor, Server, VRClient, and unit-test projects. Resolve extraction/retargeting failures and warnings; identify unrelated failures separately. Done 2026-09-30 with no warnings; see the [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md).
- [ ] Execute the dependency/source contract tests and native subsystem inventory. A first filtered run on 2026-09-30 had 61 failures of 233, all predating the build fixes: stale source paths, moved types, and existing Server/VRClient/Bootstrap references to the model asset pipeline. See the [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md). Inspect evaluated references, restored packages, generated compile items, and native copy/publish items. Confirm lower projects do not depend on native modules, backend modules do not reference each other, and removed packages are absent from the active closure.
- [ ] Run the whole-project `browser-wasm` compile lane for every entry in `Build/Portable/PortableProjects.tsv`, locally and in CI. Confirm full source inclusion, reviewed package versions, exact generated command registration, and rejection of native assets/unreviewed APIs. The removed subset profiles/build switch must not be required. This check does not qualify browser execution or browser trim/AOT. The local lane passed for all 14 projects on 2026-09-30. Core index casing is now normalized to `XREngine.Runtime.Core`; Linux CI execution remains open.
- [ ] Inspect Editor, Server, VRClient, and cooked-launcher build/publish layouts. Verify `joltc`, MagicPhysX, audio, XR, image/media/UI/font, storage, mesh, and renderer assets/notices for the installed modules; check duplicate or stale cargo and default exclusion of Jitter. Preserve established submodule/SDK supply paths.
- [ ] Review the regenerated dependency and license inventory. Resolve or explicitly disposition the existing unresolved OpenVR binary, optional Audio2X bridge output, and checked-in Steam Audio binary notices before affected distribution. Dependency or supply-path changes still require owner review.

## Physics And Collider Authoring

- [ ] Rerun backend-neutral contract fixtures for registered PhysX and Jolt modules, plus Jolt query, geometry, controller, and production-hardening suites. Verify static catalog registration, saved enum values, unregistered-backend diagnostics, and explicit Jitter opt-in behavior.
- [ ] Rerun PhysX shape mutation, lifetime, serialization, boundary, and debug-frame suites. Exercise its editor physics world through initialization, play, mutation, stop, and reload, including GPU/CUDA capability diagnostics where enabled.
- [ ] Repeat Jolt and PhysX live scene checks with viewed captures and logs. The earlier PhysX run created bodies but Vulkan presentation stopped at a prepared-mesh ingress error; resolve or isolate that presentation failure before claiming visual physics acceptance. The earlier successful Jolt/OpenGL scene is checkpoint evidence only.
- [ ] Validate editor/cook collider generation through the Authoring module, then load the cooked convex, triangle-mesh, and height-field geometry in a shipping host without authoring services. Verify an explicit diagnostic when uncooked generation is requested without a backend.

## Jolt Browser Proof And Default-Promotion Gates

The [native supply proposal](../../design/platform/jolt-browser-native-supply.md) and `Tools/Dependencies/JoltBrowser/` contain a pinned build definition, a single-threaded C ABI wrapper, and a WebAssembly drop/raycast/teardown harness. Native source acquisition, build, linking, and browser execution remain unperformed. PhysX stays the desktop default until the decisions and proof below are complete.

- [ ] Obtain and record the owner decision on Jolt as the primary desktop/browser backend and the required parity criteria.
- [ ] Approve the native supply/toolchain plan: desktop NuGet plus a repository-built browser archive, or repository builds for all supported platforms. Confirm lock/toolchain pins against the selected .NET runtime pack before acquisition/build. Qualify matching determinism build settings separately if cross-platform determinism is required; do not infer it from backend selection.
- [ ] Build the pinned `joltc` static archive and link the prepared .NET browser app through `NativeFileReference`. Run create-world, drop-box, 120 checked simulation steps, raycast, and destruction through JoltPhysicsSharp; capture browser output and repeat creation/partial-failure cleanup.
- [ ] Audit and prove callback/marshalling compatibility: static unmanaged-callable targets, pointer-sized signatures, and no unsupported delegate marshalling. Record whether upstream bindings work unchanged or whether approved upstream fixes/an owned thin binding are required.
- [ ] Prove the supplied single-threaded job-system wrapper works without pthreads and tears down safely. Do not treat zero `JobSystemThreadPool` workers as proof of single-threaded execution.
- [ ] Validate body and geometry parity: static/kinematic/dynamic bodies, triggers, enabled/disabled simulation, all enabled compound colliders and runtime mutations; spheres, boxes, capsules, cylinders, cones, planes, convex hulls, triangle meshes, and height fields.
- [ ] Validate ray/sweep/overlap filtering and hit data; fixed, distance, hinge, prismatic, spherical, and 6-DOF joint limits, motors, and breaking; and the [character controller correctness gates](../physics/jolt-character-controller-correctness-todo.md).
- [ ] Validate contact events, debug frames, serialization/reload, and replication authority fields against the neutral contracts and desktop behavior.
- [ ] Resolve the optional Box3D comparison decision. If approved, run matched browser/desktop scenes and record feature coverage, binding effort, and step cost; otherwise record that comparison is deferred. Its [future integration map](../physics/box3d-backend-integration-todo.md) already targets a leaf project.
- [ ] Only after approval and parity/browser acceptance, change defaults for new projects and the unit-testing world to Jolt while retaining saved selections. Update physics architecture/user docs, editor labels, and the generated settings/schema with `Tools/Generate-UnitTestingWorldSettings.ps1`; validate the new default and saved PhysX projects. This is the remaining gated code change.

## Audio, Windowing, And XR

- [ ] Validate legacy OpenAL/EFX and V2 playback/spatialization, NAudio/SDL2 output, Steam Audio scene geometry/probes, device changes, and teardown. Exercise microphone capture and live network voice; record hardware and optional OVRLipSync/Audio2Face/voice-conversion availability explicitly.
- [ ] Validate main editor windows, detached ImGui viewports, resize/minimize/restore, focus, keyboard/mouse/gamepad input, close/reopen, and VRClient windows on the affected OpenGL and Vulkan paths. Verify event-pump/context ownership and ordered UI input replay without new per-frame allocations.
- [ ] Exercise secondary GPU-context callers. OpenGL hidden-context ownership must work; Vulkan currently reports unsupported secondary-context ownership, which must be explicit and acceptable to each caller rather than silently bypassed.
- [ ] Run the OpenXR no-HMD/Monado lane and OpenVR smoke path. Validate startup/shutdown/restart, actions, pose, render-model loading, and presentation. Record physical-headset checks separately as manual acceptance.
- [ ] Exercise XR swapchain/dispatch lifetime and acquired-image handling: normal retirement, cancellation, retry, partial failure, and device loss. Confirm children release before native parents. Vulkan post-detachment failure currently pins parents with a recovery diagnostic; validate recovery/explicit abandonment without leaks, stale calls, or unsafe unpinning.

## Images, Media, Text, And UI

- [ ] Validate image import/encoding, format conversion and mip generation; OpenGL/Vulkan screenshots and MCP captures; stride/origin/ownership handling; and cooked raw texture loading without ImageMagick.
- [ ] Validate FFmpeg video components, streamed audio, and HLS reference playback with seeking/stop/restart/disposal. Check optional yt-dlp resolution and missing-tool diagnostics without changing its supply path.
- [ ] Validate ImGui editor/debug UI, Ultralight, Rive, Skia/SVG components, FreeType character enumeration/MSDF generation, and in-world text/font atlases. Confirm missing-backend diagnostics and that requested acceleration only uses a fallback when explicitly allowed.
- [ ] Validate the optional native meshoptimizer service and OpenGL ReSTIR backend registration/execution. Confirm native failure diagnostics and explicit fallback policy; source ownership changes alone do not establish GPU correctness.

## Assets, Networking, And Runtime Services

- [ ] Validate asset load/range reads, scene streaming, and texture streaming with DirectStorage enabled and disabled. Exercise GDeflate and native hardware-compression capability failures; check managed fallback only when explicitly requested.
- [ ] Validate desktop discovery/watching/mapping, platform paths, clipboard, process launching, optional development assembly loading, and diagnostics inventory through their registered capabilities. Check teardown and unavailable-capability errors.
- [ ] Validate server/client/VRClient networking and control-plane join flows, including UDP/TCP/TLS transports, portable framing/WebSocket paths, profiler telemetry, OSC/VMC, and native capture components where enabled. Check reconnect, cancellation, shutdown, and missing transport registration.

## Serialization, AOT, And Application Acceptance

- [ ] Load existing sample/test worlds, prefabs, and settings containing moved types. Use the [type identity audit](../../progress/platform/native-subsystem-type-identities.md) to cover public and nested names, old assembly-qualified inputs, and saved backend values; record any actual migration needed. A source search finding no stored instance is not a runtime loading test.
- [ ] Qualify generated factories and cooked metadata for moved components, transforms, render commands/legacy aliases, and replication properties. Confirm published lookup uses registered metadata and missing entries fail by name; verify development authoring discovery still works.
- [ ] Publish and launch a cooked NativeAOT game through the [final-game workflow](../../../developer-guides/runtime/aot-final-game-builds.md), inspect native assets/notices, and exercise startup/config/content loading. Keep browser trim/AOT qualification in the [unified runtime tracker](unified-desktop-browser-runtime-todo.md).
- [ ] Launch final Editor, Server, and VRClient outputs and representative cooked worlds. Compare standard smoke-scene frame times and hot-path allocations against a recorded baseline using matched settings; investigate material regressions.
- [ ] Reconcile all results in the checkpoint record and feature docs, with explicit unsupported/manual/deferred cases. Close this tracker only when required integration acceptance is complete and the physics promotion decision has been implemented or explicitly superseded by the owner. Remove obsolete implementation-plan references from lasting docs/code during closeout.
