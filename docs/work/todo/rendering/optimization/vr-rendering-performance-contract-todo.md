# VR Rendering Performance Contract TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md), [OpenVR Rendering](../../../../architecture/rendering/openvr-rendering.md), [VR Output Pacing And Mirror Policy](../../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md)
Design: [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md)

## Current State

VR rendering reports output cadence and mirror policy through the frame-output manifest. OpenXR exposes view render modes and pacing modes. The cross-renderer contract still needs canonical stereo mode reporting, multiview coverage, per-eye resource checks, motion-vector correctness, foveation/VRS reporting, reprojection diagnostics, and comparable benchmark metadata.

## Open Code Items

### Profile metadata and stereo reporting

- [ ] Add profile manifest fields for XR runtime, HMD, refresh rate, render resolution, stereo path, foveation/VRS state, reprojection state, render-path tracker, graph revision, validation state, and cache state. Update profiler packet and capture writers. Done when: a VR profile capture explains its target budget and active stereo implementation without external notes.
- [ ] Add a canonical `StereoMode` stat with values such as `Mono`, `Multiview`, `ViewInstance`, `InstancedStereo`, and `TwoPass`. Update render stats, profiler packets, and pass telemetry. Done when: each frame and differing pass can report its stereo mode.
- [ ] Mark two-pass stereo as a compatibility or debug fallback in diagnostics. Done when: a scene cannot silently double CPU submission by falling into two-pass stereo.
- [ ] Distinguish mono draws, multiview or view-instanced draws, and two-pass CPU-submitted draws in draw counters. Done when: draw counters show how much work runs once, per view, or per eye.
- [ ] Ensure view-independent compute producers run once per frame. Update compute pass scheduling or diagnostics where required. Done when: producers that do not depend on view state are not executed once per eye.
- [ ] Require view-dependent passes to state why they run per eye. Done when: diagnostics or pass metadata identifies the view dependency.

### Multiview and view instancing

- [ ] Validate OpenGL `GL_OVR_multiview2` geometry passes that can support it. Done when: supported OpenGL geometry and depth passes use the correct view ID and output routing.
- [ ] Validate Vulkan `VK_KHR_multiview` render pass or dynamic rendering setup where the Vulkan path supports it. Done when: compatible Vulkan geometry, depth, and visibility passes render single-pass stereo on supported combinations.
- [ ] Document DX12 view-instancing requirements for future backend parity. Done when: the architecture doc states required resource layouts, view constants, and fallback behavior.
- [ ] Add fallback to instanced stereo or explicit two-pass when single-pass backend support is missing. Done when: fallback is reported and never hidden.
- [ ] Validate shadow maps render mono unless a pass explicitly requires per-eye shadowing. Done when: shadow passes report their view dependency and do not duplicate work without need.
- [ ] Add source-contract checks for multiview shader defines and binding layout. Done when: shader and binding changes cannot silently disable multiview.

### Per-eye resource correctness

- [ ] Validate depth, normal, velocity, visibility, post-process, and mirror resources for mono, stereo array, and multiview layouts. Done when: both eyes see the same scene state with correct per-eye projection and no stale shared resource hazards.
- [ ] Add stereo-safe Hi-Z source resolution. Use a per-eye chain, array sampler variant, or explicit conservative fallback. Done when: no eye samples stale or cross-eye Hi-Z data.
- [ ] Ensure per-eye view/projection and previous view/projection state are double-buffered correctly. Done when: temporal effects read the matching current and previous matrices per eye.
- [ ] Ensure editor overlays and UI write valid velocity or explicit zero. Done when: overlays never leave undefined velocity input.
- [ ] Ensure mirror blit does not mutate eye textures or force synchronization in measured frames. Done when: mirror output owns its copies or sampled reads and reports any synchronization.
- [ ] Add diagnostics for stale camera state shared across eyes. Done when: a shared-state hazard reports the affected view and resource.

### Motion vectors and upscaler contract

- [ ] Maintain previous-frame transform per instance. Done when: motion-vector generation can compare current and previous transforms for all visible instances.
- [ ] Maintain previous-frame skinned position buffers for skinned meshes. Done when: animated vertices can generate velocity from previous skinned position.
- [ ] Generate velocity from current clip position minus previous clip position. Done when: camera jitter convention is explicit for each upscaler integration.
- [ ] Define velocity behavior for CPU direct, zero-readback indirect, meshlet, visibility-buffer, cluster avatar, splat, UI, and editor overlay paths. Done when: each path writes valid velocity or explicit zero.
- [ ] Add a velocity validity coverage counter. Done when: profiler output shows missing, NaN, and out-of-range velocity coverage.
- [ ] Add a debug view for missing, NaN, or out-of-range velocity. Done when: visual debugging identifies invalid velocity by source path.

### VRS, foveation, and reprojection

- [ ] Add or validate VRS and foveation capability probes per backend. Done when: unsupported or disabled modes have explicit fallbacks.
- [ ] Add fixed foveation rate image support for non-eye-tracked HMDs where supported. Done when: the active rate image and fallback reason are reported.
- [ ] Add eye-tracked rate image support behind runtime capability checks where supported. Done when: eye-tracked foveation never runs without runtime support.
- [ ] Report VRS shading-rate distribution per frame. Done when: performance claims include shading-rate coverage.
- [ ] Account for effective shading rate in screen-space-error metrics where used. Done when: LOD and avatar systems do not overstate detail in low-rate regions.
- [ ] Validate VRS does not break UI readability, selection, or debug overlays. Done when: UI and diagnostic passes remain readable or opt out.
- [ ] Write valid depth for every visible pixel, including splats and impostors. Done when: reprojection does not read undefined depth.
- [ ] Mark reprojection-incompatible post effects with explicit VR opt-out or fallback. Done when: incompatible effects do not silently degrade runtime reprojection.
- [ ] Add runtime reprojection and motion-smoothing event counters where APIs expose them. Done when: profiler output shows when runtime reprojection masks app misses.
- [ ] Add warnings when app frame time appears acceptable only because runtime reprojection is masking misses. Done when: missed XR budgets remain visible in reports.

### Benchmarks and regression coverage

- [ ] Standardize VR benchmark scenes and camera paths. Done when: reports compare the same workload across renderer strategies.
- [ ] Report whole-frame budget in all benchmark notes. Done when: no report presents per-eye budget as whole-frame acceptance.
- [ ] Require Vulkan RVC `GpuIndirectZeroReadback` p95 at or below 8.33 ms for the complete minimum-three-render frame in both supported foveation states. Done when: the promotion workload includes desktop, left eye, and right eye in one frame.
- [ ] Include serial two-pass eye-slice estimates only as warnings for naive two-pass paths. Done when: estimates cannot be confused with accepted runtime behavior.
- [ ] Disable validation layers, synchronous debug output, and debug callbacks during benchmark runs. Done when: benchmark metadata states the diagnostic state.
- [ ] Capture p50, p90, p99, dropped frames, reprojection events, stereo mode, and cache policy. Done when: benchmark reports are comparable across strategies.
- [ ] Add or update desktop mono regression coverage after stereo changes. Done when: stereo work cannot break mono rendering without a targeted test failure.

## Decisions Needed

- [ ] Which VR scene is the canonical promotion workload for 120 Hz Vulkan RVC zero-readback? Owner: Rendering / XR.
- [ ] Which foveation states are required for v1 promotion? Owner: Rendering / XR.
- [ ] Which vendor reprojection counters are required before a runtime is accepted for performance claims? Owner: Rendering / XR.

## Out Of Scope

- Implementing vendor upscalers.
- Requiring every development backend to support single-pass stereo immediately.
- Hiding two-pass fallback.
- Branch and merge workflow steps.
