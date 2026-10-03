# Animation and IK stability investigation

## Scope and evidence

The reported symptom is visible IK flickering while `Assets/Walks/Sexy Walk.anim` plays. That real animation is present, along with a copy in the humanoid conformance fixtures. This investigation uses the production importer, animation evaluator, transforms, and solvers through the portable headless suite. It does **not** establish rendered flicker resolution: no matching editor/avatar capture or physical XR hardware was available in this run.

## Reproduced defects

1. The analytic limb solver aimed the lower bone using its cached position from before the upper-bone rotation. Transform world matrices are cached; local writes do not immediately update descendant matrices or the inverse parent rotation needed by `SetWorldRotation`. A reachable fixed target missed by **0.31185836 model units** after one solve and moved by **0.31185848** on the next identical solve.
2. `BaseIKSolverComponent` cleared external-evaluation suppression only from `FixedUpdate`. Without a physics tick, following animation frames remained suppressed. With a physics tick between an external evaluation and Late, the same animation frame solved twice. Both interleavings fail deterministic callback-count regressions on the original implementation.

## Changes

- Refresh the affected ancestor-to-bone matrix path between analytic upper/lower writes. Aim the lower segment from the newly positioned joint. Render publication stays deferred for scene-owned transforms; no render-thread bone writes were added.
- Consume external-evaluation suppression at the next scheduled Late callback. Physics updates no longer reset it.

After the changes the fixed-target error is **1.6049042e-7** and repeated-solve movement **2.6151432e-7** model units. Both scheduler interleavings pass.

The actual Sexy Walk clip is imported and evaluated at 90 samples across its full duration on the existing synthetic humanoid fixture. Each native humanoid frame must be accepted and its left-foot authored IK goal applied. Repeated exact samples have **zero measured left-foot drift**. This validates deterministic production animation/IK evaluation with a synthetic skeleton; it is not visual evidence for the user's avatar or a claim of independent Unity conformance.

## Reproduction

Run the existing `XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj` with .NET 10, then select `XREngine.UnitTests.Animation.IKAnimationStabilityTests` in NUnitLite (`--workers=0`). The fixture is also compiled by the ordinary desktop unit-test project.

Remaining live validation: play the same asset on the user's affected avatar, capture multiple consecutive rendered frames from at least two camera positions, and verify the scheduler and transform changes eliminate the reported flicker at differing physics/render rates. Tracking snapshot publication and calibrated-target ownership are separate integration concerns.
