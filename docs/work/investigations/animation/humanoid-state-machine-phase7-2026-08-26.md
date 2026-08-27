# Humanoid State-Machine Phase 7 Investigation

Date: 2026-08-26

Status: Phase 7 implementation and native live matrix complete; versioned
Unity known-answer conformance remains Phase 10 work

## Objective

Implement one native, model-independent Unity humanoid evaluation path for
state-machine leaves, blend trees, transitions, and layers. Each active motion
must retain its own Body sample, root projection policy, canonical reference,
sample clock, and loop epoch until runtime avatar evaluation is complete.

## Pulled Phase 6 Baseline

- Direct and one-leaf state-machine playback share the Phase 6 root projection
  policy and compiled `HumanoidComponent` avatar definition.
- State-machine scalar `RootQ` components are grouped and normalized during
  typed-store blending.
- State-machine playback currently resolves one root-motion clip only. It
  recursively counts every reachable blend-tree clip, regardless of its active
  weight, and rejects the sample when more than one clip exists.
- The runtime currently wraps the already-blended `RootT`/`RootQ` scalar result
  in one humanoid transaction. At that point the contributing clips' settings,
  canonical samples, sample phases, and loop epochs have already been lost.

## Phase 6 Issues Found While Scoping Phase 7

1. The loop-cycle counter is read after state motions advance, while the Body
   sample phase was captured before the tick. A boundary frame can therefore
   pair one sample with the following loop epoch.
2. `BlendTree1D` can index child `-1` below its first threshold and does not
   clamp its interpolation interval.
3. `BlendTreeDirect` repeatedly copies complete child stores, so later children
   overwrite earlier children instead of producing a direct weighted blend.
4. Scalar quaternion grouping recognizes only Body `RootQ`; Unity IK rotations
   and imported generic transform `QuaternionX/Y/Z/W` channels remain unrelated
   scalar values in blends.

## Implementation Direction

- Carry a preallocated, engine-neutral contribution buffer in parallel with
  every `AnimationValueStore`. A leaf contribution snapshots its clip identity,
  compiled root policy, exact sample time/phase, and loop cycle before clocks
  advance.
- Propagate and scale those records through 1D/2D/direct blend trees and state
  transitions using the same weights as the pose values.
- Compose override layers by attenuation/replacement and retain additive-layer
  contributions in a separate domain.
- Prewarm avatar-dependent leaf caches when the state machine initializes.
  Runtime evaluation then samples and projects each active leaf independently,
  blends Body/root results deterministically, and publishes one composed result.
- Rebase temporal placement explicitly on state/transition lifecycle changes,
  seeks, replay, and evaluator handoff. Clip names are diagnostics only and are
  never part of evaluation identity.

## Implementation Completed

- Added a preallocated humanoid motion-contribution stream alongside each typed
  value store. Every active leaf retains its occurrence identity, clip-local
  root settings, exact state clock, sample phase, loop epoch, lifecycle
  generation, mirror state, and effective graph weight through avatar runtime
  evaluation.
- Made state clocks state-owned and unbounded so reusing a motion asset in
  multiple states or tree positions does not share mutable playback time.
- Reworked 1D, 2D, and direct trees to propagate the same weights into pose and
  humanoid contribution domains. This also corrected the 1D below-minimum child
  index and changed direct trees from last-child overwrite to an actual weighted
  blend, including the non-normalized-weight mode.
- Replaced the shared clip/settings gate with per-leaf avatar caches and a
  deterministic runtime compositor. Override and additive Body/root channels are
  separate; override rotations accumulate in quaternion tangent space and the
  single-contributor path preserves direct `Slerp` behavior.
- Added explicit lifecycle invalidation for entry, transition start/completion,
  interruption, replay, seek, and evaluator handoff. Transition interruption and
  self-replay preserve a source snapshot, and a completed transition emits its
  final `t = 1` frame before state ownership changes.
- Grouped scalar Body `RootQ`, Unity humanoid IK rotations, and generic transform
  `QuaternionX/Y/Z/W` components into atomic shortest-arc normalized quaternion
  operations.
- Added stable mirrored semantic slots for humanoid muscles, IK goals, vectors,
  and quaternions. Evaluation no longer mutates shared method arguments, so
  mirrored and unmirrored leaves can coexist in one graph without order-dependent
  binding state.
- Fixed an immediate-evaluation race: a zero-delta state-machine sample was
  composed correctly and then cleared by the next scene muscle tick. The
  humanoid now preserves that immediate result unless a newer staged frame
  arrives.
- Corrected converted-Body diagnostics to report the canonical-relative leaf
  delta, matching direct playback semantics rather than final Hips allocation.
- Made ordinary pose composition sparse-binding- and coverage-aware. Override
  and additive layers now honor `AnimLayer.Weight` for typed muscles/transforms
  as well as the Body/root sidecar, including non-normalized direct-tree weights.
- Added per-leaf additive reference-pose evaluation before blend-tree,
  transition, and layer composition.
- Executed imported state timing rather than only preserving it: exit-time
  crossing, fixed versus normalized transition duration, destination offset,
  interruption, Any State precedence, self-replay, and balanced enter/exit
  callbacks.
- Unified child speed sign/magnitude and cycle-offset phase resolution across
  pose sampling, seeks, and Body/root contribution collection. This prevents
  double speed application and keeps reverse and zero-speed playback coherent.
- Made arbitrary-weight quaternion accumulation registration-order-independent
  by selecting a deterministic canonical reference before shortest-arc
  normalization.

## Validation Evidence

An isolated named editor session ran `desktop/misc/Mitsuki.fbx` with
`Assets/Walks/Sexy Walk.anim` through the one-state state-machine path. Rendering
was deliberately excluded from acceptance because renderer work is being handled
separately; validation used runtime pose/root state and logs.

- The imported clip was a 3.2-second looping Unity humanoid-muscle clip with
  Body/root and IK channels. The finalized avatar reported all required bones,
  39 mapped bones, no mapping fallbacks, and 95% profile coverage.
- Fixed samples at 0.8 s, 1.6 s, and 2.4 s produced the same Hips local
  translation and rotation as direct playback to printed float precision.
  Projected-root values matched with only ordinary floating-point rounding; the
  largest observed component delta was approximately `3.8e-6`.
- At 1.6 s, both paths reported converted Body translation
  `<0.9558003, 0.39088184, 0.8099559>` and rotation
  `{X:0.04161873 Y:-0.10924073 Z:-0.022250306 W:0.9928944}`.
- Normal playback stayed finite, retained unit Hips scale, advanced evaluation
  sequence `4413 -> 4510 -> 4603`, crossed loop cycle `23 -> 24`, retained one
  active contributor, and reported no contribution overflow or rejection.
- Renaming the in-memory clip from `Sexy Walk` to `Renamed Phase 7 Probe` left
  Hips, converted Body, and projected-root output bit-identical. The original
  name was restored before shutdown.
- The animation log contained no exception, error, overflow, rejection,
  non-finite value, or failed avatar evaluation. The general log contained one
  missing optional `HumanoidIKSolverComponent` lookup. Body animation remained
  valid; the overly broad warning text was corrected so future logs state only
  that downstream bindings below the failed lookup are skipped.

The completion matrix then ran three additional isolated named editor sessions.
Rendering was deliberately excluded because renderer work is concurrent and was
outside this task.

- Mitsuki used Sexy Walk, Basic Walk, and Shutka Walk in 1D, 2D, direct,
  additive, mirror, reverse-speed, cycle-offset, transition, and authored-IK
  cases. Its generated avatar definition reported all required bones, 39 axis
  mappings, calibrated IK, and 95% profile coverage.
- The exact `ARYIA.fbx` extracted from
  `C:\Users\DavidEddy\Downloads\Aryia_By_Mimiiu_V1.3.unitypackage` ran through
  the same graph and production path. Its generated avatar definition reported
  all required bones, 39 axis mappings, calibrated IK, and 93% profile coverage.
- In both avatars, changing a frozen direct child from weight 0 to 0.7 and back
  changed contributor count `3 -> 4 -> 3` without changing the lifecycle epoch,
  kept root delta identity, and returned exactly to the starting pose.
- A genuine in-flight transition interruption was observed repeatedly. Captured
  frames covered contributor counts 2 through 9 within capacity 16, advancing
  lifecycle epochs and evaluation sequence without non-finite data, rejection,
  or overflow.
- Five IK solvers were active on each avatar. All four authored hand/foot goals
  reported `AppliedAuthored`; no animation/IK warning was emitted.
- Missing Aryia textures referenced by the package creator's machine produced
  material warnings only and were not treated as animation failures.

All temporary Unit Testing World model/state-machine code was removed, the
original settings file was restored byte-for-byte, and all three named sessions
were stopped. Production code contains no model, package, path, or clip-name
special case.

Final targeted builds of `XREngine.Animation`,
`XREngine.Runtime.AnimationIntegration`, and `XREngine.Editor` completed with
zero warnings and zero errors. The effective Phase 7 diff also passed
`git diff HEAD --check`.

## Remaining Work

No known Phase 7 runtime implementation item remains. Later parity phases still
need to close these broader contracts:

- Phase 8: weighted tangents, all declared infinity modes, events, object
  bindings, and compressed/dense/streamed `.anim` encodings.
- Phase 9: finish the fully native avatar solver and editor correction workflow
  without fitted, avatar-specific, or clip-specific production calibration.
- Phase 10: check in redistributable, versioned known-answer data and run strict
  numerical whole-pose/root/IK comparisons, including reversed child
  registration. This validation must not launch or require Unity.
- Per repository policy, no tests were added or run during feature work. Add the
  deterministic regression matrix only after explicit user clearance; until
  then, the isolated live-editor evidence is the Phase 7 acceptance record.
