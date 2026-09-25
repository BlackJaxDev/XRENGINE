# OpenXR VR, Full-Body Calibration, and Third-Person Spectator Capture — TODO

**Project:** XRENGINE  
**Created:** September 23, 2026  
**Review baseline:** `master` at [`4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63`][baseline] — “More Vulkan work”  
**Status:** Baseline harness implemented and target-loss regression reproduced; production target ownership and remaining integration work are pending.  
**Baseline evidence:** [Six-device calibration baseline](../../investigations/avatar/vr-calibration-baseline-2026-09-24.md).

> **Required assignment policy:** XREngine determines which physical trackers drive the hips, left foot, and right foot. Do not use SteamVR's tracker body roles by default. Support SteamVR role mapping only as an explicit, opt-in override. A runtime role may be transport metadata without becoming the avatar's body assignment.

## 1. Goal and scope

Deliver a local-player experience in which:

- OpenXR renders the headset's first-person stereo view.
- One headset, two controllers, and three additional trackers drive six avatar targets: head, left hand, right hand, hips, left foot, and right foot.
- The player can identify trackers, calibrate, confirm, cancel, and recalibrate entirely in-engine.
- Engine-owned automatic or manual assignment works independently of SteamVR's body-role labels.
- A separate, monoscopic third-person camera follows the avatar and produces a desktop or render-texture spectator POV without replacing the headset view.
- Tracking loss, reconnection, recentering, and repeated calibration do not corrupt the rig or silently reassign body parts.

This is an integration-and-correctness milestone, not a new OpenXR implementation or a new full-body IK solver. Existing rendering, input, calibration, IK, camera, and smoothing components should be repaired and reused. The preceding source review did not build or run this revision and did not establish a physical-headset pass. Recheck the relevant call sites against the implementation branch before changing them. [S1][frame-lifecycle] [S2][input-core] [S3][calibrator] [S4][bootstrap-pawns]

### Initial milestone exclusions

GPU IK, a replacement animation system, additional chest/elbow/knee trackers, networking changes, quad-view/foveation expansion, and a built-in video encoder are not prerequisites. Preserve compatibility, but do not expand the first milestone into those projects. Producing a spectator POV and encoding that POV into a video file are separate acceptance items.

## 2. Non-negotiable architecture decisions

### 2.1 Separate tracking transport, device identity, and body assignment

Treat these as three different layers:

| Layer | Responsibility | Must not be used as |
|---|---|---|
| Pose transport | Obtain a current pose from an OpenXR action space or another explicitly selected provider | Implicit authority over the avatar's hips/foot assignment |
| Physical device identity | Recognize the same tracker across supported reconnects and sessions | A transient device-array index or body-role label |
| Engine body assignment | Bind a selected physical tracker to `Hips`, `LeftFoot`, or `RightFoot` | A silent mirror of SteamVR settings |

The existing OpenXR implementation tracks both persistent and role paths, but its canonical-path resolution prefers a role when available. The scene collection is keyed by that user path. This needs a stable physical-identity layer before saved engine mappings can reliably survive role changes. [S5][input-neutral] [S6][tracker-collection]

**Do not conflate “ignore SteamVR roles for assignment” with “every runtime can provide every unassigned tracker pose.”** Validate role-free pose acquisition on the intended runtime. The HTCX extension provides tracker path discovery and connection/role-change events; that alone does not prove the target runtime will locate every desired persistent-path binding. Keep this as an explicit compatibility gate, not an assumption. [XR1][htcx-spec] [XR2][htcx-event]

If the target runtime cannot expose usable unassigned-tracker poses through the selected path, report that limitation separately from assignment. Do not quietly force matching SteamVR body roles, enable the role override, or switch the renderer to OpenVR. An alternative tracker-only provider is a separately approved compatibility option, with reference-space and timing reconciliation required before use.

### 2.2 Assignment defaults and overrides

**Default:** `EngineAutomatic`, using engine calibration geometry and player confirmation.  
**Alternative:** `EngineManual`, using explicit in-engine tracker selection or guided identification.  
**Optional override:** use a chosen SteamVR role to select a body slot only after the player enables that override.

Proposed settings below describe intended behavior; they are not claims that these property names already exist:

| Proposed setting | Default | Meaning |
|---|---|---|
| `TrackerAssignmentMode` | `EngineAutomatic` | Automatic, confidence-gated identification; manual assignment remains available |
| `EnableSteamVrRoleOverrides` | `false` | Enables an explicit role-based selection override |
| `TrackerSlotOverrides` | Empty | Per-slot manual physical-device selection or opt-in runtime-role override |
| `RequireSixPointCalibration` | `true` for this milestone | Do not claim full-body calibration with a missing required device |
| `TrackerPoseProvider` | OpenXR | Keep pose-source selection separate from assignment policy |
| `AllowTrackerProviderFallback` | `false` | No hidden OpenVR or other-provider fallback |
| `SpectatorEnabled` | Configurable | Separate monoscopic output; never changes headset ownership |

Each body slot has one selection authority. A manual device lock and a SteamVR role override cannot simultaneously own the same slot. Reserve explicitly selected devices first, then solve automatic assignment for the remaining slots. Reject conflicts rather than resolving them through undocumented precedence.

A newly observed SteamVR role change must not change an engine-owned assignment. An explicitly role-overridden slot may respond to the role change, but switching physical devices requires an explicit rebind/recalibration decision; do not reuse the old device's mount offset on a different tracker.

### 2.3 Separate raw devices from calibrated avatar targets

Maintain distinct references for:

1. Raw HMD/controller/tracker poses and physical identities.
2. Committed device-to-avatar calibration offsets.
3. Six concrete, stable avatar target transforms consumed by IK.

The raw tracking nodes must keep their VR transform behavior. Do not replace them with ordinary `Transform` nodes just to satisfy a cast. Conversely, do not let per-frame target synchronization replace calibrated solver targets with raw devices.

### 2.4 Keep the spectator independent

The headset's eye cameras remain OpenXR-controlled. The spectator has its own monoscopic camera, output ownership, visibility, temporal state, and follow behavior. Selecting the spectator as desktop output must not transfer controller-input possession or alter the tracking origin.

## 3. Source-review findings to reproduce

These are findings from the cited baseline, not a claim that every symptom was reproduced at runtime.

| ID | Finding | Evidence and required response |
|---|---|---|
| F01 | Calibrated targets can be cleared by the next solver synchronization | `VRDeviceTransformBase` inherits `TransformBase`; the IK helper casts humanoid target references to `Transform`. The player stores raw device targets, while the calibrator assigns concrete children directly to the solver. `SyncSolverTargets()` then repopulates the solver from the humanoid slots. Reproduce and fix target ownership. [S7][device-transform] [S8][ik-base] [S9][vr-solver] [S10][player-character] [S3][calibrator] |
| F02 | Nearest-body-part assignment is not a one-to-one six-point assignment | `FindNearestTrackerTargets()` independently selects among hips, chest, feet, elbows, and knees using a fixed radius. A later tracker can overwrite a body slot. Replace this with engine-owned, one-to-one assignment; do not replace it with default SteamVR role mapping. [S10][player-character] |
| F03 | Discovery metadata and current pose availability can disagree | Tracker metadata keeps availability true once either the previous or current value is true, whereas pose caches are rebuilt. Raw device transforms can fall back to an offset/identity when pose lookup fails. Separate current validity from history and prevent origin snaps. [S5][input-neutral] [S2][input-core] [S7][device-transform] |
| F04 | Tracker collection maintenance is add/update oriented | The collection does not reconcile obsolete entries in its OpenXR refresh method. Deactivation clears dictionaries without destroying owned tracker child nodes in that method. Define ownership, disconnect retention, and cleanup. [S6][tracker-collection] |
| F05 | Calibration is not transactional | The player enables the solver and discards the calibrator's returned result. Cancel restores targets and then recalibrates. The fence wait result is ignored. Replace this with explicit success/failure and rollback. [S10][player-character] [S3][calibrator] [S11][runtime-calibrator] |
| F06 | Calibration and input routing still contain test-scene wiring | Editor locomotion initialization toggles calibration from `IsMutedChanged`; the VR/editor possession path also has an input-routing TODO. Add a dedicated calibration action and explicit player routing. [S12][editor-pawns] |
| F07 | OpenXR rendering exists, but the documented physical hardware matrix is pending | Frame submission and swapchain handling exist. Strict single-pass mode deliberately rejects unsupported execution instead of silently falling back. Establish a sequential-view hardware baseline, then validate single pass independently. [S1][frame-lifecycle] [S13][runtime-guide] [S14][hardware-validation] |
| F08 | The existing third-person toggle is a desktop-pawn feature, not a VR spectator-follow feature | VR construction provides first-person desktop output and an optional pickup camera. Add an independent follow rig and connect it to the chosen output. [S12][editor-pawns] [S4][bootstrap-pawns] |

## 4. Execution order

| Work package | Priority | Dependencies | Exit condition |
|---|---|---|---|
| W00 — Baseline and regression harness | P0 | None | Current call sites identified and a repeatable six-device test scene exists |
| W10 — Calibrated target ownership | P0 | W00 | All six targets survive repeated solver updates |
| W20 — Tracker identity, acquisition, and validity | P0 | W00 | Distinct devices have honest current pose state and tested provider behavior |
| W30 — Engine-owned assignment | P0 | W20 | Automatic/manual assignment works without semantic dependence on SteamVR roles |
| W40 — Transactional player calibration | P1 | W10, W30 | Confirm/cancel/retry produces a valid rig or restores the previous one |
| W50 — Pose timing and movement integration | P1 | W10, W20, W40 | Tracking, locomotion, IK, and render consumers agree on spaces and timing |
| W60 — Third-person spectator output | P1 | W50 for final integration | Independent follow POV renders without affecting headset/input ownership |
| W70 — Saved profiles and restore | P2 | W20, W30, W40 | Valid profiles restore safely; incompatible profiles request recalibration |
| W80 — Hardware, regression, and documentation gate | Release gate | Relevant packages above | Evidence establishes the complete experience on the intended hardware |

W10 and W20 may proceed independently after W00. Camera-follow mechanics may be developed in a synthetic scene in parallel, but final spectator acceptance depends on the calibrated avatar and pose-publication work.

## 5. W00 — Establish the baseline and test harness

**Primary files:** [player component][player-character], [VRIK solver][vr-solver], [humanoid IK base][ik-base], [calibrator][calibrator], [editor pawn factory][editor-pawns], [runtime pawn factory][bootstrap-pawns].

- [x] **W00.01** Record the actual implementation commit and compare the affected call sites with the reviewed baseline. Update findings that have already changed; do not patch stale line numbers or duplicate an existing fix.
- [x] **W00.02** Build a minimal scene with a valid humanoid, VRIK solver, playspace, HMD, two controllers, and exactly three synthetic body trackers.
- [x] **W00.03** Give synthetic devices deterministic identities and configurable pose validity, timestamps, rotations, and connection state. Do not require an OpenXR runtime for unit-level assignment and calibration tests.
- [x] **W00.04** Reproduce F01 by calibrating, advancing several solver ticks, and asserting the identity and non-null state of every target. Preserve this as a regression test.
- [x] **W00.05** Record target counts, avatar/root scale, tracking origin, and component activation state before and after calibration. Add assertions for leaked or duplicated target nodes.
- [x] **W00.06** Audit both pawn-construction paths. Decide which shared runtime factory/service owns the new integration so editor and runtime behavior do not drift.

Implemented by `SyntheticVrCalibrationRig`, `SyntheticVrDeviceTransform`, and `VRIKCalibrationTests` in `XREngine.UnitTests/Animation/`. The required persistence regression is explicit and currently fails; ordinary characterization/control tests pass. The baseline run also exposed and repaired a settings-type alias collision that prevented the runtime bridge from finding the calibration entry point. W10 remains unchecked: neither target persistence nor duplicate-node cleanup is fixed by this harness.

**Acceptance:** a deterministic test exercises calibration and subsequent pose solving, not just a successful calibrator return or the presence of method names in source text.

## 6. W10 — Repair calibrated-target ownership and offsets

**Primary files:** [player component][player-character], [humanoid component][humanoid], [humanoid IK base][ik-base], [VRIK solver][vr-solver], [calibrator][calibrator], [runtime calibrator bridge][runtime-calibrator].

- [ ] **W10.01** Introduce or reuse one avatar-rig owner for six stable concrete `Transform` targets: head, left hand, right hand, hips, left foot, and right foot.
- [ ] **W10.02** Store raw device references separately from those target transforms. A raw pose source must not be installed in a slot that the solver expects to be a concrete calibrated target.
- [ ] **W10.03** Make successful calibration publish its authoritative targets into `HumanoidComponent`, or otherwise make `SyncSolverTargets()` consume the same authoritative target store. Remove the current split ownership.
- [ ] **W10.04** Choose one offset representation per target: either a child target with the calibration in its local transform, or an explicitly updated target with a stored offset. Do not apply both the child offset and a non-identity humanoid tuple offset.
- [ ] **W10.05** Keep raw HMD/controller/tracker node types unchanged. Do not fix the issue merely by weakening a cast while leaving target semantics inconsistent.
- [ ] **W10.06** Ensure target reuse/reparenting follows a new device binding correctly. Recalibrating onto a different tracker must not leave an existing target under the previous tracker.
- [ ] **W10.07** Keep solver targets stable across initialization, activation, calibration, normal updates, and avatar rebinds. Explicitly clear invalidated targets during teardown.
- [ ] **W10.08** Return a typed success/failure result and calibrated target data through the runtime bridge, within the existing assembly dependency direction. Do not let a non-null reflection invocation or an enabled component stand in for calibration success.
- [ ] **W10.09** Validate matrices, scales, and quaternions before committing them. Reject non-finite values, singular transforms, zero-length orientations, and invalid scale ratios.
- [ ] **W10.10** Test nonzero tracker mount offsets while each device translates and rotates. Include feet mounted with substantially different tracker orientations.

### Offset convention to preserve

For the existing row-vector matrix convention, a target reconstructed from a raw device pose should follow:

```text
TargetWorld(t) = DeviceToTargetOffset * DeviceWorld(t)
DeviceToTargetOffset = DesiredTargetWorldAtCalibration * inverse(DeviceWorldAtCalibration)
```

A target parented directly to the raw device can store this offset as its local transform, provided the parent relationship and scale policy are consistent. If that child already applies the offset, the corresponding humanoid target tuple should use identity. Keep avatar scaling separate from the tracking-space metric basis and test transformed playspaces explicitly. The reviewed helper already multiplies `target.offset * target.RenderMatrix`; avoid reversing or duplicating that convention. [S8][ik-base]

**Acceptance:** all six targets remain bound for multiple frames; the expected target world matrices match actual transforms under translation, rotation, and a non-origin playspace; repeated calibration does not accumulate offsets, scale, or nodes.

## 7. W20 — Make tracker acquisition, identity, and validity reliable

**Primary files:** [OpenXR core input][input-core], [runtime-neutral OpenXR input][input-neutral], [OpenXR state][xr-state], [VR state contract][vr-state-contract], [tracker collection][tracker-collection], [tracker transform][tracker-transform], [device transform base][device-transform].

### Physical identity and transport

- [ ] **W20.01** Introduce a runtime-neutral tracker identity distinct from the native pose lookup key. Prefer a provider-qualified persistent identity when available; mark nonpersistent identities as session-local.
- [ ] **W20.02** Keep role paths as optional metadata or transport aliases, not canonical physical identity. Do not serialize a numeric `XrPath`, synthetic device index, or collection order as a persistent device identity.
- [ ] **W20.03** Reconcile role and persistent aliases so the same physical tracker appears exactly once. When pose transport is role-based, resolve the current physical-device association before publishing the pose snapshot.
- [ ] **W20.04** Validate that the target runtime can discover and locate all three trackers without requiring matching SteamVR body-role assignments. Record extension availability, enumeration results, supported bindings, action activity, and pose-location results.
- [ ] **W20.05** Verify native action/action-space lifecycle for persistent paths and late connections. Adding a string to `_trackerSubactionPaths` is not evidence that a usable native action space and binding now exist. Follow supported lifecycle rules and report any controlled session rebuild requirement.
- [ ] **W20.06** Expose a clear capability result: supported role-free acquisition, role-addressed transport only, or unavailable tracker poses. Do not claim full default-flow completion merely because role names were ignored after obtaining poses through a setup that still requires them.
- [ ] **W20.07** If role-free acquisition is unsupported on the target setup, document the concrete blocker and any separately approved alternative provider. Keep headset rendering and controller input on OpenXR; never introduce a hidden provider fallback.

### Current pose state and lifecycle

- [ ] **W20.08** Replace sticky availability with current state: connected/known, action active, position valid, orientation valid, tracking flags where available, sample time, frame/snapshot ID, and last-valid sample.
- [ ] **W20.09** Separate `EverTracked` diagnostics from `PoseCurrentlyUsable`. Recompute current state when each pose snapshot is published; a failed location must clear current usability.
- [ ] **W20.10** Audit head/controller validity along with tracker validity. In particular, do not interpret an always-successful cached HMD accessor as proof of a fresh tracked calibration pose. [S15][xr-state]
- [ ] **W20.11** Prevent unavailable real devices from falling through to identity/manual-offset poses during normal gameplay. Keep explicit synthetic/debug pose behavior separate from real-device failure handling.
- [ ] **W20.12** Define disconnect retention: keep the physical identity and saved assignment, mark the device unavailable, and disable/hide relevant live visuals as appropriate. Remove/destroy owned nodes only through explicit lifecycle rules.
- [ ] **W20.13** Reconcile collection membership, native handles, and owned scene nodes across reconnect, role changes, component reactivation, and runtime/session recreation. Do not accumulate duplicate tracker nodes.
- [ ] **W20.14** Publish immutable or safely double-buffered tracking snapshots. Marshal scene-graph additions/removals onto the scene owner instead of mutating live scene collections from runtime callbacks.
- [ ] **W20.15** Distinguish “not discovered,” “discovered but unbound,” “bound but inactive,” “pose stale,” and “tracking lost” in diagnostics. Do not recommend assigning SteamVR body roles as the default remedy for every error.

**Acceptance:** three unique physical trackers produce current, correctly associated poses; current validity becomes false when tracking is lost; reconnect preserves identity; role changes cannot change engine assignment; the role-free transport capability is demonstrated or explicitly reported as blocked.

## 8. W30 — Implement engine-owned, one-to-one body assignment

**Primary files:** [player component][player-character], [tracker collection][tracker-collection], [tracker transform][tracker-transform], plus a focused assignment service in the existing runtime/input integration layer.

### Automatic assignment

- [ ] **W30.01** Replace independent nearest-body-part selection with assignment across exactly the required slots: hips, left foot, right foot. Do not allow the three-tracker default workflow to consume a tracker as chest, elbow, or knee.
- [ ] **W30.02** Start from a coherent snapshot of distinct, current, eligible physical trackers. Exclude controllers, duplicates, unavailable devices, and unrelated props. Require explicit candidate selection when more than three plausible body trackers are present.
- [ ] **W30.03** Define an in-engine calibration stance and a stable player-relative frame. Use the tracking floor/up direction and a player-confirmed facing direction or validated head/body heading; never assume world X is always player-left/right.
- [ ] **W30.04** Compute expected hip and foot regions from the aligned calibration pose and valid avatar measurements. Use configurable, scale-aware tolerances; do not retain the old fixed 0.25-unit cutoff as the sole criterion.
- [ ] **W30.05** Score a complete one-to-one assignment using positional fit, height, lateral separation, and stability. Use attachment orientation only where it is a validated mount constraint, not as an assumed body-role label.
- [ ] **W30.06** For exactly three selected trackers, evaluate all six permutations after applying explicit slot reservations. Reject impossible assignments before choosing the lowest-cost valid assignment.
- [ ] **W30.07** Require an adequate confidence margin between the best and next-best assignment. Close feet, crossed legs, unusual proportions, or an unclear facing direction should trigger guided/manual identification, not a silently arbitrary choice.
- [ ] **W30.08** Average or robustly sample a short stationary interval and reject high-motion samples. Preserve the sample timing needed to distinguish stability from stale data.
- [ ] **W30.09** Show the proposed mapping on the avatar and on tracker markers for confirmation. Do not commit while the player is still identifying which device is which.
- [ ] **W30.10** Freeze the committed mapping during gameplay. Crossing feet, crouching, turning around, or moving a tracker near another body part must not trigger proximity reassignment.

### Manual assignment and SteamVR override

- [ ] **W30.11** Provide explicit in-engine selection for each body slot, showing stable tracker identifiers and tracked visual markers. Do not require tracker display names to contain a body-part name.
- [ ] **W30.12** Support guided identification when visual selection is inconvenient: identify one moving device at a time, such as left foot then right foot, then take a fresh neutral calibration snapshot. Reject ambiguous simultaneous movement.
- [ ] **W30.13** Enforce one physical tracker per slot and one slot per tracker in every mode. Give a clear error for manual/automatic/override collisions.
- [ ] **W30.14** Add `EnableSteamVrRoleOverrides = false` by default and an explicit per-slot override UI. When enabled for a slot, map the chosen runtime role to a physical device; do not erase the independent engine assignment profile.
- [ ] **W30.15** Validate duplicate, missing, inactive, or unsupported role overrides. Do not silently fall back to a different role or permanently overwrite an engine-owned mapping.
- [ ] **W30.16** Make enabling/disabling an override an explicit transition. Preserve the previous engine map, preview the resulting change, and require recalibration if the physical device or mount relationship changes.

**Acceptance:** the default assigns the correct hips and feet with empty or deliberately incorrect runtime body-role metadata; changing SteamVR roles leaves the engine map unchanged; manual corrections are stable; the opt-in override follows its documented behavior without duplicate assignments.

## 9. W40 — Make calibration transactional and player-facing

**Primary files:** [player component][player-character], [calibrator][calibrator], [runtime bridge][runtime-calibrator], [editor pawn wiring][editor-pawns], plus the current input-action and VR UI integration points.

### State and ownership

- [ ] **W40.01** Replace the single calibration boolean as the sole state model with explicit states: uncalibrated, preparing, ready, calibrating, calibrated, and recoverable failure. Distinguish tracking degradation from loss of saved calibration.
- [ ] **W40.02** Route begin/confirm/cancel/retry requests onto one scene/simulation owner. Do not block the render/pacing thread waiting for a calibration fence.
- [ ] **W40.03** Remove reliance on the ignored `Wait(100)` result. If a synchronization primitive remains temporarily, make every acquisition/reset exception-safe and handle timeout explicitly.
- [ ] **W40.04** Before entering calibration, snapshot the committed assignment, offsets, target references, avatar scale, solver activation/weights, root-controller state, and other state that calibration may modify.
- [ ] **W40.05** Verify the humanoid bindings and solver initialization before sampling. Report missing head, hips, hand/arm, or foot/leg references rather than treating a partial result as full-body success.
- [ ] **W40.06** Require six valid device poses from one coherent snapshot for the strict full-body mode. A three-point fallback may exist separately, but must not be reported as six-point calibration.
- [ ] **W40.07** Pause competing animation/root-motion writers while establishing the neutral calibration pose. Restore their intended state after commit or rollback.
- [ ] **W40.08** Build the proposed offsets, targets, and scale as temporary calibration state. Validate all outputs before publishing them atomically to the live rig.
- [ ] **W40.09** Treat a null calibrator result, invalid output, missing device, or exception as failure. Do not enable the solver and report success unconditionally.
- [ ] **W40.10** Make cancel restore the previous committed calibration without invoking calibration again. Roll back any changes made to temporary targets or root scale.
- [ ] **W40.11** Make first calibration and recalibration follow the same validated path. Repeated confirmation must not multiply avatar scale, duplicate target children, or accumulate offsets.

### Height, mounts, and UI

- [ ] **W40.12** Establish one authority for height scaling between the height-scale component and VRIK calibration. Validate denominator/range/finite checks and keep tracked real-world units unaffected by avatar scale.
- [ ] **W40.13** Validate eye-to-head and controller-grip-to-wrist offsets independently of tracker body assignment. Support arbitrary supported hip/foot mount rotations by deriving offsets from the confirmed calibration pose rather than assuming one tracker forward/up orientation.
- [ ] **W40.14** Add dedicated begin/confirm/cancel calibration actions through the runtime-neutral input layer. Remove the `IsMutedChanged` calibration hookup; muting must not modify calibration.
- [ ] **W40.15** Provide an in-VR panel with six-device status, assignment preview, stance/facing instructions, confirmation/countdown, cancel, and specific errors. Keep manual assignment reachable from that panel.
- [ ] **W40.16** Ensure the calibration action reaches the VR player when a desktop editor camera has focus or possession. Do not make calibration depend on the spectator or editor camera becoming the player pawn.
- [ ] **W40.17** Expose the last calibration outcome and diagnostics without marking an in-progress attempt as committed success.

**Acceptance:** a player can complete, cancel, fail, and retry calibration without a keyboard; cancellation restores the previous rig; failure does not leave the solver active on invalid targets; muting and camera switching have no calibration side effects.

## 10. W50 — Integrate pose timing, tracking loss, and locomotion

**Primary files:** [VR state contract][vr-state-contract], [OpenXR state][xr-state], [device transform base][device-transform], [player component][player-character], [VRIK solver][vr-solver], [IK tick base][base-ik-solver].

The existing IK base schedules normal/late animation work, while VR device transforms also react to predicted/late runtime updates. Audit the resulting order rather than assuming a calibrated target implies a fresh rendered skeleton. [S16][base-ik-solver] [S7][device-transform]

- [ ] **W50.01** Define the frame order explicitly: publish tracking snapshot, update playspace/locomotion, update calibrated targets, evaluate the avatar/IK, publish renderable pose, then evaluate the spectator anchor from the matching state.
- [ ] **W50.02** Identify the owner and coordinate space of each matrix: runtime reference space, playspace, world, avatar root, raw device, and calibrated target. Remove hierarchy-order assumptions where practical by storing explicit rig references.
- [ ] **W50.03** Keep the simulation snapshot coherent. Do not combine a newly located controller with old hip/foot transforms in a calibration sample.
- [ ] **W50.04** Document how late-located HMD/controller poses relate to the avatar skeleton rendered for that frame. Use a deliberate supported late-pose strategy or documented simulation-pose behavior; never concurrently mutate live bones from the render thread as an ad hoc latency fix.
- [ ] **W50.05** Test locomotion and room-scale movement with nonzero tracker offsets. In particular, audit `MovePlayer()` so a device-to-body offset is not applied once before playspace conversion and again during movement construction. [S10][player-character]
- [ ] **W50.06** Briefly retain last-valid targets during transient loss using a configurable policy, then reduce the affected IK influence or enter a defined fallback. Retained poses must remain invalid for new calibration.
- [ ] **W50.07** Restore target influence smoothly when the same physical tracker returns. Never choose a nearby replacement automatically; request reassignment for a different physical device.
- [ ] **W50.08** Handle reference-space changes/recenter as coordinate-basis events. Apply a known transform coherently or invalidate/recalibrate when the relationship is unknown; do not retain mismatched world-space offsets.
- [ ] **W50.09** Reset relevant smoothing/history on teleport, discontinuous root movement, avatar replacement, or session-generation changes. Do not interpolate the spectator or feet through a large discontinuity.
- [ ] **W50.10** Avoid new recurring allocations, blocking waits, and unsynchronized collection mutation in pose update, assignment reuse, IK target update, and spectator-follow paths. Discovery/UI/calibration allocations must stay off the per-frame path where possible.

**Acceptance:** standing, turning, walking, crouching, and lifting either foot preserve alignment; tracking loss does not collapse the avatar to the origin; recenter and teleport do not apply offsets twice; all view consumers see a consistent published avatar pose.

## 11. W60 — Add the third-person spectator-follow camera

**Primary files:** [runtime pawn factory][bootstrap-pawns], [editor pawn factory][editor-pawns], existing camera/smoothing/boom types, and the existing viewport/output ownership integration.

- [ ] **W60.01** Add a dedicated spectator-follow component or rig associated explicitly with the local VR player. Do not reinterpret the existing desktop-only `ThirdPersonPawn` switch as complete VR support.
- [ ] **W60.02** Build a follow anchor from player-root position and the calibrated torso/hips with world-up and stable body yaw. Do not parent the spectator directly to the headset's full rotation.
- [ ] **W60.03** Provide configurable distance, height, shoulder offset, aim point, field of view, and follow smoothing. In the engine's `-Z` forward convention, a trailing offset is behind the player's chosen forward direction; verify the sign in a test scene.
- [ ] **W60.04** Use frame-rate-independent follow behavior. Reuse existing smoothing components when their update/space semantics fit; do not smooth the OpenXR eye cameras with spectator settings.
- [ ] **W60.05** Add collision avoidance using the existing boom/shapecast pattern where suitable. Exclude the player's own collision body and define behavior when the desired camera position is obstructed.
- [ ] **W60.06** Keep body turns distinct from head glances. Define a stable fallback heading when hips tracking is unavailable, and handle degenerate look-at vectors safely.
- [ ] **W60.07** Give the spectator an independent monoscopic camera and output/viewport. Keep its temporal histories, projection, culling, and mutable pipeline state isolated from both eyes and other cameras.
- [ ] **W60.08** Render the complete local avatar in the spectator view while applying first-person head/body hiding only to the appropriate HMD views. Do not globally deactivate meshes to hide them from the headset.
- [ ] **W60.09** Route desktop output between first-person preview, third-person spectator, and editor camera without changing VR pawn possession, controller routing, HMD camera ownership, or the tracking origin.
- [ ] **W60.10** Retain the HMD listener for player audio by default. A recording-specific listener or audio bus is an explicit optional feature, not a side effect of creating another camera.
- [ ] **W60.11** Produce one clean spectator output suitable for desktop capture and/or a render texture. Reuse that output for previews instead of rendering the same spectator scene redundantly.
- [ ] **W60.12** Add configurable spectator resolution and update cadence. A slower capture cadence may reuse the last completed spectator frame, but must not delay OpenXR frame submission.
- [ ] **W60.13** Respect GPU completion and texture-consumer lifetimes when publishing offscreen output. Do not expose an unfinished target or reuse its storage before outstanding consumers are done.
- [ ] **W60.14** Reset follow interpolation and per-view temporal history on teleport, recenter, camera mode changes, and avatar replacement as appropriate.
- [ ] **W60.15** Validate output orientation, aspect ratio, color space, post-processing, and visibility independently from the headset, especially on Vulkan.

**Acceptance:** the headset remains first-person while the desktop/render texture shows a stable third-person follow POV of the fully tracked avatar. Camera activation, capture rate, and desktop focus do not steal VR input or change headset tracking.

### Optional encoded capture follow-up

- [ ] **W60.16 — Optional** If video recording/export is required, connect an encoder or external capture integration to the completed spectator output. Keep capture queues bounded and avoid blocking GPU readback on the headset submission path. Define frame timestamps, audio alignment, and behavior when encoding falls behind.

This optional item is not required to call a rendered spectator POV complete, and rendering a texture must not be reported as proof of working video encoding.

## 12. W70 — Save and restore engine-owned calibration profiles

**Primary files:** [calibration result/data][calibrator], [player component][player-character], existing persistence/settings infrastructure. The reviewed caller discards `CalibrationData`, and the saved-data application overload in the calibrator is commented out. Implement and test the actual restore path rather than assuming serialization alone completes persistence. [S3][calibrator]

- [ ] **W70.01** Define a versioned calibration profile containing avatar identity/rig compatibility, scale policy, engine slot-to-device mappings, mount offsets, provider-qualified physical identities, and explicit override selections.
- [ ] **W70.02** Save engine mappings independently of SteamVR role metadata. Persist the role-override flag only as an explicit user choice.
- [ ] **W70.03** Do not persist native handles, numeric `XrPath` values, collection indices, or synthetic OpenVR-style indices as stable identifiers.
- [ ] **W70.04** Resolve a saved profile against currently discovered physical devices, validate compatibility, and recreate/rebind targets through the same authoritative target owner used by live calibration.
- [ ] **W70.05** Keep device identity and mounting calibration separate: the same tracker can be reattached differently. Offer a quick recalibration/verification step rather than assuming a reconnect proves the previous mount is unchanged.
- [ ] **W70.06** Treat a missing device, incompatible rig, changed scale convention, invalid data, or changed physical-device binding as a recoverable restore failure. Do not silently substitute a nearby tracker or a similarly named role.
- [ ] **W70.07** Test profile round-trip, engine restart, runtime restart, device reconnection in a different order, override toggling, and schema migration/rejection.

**Acceptance:** compatible profiles restore the same physical bindings and target offsets; incompatible profiles leave the player in a clear uncalibrated/recovery state instead of activating a malformed rig.

## 13. W80 — Validation and release gate

### Automated behavioral coverage

These are proposed tests; add them to the appropriate test projects rather than assuming classes with these names already exist.

- [ ] **W80.01** Target-lifetime regression: calibrate and advance multiple solver ticks; all six target identities and offsets remain correct.
- [ ] **W80.02** Assignment invariance: all six input enumeration orders produce the same correct three-slot assignment for an unambiguous stance.
- [ ] **W80.03** Role independence: blank, incorrect, swapped, and changing SteamVR role metadata produce the same default engine assignment for identical physical poses/identities.
- [ ] **W80.04** Override behavior: explicit role overrides work only when enabled; missing/duplicate overrides fail clearly; disabling overrides restores the preserved engine selection policy.
- [ ] **W80.05** Ambiguity: close/crossed feet, unusual body proportions, unclear heading, moving samples, and extra trackers request guided/manual assignment rather than arbitrary success.
- [ ] **W80.06** Mount transforms: nonzero offsets and differently rotated hip/foot mounts reconstruct the desired target poses without double application.
- [ ] **W80.07** Validity transitions: current pose availability clears on failed acquisition; retained poses cannot satisfy calibration; reconnect does not snap to identity.
- [ ] **W80.08** Lifecycle: reconnect, role reassignment, activation/deactivation, and session recreation do not duplicate physical identities or scene nodes.
- [ ] **W80.09** Transactionality: missing dependencies, invalid matrices, calibrator failure, cancellation, and repeated confirmation preserve or restore a coherent rig.
- [ ] **W80.10** Scale and spaces: calibration at nonzero world position, rotated playspace, supported uniform avatar scales, recenter, and teleport preserves the defined matrix convention.
- [ ] **W80.11** Spectator isolation: enabling/changing the spectator does not change HMD matrices, input possession, calibration state, or first-person-only visibility rules.
- [ ] **W80.12** Persistence: profile round-trip and incompatible-profile rejection behave as specified.

### OpenXR hardware procedure

The repository already supplies a SteamVR OpenXR smoke runner. The documented commands are: [S13][runtime-guide] [S14][hardware-validation]

```powershell
# Primary hardware diagnostic lane; select SequentialViews explicitly
# in the existing rendering settings for initial isolation.
powershell -ExecutionPolicy Bypass `
  -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 `
  -UseActiveRuntime `
  -Renderer Vulkan

# Separate OpenGL diagnostic lane.
powershell -ExecutionPolicy Bypass `
  -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 `
  -UseActiveRuntime `
  -Renderer OpenGL
```

Do not invent a command-line flag for `SequentialViews`; locate and use the current existing setting. Explicit `VR.Mode=OpenXR` must not silently fall back to OpenVR. Validate strict single-pass mode separately: a rejected strict mode can submit no projection layer by design. [S1][frame-lifecycle] [S13][runtime-guide]

- [ ] **W80.13** Record the headset, controller models, tracker models/count, runtime and version, active manifest, GPU/driver, backend, view mode, and implementation commit.
- [ ] **W80.14** Validate sequential OpenXR stereo with headset only, then add controllers, then the three body trackers. Check pose direction, eye order, projection, and output orientation.
- [ ] **W80.15** Run the default engine-owned calibration flow with SteamVR role overrides disabled. Explicitly test unassigned tracker roles where the provider claims role-free acquisition; record a compatibility blocker if poses are unavailable.
- [ ] **W80.16** Run with deliberately mismatched SteamVR body-role labels and confirm the engine's physical assignment remains correct. Then test the opt-in override independently.
- [ ] **W80.17** Test standing, looking around independently of body yaw, turning, crouching, lifting either foot, close feet, crossed feet after calibration, and room-scale movement.
- [ ] **W80.18** Test tracking occlusion, tracker power-cycle, reconnect order changes, controller loss, headset removal, dashboard focus, recenter, and runtime/session restart.
- [ ] **W80.19** Test first calibration, cancel, failed confirmation, repeated recalibration, avatar change, and saved-profile restoration.
- [ ] **W80.20** Run the third-person output concurrently with VR. Check full-avatar visibility, camera obstruction, teleport resets, input focus, output cadence, and audio-listener ownership.
- [ ] **W80.21** Validate strict single-pass stereo on supported configurations after the sequential baseline passes. Record unsupported/rejected configurations distinctly from rendering regressions.
- [ ] **W80.22** Measure headset frame time, missed deadlines, allocations, and spectator cost with spectator disabled/enabled. Report actual results; do not infer refresh-rate performance from code structure.
- [ ] **W80.23** Re-run the existing no-HMD OpenXR and OpenVR regression lanes as applicable without treating synthetic success as a physical-hardware pass.
- [ ] **W80.24** Attach logs, normalized smoke summaries, test results, and short visual evidence for the combined experience. Update the hardware-validation report with the actual tested commit/configuration and remaining failures.

### Documentation and completion

- [ ] **W80.25** Update runtime and player instructions to explain engine-owned automatic/manual assignment first and SteamVR role overrides second. Remove any implication that matching SteamVR body roles are the default calibration policy.
- [ ] **W80.26** Document pose-provider limitations separately from body assignment, including any unresolved role-free acquisition restriction.
- [ ] **W80.27** Document the calibration UI, failure recovery, spectator controls, profile compatibility, and which capture outputs were actually validated.
- [ ] **W80.28** Mark work complete only after behavioral tests and the relevant hardware evidence are attached. Existing checkboxes in older parity documents are not a substitute for this evidence.

## 14. Definition of done

The initial local-player milestone is complete when all of the following are demonstrated on a named, tested configuration:

- [ ] OpenXR renders a correct first-person headset view while controller gameplay input remains routed to the VR player.
- [ ] Six valid poses drive a calibrated avatar whose targets survive subsequent solver updates.
- [ ] Engine-owned assignment is the default; physical hip/foot mappings do not depend on SteamVR's semantic body roles.
- [ ] The declared default provider behavior has been proven, including role-free acquisition where claimed. A setup that still requires runtime role configuration is documented as a limitation, not hidden behind the assignment abstraction.
- [ ] Automatic identification rejects ambiguity, and manual/guided assignment is usable in VR.
- [ ] SteamVR role mapping is an explicit override, disabled by default, with conflicts and rebinds handled safely.
- [ ] Calibration confirm/cancel/failure/retry is transactional, and muting has no calibration side effect.
- [ ] Tracking loss, reconnect, recenter, and repeated calibration do not produce origin snaps, duplicate nodes, silent body swaps, or accumulated scale/offsets.
- [ ] An independent third-person follow output shows the complete avatar without stealing HMD ownership, tracking origin, or controller possession.
- [ ] Automated regression tests and physical-hardware evidence identify the implementation commit, runtime, backend, and outstanding limitations.

Profile persistence is the W70 product-completeness follow-up; encoded recording is optional W60.16. Neither should be represented as implemented merely because the single-session spectator experience works.

## 15. Implementation guardrails

Keep each change focused and independently testable. First reproduce the target-handoff failure, then establish pose identity/validity and the default assignment path, then complete transactional calibration and spectator integration. Avoid a broad renderer/animation rewrite while fixing these interfaces.

New type and setting names in this document are proposals. Prefer extending the existing runtime-neutral services and component contracts where appropriate. Preserve assembly dependency boundaries, the existing render-thread ownership rules, and explicit runtime selection. Record unsupported hardware behavior honestly instead of hiding it with fallback.

---

## Source references

Repository links are pinned to the reviewed commit so this TODO does not silently refer to a later implementation. They support the baseline findings; proposed architecture, tasks, and acceptance criteria above are implementation recommendations.

- **S1:** [OpenXR frame lifecycle and submission][frame-lifecycle]
- **S2:** [OpenXR action creation, spaces, bindings, and pose caches][input-core]
- **S3:** [VRIK calibration and target creation][calibrator]
- **S4:** [Runtime bootstrap pawn/camera construction][bootstrap-pawns]
- **S5:** [OpenXR runtime-neutral input and tracker path metadata][input-neutral]
- **S6:** [VR tracker scene collection][tracker-collection]
- **S7:** [VR device transform base and pose fallback][device-transform]
- **S8:** [Humanoid IK target helpers and matrix convention][ik-base]
- **S9:** [VRIK target synchronization][vr-solver]
- **S10:** [VR player calibration and nearest-tracker assignment][player-character]
- **S11:** [Runtime calibration invocation bridge][runtime-calibrator]
- **S12:** [Editor test-world pawn/camera/calibration wiring][editor-pawns]
- **S13:** [OpenXR runtime guide and smoke commands][runtime-guide]
- **S14:** [Recorded SteamVR OpenXR hardware-validation status][hardware-validation]
- **S15:** [OpenXR cached-pose accessors and tracker state][xr-state]
- **S16:** [IK component update scheduling][base-ik-solver]
- **XR1:** [Khronos: XR_HTCX_vive_tracker_interaction][htcx-spec]
- **XR2:** [Khronos: XrEventDataViveTrackerConnectedHTCX][htcx-event]

[baseline]: https://github.com/BlackJaxDev/XRENGINE/commit/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63
[frame-lifecycle]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs
[input-core]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.cs
[calibrator]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/VRIKCalibrator.cs
[bootstrap-pawns]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Bootstrap/BootstrapPawnFactory.cs
[input-neutral]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.RuntimeNeutral.cs
[tracker-collection]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.InputIntegration/Scene/Components/VR/VRTrackerCollectionComponent.cs
[device-transform]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRDeviceTransformBase.cs
[ik-base]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/HumanoidIKComponentBase.cs
[vr-solver]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/VRIKSolverComponent.cs
[player-character]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.InputIntegration/Scene/Components/VR/VRPlayerCharacterComponent.cs
[runtime-calibrator]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Core/Components/Animation/RuntimeVRIKCalibrator.cs
[editor-pawns]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Editor/Unit%20Tests/Default/UnitTestingWorld.Pawns.cs
[runtime-guide]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/docs/developer-guides/vr/openxr-runtime.md
[hardware-validation]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/docs/work/testing/openxr-steamvr-hardware-validation.md
[xr-state]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs
[base-ik-solver]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/BaseIKSolverComponent.cs
[humanoid]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs
[tracker-transform]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRTrackerTransform.cs
[vr-state-contract]: https://github.com/BlackJaxDev/XRENGINE/blob/4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63/XREngine.Input/RuntimeVrStateServices.cs
[htcx-spec]: https://registry.khronos.org/OpenXR/specs/1.1/man/html/XR_HTCX_vive_tracker_interaction.html
[htcx-event]: https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrEventDataViveTrackerConnectedHTCX.html
