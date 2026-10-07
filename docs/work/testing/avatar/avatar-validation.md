# Avatar Validation

[Work docs index](../../README.md) · [Testing docs](../README.md)

## Scope

This document owns avatar validation checks. It covers OpenXR body calibration, spectator output, VR full-body estimation, and avatar optimization. It does not own humanoid animation conformance; use [Animation Validation](../animation/animation-validation.md#humanoid-body-and-root) for that area.

Architecture links: [OpenXR body tracking and spectator integration](../../../developer-guides/vr/openxr-body-tracking.md), [Local VR body calibration](../../../developer-guides/vr/full-body-calibration.md), [Independent VR spectator](../../../developer-guides/vr/spectator-camera.md), [OpenXR runtime](../../../developer-guides/vr/openxr-runtime.md#steamvr-openxr-hardware-lane), [Avatar optimization design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md), [GPU skinned BVH proxy LOD design](../../design/rendering/gpu/gpu-skinned-bvh-proxy-lod-design.md).

Code todo links: [VR full-body estimation](../../todo/avatar/vr-full-body-estimation-todo.md), [Avatar optimization roadmap](../../todo/avatar/avatar-optimization-roadmap.md).

Related validation: [SteamVR OpenXR hardware validation](../xr/openxr-steamvr-hardware-validation.md), [Windows calibration and spectator validation record](../../investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md), [Animation Validation](../animation/animation-validation.md#humanoid-body-and-root).

## Setup

Use `Build-Editor` before live editor validation. Use `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug` for SteamVR smoke sessions. Use `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug` for Monado smoke sessions. Use `Test-OpenXR-SteamVR-Smoke`, `Test-OpenXR-Monado-Smoke`, and `Test-OpenXR-SceneOnlyVR-Smoke` for scripted OpenXR smoke checks. Launch profiles that apply are `Editor (Default World)`, `Editor (Unit Testing World)`, and `Editor (Unit Testing OpenXR SteamVR)`.

Relevant environment variables are `XRE_WORLD_MODE=UnitTesting`, `XRE_UNIT_TEST_WORLD_KIND=Default`, `XRE_UNIT_TEST_VR_MODE=OpenXR`, `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=1`, and `XRE_WINDOW_TITLE`. Record the implementation commit; headset, controller, and tracker models; tracker count; GPU and driver; Windows version; SteamVR and OpenXR runtime versions; active runtime manifest; tracker extension revision; graphics backend; stereo view mode; and avatar settings that affect calibration or spectator output.

For avatar optimization, use a corpus that includes the observed 1.08M triangle and 62 material avatar, a simple humanoid with clean materials, a hair-card-heavy avatar, a blendshape-heavy face avatar, a VRM avatar with spring bones, an accessory-heavy avatar, and an intentionally broken import. Use this animation sample set: bind pose, T-pose, A-pose, walk, run, jump apex, arms overhead, arms behind back, extreme crouch or sit, and facial range-of-motion sweep.

## Checks

### OpenXR body calibration and spectator

Architecture: [OpenXR body tracking](../../../developer-guides/vr/openxr-body-tracking.md), [Local VR body calibration](../../../developer-guides/vr/full-body-calibration.md), [Independent VR spectator](../../../developer-guides/vr/spectator-camera.md).

Procedures:

- **Tracker transport.** Start with all intended trackers connected. On SteamVR's OpenXR runtime, capture extension revision, enumeration, action bindings, current pose activity and validity, identity, and sample times for every tracker. Repeat with default and duplicate SteamVR role mappings, a disabled tracker, occlusion, power cycling, and reconnection. Each physical tracker must appear once, stream through its persistent-path subaction, and keep its identity across role changes. A nonempty enumeration alone does not prove pose streaming. A tracker discovered after action attachment must report that a player-initiated VR restart is necessary. If a tracker cannot stream independent of its role mapping, record the runtime version and the exact enumeration, binding, and pose failure.
- **Calibration flow.** On every available controller interaction profile, exercise open, head-tilt refusal, stationary capture with both triggers or both select buttons, cancel, retry, and recalibration. Each gesture works while the desktop editor camera has focus, needs no keyboard, and has no mute side effect. Eye-level messages, tracker labels, markers, footprints, and the T-pose are readable in the headset. Missing or stale headset or controller samples refuse capture and keep the last committed rig. A changed avatar requests recalibration.
- **Tracker binding and body motion.** Use six points and the largest tracker set, up to eleven. Include default SteamVR state and trackers swapped between sessions. Proximity assigns each tracker to at most one slot and leaves distant extras unassigned. Crossing feet after capture does not rebind. Check head, hand, hip, foot, chest, upper-arm, and knee motion, repeated calibration, and no origin snaps, duplicate targets, or accumulated offsets. Compare standing, head glances, body turns, crouching, either foot lifted, and room-scale walking. Height and arm-span settings drive avatar scale without changing tracking-space meters. An unset or implausible measurement shows a notice and infers nothing.
- **Grip-to-wrist preset.** For each physical controller profile, record the controller model, interaction-profile path, measured grip-to-wrist translation and rotation, the measurement source, and hand alignment across several wrist poses. The shared geometric default is an unqualified estimate.
- **Loss and recovery.** Exercise tracker occlusion, loss, recovery, power cycling, and a different tracker replacing a bound one. The last target may hold briefly and fade, but never snaps to the origin or passes to the replacement. Exercise controller loss, headset removal, dashboard focus, recenter, teleport, runtime and session restart, and avatar change. Check validity, input routing, event resets, and recalibration when the reference-space relationship is unknown.
- **Editor VR toggle.** Toggle OpenXR off and on and recreate the VR pawn. Record whether provider generation and reference-space basis are unchanged. Committed bindings and offsets restore only when avatar, measurement, provider generation, reference basis, and physical tracker identities match. Missing trackers leave slots empty. Otherwise require recalibration. A synthetic same-generation restore does not prove hardware toggle continuity.
- **Spectator image.** With headset rendering active, inspect the headset image and the third-person desktop or render-texture output together. Check full-avatar visibility, follow direction and body yaw independent of head glances, obstruction recovery, teleport and camera-mode resets, orientation, aspect ratio, color space, post-processing, and per-view visibility, on Vulkan and OpenGL. Switching desktop cameras, changing spectator cadence, holding an output lease, and desktop focus never take VR input, change the tracking origin, alter eye cameras, or move the audio listener. A slower cadence may reuse a frame but never delays headset submission.
- **Spectator performance.** Measure headset frame time, missed deadlines, allocations, and spectator CPU and GPU cost with the spectator off and on. Record values and scene settings. Source inspection or synthetic fence tests do not count.
- **Optional integrations.** If an encoder or capture integration is added, validate bounded queues, readback lifetime, timestamps, audio alignment, and fall-behind behavior. If a calibration mirror is added, validate it in the headset with input.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Role-free tracker streaming on SteamVR OpenXR | Tracker transport. Include duplicate and default roles, a tracker disabled in SteamVR, and late connection. | Every connected tracker streams through its persistent-path subaction path, or the exact limitation is recorded. | Failed (blocker) | Two native HTCX enumeration probes returned zero persistent paths while OpenVR listed three Tundra trackers with valid poses. Duplicate-role, disabled-tracker, and late-connection evidence is missing. |
| Spectator output on Vulkan and OpenGL | Spectator image. | Orientation, aspect ratio, color space, post-processing, and visibility are correct without the headset. | Open | A Vulkan offscreen readback was upright at 1920 × 1080 with finite HDR samples, but the avatar color was overbright and garment geometry was straight in the full scene. The cause is unresolved. |
| Calibration at 6 to 11 points | Tracker binding and body motion, at 6 points and with all trackers, default state and swapped between sessions. | One-to-one proximity binding; chest, upper arms, and knees drive the solver. | Open | none |
| Body motion | Stand, look around without body yaw, turn, crouch, lift either foot, cross feet after calibration, and walk room-scale. | Alignment holds; no rebinding. | Open | none |
| Grip-to-wrist preset | Grip-to-wrist preset, per controller profile. | Measured preset recorded; hands align across wrist poses. | Open | none |
| Loss and recovery | Loss and recovery, plus Editor VR toggle. | No origin snaps, duplicate nodes, silent rebinding, or accumulated offsets. | Open | none |
| Calibration flow per controller profile | Calibration flow. | The flow is transactional and has no mute side effect. | Open | none |
| Spectator alongside VR | Spectator image, with full-avatar visibility, obstruction, teleport resets, input focus, cadence, and audio-listener ownership. | The spectator never takes headset ownership, the tracking origin, or controller possession. | Open | none |
| Calibration mirror | Optional integrations. | The mirror appears only during calibration and reuses the completed spectator frame without a second scene render. | Open | none |
| Headset performance with spectator | Spectator performance. | Measured values are recorded; spectator cost is isolated. | Open | Short physical OpenGL Debug samples missed every sampled deadline. Allocation rates included editor and MCP work. Spectator-only cost is not isolated. |
| Evidence record | Attach logs, smoke summaries, test results, and short visual evidence. Update the hardware record with the commit, configuration, and remaining failures. | One named configuration has full evidence. | Open | A later physical retry returned `ErrorFormFactorUnavailable` before graphics startup. |
| Milestone acceptance | All rows above on one named configuration. | OpenXR renders first-person while controller input stays with the VR player. No SteamVR role is read, required, or shown to players. Avatar scale comes only from the player's measurement. | Open | none |

### VR full-body estimation

Architecture: [OpenXR body tracking](../../../developer-guides/vr/openxr-body-tracking.md), [Local VR body calibration](../../../developer-guides/vr/full-body-calibration.md), [Independent VR spectator](../../../developer-guides/vr/spectator-camera.md).

Procedure for this group: use live sessions through the spectator view, with trackers hidden or powered off mid-session. Optional recorded sessions stay outside tracked docs. No test depends on them.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Hardware tuning matrix | Tune and observe at 3 points, 4 points with hips only, 5 points with feet only, and 6 points. | Planted feet move less than 1 cm and never go below the floor. Knees never hyperextend or invert. The pelvis stays over the support area while standing. Handoff crossfades have no single-frame jump beyond the bound. | Open | none |
| Ground-truth baseline | Replay real tracker sessions with selected trackers hidden. | Hips and feet error is reported per behavior. The first baseline sets the thresholds. Later runs do not regress. | Open | none |
| Cost budget | Profile estimation per avatar. | The estimator stays within the per-avatar budget with zero per-frame heap allocation. | Open | none |
| Postures | Exercise crouch, bow, rise, kneel, walk room-scale, turn in place, snap turn, artificial locomotion, sit, and lie in each orientation. | Poses are plausible and stable, with no foot sliding or pops. | Open | none |
| Evidence record | Record hardware evidence with the calibration acceptance results above. | One named configuration has full evidence. | Open | none |

### Avatar optimization

Architecture: [Avatar optimization design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md), [GPU skinned BVH proxy LOD design](../../design/rendering/gpu/gpu-skinned-bvh-proxy-lod-design.md), [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Baseline | Record draws, submeshes, material slots, textures, texture memory, vertices, triangles, bones, influences, blendshapes, shader variants, and renderer cost for each corpus asset, plus visual captures and sampled animation poses. | The corpus shows material fan-out, geometry density, skinning, blendshape, hair, special-region, and runtime-reference cases. | Open | none |
| Analyzer accuracy | Compare analyzer metrics with imported data and renderer counters on representative avatars. | They match. The report explains why the 62-material avatar is expensive with lights disabled. | Open | none |
| Editor panel smoke | Analyze and plan one corpus avatar in the Avatar Optimizer panel. | Users can analyze and plan without creating an optimized copy. | Open | none |
| Material and texture visual check | Render before and after thumbnails for atlas-consolidated materials. | Output stays within profile visual thresholds. Alpha coverage, color space, and sampler behavior are kept. | Open | none |
| Geometry visual check | Render before and after thumbnails and error heatmaps for simplified meshes. | Output is renderer-ready, animation-safe, and within thresholds. | Open | none |
| Deformation check | Compare expression poses and the animation sample set before and after. Compute maximum and average position and normal error and skinning and blendshape heatmaps. Check bounds at extreme poses. | Output stays within profile error. Validation never passes on bind pose alone. | Open | none |
| Spring-bone and blendshape avatars | Run a visual check on blendshape-heavy and spring-bone corpus avatars. | Facial tracking, blink, viseme, expression controls, and spring chains still work. | Open | none |
| LOD and meshlet variants | Check topology, bounds, materials, textures, UVs, tangents, weights, bone indices, and blendshape references per LOD. Check LOD transitions in mono and VR. Check profiler counters for source versus optimized. Check shader prewarm. Render generated meshlets through the meshlet path and the fallback. | Variants are valid, selectable, cooked, and renderer-ready. | Open | none |
| Variant identity and invalidation | Publish a generated variant, change the source or profile, and inspect diagnostics. | The generated avatar variant has identity, invalidation, and debug data separate from the source import. | Open | none |
| Publish and select | Publish a variant in the editor and switch runtime selection. | No asset identity confusion occurs. Warm loads do not repeat optimizer work. | Open | none |
| Release performance | Capture Release data for source and optimized variants of the 62-material avatar. | Output meets the performance target set for that avatar. | Open | none |
| Cluster path quality | Compare close-up cluster rendering with the optimized LOD reference. Exercise VR stereo transitions, head motion, customization changes, streaming under fast camera motion, fallback to LODs, and no current-frame readback. | Output is correct. Missing cluster data degrades quality conservatively instead of stalling or popping to invalid geometry. | Open | none |
| Cluster cost | Profile at 90 Hz. | Engineering targets: cull, select, and compact about 0.3 ms; skinning about 0.5 ms per 50K active vertices; raster about 1.0 ms close-up; material tile shading about 1.0 ms; total close hero avatar about 3 ms. The profiler explains whether cost follows pixels, clusters, shading, or skinned vertices. | Open | none |
| Distant crowd baseline | Record triangle and impostor cost and identity quality for at least 50 unique avatars. | Baseline data is recorded. | Open | none |
| Distant crowd budget | Run 50 or more unique avatars at 90 Hz stereo on target desktop hardware after foveation or VRS, culling, and pruning. | The run reaches about 1 ms as an engineering goal. Crowds degrade gracefully without frame spikes or generic placeholder swaps. | Open | none |
| Splat quality | Compare splats against high-detail renders across the sample pose set and customization values. | Identity and motion stay within the profile threshold. | Open | none |
| Splat transitions | Transition between triangle or cluster LOD and splats under VR head motion. Compare with the octahedral impostor fallback. | Both eyes always see the same representation. Depth and velocity match. | Open | none |

## Hardware Matrix

| Area | Hardware or runtime | Required coverage | Last evidence |
|---|---|---|---|
| OpenXR body calibration | SteamVR OpenXR with headset, controllers, and body trackers | Tracker streaming, calibration, body motion, loss, recovery, and spectator output. | Partial. HTCX enumeration failed on the recorded local probe. |
| OpenXR smoke | SteamVR and Monado where available | Scripted smoke checks plus active runtime diagnostics. | none |
| Spectator output | Vulkan and OpenGL | Headset and desktop or texture output together. | Partial Vulkan and OpenGL evidence. Spectator-only cost is not isolated. |
| VR full-body estimation | 3-point, 4-point, 5-point, and 6-point setups | Estimator quality, handoff, posture, and cost. | none |
| Avatar optimization | Target desktop VR hardware | Corpus quality, Release performance, cluster cost, and distant crowd cost. | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Role-free tracker streaming on SteamVR OpenXR | HTCX enumeration returns zero tracker paths. | Decision on a SteamVR tracker provider in the [VR full-body estimation todo](../../todo/avatar/vr-full-body-estimation-todo.md#decisions-needed). |
| Spectator output on Vulkan and OpenGL | Overbright avatar and straight garment geometry in the full-scene Vulkan readback. | [Implementation and validation record](../../progress/avatar/openxr-full-body-calibration-spectator-implementation.md). |
