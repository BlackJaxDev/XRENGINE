# VR Full-Body Estimation — TODO

**Project:** XRENGINE  
**Created:** September 26, 2026  
**Status:** Planned; nothing is implemented. `IKSolverVR`'s procedural leg locomotion is commented out, so 3-point play has no stepping today.  
**Depends on:** the [calibration TODO][calibration-todo]: W10 (per-slot source contract), W45 (body measurements and scale), W50 (fallback crossfade and discontinuity events), W35 (chest, upper-arm, and knee consumers), and W60 (spectator view for evaluation).

## 1. Goal

Estimate each body slot that no tracker currently drives—hips, chest, feet, knees, and upper arms—from the headset, the controllers, and any bound trackers, so that:

- 3-point play (headset and controllers only) produces a plausible, stable full body;
- 4- and 5-point setups, and any other partial tracker set, fill only the missing slots and stay consistent with the tracked ones;
- a tracker lost mid-session, through occlusion or a dead battery, hands its slot to the estimator and takes it back without a visible pop.

### Approach

Estimation is analytic: rigid-body geometry, kinematic limits, filtering, and small state machines over the tracked poses, using the avatar's proportions at the player's scale. There are no learned models, training data, motion-capture datasets, or inference runtimes. This keeps the output deterministic, debuggable, cheap enough to run every frame, and free of dataset licensing constraints.

### Non-goals

- Solving the skeleton: the estimator produces slot targets, and the VRIK solver still solves the bones.
- Hands and fingers, which the controllers and hand tracking already provide.
- Overriding tracked slots: a valid tracker always wins.
- Networking changes: remote players receive the solved pose as they do today.
- Calibration and avatar scale, which the calibration TODO owns.

## 2. Contract

### 2.1 Inputs

One coherent snapshot per frame:

- the headset and both controller grip poses, each with validity and sample time;
- for each of the eight slots, the bound tracker's calibrated target when currently valid;
- the tracking floor, the up axis, and the playspace-to-world transform;
- discontinuity events from calibration W50.09: teleport, snap turn, recenter, avatar replacement, and session change;
- artificial locomotion velocity from character movement, kept separate from physical motion;
- body proportions: the avatar's segment lengths at the scale set by the player's measurement, and the standing eye height that scale implies.

### 2.2 Outputs

- For each of the eight slots, an estimated target pose and a confidence.
- A posture state—standing, stepping, crouching, seated, lying, or transitioning—for diagnostics and for other systems.

The calibration TODO's per-slot source mixer chooses between tracker and estimate and owns the crossfade. The estimator never writes targets or bones itself.

### 2.3 Spaces and persistent state

- Upper-body estimates are recomputed each frame from the snapshot.
- Planted feet persist in world space, so both physical walking and artificial locomotion produce steps and planted feet never slide. Discontinuity events re-seed foot placement instead of stepping through the jump.
- The standing reference height starts from the player's measurement and is refined by the observed upright eye height. It is used only for posture detection and never changes avatar scale.

## 3. Work packages

| Work package | Priority | Dependencies | Exit condition |
|---|---|---|---|
| E00 — Contract, harness, and metrics | P0 | Calibration W10 | The replay harness reports metrics for a stub estimator |
| E10 — Standing | P0 | E00, calibration W45 | Stable standing and look-around at 3 points |
| E20 — Height changes | P1 | E10 | Crouching and bowing without foot sliding |
| E30 — Stepping and turning | P1 | E10 | Room-scale walking, turning, and artificial locomotion step naturally |
| E40 — Hybrid fill-in and handoff | P1 | E30, calibration W50 | Partial tracker sets and tracker loss blend seamlessly |
| E50 — Seated | P2 | E20 | Sitting is detected and posed, with a player override |
| E60 — Lying | P2 | E50 | Lying in any orientation with smooth transitions |
| E70 — Tuning, settings, and documentation | Release gate | All above | The section 4 quality bar is met on hardware |

### E00 — Contract, harness, and metrics

- [ ] **E00.01** Define the input snapshot and output types (2.1–2.2) in the animation integration layer next to the VRIK solver, using only runtime-neutral pose types.
- [ ] **E00.02** Connect to the per-slot source mixer. A stub estimator's output must flow through without affecting tracked slots.
- [ ] **E00.03** Build a deterministic replay harness that feeds recorded or synthetic tracking sequences through the estimator and reports the section 4 metrics.
- [ ] **E00.04** Add a ground-truth mode that hides bound trackers from the estimator and compares its output with them.
- [ ] **E00.05** Draw estimated slots, the support area, planted feet, and the posture state for debugging, and review behavior through the calibration TODO's spectator view.
- [ ] **E00.06** Set a per-avatar cost budget and measure the stub against it.

### E10 — Standing

- [ ] **E10.01** Derive the neck base from the headset using the avatar's eye-to-neck offset.
- [ ] **E10.02** Separate head yaw from body yaw. Body yaw follows the head through a dead zone and rate limit, is biased toward the controllers' horizontal midpoint when the hands are in front of the body, and turns fully when the head stays rotated or the feet step.
- [ ] **E10.03** Hang the spine from the neck toward the support center between the planted feet, using the avatar's spine length. Lean the torso when the head moves beyond what neck flexion covers, keeping the pelvis over the support area.
- [ ] **E10.04** Place the chest between neck and pelvis with a stiffness profile, rotated with body yaw.
- [ ] **E10.05** Place the feet flat on the floor at the avatar's hip width, toes along body yaw with a slight outward angle.
- [ ] **E10.06** Solve the knees as a two-bone chain from hip to foot, with the pole taken from body forward and foot yaw.
- [ ] **E10.07** Point the elbows down, back, and outward from the shoulder and hand orientation, or document reliance on the solver's arm heuristics where they are already adequate.
- [ ] **E10.08** Maintain the standing reference height (2.3).

### E20 — Height changes

- [ ] **E20.01** Crouch: when eye height drops below the standing reference, lower the pelvis and bend the knees over the planted feet, moving the hips back and the torso forward to keep balance.
- [ ] **E20.02** Bow: when the head moves forward and down without a matching drop in height, hinge at the hips with straight legs.
- [ ] **E20.03** Rise: above the standing reference, extend onto the toes within joint limits, clamping rather than stretching bones.
- [ ] **E20.04** Kneel: at a very low eye height with an upright torso, kneel instead of squatting.

### E30 — Stepping and turning

- [ ] **E30.01** Keep each planted foot fixed in world space until it steps.
- [ ] **E30.02** Start a step when a foot leaves its comfort region around the ideal support position, when body yaw rotates past a threshold, or when predicted pelvis motion needs support. While walking, swing one foot at a time and alternate.
- [ ] **E30.03** Aim each step at the pelvis position predicted at landing from physical and artificial velocity, plus the stance offset, clamped to leg reach.
- [ ] **E30.04** Swing along a timed arc whose height and duration scale with step length and speed, with heel-to-toe orientation.
- [ ] **E30.05** Pivot-step when turning in place; re-seed the feet on snap turns.
- [ ] **E30.06** Generate a gait for artificial locomotion with cadence and stride derived from speed and leg length, lengthening into a run above a speed threshold.
- [ ] **E30.07** Re-seed foot placement on discontinuity events without swinging through the jump.
- [ ] **E30.08** Remove `IKSolverVR`'s commented-out procedural locomotion once stepping lands, so the engine never runs two stepping systems.

### E40 — Hybrid fill-in and handoff

- [ ] **E40.01** Treat tracked slots as hard constraints and estimate missing slots consistently with them: tracked hips without feet step the legs under the real pelvis; tracked feet without hips place the pelvis between head and feet from the leg lengths; one tracked foot steps the other relative to it.
- [ ] **E40.02** Estimate the chest, knees, and upper arms from the solved hips and feet unless they are tracked.
- [ ] **E40.03** When a slot switches to the estimator, seed its state from the slot's last tracked pose—a lost foot stays planted where it was—so the crossfade starts from agreement. When the tracker returns, the mixer crossfades back.

### E50 — Seated

- [ ] **E50.01** Detect sitting from a stable lowered eye height with little horizontal motion and the controllers near lap height.
- [ ] **E50.02** Seat the pelvis below and behind the head with the thighs near horizontal and the feet planted forward; lean and turn from the torso.
- [ ] **E50.03** Blend between standing and sitting from eye-height velocity.
- [ ] **E50.04** Add a player posture override: automatic, always standing, or always seated.

### E60 — Lying

- [ ] **E60.01** Detect lying from a head near the floor with its up axis near horizontal, in any orientation: back, stomach, or either side.
- [ ] **E60.02** Extend the body along the floor from the head in the direction implied by the head orientation and controller positions, with the limbs at rest.
- [ ] **E60.03** Transition between standing, sitting, kneeling, and lying without popping.

### E70 — Tuning, settings, and documentation

- [ ] **E70.01** Expose `EstimateMissingBodyParts` (default on) and the posture override. Keep thresholds and gains in one internal settings object until hardware tuning justifies exposing them.
- [ ] **E70.02** Tune on hardware at 3 points, 4 (hips only), 5 (feet only), and 6, with trackers hidden or powered off mid-session.
- [ ] **E70.03** Document behavior, settings, and known limits in the VR user and developer guides.
- [ ] **E70.04** Record hardware evidence alongside the calibration TODO's W80 results.

## 4. Quality bar

Every work package's acceptance uses these invariants:

- Planted feet move less than 1 cm while planted, and feet never go below the floor.
- Knees never hyperextend or invert, and pole direction stays continuous.
- While standing still, the pelvis stays over the support area.
- Per-frame target changes stay within a bound except at discontinuity events, and handoff crossfades have no single-frame jump beyond that bound.
- The same input sequence produces the same output, and results agree within tolerance across 60–144 Hz update rates.
- Estimation makes no per-frame heap allocations, and measured cost per avatar stays within the E00 budget.
- On recorded sessions with real trackers, hips and feet error with those trackers hidden is reported per behavior. Thresholds are set from the first baseline and must not regress.

## 5. Validation

- Deterministic tests in `XREngine.UnitTests/` use small checked-in synthetic trajectories: idle, look-around, turning in place, walking, crouching, sitting, and lying. Recorded sessions are optional evidence under `Build/_AgentValidation/`; no test depends on them.
- Live validation runs through the spectator view at 3 points, hips only, feet only, and 6 points, including trackers powered off mid-session.
- While debugging a regression, validate through the live path before adding or changing tests.

## 6. Guardrails

- A valid tracked slot is never overridden.
- Estimation runs on the simulation thread before IK, never on the render thread.
- No per-frame allocations, LINQ, captured closures, or boxing; state lives in preallocated storage.
- Never key behavior to a specific avatar, player, or recording.
- Keep OpenXR and OpenVR types out of the estimator; it consumes runtime-neutral snapshots only.

[calibration-todo]: openxr-full-body-calibration-spectator-todo.md
