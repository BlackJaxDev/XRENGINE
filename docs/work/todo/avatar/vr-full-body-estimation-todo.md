# VR Full-Body Estimation TODO

Last Updated: 2026-10-06
Status: Planned. No estimator code exists. `IKSolverVR` procedural leg locomotion is commented out, so 3-point play has no stepping.
Architecture: [OpenXR body tracking](../../../developer-guides/vr/openxr-body-tracking.md), [Local VR body calibration](../../../developer-guides/vr/full-body-calibration.md), [Independent VR spectator](../../../developer-guides/vr/spectator-camera.md)
Validation: [Avatar Validation](../../testing/avatar/avatar-validation.md#vr-full-body-estimation)

## Current State

The body tracking contract is in code: per-slot sources, body measurements and scale, fallback crossfade and discontinuity events, chest, upper-arm, and knee consumers, and spectator output. Nothing estimates an untracked slot. The estimator described below fills hips, chest, feet, knees, and upper arms from the headset, controllers, and bound trackers, with analytic geometry, kinematic limits, filtering, and small state machines. It uses no learned model or dataset.

## Design

- Input: one snapshot per frame with headset and controller grip poses (validity and sample time), the calibrated target of each bound and valid tracker for the eight slots, the tracking floor, up axis, and playspace-to-world transform, the shared discontinuity events (teleport, snap turn, recenter, avatar replacement, session change), artificial locomotion velocity kept separate from physical motion, and avatar segment lengths at the player's measured scale.
- Output: an estimated target pose and a confidence per slot, and a posture state (standing, stepping, crouching, seated, lying, transitioning). The per-slot source mixer chooses tracker or estimate and owns the crossfade. The estimator never writes targets or bones.
- Upper-body estimates are recomputed each frame. Planted feet persist in world space. Discontinuity events re-seed foot placement. The standing reference height starts from the player's measurement, is refined by observed upright eye height, and never changes avatar scale.
- A valid tracked slot is never overridden. Estimation runs on the simulation thread before IK, allocates nothing per frame, never keys behavior to an avatar, player, or recording, and uses runtime-neutral types only (no OpenXR or OpenVR types).

## Open Code Items

### Contract and harness

- [ ] Define the input snapshot and output types next to the VRIK solver in the animation integration layer. Done when: the types use only runtime-neutral pose types.
- [ ] Connect a stub estimator to the per-slot source mixer. Done when: stub output flows through without changing tracked slots.
- [ ] Build a deterministic replay harness for recorded or synthetic sequences that reports the quality metrics, with a ground-truth mode that hides bound trackers and compares the output with them. Done when: the harness reports metrics for the stub.
- [ ] Add a debug draw for estimated slots, the support area, planted feet, and the posture state. Done when: the overlay shows in the spectator view.
- [ ] Add a per-avatar cost budget and a cost counter. Done when: the stub reports its cost against the budget.
- [ ] Add deterministic unit tests with small checked-in synthetic trajectories: idle, look-around, turning in place, walking, crouching, sitting, and lying. Assert: planted feet move less than 1 cm and never go below the floor; knees never hyperextend or invert and the pole stays continuous; the pelvis stays over the support area while standing still; per-frame target change stays within a bound except at discontinuities; the same input gives the same output; results agree across 60 to 144 Hz; no per-frame allocation. Done when: the tests exist in `XREngine.UnitTests/`.

### Standing

- [ ] Derive the neck base from the headset with the avatar eye-to-neck offset. Done when: the neck target follows the headset.
- [ ] Separate head yaw from body yaw. Body yaw follows the head through a dead zone and rate limit, biases toward the controller midpoint when the hands are in front, and turns fully when the head stays rotated or the feet step. Done when: a look-around trajectory keeps body yaw stable.
- [ ] Hang the spine from the neck toward the support center with the avatar spine length. Lean the torso when the head moves beyond neck flexion. Place the chest between neck and pelvis with a stiffness profile. Done when: the pelvis stays over the support area.
- [ ] Place the feet flat at hip width with toes along body yaw and a small outward angle. Solve knees as a two-bone chain with the pole from body forward and foot yaw. Done when: idle trajectories pass the foot and knee assertions.
- [ ] Point the elbows down, back, and outward, or document reliance on the solver arm heuristics. Done when: the upper-arm targets are set or the reliance is documented.
- [ ] Maintain the standing reference height. Done when: it refines from upright eye height without changing avatar scale.

### Height changes

- [ ] Crouch: lower the pelvis and bend the knees over planted feet, with hips back and torso forward. Bow: hinge at the hips with straight legs when the head moves forward and down without a height drop. Rise: extend onto the toes within joint limits and clamp. Kneel: kneel at very low eye height with an upright torso. Done when: crouch and bow trajectories pass without foot sliding.

### Stepping and turning

- [ ] Keep each planted foot fixed in world space until it steps. Start a step when a foot leaves its comfort region, when body yaw passes a threshold, or when predicted pelvis motion needs support. Swing one foot at a time. Done when: a walking trajectory alternates feet.
- [ ] Aim each step at the pelvis position predicted at landing from physical and artificial velocity plus the stance offset, clamped to leg reach. Swing along a timed arc that scales with step length and speed, heel to toe. Done when: walking trajectories pass the foot assertions.
- [ ] Pivot-step when turning in place. Re-seed feet on snap turns and other discontinuity events. Done when: a snap turn causes no swing through the jump.
- [ ] Generate a gait for artificial locomotion with cadence and stride from speed and leg length, and a run above a speed threshold. Done when: artificial locomotion produces steps.
- [ ] Remove the commented-out procedural locomotion from `IKSolverVR` when stepping lands. Done when: one stepping system remains.

### Hybrid fill-in and handoff

- [ ] Treat tracked slots as hard constraints. Tracked hips without feet step the legs under the real pelvis. Tracked feet without hips place the pelvis from the leg lengths. One tracked foot steps the other relative to it. Estimate chest, knees, and upper arms from the solved hips and feet unless tracked. Done when: ground-truth mode tests pass for 4-point and 5-point sets.
- [ ] Seed a slot that switches to the estimator from its last tracked pose (a lost foot stays planted). Done when: the handoff has no single-frame jump beyond the bound.

### Seated and lying

- [ ] Detect sitting from a stable lowered eye height, small horizontal motion, and controllers near lap height. Seat the pelvis below and behind the head with thighs near horizontal and feet forward. Blend from eye-height velocity. Done when: the sitting trajectory passes.
- [ ] Add a player posture override: automatic, always standing, or always seated. Done when: the setting changes posture selection.
- [ ] Detect lying from a head near the floor with its up axis near horizontal in any orientation. Extend the body along the floor from the head direction and controller positions. Transition between standing, sitting, kneeling, and lying without pops. Done when: the lying trajectory passes.

### Settings and documentation

- [ ] Expose `EstimateMissingBodyParts` (default on) and the posture override. Keep thresholds and gains in one internal settings object. Done when: the settings exist and serialize.
- [ ] Document behavior, settings, and limits in the VR user and developer guides. Done when: the guides describe the estimator.

### Calibration and spectator follow-ups

- [ ] Add a SteamVR tracker provider beside the OpenXR tracker transport that feeds `RuntimeVrTrackerInfo` snapshots, only after the provider decision is approved. Headset rendering and controller input stay on OpenXR. Done when: tracker serial identity, pose publication time, and the OpenVR-to-OpenXR reference-space transform are reconciled explicitly; an explicit setting selects the provider (never a silent fallback); and `RuntimeVrTrackerStatus` reports a tracker disabled in SteamVR separately from one that is not reported.
- [ ] Optional: connect an encoder or external capture integration to the completed spectator output lease. Done when: queues are bounded, GPU readback never blocks headset submission, and frame timestamps, audio alignment, and fall-behind behavior are defined.
- [ ] Optional: show the spectator output as an eye-level mirror while calibration is open. `BootstrapVrCalibrationFeedbackFactory`. Done when: the mirror appears only during calibration and reuses the completed spectator frame without a second scene render.

## Decisions Needed

- [ ] Approve or reject a separate SteamVR tracker provider. On the tested SteamVR OpenXR runtime, HTCX enumeration returned zero tracker paths while OpenVR reported valid tracker poses. Without a provider, body trackers cannot calibrate on that configuration through OpenXR. Owner: XR runtime.

## Out Of Scope

- Solving the skeleton. The VRIK solver solves bones.
- Hands and fingers.
- Overriding tracked slots.
- Networking changes.
- Calibration and avatar scale.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/avatar/vr-full-body-estimation-todo.md`

- [ ] **E10.07** Point the elbows down, back, and outward from the shoulder and hand orientation, or document reliance on the solver's arm heuristics where they are already adequate.
- [ ] **E20.03** Rise: above the standing reference, extend onto the toes within joint limits, clamping rather than stretching bones.
- [ ] **E20.04** Kneel: at a very low eye height with an upright torso, kneel instead of squatting.
- [ ] **E60.01** Detect lying from a head near the floor with its up axis near horizontal, in any orientation: back, stomach, or either side.
