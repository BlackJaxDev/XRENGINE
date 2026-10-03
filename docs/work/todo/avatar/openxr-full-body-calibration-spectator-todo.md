# OpenXR VR, Full-Body Calibration, and Third-Person Spectator — TODO

**Project:** XRENGINE  
**Created:** September 23, 2026  
**Revised:** September 26, 2026 — calibration model settled (proximity binding for up to eight trackers, manual body measurements), SteamVR tracker roles dropped, and sparse-tracking estimation moved to the [body estimation TODO][estimator-todo].  
**Review baseline:** `master` at `7ab827983`. Findings describe that commit; links are repository-relative and open the current code.  
**Status:** Runtime implementation is integrated and synthetic live validation passes 3-, 6-, and 11-point calibration, stable targets, limb response, source loss/restoration, and spectator image publication. Checked work items record implemented changes; each package acceptance statement and the final definition of done remain separate release gates. The current focused selection passes 24/24. The broader selection passes 229/290 with the same 61 failure names as the prior 224/285 selection; this is not a clean repository-HEAD baseline. An earlier physical SteamVR OpenGL session reached Focused and submitted both eyes, and one editor avatar borrow/restore cycle passed. Native HTCX enumeration returned zero tracker paths despite trackers appearing through OpenVR. A later Ready/zero-frame session exposed a root-list enumeration race; the stable-snapshot fix builds and its regression passes, but live confirmation is pending. The next physical retry could not acquire an OpenXR system because the Beyond was out of range and then the SteamVR server was absent. Full hardware calibration, final eye preview, Vulkan output, and performance acceptance remain open. See the [implementation and live validation record](../../progress/avatar/openxr-full-body-calibration-spectator-implementation.md) for exact evidence and limitations.
**Related:** [calibration baseline evidence][baseline-evidence] · [body estimation TODO][estimator-todo] · [editor OpenXR toggle TODO][toggle-todo] · [OpenXR/OpenVR parity TODO][parity-todo] · [SteamVR OpenXR hardware validation][hardware-validation]

> **Assignment policy:** XREngine binds trackers to body slots by proximity every time the player calibrates. SteamVR tracker roles are never read for assignment, offered as an override, or mentioned in player instructions. Players rarely know roles exist and routinely swap in whichever tracker is charged.

## 1. Goal and scope

Deliver a local-player experience in which:

- OpenXR renders the headset's first-person stereo view.
- A headset, two controllers, and zero to eight trackers drive the avatar. Trackers bind to the hips, chest, feet, upper arms, and knees, giving 6- to 11-point tracking.
- Slots without a tracker—3- to 5-point setups, partial tracker sets, and trackers lost mid-session—are filled by the [body estimator][estimator-todo] through the per-slot contract defined here. Until the estimator exists, an empty slot contributes no IK target, as today.
- The player opens calibration, captures, cancels, and recalibrates entirely in VR, without a keyboard.
- A separate monoscopic third-person camera follows the avatar and produces a desktop or render-texture spectator view without affecting the headset.
- Tracking loss, reconnection, recentering, tracker swaps, and repeated calibration never corrupt the rig or silently move a body part to a different tracker.

This is an integration-and-correctness milestone. Existing rendering, input, calibration, IK, camera, and smoothing components are repaired and reused; chest, upper-arm, and knee solving (W35) is the only new solver work.

### Non-goals

- SteamVR role-based assignment, role overrides, or instructions that depend on roles.
- Persisting calibration across application sessions. Trackers get swapped between sessions and mount offsets change whenever straps move; W70 covers continuity within a session only.
- Estimator internals, which belong to the [body estimation TODO][estimator-todo].
- GPU IK, a replacement animation system, networking changes, quad-view or foveation, finger tracking, and a built-in video encoder (optional follow-up W60.16).

## 2. Calibration model

This section is the player-facing contract. Work packages implement it and do not redefine it.

### 2.1 Body slots

| Slot | Driven by | Solver consumer |
|---|---|---|
| Head | Headset with a fixed eye offset (2.4) | Head target |
| Left/right hand | Controller with a per-profile preset (2.4) | Hand targets |
| Hips | Nearest tracker, captured offset | Pelvis target |
| Chest | Nearest tracker, captured offset | Spine chest goal (W35) |
| Left/right foot | Nearest tracker, captured offset | Foot targets |
| Left/right upper arm | Nearest tracker, captured offset | Shoulder and elbow (W35) |
| Left/right knee | Nearest tracker, captured offset | Knee bend goals (W35) |

Each of the eight tracker slots has one source per frame: its bound tracker, the estimator, or none. Sources crossfade instead of switching instantaneously (W50).

### 2.2 Calibration pose and placement

While calibrating, the avatar holds a canonical T-pose, scaled by the player's body measurement (2.5), with its eyes at the headset and its facing matched to the headset's yaw. It follows the headset live so the player can step into it. The pose is a true T-pose even when the avatar's bind pose is an A-pose.

### 2.3 Player flow

1. The player opens calibration from the menu or a dedicated binding. IK pauses and the T-pose avatar appears.
2. Each tracker shows a marker labeled with the slot it would bind to right now, or as unassigned. Footprints on the floor show where to stand.
3. The player stands straight inside the avatar: head level, feet in the footprints, and, when upper-arm trackers are worn, arms matching the T-pose. Controllers can be held anywhere.
4. The player pulls both triggers. Capture is refused with a specific message when the head is tilted beyond tolerance or the headset or a controller is not tracking. Otherwise the rig commits immediately; there is no confirmation step.
5. Cancel at any point restores the previous rig. Recalibrating repeats the flow and re-binds every tracker.

Capture keeps whatever offset exists between each tracker and the displayed pose, so a mismatch—feet wider than the avatar's, arms below the T-pose—persists until the next calibration. The markers and footprints exist to make matching easy.

### 2.4 Fixed offsets

- The headset maps to the avatar's eye position, measured from the avatar's eye geometry and scaled with the avatar. It does not depend on the headset pose at capture.
- Controllers use preset grip-to-wrist offsets per interaction profile, so hands never need alignment during calibration.

### 2.5 Body measurements and scale

The player chooses a measurement mode, height or arm span, and enters the value in settings. Calibration never derives scale from the headset or changes it; a single owner applies the player's value (W45). A wrong value shows up as a crouched or stretched avatar or feet off the floor, and the player corrects it in settings.

### 2.6 Binding rules

- At capture, trackers and slots are matched one-to-one, nearest pair first, across all currently valid trackers. Enumeration order never affects the result.
- Upper-arm and knee distances are measured to the limb segment around the joint, because trackers are worn along the limb; hips, chest, and feet use their bones.
- A tracker farther than a scale-aware cutoff from every slot stays unassigned, for example one mounted on a camera.
- Each offset is captured as-is, with no assumed mount orientation. In the existing row-vector convention, `TargetWorld(t) = DeviceToTargetOffset * DeviceWorld(t)` and `DeviceToTargetOffset = TargetWorldAtCapture * inverse(DeviceWorldAtCapture)`.
- Bindings stay frozen until the next calibration. Crossing feet, crouching, or moving a tracker near another body part never re-binds.
- When a bound tracker disconnects, its slot falls back (W50). The same physical tracker reconnecting within the session resumes its slot; a different tracker never inherits a slot without recalibration.

### 2.7 Proposed settings

These names are proposals, not claims that the properties exist.

| Setting | Default | Meaning |
|---|---|---|
| `BodyMeasurementMode` | `Height` | Whether height or arm span drives avatar scale |
| `PlayerHeight` | Unset | Standing height in meters, entered by the player |
| `PlayerArmSpan` | Unset | Fingertip-to-fingertip span in meters, entered by the player |
| `CalibrationHeadTiltTolerance` | 10° | Maximum headset pitch or roll accepted at capture |
| `TrackerBindingCutoff` | Scale-aware | Beyond this distance from every slot, a tracker stays unassigned |
| `TrackerPoseProvider` | OpenXR | A SteamVR tracker provider only after approval (W20.04), never as a silent fallback |
| `SpectatorEnabled` | Configurable | Separate monoscopic output that never changes headset ownership |

## 3. Architecture decisions

### 3.1 Transport, identity, and binding are separate layers

| Layer | Responsibility | Must not be used as |
|---|---|---|
| Pose transport | Current pose from an OpenXR action space or an explicitly approved provider | A body assignment |
| Physical identity | Recognizing one tracker across reconnects within a session | A persisted key, device index, or role |
| Slot binding | The result of the last calibration | Anything the runtime can change |

Identity is session-scoped. OpenXR's VIVE tracker interaction profile binds through role paths, so role paths may appear as an internal binding detail, but they carry no meaning and trackers are addressed by persistent path. Whether SteamVR's OpenXR runtime streams every tracker that way is the first hardware gate (W20.01). If it does not, record the limitation. Do not ask players to configure roles, and do not switch providers silently; a SteamVR tracker provider is a separately approved option that requires reference-space and timing reconciliation.

### 3.2 Raw devices, stable targets, one offset representation

Keep three layers distinct:

1. Raw headset, controller, and tracker transforms, with their VR behavior intact. Never replace them with ordinary `Transform` nodes to satisfy a cast.
2. Per-slot sources: a bound tracker with its captured offset, estimator output, or none, each with a crossfade weight.
3. One stable concrete target per slot, owned by a single rig owner and consumed by IK.

Each target uses exactly one offset representation: a child of the raw device carrying the offset in its local transform, or an explicitly updated target with a stored offset. The IK helper already computes `target.offset * target.RenderMatrix`; do not reverse or duplicate that convention. Keep avatar scale separate from the tracking-space metric basis.

### 3.3 One scale owner

Avatar scale derives only from the player's measurement (2.5) and the avatar's measured proportions. Calibration and IK never write scale.

### 3.4 Spectator independence

The headset's eye cameras remain OpenXR-controlled. The spectator has its own monoscopic camera, output ownership, visibility, temporal state, and follow behavior. Selecting the spectator as desktop output never transfers controller-input possession or alters the tracking origin.

## 4. Source-review findings

These come from reading `7ab827983`, except F01, which the W00 harness reproduced.

| ID | Finding | Owner |
|---|---|---|
| F01 | Calibrated targets are discarded on the next solver update. The [player component][player] stores raw device transforms on the humanoid, the [calibrator][calibrator] assigns concrete children directly to the solver, and `SyncSolverTargets` in the [VRIK solver][vr-solver] re-reads the humanoid slots through a `Transform` cast that raw `VRDeviceTransformBase` instances fail. Recalibrating after a solver update duplicates the target nodes. | W10 |
| F02 | `FindNearestTrackerTargets` matches each tracker independently to its nearest joint within a fixed 0.25 radius, so a later tracker overwrites an earlier one in the same slot. | W30 |
| F03 | [Tracker metadata][input-neutral] keeps `PoseAvailable` true once it has ever been true, and [raw device transforms][device-transform] fall back to their offset or identity when pose lookup fails. | W20 |
| F04 | The [tracker collection][tracker-collection] only adds and updates entries; deactivation clears its dictionaries without destroying the tracker nodes it created. | W20 |
| F05 | Calibration is not transactional: the calibrator's result is ignored, the solver is enabled unconditionally, the fence's `Wait(100)` result is ignored, and cancel restores targets and then calibrates again. | W40 |
| F06 | Calibration is tied to the mute action. The player component begins calibrating on activation. The [input service][input-services] dispatches the mute bool on both edges and once with `false` when it first becomes active, and the [editor wiring][editor-pawns] toggles calibration on every dispatch. The auto-started calibration therefore ends on the first active input frame; afterwards, pressing mute begins calibration and releasing it captures, with the microphone muted while held. `khr/simple_controller` has no mute binding. | W40 |
| F07 | OpenXR rendering exists, but the physical hardware matrix is pending. | [Parity TODO][parity-todo] and [hardware validation][hardware-validation]; W80 consumes their results |
| F08 | The existing third-person toggle is a desktop-pawn feature; VR construction provides only first-person desktop output and a pickup camera. | W60 |
| F09 | Production calibration passes `RuntimeVrStateServices.CalibrationSettings`, which stays null because only an uncalled lazy getter in [`EngineVrLifecycle`][vr-lifecycle] creates it; the calibrator dereferences it. The [synthetic rig][synthetic-rig] passes its own settings, so tests miss this. | W10 |
| F10 | The calibrator does not capture offsets the way the model requires. Feet re-derive yaw from `FootTrackerForward`/`FootTrackerUp` and snap height to the avatar's foot bone; `CalibrateScale` multiplies root scale while [`HeightScaleBaseComponent`][height-scale] sets it absolutely; and the head target is captured from the headset's full orientation alongside a separate eye-offset target. | W15, W45 |
| F11 | Chest, elbow, and knee slots are matched but never calibrated. The calibrator accepts six devices and forces knee weight to 0, `SyncSolverTargets` never feeds the [spine's chest goal][ik-spine], and nothing drives the shoulder from an upper-arm tracker. | W35 |
| F12 | Calibration poses the avatar with `HumanoidComponent.ResetPose`, which restores the humanoid bind pose; for some avatars that is an A-pose. | W15 |
| F13 | There is no player measurement setting. [`RealWorldHeight`][vr-state] is an internal eye height that defaults to 1.8 m, there is no arm-span mode, and in the [calibration settings][calibration-settings] `HandOffset` is zero while the player's controller offsets are identity. | W15, W45 |
| F14 | Tracker transport depends on roles and is probably broken at startup. [Bindings are suggested per role path][input-core], and trackers enumerated at startup add their persistent paths to the same binding table; OpenXR requires the runtime to [reject the entire suggestion][suggest-bindings] when any binding path is outside the profile's allowlist. Persistent paths added after action creation can never be used. Scene trackers are keyed by a canonical path that prefers the role path, so a role change creates a second scene tracker for the same device. | W20 |
| F15 | The [editor OpenXR pawn switcher][pawn-switcher] builds temporary rigs with `BootstrapPawnFactory.CreateVrPawn`, which creates no avatar or calibration, and destroys them when VR stops. `UnitTestingWorld.InitializeLocomotion` is the only place that creates `VRPlayerCharacterComponent`. | W10, W70 |
| F16 | Procedural leg locomotion in [`IKSolverVR`][ik-solver-vr] is commented out, so 3-point play has no stepping. | [Body estimation TODO][estimator-todo] |

## 5. Execution order

| Work package | Priority | Dependencies | Exit condition |
|---|---|---|---|
| W00 — Baseline and harness | Done | — | The harness reproduces F01 |
| W10 — Rig ownership and slot contract | P0 | W00 | Targets survive solver updates; per-slot sources exist |
| W15 — Capture math | P0 | W10, W45 | Captured offsets match the model under perturbed captures |
| W20 — Tracker transport, identity, and validity | P0 | W00 | Every connected tracker streams without meaningful roles, or the limitation is recorded |
| W30 — Nearest-slot binding | P0 | W10, W20 | One-to-one binding for 0–8 trackers, independent of order |
| W35 — Chest, upper-arm, and knee solving | P1 | W10, W15 | An 11-point rig drives chest, shoulders, elbows, and knees |
| W40 — Player calibration flow | P1 | W15, W30 | The section 2.3 flow works on every supported controller profile |
| W45 — Body measurements and scale | P0 | W00 | One scale owner driven by the player's setting |
| W50 — Timing, tracking loss, and locomotion | P1 | W10, W20, W40 | Consumers agree on spaces and timing; slot fallback crossfades |
| W60 — Third-person spectator | P1 | W50 for final integration | An independent follow view that leaves the headset and input untouched |
| W70 — Session continuity | P2 | W40, W50 | Calibration survives VR toggles and pawn recreation within a session |
| W80 — Hardware validation and documentation | Release gate | All above | Evidence on named hardware |

Run W20.01 on hardware as early as possible; its result decides whether this milestone needs a separately approved SteamVR tracker provider. Spectator follow mechanics can be developed in a synthetic scene in parallel.

## 6. W00 — Baseline and test harness (done)

**Primary files:** [player component][player], [VRIK solver][vr-solver], [humanoid IK base][ik-base], [calibrator][calibrator], [editor pawn factory][editor-pawns], [runtime pawn factory][bootstrap-pawns].

- [x] **W00.01** Record the actual implementation commit and compare the affected call sites with the reviewed baseline. Update findings that have already changed; do not patch stale line numbers or duplicate an existing fix.
- [x] **W00.02** Build a minimal scene with a valid humanoid, VRIK solver, playspace, HMD, two controllers, and exactly three synthetic body trackers.
- [x] **W00.03** Give synthetic devices deterministic identities and configurable pose validity, timestamps, rotations, and connection state. Do not require an OpenXR runtime for unit-level assignment and calibration tests.
- [x] **W00.04** Reproduce F01 by calibrating, advancing several solver ticks, and asserting the identity and non-null state of every target. Preserve this as a regression test.
- [x] **W00.05** Record target counts, avatar/root scale, tracking origin, and component activation state before and after calibration. Add assertions for leaked or duplicated target nodes.
- [x] **W00.06** Audit both pawn-construction paths. Decide which shared runtime factory/service owns the new integration so editor and runtime behavior do not drift.

Implemented by [`SyntheticVrCalibrationRig`][synthetic-rig], `SyntheticVrDeviceTransform`, and [`VRIKCalibrationTests`][calibration-tests]. The persistence regression `Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates` now runs without `Explicit` and checks eleven stable avatar-owned targets. Baseline findings are in the [baseline evidence][baseline-evidence], and current results are in the [implementation record](../../progress/avatar/openxr-full-body-calibration-spectator-implementation.md). W10.07 covers the editor pawn switcher as well as runtime pawn construction.

## 7. W10 — Rig ownership and slot contract

**Primary files:** [player component][player], [humanoid component][humanoid], [humanoid IK base][ik-base], [VRIK solver][vr-solver], [calibrator][calibrator], [runtime calibrator bridge][runtime-calibrator], [runtime pawn factory][bootstrap-pawns], [editor pawn switcher][pawn-switcher].

- [x] **W10.01** Introduce one rig owner that holds a stable concrete target for the head, both hands, and each of the eight tracker slots. Store raw device references separately; never install a raw pose source where the solver expects a calibrated target.
- [x] **W10.02** Make `SyncSolverTargets` consume the rig owner's targets, or have calibration publish them into `HumanoidComponent`, so one store is authoritative. Then remove `Explicit` from `Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates` and replace the loss characterization with the corrected contract.
- [x] **W10.03** Give each target exactly one offset representation (3.2) and remove the duplicate humanoid tuple offsets.
- [x] **W10.04** Define the per-slot source contract used by W50 and the estimator: bound tracker, estimator, or none, each with a crossfade weight. A slot with no source contributes zero weight.
- [x] **W10.05** Rebuild or re-parent a target when its slot binds to a different tracker, so no target stays under the previous tracker. Destroy owned targets on teardown; repeated calibration must not accumulate nodes.
- [x] **W10.06** Return a typed success or failure result with the calibrated data through the runtime bridge; a non-null reflection result or an enabled component is not success. Supply calibration settings in production (F09) and fail explicitly when they are missing.
- [x] **W10.07** Build the avatar rig and calibration in one shared runtime service used by `BootstrapPawnFactory.CreatePlayerPawn`, `BootstrapPawnFactory.CreateVrPawn`, and the unit-testing world. The editor supplies only UI and synthetic inputs.
- [x] **W10.08** Validate matrices and rotations before committing them; reject non-finite values, singular transforms, and zero-length quaternions.

**Acceptance:** every target stays bound across solver updates; expected and actual target matrices match under translation, rotation, and a non-origin playspace; repeated calibration never accumulates offsets or nodes.

## 8. W15 — Capture math

**Primary files:** [calibrator][calibrator], [calibration settings][calibration-settings], [player component][player], [humanoid component][humanoid], [synthetic rig][synthetic-rig].

- [x] **W15.01** Capture every tracker slot's offset as-is (2.6). Remove the foot path's yaw derivation from `FootTrackerForward`/`FootTrackerUp` and its snap to the avatar's foot height.
- [x] **W15.02** Bind the headset to the avatar's eye position with a fixed offset derived from the avatar's eye geometry, independent of the headset pose at capture. Remove the captured head target.
- [x] **W15.03** Add preset grip-to-wrist offsets for every supported interaction profile, replacing the zero `HandOffset` and the identity controller offsets. Record where each preset comes from.
- [x] **W15.04** Pose the avatar in a canonical T-pose during calibration regardless of its bind pose (F12), reusing the humanoid avatar definition where possible.
- [x] **W15.05** Place the calibration pose as 2.2 specifies—eyes at the headset, headset yaw, scale from W45—and remove calibration-time scale writes (F10).
- [x] **W15.06** Refuse capture when headset pitch or roll exceeds `CalibrationHeadTiltTolerance`, with a message telling the player to look straight ahead.
- [x] **W15.07** Sample a short stationary window ending at the capture gesture, reject high-motion samples, and take every device from one coherent snapshot.
- [x] **W15.08** Retire the `VRIKCalibrationSettings` fields that only served removed paths—the foot and head axes and offsets and the scale multiplier—after checking for serialized instances.
- [x] **W15.09** Extend the synthetic harness with perturbed captures: a tilted head, which must be refused; a head yawed relative to the body; arbitrarily rotated tracker mounts; a stance wider than the avatar's; and player measurements that differ from the avatar's. The current rig puts devices exactly on the bones with a level head, so W10 alone passes there.

**Acceptance:** arbitrarily rotated tracker mounts reproduce the displayed pose exactly at capture and follow rigidly afterwards; a tilted head is refused; calibration never changes scale.

## 9. W20 — Tracker transport, identity, and validity

**Primary files:** [OpenXR core input][input-core], [runtime-neutral OpenXR input][input-neutral], [OpenXR state][xr-state], [VR state contract][vr-state-contract], [tracker collection][tracker-collection], [tracker transform][tracker-transform], [device transform base][device-transform].

### Transport

- [ ] **W20.01** On SteamVR's OpenXR runtime, determine whether every connected tracker streams through a persistent-path subaction path whatever its role, including duplicated and default roles; the [extension][htcx-spec] allows either path as a subaction path. Record the runtime version, extension revision, enumeration results, bindings, and pose results, and what happens to a tracker SteamVR has disabled.
- [x] **W20.02** Suggest bindings only on role paths, covering every role the extension defines (including `handheld_object` and the revision 3 wrist and ankle roles), and use persistent paths only as subaction paths. This removes the invalid bindings behind F14.
- [x] **W20.03** Define the late-connection policy. Actions cannot change after action sets are attached, so a tracker connected after session start needs a controlled input rebuild. The candidate is to rebuild when calibration opens and the connected set has changed; never rebuild during gameplay without a player action.
- [x] **W20.04** If W20.01 fails, record the blocker and propose a SteamVR tracker provider for approval (3.1). Headset rendering and controller input stay on OpenXR.

The tested SteamVR OpenXR runtime advertised `XR_HTCX_vive_tracker_interaction`, but two native HTCX enumeration probes returned zero persistent paths while a later OpenVR inventory named three Tundra trackers with valid poses plus a pair of Knuckles controllers. This is a concrete blocker for tracker capture on that configuration. The proposed separate SteamVR tracker provider would keep headset rendering and controller input on OpenXR while reconciling tracker serial identity, pose publication time, and OpenVR-to-OpenXR reference-space transforms explicitly. Provider implementation needs the pending owner approval; W20.01 still lacks duplicate-role, disabled-tracker, and late-connection evidence.

### Identity and lifecycle

- [x] **W20.05** Use a session-scoped physical identity, the provider's persistent path. Never serialize a numeric `XrPath`, synthetic device index, or collection order.
- [x] **W20.06** Key scene trackers by physical identity so each tracker appears exactly once, including across role changes.
- [x] **W20.07** Reconcile collection membership, native handles, and owned nodes across disconnects, reconnects, component reactivation, and session recreation without duplicating nodes (F04).
- [x] **W20.08** Publish immutable or double-buffered tracking snapshots, and marshal scene-graph changes onto the scene owner instead of mutating live collections from runtime callbacks.

### Current validity

- [x] **W20.09** Replace sticky availability with current state: connected, action active, position and orientation valid, sample time, snapshot ID, and last valid sample. A failed location clears current usability; keep an `EverTracked` diagnostic separately.
- [x] **W20.10** Audit headset and controller validity the same way; a cached accessor that always succeeds is not proof of a fresh calibration pose.
- [x] **W20.11** Never let an unavailable real device fall through to identity or its offset during gameplay; keep synthetic and debug pose behavior separate.
- [ ] **W20.12** Distinguish not discovered, disabled in SteamVR, discovered but unbound, bound but inactive, stale, and tracking lost in diagnostics. No message tells players to assign roles.

**Acceptance:** every connected tracker appears once with an honest current pose; identity survives reconnection within a session; role-free streaming is demonstrated or its limitation is recorded.

## 10. W30 — Nearest-slot binding

**Primary files:** [player component][player], [tracker collection][tracker-collection], plus a focused binding service in the runtime input integration layer.

- [x] **W30.01** Replace per-tracker nearest search with one-to-one nearest-pair matching between the eight slots and all current, valid trackers. The result must not depend on enumeration order.
- [x] **W30.02** Measure upper-arm distances to the shoulder-to-elbow segment and knee distances to the segment spanning the knee (mid-thigh to mid-shin); hips, chest, and feet use their bones.
- [x] **W30.03** Scale the cutoff with avatar scale. Trackers beyond it stay unassigned and appear that way in the preview.
- [x] **W30.04** Drive the live marker preview (2.3) from the same matching function capture uses.
- [x] **W30.05** Freeze bindings until the next calibration (2.6).
- [x] **W30.06** Exclude controllers and invalid trackers; enforce one tracker per slot and one slot per tracker.

**Acceptance:** for canonical stances, 0–8 trackers bind correctly under every enumeration order; distant extra trackers stay unassigned; bindings never change during gameplay.

## 11. W35 — Chest, upper-arm, and knee solving

**Primary files:** [calibrator][calibrator], [VRIK solver][vr-solver], [VRIK spine solver][ik-spine], [VRIK solver core][ik-solver-vr].

- [x] **W35.01** Calibrate the chest, upper-arm, and knee slots through the W15 capture path.
- [x] **W35.02** Feed the chest target to the spine solver's chest goal.
- [x] **W35.03** Drive knee bend goals from knee trackers and remove the forced zero weight.
- [x] **W35.04** Drive shoulder (clavicle) and elbow placement from upper-arm trackers while hand targets stay authoritative for the wrists. Define and document the priority when these constraints conflict.
- [x] **W35.05** Rename the `LeftElbow`/`RightElbow` humanoid targets to upper-arm slots, or document that they represent upper-arm trackers.

**Acceptance:** an 11-point synthetic rig reproduces chest rotation, shoulder elevation, elbow direction, and knee direction from its trackers without disturbing hand or foot targets.

## 12. W40 — Player calibration flow

**Primary files:** [player component][player], [calibrator][calibrator], [runtime bridge][runtime-calibrator], [player input set][input-set], [editor pawn wiring][editor-pawns], [runtime-neutral OpenXR input][input-neutral].

- [x] **W40.01** Replace the calibration boolean with explicit states: uncalibrated, calibrating, calibrated, and failed. Tracking degradation is not loss of calibration.
- [x] **W40.02** Remove the mute hookup and the automatic start on activation (F06); muting never affects calibration.
- [x] **W40.03** Add dedicated open and cancel actions to the runtime-neutral input layer, bound on every supported interaction profile, and capture when both triggers are pulled together. Handlers act on the press edge and read the action value.
- [x] **W40.04** Before calibrating, snapshot bindings, offsets, targets, solver activation and weights, and root-controller state. Cancel restores the snapshot without calibrating again.
- [x] **W40.05** Build proposed bindings and offsets as temporary state, validate them, and publish them atomically. A missing headset or controller pose, invalid output, or exception is a failure that leaves the previous rig active.
- [x] **W40.06** Pause competing animation and root-motion writers while calibrating and restore them afterwards.
- [x] **W40.07** Route requests to one scene or simulation owner, never block the render thread, and remove the ignored `Wait(100)`.
- [x] **W40.08** Provide in-VR feedback: markers, slot labels, footprints, the head-tilt message, and specific failure messages. Place text at eye level so reading it never tilts the head.
- [x] **W40.09** Make calibration input reach the VR player while the desktop editor camera has focus or possession.
- [x] **W40.10** When the player enters a VR pawn without a calibration, run head-and-hands IK and offer calibration instead of starting it.

**Acceptance:** on every supported controller profile, a player opens calibration, captures with one trigger pull, cancels, and retries without a keyboard; a failure leaves the previous rig active; muting and camera switching have no calibration side effects.

## 13. W45 — Body measurements and scale

**Primary files:** [height scale base][height-scale], [VR state][vr-state], [VR state contract][vr-state-contract], [humanoid component][humanoid], [calibrator][calibrator].

- [x] **W45.01** Add player settings for the measurement mode and value (2.7), stored with the player's settings rather than the unit-testing world.
- [x] **W45.02** Players enter standing height; define and document how it converts to the eye height the scale ratio uses.
- [x] **W45.03** Measure avatar arm span fingertip to fingertip in the canonical T-pose, the way people measure themselves. When finger bones are missing, use wrist span plus an estimated hand length and report that fallback.
- [x] **W45.04** Make one component own avatar scale (3.3): remove `CalibrateScale` and reconcile `HeightScaleBaseComponent`. Validate ranges, denominators, and finite values.
- [x] **W45.05** Until the player sets a value, show a notice in the calibration flow rather than silently using the 1.8 m default.
- [x] **W45.06** In height mode, warn at capture when the headset's eye height disagrees with the setting by more than a tolerance. Never correct the setting automatically.

**Acceptance:** changing the measurement rescales the avatar through one owner; both modes produce the expected scale for synthetic avatars; tracked real-world units are unaffected.

## 14. W50 — Pose timing, tracking loss, and locomotion

**Primary files:** [VR state contract][vr-state-contract], [OpenXR state][xr-state], [device transform base][device-transform], [player component][player], [VRIK solver][vr-solver], [IK tick base][base-ik-solver].

The IK base schedules normal and late animation work, while VR device transforms also react to predicted and late runtime updates. Audit the resulting order instead of assuming a calibrated target implies a fresh rendered skeleton.

- [x] **W50.01** Define the frame order explicitly: publish the tracking snapshot, update the playspace and locomotion, update slot sources (tracker or estimator), update targets, evaluate the avatar and IK, publish the renderable pose, then evaluate the spectator anchor from the matching state.
- [x] **W50.02** Identify the owner and coordinate space of each matrix: runtime reference space, playspace, world, avatar root, raw device, and calibrated target. Replace hierarchy-order lookups such as `GetRelevantMovementTransforms` with explicit rig references where practical.
- [x] **W50.03** Keep the simulation snapshot coherent; never combine a newly located controller with old hip or foot transforms in a calibration sample.
- [x] **W50.04** Document how late-located headset and controller poses relate to the skeleton rendered for that frame. Use a deliberate late-pose strategy or documented simulation-pose behavior; never mutate live bones from the render thread as an ad hoc latency fix.
- [x] **W50.05** Fix `MovePlayer()`, which applies the device-to-body offset before playspace conversion and again when building the movement matrix. Test locomotion and room-scale movement with nonzero tracker offsets.
- [x] **W50.06** When a bound tracker stops being usable, hold its last valid target briefly (configurable), then crossfade the slot to the estimator, or fade its weight to zero until the estimator exists. Retained poses never satisfy calibration.
- [x] **W50.07** When the same physical tracker returns, crossfade the slot back. A different tracker never inherits the slot; show that recalibration is needed.
- [x] **W50.08** Handle reference-space changes and recenter as coordinate-basis events: apply a known transform coherently, or invalidate and request recalibration when the relationship is unknown.
- [x] **W50.09** Publish discontinuity events—teleport, snap turn, recenter, avatar replacement, and session-generation change—and reset smoothing and history on them. The spectator and the estimator consume the same events.
- [x] **W50.10** Avoid new per-frame allocations, blocking waits, and unsynchronized collection mutation in pose updates, binding lookup, target updates, and spectator follow. Keep discovery, UI, and calibration allocations off the per-frame path.

**Acceptance:** standing, turning, walking, crouching, and lifting either foot preserve alignment; tracker loss never snaps to the origin and fades without popping; recenter and teleport never apply offsets twice; all view consumers see one published avatar pose.

## 15. W60 — Third-person spectator

**Primary files:** [runtime pawn factory][bootstrap-pawns], [editor pawn factory][editor-pawns], existing camera, smoothing, and boom types, and the viewport/output ownership integration. Read the [DefaultRenderPipeline invariants][pipeline-notes] and [mesh submission contracts][mesh-submission] before changing rendering code.

- [x] **W60.01** Add a dedicated spectator-follow component or rig associated explicitly with the local VR player. Do not reinterpret the existing desktop-only `ThirdPersonPawn` switch as VR support.
- [x] **W60.02** Build a follow anchor from the player-root position and the hips slot, with world up and stable body yaw. Never parent the spectator directly to the headset's full rotation.
- [x] **W60.03** Provide configurable distance, height, shoulder offset, aim point, field of view, and follow smoothing. In the engine's `-Z` forward convention a trailing offset lies behind the player's forward direction; verify the sign in a test scene.
- [x] **W60.04** Use frame-rate-independent follow behavior. Reuse existing smoothing components where their update and space semantics fit; never smooth the OpenXR eye cameras with spectator settings.
- [x] **W60.05** Add collision avoidance using the existing boom and shapecast pattern where suitable. Exclude the player's own collision body and define behavior when the desired position is obstructed.
- [x] **W60.06** Keep body turns distinct from head glances. Define a stable fallback heading when the hips slot has no source, and handle degenerate look-at vectors safely.
- [x] **W60.07** Give the spectator its own monoscopic camera and output or viewport, with temporal history, projection, culling, and mutable pipeline state isolated from both eyes and other cameras.
- [x] **W60.08** Render the complete local avatar in the spectator view while applying first-person head and body hiding only to the headset views. Never deactivate meshes globally to hide them from the headset.
- [x] **W60.09** Route desktop output between the first-person preview, the spectator, and the editor camera without changing VR pawn possession, controller routing, headset camera ownership, or the tracking origin.
- [x] **W60.10** Keep the headset listener for player audio by default. A recording-specific listener or audio bus is an explicit optional feature, not a side effect of adding a camera.
- [x] **W60.11** Produce one clean spectator output usable for desktop capture or a render texture, and reuse it for previews instead of rendering the spectator scene again.
- [x] **W60.12** Add configurable spectator resolution and update cadence. A slower cadence may reuse the last completed spectator frame but must never delay OpenXR frame submission.
- [x] **W60.13** Respect GPU completion and consumer lifetimes when publishing offscreen output: never expose an unfinished target or reuse its storage before outstanding consumers finish.
- [x] **W60.14** Reset follow interpolation and per-view temporal history on the W50.09 discontinuity events and on camera mode changes.
- [ ] **W60.15** Validate output orientation, aspect ratio, color space, post-processing, and visibility independently of the headset, especially on Vulkan.

**Acceptance:** the headset stays first-person while the desktop or render texture shows a stable third-person follow view of the complete avatar; camera activation, capture cadence, and desktop focus never steal VR input or change headset tracking.

### Optional follow-ups

- [ ] **W60.16 — Optional** If recording is required, connect an encoder or external capture integration to the completed spectator output. Keep capture queues bounded, never block GPU readback on the headset submission path, and define frame timestamps, audio alignment, and behavior when encoding falls behind. A rendered texture is not proof of working video encoding.
- [ ] **W60.17 — Optional** Offer the spectator output as an eye-level mirror during calibration, so players can check their stance without looking down.

## 16. W70 — Session continuity

**Primary files:** [editor pawn switcher][pawn-switcher], [runtime pawn factory][bootstrap-pawns], and the W10 rig owner.

- [x] **W70.01** Keep the committed calibration—bindings by session identity, offsets, and the measurement in use—outside the temporary rig, so VR toggles and pawn recreation within a session restore it while the same trackers are connected.
- [x] **W70.02** When a bound tracker is missing on restore, restore the other slots, let missing slots fall back (W50), and offer recalibration.
- [x] **W70.03** Require recalibration after an avatar change, because offsets are relative to the previous avatar's bones.
- [x] **W70.04** Stay consistent with the [editor OpenXR toggle TODO][toggle-todo], which destroys the temporary rig when OpenXR is turned off.

**Acceptance:** toggling VR off and on in the editor restores the previous calibration while the same trackers are connected, and never binds a different tracker.

## 17. W80 — Hardware validation and documentation

### Automated regression coverage

Each work package's acceptance defines its own tests. Keep these guards in `XREngine.UnitTests/`:

- [x] **W80.01** Target persistence: the W00 regression passes without `Explicit`.
- [x] **W80.02** Binding invariance: every enumeration order produces the same one-to-one binding, and distant trackers stay unassigned.
- [x] **W80.03** Transactional rollback: failure and cancel restore the previous rig exactly.
- [x] **W80.04** Capture math: the W15.09 perturbations reproduce the displayed pose at capture.

### OpenXR hardware procedure

The repository's SteamVR OpenXR smoke runner is documented in the [runtime guide][runtime-guide] and the [hardware validation record][hardware-validation]:

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

Do not invent a command-line flag for `SequentialViews`; use the existing rendering setting. Explicit `VR.Mode=OpenXR` must never fall back to OpenVR. Headset rendering baselines, sequential and single-pass stereo, are owned by the [parity TODO][parity-todo]; this milestone consumes them.

- [x] **W80.05** Record the headset, controller models, tracker models and count, runtime and version, active manifest, GPU and driver, backend, view mode, and implementation commit.
- [ ] **W80.06** Calibrate at 6 points and with as many trackers as are available (up to 11 points), using trackers in their default SteamVR state and trackers swapped between sessions.
- [ ] **W80.07** Test standing, looking around independently of body yaw, turning, crouching, lifting either foot, crossed feet after calibration, and room-scale walking.
- [ ] **W80.08** Test tracker occlusion, power cycling, swapping a tracker mid-session, controller loss, headset removal, dashboard focus, recenter, runtime and session restart, and the editor VR toggle.
- [ ] **W80.09** Run the calibration flow on each available controller profile: open, head-tilt refusal, capture, cancel, recalibrate, and avatar change.
- [ ] **W80.10** Run the spectator alongside VR and check full-avatar visibility, obstruction, teleport resets, input focus, output cadence, and audio-listener ownership.
- [ ] **W80.11** Measure headset frame time, missed deadlines, allocations, and spectator cost with the spectator disabled and enabled; report measurements, not inferences from code structure.

Short physical OpenGL Debug samples recorded first-person and spectator timing, deadline misses, and whole-process allocation rates. Every sampled headset submission missed its deadline, and the allocation rates include editor and MCP work; spectator-only cost has not been isolated. This partial measurement does not pass W80.11.
- [ ] **W80.12** Attach logs, normalized smoke summaries, test results, and short visual evidence, and update the hardware validation record with the tested commit, configuration, and remaining failures.

The evidence run contains named OpenGL hardware diagnostics, a normalized smoke summary, test reports, and synthetic spectator images. The Ready/zero-frame retry exposed a root-list enumeration exception; its stable-snapshot fix passed a deterministic regression, but a later physical retry returned `ErrorFormFactorUnavailable` before graphics startup. An initial simulated Vulkan run stopped on a concurrent mesh-swap fault after offscreen output was enabled. The corrected staged spectator path subsequently completed 119 captures through cuts, disable/re-enable, resize, and component reactivation without that fault. A final Vulkan offscreen readback produced a viewed, upright 1920 × 1080 image with finite HDR samples and no editor overlays. It still showed overbright avatar color and straight garment geometry in the full scene; its broad gray lower region is consistent with the scene's physics floor. A held Body-and-Shirt view did not reproduce the straight arms, and RenderDoc confirmed populated finite GPU skin palettes for both meshes in that capture. The full-scene visual cause remains unresolved. Full visual/color acceptance, final eye evidence, and physical Vulkan headset evidence remain missing. The final focused selection passed 36/36 and the broad selection retained the same 61 failure names. W60.15 and W80.12 remain open.

### Documentation

- [x] **W80.13** Update the [runtime guide][runtime-guide]'s tracker sections: remove the instruction to assign SteamVR roles once W20 lands, and document any recorded runtime limitation.
- [x] **W80.14** Document the calibration flow, measurement settings, failure messages, spectator controls, and which capture outputs were validated.
- [ ] **W80.15** Mark work complete only with behavioral tests and hardware evidence attached; checkboxes in older parity documents do not substitute for this evidence.

## 18. Definition of done

The milestone is complete when the following are demonstrated on a named, tested configuration:

- [ ] OpenXR renders a correct first-person view while controller input stays routed to the VR player.
- [ ] At 6 to 11 points, trackers bind one-to-one by proximity, calibrated targets survive solver updates, and the chest, upper arms, and knees drive the solver.
- [ ] No SteamVR role is read, required, or mentioned to players; any runtime limitation is documented as such.
- [ ] The section 2.3 flow works on every supported controller profile, is transactional, and has no mute side effect.
- [ ] Avatar scale comes only from the player's measurement setting.
- [ ] Tracking loss, reconnection, tracker swaps, recenter, and repeated calibration cause no origin snaps, duplicate nodes, silent re-binding, or accumulated offsets.
- [ ] The spectator shows the complete avatar without taking headset ownership, the tracking origin, or controller possession.
- [ ] Regression tests and hardware evidence identify the commit, runtime, backend, and outstanding limitations.

Quality at 3 to 5 points is accepted under the [body estimation TODO][estimator-todo]. W70 session continuity is a P2 follow-up and W60.16 is optional; neither is implied by this definition of done.

## 19. Guardrails

- Keep each change focused and independently testable; reproduce a failure before fixing it.
- Type and setting names in this document are proposals. Prefer extending existing runtime-neutral services and component contracts.
- Preserve assembly dependency direction, render-thread ownership rules, and explicit runtime selection.
- Record unsupported hardware behavior honestly instead of hiding it behind a fallback.

## Source references

Links are repository-relative; findings describe `7ab827983`.

- Player and calibration: [player component][player], [calibrator][calibrator], [calibration settings][calibration-settings], [runtime calibrator bridge][runtime-calibrator], [player input set][input-set], [VR input service][input-services]
- IK: [VRIK solver][vr-solver], [humanoid IK base][ik-base], [IK tick base][base-ik-solver], [VRIK solver core][ik-solver-vr], [VRIK spine solver][ik-spine], [humanoid component][humanoid]
- Scale: [height scale base][height-scale], [VR state][vr-state], [VR state contract][vr-state-contract], [VR lifecycle][vr-lifecycle]
- Trackers and OpenXR: [tracker collection][tracker-collection], [tracker transform][tracker-transform], [device transform base][device-transform], [OpenXR core input][input-core], [runtime-neutral OpenXR input][input-neutral], [OpenXR state][xr-state], [OpenXR frame lifecycle][frame-lifecycle]
- Pawn construction: [runtime pawn factory][bootstrap-pawns], [editor pawn factory][editor-pawns], [editor pawn switcher][pawn-switcher]
- Tests: [synthetic rig][synthetic-rig], [calibration tests][calibration-tests]
- Khronos: [`XR_HTCX_vive_tracker_interaction`][htcx-spec], [`xrSuggestInteractionProfileBindings`][suggest-bindings]

[estimator-todo]: vr-full-body-estimation-todo.md
[baseline-evidence]: ../../investigations/avatar/vr-calibration-baseline-2026-09-24.md
[toggle-todo]: ../rendering/vr/editor-openxr-toggle-and-rendering-todo.md
[parity-todo]: ../rendering/vr/openxr-steamvr-openvr-parity-todo.md
[hardware-validation]: ../../testing/openxr-steamvr-hardware-validation.md
[runtime-guide]: ../../../developer-guides/vr/openxr-runtime.md
[pipeline-notes]: ../../../architecture/rendering/default-render-pipeline-notes.md
[mesh-submission]: ../../../architecture/rendering/mesh-submission-strategies.md
[player]: ../../../../XREngine.Runtime.InputIntegration/Scene/Components/VR/VRPlayerCharacterComponent.cs
[calibrator]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/VRIKCalibrator.cs
[calibration-settings]: ../../../../XREngine.Animation/VRIKCalibrationSettings.cs
[runtime-calibrator]: ../../../../XREngine.Runtime.Core/Components/Animation/RuntimeVRIKCalibrator.cs
[input-set]: ../../../../XREngine.Runtime.InputIntegration/Scene/Components/Pawns/VRPlayerInputSet.cs
[input-services]: ../../../../XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeVrInputServices.cs
[vr-solver]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/VRIKSolverComponent.cs
[ik-base]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/HumanoidIKComponentBase.cs
[base-ik-solver]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/BaseIKSolverComponent.cs
[ik-solver-vr]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.cs
[ik-spine]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.Spine.cs
[humanoid]: ../../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs
[height-scale]: ../../../../XREngine.Runtime.InputIntegration/Scene/Components/Movement/HeightScaleBaseComponent.cs
[vr-state]: ../../../../XREngine.Runtime.Rendering/Runtime/RuntimeVrState.cs
[vr-state-contract]: ../../../../XREngine.Input/RuntimeVrStateServices.cs
[vr-lifecycle]: ../../../../XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs
[tracker-collection]: ../../../../XREngine.Runtime.InputIntegration/Scene/Components/VR/VRTrackerCollectionComponent.cs
[tracker-transform]: ../../../../XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRTrackerTransform.cs
[device-transform]: ../../../../XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRDeviceTransformBase.cs
[input-core]: ../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.cs
[input-neutral]: ../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.RuntimeNeutral.cs
[xr-state]: ../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs
[frame-lifecycle]: ../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs
[bootstrap-pawns]: ../../../../XREngine.Runtime.Bootstrap/BootstrapPawnFactory.cs
[editor-pawns]: ../../../../XREngine.Editor/Unit%20Tests/Default/UnitTestingWorld.Pawns.cs
[pawn-switcher]: ../../../../XREngine.Editor/EditorOpenXrPawnSwitcher.cs
[synthetic-rig]: ../../../../XREngine.UnitTests/Animation/SyntheticVrCalibrationRig.cs
[calibration-tests]: ../../../../XREngine.UnitTests/Animation/VRIKCalibrationTests.cs
[htcx-spec]: https://registry.khronos.org/OpenXR/specs/1.1/man/html/XR_HTCX_vive_tracker_interaction.html
[suggest-bindings]: https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrSuggestInteractionProfileBindings.html
