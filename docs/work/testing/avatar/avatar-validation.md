# Avatar Validation

[Work docs index](../../README.md) · [Testing docs](../README.md)

Architecture: [OpenXR body tracking and spectator integration](../../../developer-guides/vr/openxr-body-tracking.md), [Local VR body calibration](../../../developer-guides/vr/full-body-calibration.md), [Independent VR spectator](../../../developer-guides/vr/spectator-camera.md), [OpenXR runtime](../../../developer-guides/vr/openxr-runtime.md#steamvr-vive-trackers), [Avatar optimization design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
Code todos: [VR full-body estimation](../../todo/avatar/vr-full-body-estimation-todo.md), [Avatar optimization roadmap](../../todo/avatar/avatar-optimization-roadmap.md)
Related validation: [SteamVR OpenXR hardware validation](../xr/openxr-steamvr-hardware-validation.md), [Windows calibration and spectator validation record](../../investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md), [Animation Validation](../animation/animation-validation.md) (humanoid body and root)

## Setup

### Configuration record

For each VR run, record the implementation commit; headset, controller and tracker models and tracker count; GPU and driver; Windows version; SteamVR and OpenXR runtime versions; active runtime manifest; tracker extension revision; graphics backend; stereo view mode; and any application settings that affect calibration or spectator output. Keep logs, normalized smoke summaries, test results, and short visual evidence with the run. Record failures and unsupported behavior by configuration. Update the [SteamVR hardware record](../xr/openxr-steamvr-hardware-validation.md) with the same configuration and findings. Software tests and renderer compilation do not count as hardware acceptance.

### SteamVR OpenXR smoke runner

Select `SequentialViews` in the existing rendering setting for the initial isolation pass; it is not a smoke-runner flag. Run the primary Vulkan lane and the separate OpenGL diagnostic lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer OpenGL
```

Explicit `VR.Mode=OpenXR` must expose failure rather than silently switching to OpenVR. Use the [OpenXR runtime guide](../../../developer-guides/vr/openxr-runtime.md) for runtime and loader diagnostics. Consume the existing headset sequential and single-pass stereo evidence where it matches the tested configuration; otherwise record the missing lane.

### Avatar optimization

- Build and run an isolated editor session with `Tools/Manage-McpEditorSession.ps1`. Keep captures under the active `Build/_AgentValidation/<run>/mcp-captures/` root.
- Corpus (owner decision pending in the roadmap): the observed 1.08M triangle and 62 material avatar, a simple humanoid with clean materials, a hair-card-heavy avatar, a blendshape-heavy face avatar, a VRM avatar with spring bones, an accessory-heavy avatar, and an intentionally broken import.
- Animation sample set: bind pose, T-pose, A-pose, walk, run, jump apex, arms overhead, arms behind back, extreme crouch or sit, facial range-of-motion sweep.

## Checks

### OpenXR body calibration and spectator

Procedures:

- **Tracker transport.** Start with all intended trackers connected. On SteamVR's OpenXR runtime, capture extension revision, enumeration, action bindings, current pose activity and validity, identity, and sample times for every tracker. Repeat with default and duplicate SteamVR role mappings, a disabled tracker, occlusion, power cycling, and reconnection. Each physical tracker must appear once, stream through its persistent-path subaction, and keep its identity across role changes. A nonempty enumeration alone does not prove pose streaming. A tracker discovered after action attachment must report that a player-initiated VR restart is necessary. If a tracker cannot stream independent of its role mapping, record the runtime version and the exact enumeration, binding, and pose failure.
- **Calibration flow.** On every available controller interaction profile, exercise open, head-tilt refusal, stationary capture with both triggers or both select buttons, cancel, retry, and recalibration. Each gesture works while the desktop editor camera has focus, needs no keyboard, and has no mute side effect. Eye-level messages, tracker labels, markers, footprints, and the T-pose are readable in the headset. Missing or stale headset or controller samples refuse capture and keep the last committed rig. A changed avatar requests recalibration.
- **Tracker binding and body motion.** Use six points and the largest tracker set, up to eleven. Include default SteamVR state and trackers swapped between sessions. Proximity assigns each tracker to at most one slot and leaves distant extras unassigned; crossing feet after capture does not rebind. Check head, hand, hip, foot, chest, upper-arm, and knee motion, repeated calibration, and no origin snaps, duplicate targets, or accumulated offsets. Compare standing, head glances, body turns, crouching, either foot lifted, and room-scale walking. Height and arm-span settings drive avatar scale without changing tracking-space meters; an unset or implausible measurement shows a notice and infers nothing.
- **Grip-to-wrist preset.** For each physical controller profile, record the controller model, interaction-profile path, measured grip-to-wrist translation and rotation, the measurement source, and hand alignment across several wrist poses. The shared geometric default is an unqualified estimate.
- **Loss and recovery.** Exercise tracker occlusion, loss, recovery, power cycling, and a different tracker replacing a bound one. The last target may hold briefly and fade, but never snaps to the origin or passes to the replacement. Exercise controller loss, headset removal, dashboard focus, recenter, teleport, runtime and session restart, and avatar change. Check validity, input routing, event resets, and recalibration when the reference-space relationship is unknown.
- **Editor VR toggle.** Toggle OpenXR off and on and recreate the VR pawn. Record whether provider generation and reference-space basis are unchanged. Committed bindings and offsets restore only when avatar, measurement, provider generation, reference basis, and physical tracker identities match. Missing trackers leave slots empty. Otherwise require recalibration. A synthetic same-generation restore does not prove hardware toggle continuity.
- **Spectator image.** With headset rendering active, inspect the headset image and the third-person desktop or render-texture output together. Check full-avatar visibility, follow direction and body yaw independent of head glances, obstruction recovery, teleport and camera-mode resets, orientation, aspect ratio, color space, post-processing, and per-view visibility, on Vulkan and OpenGL. Switching desktop cameras, changing spectator cadence, holding an output lease, and desktop focus never take VR input, change the tracking origin, alter eye cameras, or move the audio listener. A slower cadence may reuse a frame but never delays headset submission.
- **Spectator performance.** Measure headset frame time, missed deadlines, allocations, and spectator CPU and GPU cost with the spectator off and on. Record values and scene settings. Source inspection or synthetic fence tests do not count.
- **Optional features.** If an encoder or capture integration is added, validate bounded queues, readback lifetime, timestamps, audio alignment, and fall-behind behavior. If a calibration mirror is added, validate it in the headset with input.

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
| Headset performance with spectator | Spectator performance. | Measured values are recorded; spectator cost is isolated. | Open | Short physical OpenGL Debug samples missed every sampled deadline. Allocation rates included editor and MCP work. Spectator-only cost is not isolated. |
| Evidence record | Attach logs, smoke summaries, test results, and short visual evidence. Update the hardware record with the commit, configuration, and remaining failures. | One named configuration has full evidence. | Open | A later physical retry returned `ErrorFormFactorUnavailable` before graphics startup. |
| Milestone acceptance | All rows above on one named configuration. | OpenXR renders first-person while controller input stays with the VR player; no SteamVR role is read, required, or shown to players; avatar scale comes only from the player's measurement. | Open | none |

### VR full-body estimation

Procedure for this group: live sessions through the spectator view, with trackers hidden or powered off mid-session. Optional recorded sessions stay under `Build/_AgentValidation/`; no test depends on them.

- [ ] Hardware tuning matrix. Procedure: tune and observe at 3 points, 4 points (hips only), 5 points (feet only), and 6 points. Expected: planted feet move less than 1 cm and never go below the floor; knees never hyperextend or invert; the pelvis stays over the support area while standing; handoff crossfades have no single-frame jump beyond the bound. Last evidence: none.
- [ ] Ground-truth baseline. Procedure: recorded sessions with real trackers, replayed with those trackers hidden. Expected: hips and feet error is reported per behavior. The first baseline sets the thresholds; later runs must not regress. Last evidence: none.
- [ ] Cost budget. Procedure: profile estimation per avatar. Expected: within the per-avatar budget with zero per-frame heap allocation. Last evidence: none.
- [ ] Postures. Procedure: crouch, bow, rise, kneel, walk room-scale, turn in place, snap turn, artificial locomotion, sit, and lie in each orientation. Expected: plausible, stable poses with no foot sliding or pops. Last evidence: none.
- [ ] Evidence record. Procedure: record hardware evidence with the calibration acceptance results above. Expected: one named configuration has full evidence. Last evidence: none.

### Avatar optimization

- [ ] Baseline. Procedure: record draws, submeshes, material slots, textures, texture memory, vertices, triangles, bones, influences, blendshapes, shader variants, and renderer cost for each corpus asset, plus visual captures and sampled animation poses. Expected: the corpus shows material fan-out, geometry density, skinning, blendshape, hair, special-region, and runtime-reference cases. Last evidence: none.
- [ ] Analyzer accuracy. Procedure: compare analyzer metrics with imported data and renderer counters on representative avatars. Expected: they match. The report explains why the 62-material avatar is expensive with lights disabled. Last evidence: none.
- [ ] Editor panel smoke. Procedure: analyze and plan one corpus avatar in the Avatar Optimizer panel. Expected: users can analyze and plan without creating an optimized copy. Last evidence: none.
- [ ] Material and texture visual check. Procedure: render before and after thumbnails for atlas-consolidated materials. Expected: within profile visual thresholds; alpha coverage, color space, and sampler behavior kept. Last evidence: none.
- [ ] Geometry visual check. Procedure: before and after thumbnails and error heatmaps for simplified meshes. Expected: renderer-ready, animation-safe, within thresholds. Last evidence: none.
- [ ] Deformation check. Procedure: compare expression poses and the animation sample set before and after; compute max and average position and normal error and skinning and blendshape heatmaps; check bounds at extreme poses. Expected: within profile error. Validation never passes on bind pose alone. Last evidence: none.
- [ ] Spring-bone and blendshape avatars. Procedure: visual check on blendshape-heavy and spring-bone corpus avatars. Expected: facial tracking, blink, viseme, expression controls, and spring chains still work. Last evidence: none.
- [ ] LOD and meshlet variants. Procedure: check topology, bounds, materials, textures, UVs, tangents, weights, bone indices, and blendshape references per LOD; check LOD transitions in mono and VR; check profiler counters for source versus optimized; check shader prewarm; render generated meshlets through the meshlet path and the fallback. Expected: valid, selectable, cooked, renderer-ready variants. Last evidence: none.
- [ ] Publish and select. Procedure: publish a variant in the editor and switch runtime selection. Expected: no asset identity confusion; warm loads do not repeat optimizer work. Last evidence: none.
- [ ] Release performance. Procedure: Release captures for source and optimized variants of the 62-material avatar. Expected: meets the performance target set for that avatar. Last evidence: none.
- [ ] Cluster path quality. Procedure: compare close-up cluster rendering with the optimized LOD reference; VR stereo transitions and head motion; customization changes; streaming under fast camera motion; fallback to LODs; no current-frame readback. Expected: correct output and safe fallback. Last evidence: none.
- [ ] Cluster cost. Procedure: profile at 90 Hz. Expected targets (engineering goals): cull, select, and compact about 0.3 ms; skinning about 0.5 ms per 50K active vertices; raster about 1.0 ms close-up; material tile shading about 1.0 ms; total close hero avatar about 3 ms. The profiler explains whether cost follows pixels, clusters, shading, or skinned vertices. Last evidence: none.
- [ ] Distant crowd baseline. Procedure: triangle and impostor cost and identity quality for at least 50 unique avatars. Expected: recorded. Last evidence: none.
- [ ] Distant crowd budget. Procedure: 50+ unique avatars at 90 Hz stereo on target desktop hardware after foveation or VRS, culling, and pruning. Expected: about 1 ms (engineering goal); graceful degradation with no frame spikes or generic placeholders; identity thumbnails at target distance show each avatar. Last evidence: none.
- [ ] Splat transitions. Procedure: transition between triangle or cluster LOD and splats under VR head motion; compare with the octahedral impostor fallback. Expected: no per-eye disagreement; matched depth and velocity. Last evidence: none.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Role-free tracker streaming on SteamVR OpenXR | HTCX enumeration returns zero tracker paths. | Decision on a SteamVR tracker provider in the [VR full-body estimation todo](../../todo/avatar/vr-full-body-estimation-todo.md#decisions-needed) |
| Spectator output on Vulkan and OpenGL | Overbright avatar and straight garment geometry in the full-scene Vulkan readback. | [Implementation and validation record](../../progress/avatar/openxr-full-body-calibration-spectator-implementation.md) |

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/avatar/openxr-full-body-calibration-spectator-todo.md`

- [ ] **W60.17 ΓÇö Optional** Offer the spectator output as an eye-level mirror during calibration, so players can check their stance without looking down.
- [ ] **W80.07** Test standing, looking around independently of body yaw, turning, crouching, lifting either foot, crossed feet after calibration, and room-scale walking.
- [ ] No SteamVR role is read, required, or mentioned to players; any runtime limitation is documented as such.
