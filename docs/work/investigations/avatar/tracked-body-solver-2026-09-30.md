# Tracked chest and upper-arm solver constraints

## Target contract

- `IKSolverVR.Spine.ChestTarget` is a calibrated chest-bone orientation. `ChestTargetWeight` controls its influence. It is separate from the existing positional `ChestGoal` look-at control.
- `IKSolverVR.ArmSolver.UpperArmTarget` is the calibrated upper-arm bone pose. Its position chooses the clavicle direction while preserving clavicle length; its rotation and the local upper-arm axis choose the elbow bend plane. `ArmSettings.UpperArmTargetWeight` controls both influences.
- Hand position/orientation remains the final arm constraint within physical chain reach. The upper-arm tracker does not translate the wrist or stretch the skeleton to satisfy an incompatible pose. Missing or zero-weight targets preserve the existing untracked behavior. Degenerate bend planes retain the ordinary solver fallback.
- Chest orientation is a soft constraint applied before the existing head and pelvis reach corrections. The headset and hips take precedence when constraints disagree. Chest pitch/roll is consequently not guaranteed to equal the tracker exactly; this is a documented compromise rather than an exact multi-effector constraint solver.
- Target matrices are sampled during the solver's simulation-side `PreSolve`; there are no render-thread bone writes or new per-frame allocations.

## Reproduced underlying defects

The new reach assertions uncovered two pre-existing virtual-chain defects:

1. `VirtualBone.GetBendDirection` used negative longitudinal reach despite `XRMath.LookRotation` mapping local positive Z toward the requested direction. A reachable two-segment target missed by **1.3252485** model units.
2. Solves skipping intermediate joints used adjacent-bone cached lengths instead of the distances between the selected joints. A shoulder/intermediate/elbow/hand-style regression missed by **0.5233683** model units even after correcting the direction sign.

Using positive reach and measuring the selected segments reduces both regression errors to **1.6049042e-7** model units. This applies to existing shoulder, spine, and foot solves that select non-adjacent indices, without adding an alternate IK implementation.

## Headless evidence and limits

`VRIKAdditionalTargetTests` runs actual VRIK solver updates on `SyntheticVrCalibrationRig`, with deliberately reachable controller targets and the heuristic shoulder swing disabled to isolate the new tracker constraint:

- A 0.2-radian chest yaw is reproduced exactly; compatible pitch and roll samples finish with approximately **0.0713** and **0.0890** radians of residual error after head/pelvis correction. Head error stays below **0.00018** and hand displacement below **1e-6** model units.
- Both upper-arm targets move the upper-arm joint by approximately **0.1235** model units and alter elbow direction while wrist displacement stays below **2e-7** model units.
- Direct and skipped-joint virtual-chain reachable-target regressions pass.

These are deterministic production-runtime numerical checks, not rendered or physical-tracker validation. The integration must map all calibrated slots to these properties and manage target weights for tracking loss. Named hardware, occlusion/reconnection, animated-avatar rendering, default heuristic shoulder combinations, and performance captures still require their respective runtime validation.

## Knee bend direction

A lifted-foot test exposed the existing knee-goal normal pointing away from the target on both sides. Signed outward knee movement was **-0.1900** model units instead of positive. Correcting the normal cross-product order and normalizing swivel quaternion axes gives **+0.1431** outward motion while foot displacement remains below **2e-7**. `VRIKKneeTargetTests` exercises left and right goals using the existing calibrated foot targets and the humanoid knee slots.

## Integrated fixed-headset target correction

Integrating the fixed headset-to-head mapping exposed a torso-compression case absent from the original captured-head setup: the synthetic headset target is 1.70 m while the straight skeleton's head is 1.78 m. With the same 0.2-radian chest-roll sample and unchanged acceptance thresholds, the residual increased to **0.14611429** radians.

The pelvis endpoint correction always selected the untracked sagittal bend plane (`anchor right`), overwriting the tracked torso's bend side. For a valid tracked chest sample, each pelvis correction triangle now retains its current bend-plane normal before correcting head/hip reach. Untracked or degenerate triangles retain the original fallback. This adds no allocation and leaves the head/hip endpoint correction active.

The integrated compressed-torso roll residual falls to **0.008597085** radians, with head error **0.000537683** and hand displacement **0.000003487** model units. The original seven additional-target cases pass without weakening thresholds. Coverage additionally exercises straight and compressed torso geometries and conflicting chest/head orientations, with explicit hip-position bounds.

After this correction the integrated headless suite passes **116/116** tests with no skipped tests, and the build reports zero warnings/errors. All six straight/compressed torso axis cases pass. For incompatible 1.2-radian chest pitch/roll goals with stationary headset and hips, the solver leaves chest residuals of approximately 0.739/0.661 radians while head drift remains below 0.000538 and hip drift below 0.0000016 model units, confirming the documented endpoint priority.
