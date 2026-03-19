# Unity Humanoid `.anim` Assumptions Audit

Date: 2026-03-03 (updated 2026-03-07)

Scope:
- Clip: `Assets/Walks/Sexy Walk.anim` — confirmed full Unity humanoid muscle clip (Path A)
- Current test settings: `Assets/UnitTestingWorldSettings.jsonc`
- Runtime path traced through `.anim` import, `AnimationClipComponent`, and `HumanoidComponent`
- Goal: identify incorrect assumptions, not propose code changes

Update 2026-03-07:
- A direct pose audit against Unity's `HumanPose` output showed that the engine is faithfully replaying the raw `.anim` float curves, but those raw values do not match Unity's sampled `HumanPose.muscles` for `Sexy Walk.anim`.
- Representative sample-0 differences:
  - `Left Arm Down-Up`: Unity `0.400032`, raw `.anim` / engine `-0.687864`
  - `Left Upper Leg Front-Back`: Unity `0.599612`, raw `.anim` / engine `0.862525`
  - `Right Upper Leg Front-Back`: Unity `0.599612`, raw `.anim` / engine `-0.021309`
  - `Spine Front-Back`: Unity `0.000000`, raw `.anim` / engine `0.090756`
- This means the current pipeline's first broken assumption is earlier than bone application: we currently treat the humanoid float curves as if they were already in the same semantic space as Unity's `HumanPose.muscles`, and for this clip that assumption is false.
- The Unity audit exporter was then extended to emit both `HumanPose` samples and editor-evaluated raw curve bindings plus default muscle ranges, so future audit JSONs can prove whether the importer matches raw Unity clip data or whether the divergence appears even earlier.

**Revised goal framing** (clarified by user): we are not trying to replicate all of Unity Mecanim. The target is a character with Unity's **default humanoid setup** — meaning the muscle ranges, bone axis conventions, and body-space definitions are Unity's published defaults, not avatar-specific overrides. The fix should produce correct output using good defaults plus any per-model adjustments derivable from the imported FBX bind pose, without requiring a Unity avatar asset.

---

## Runtime Path Verified

1. `AnimationClip.Load3rdParty()` routes `.anim` files to `AnimYamlImporter.Import()` (`XREngine.Animation/Property/Core/AnimationClip.cs:252`, `XREngine.Animation/Importers/UnityAnimImporter.cs`).
2. The current unit-test world loads `Assets/Walks/Sexy Walk.anim` and attaches it with `AnimationClipComponent` (`Assets/UnitTestingWorldSettings.jsonc`, `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs:455-470`).
3. `AnimationClipComponent` evaluates animated members in `ETickOrder.Animation` and then applies values to methods/properties (`XRENGINE/Scene/Components/Animation/AnimationClipComponent.cs:95-128`).
4. `HumanoidComponent.ApplyMusclePose()` runs later in `ETickOrder.Scene`, so the muscle values are sampled first and applied second (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:29,151`, `XRENGINE/Scene/Components/XRComponent.cs:407-425`).
5. In the current settings, `AddCharacterIK` is `false`, and avatar import explicitly sets `HumanoidComponent.SolveIK = false` unless extra test IK is enabled (`Assets/UnitTestingWorldSettings.jsonc`, `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs:271-272,326`).

**Confirmed**: `Sexy Walk.anim` is a full Unity humanoid clip containing root channels (`RootT`, `RootQ`), IK goal channels (`LeftFootT/Q`, `RightFootT/Q`, `LeftHandT/Q`, `RightHandT/Q`), full humanoid muscle channels, and blendshapes. It routes entirely through **Path A** (muscle values → `HumanoidComponent.SetValue` → `ApplyMusclePose`). No explicit per-bone transform quaternions are involved for the main pose. The `ConvertRotation` path at importer line 261 is **not exercised** for this clip's primary data.

Conclusion:
- The current "close but not correct" result is not explained only by `HumanoidComponent` bone application.
- The imported humanoid float curves themselves already diverge from Unity's sampled `HumanPose.muscles`, so the pipeline is misidentifying the meaning of the clip data before the bone solve even starts.
- Import-time coordinate conversion is still not the issue for this clip's primary pose path.

---

## Coordinate Conversion Math: Verified Correct

The engine uses RH Y-up (OpenGL), Unity uses LH Y-up (Z-forward). The transform between them is `M = diag(1,1,-1)` (Z-reflection).

### ConvertRotation formula

For an improper transformation (det(M) = −1):

```
M · R(â, θ) · M = R(M·â, −θ)
```

Quaternion form: `q = (sin(θ/2)·ax, sin(θ/2)·ay, sin(θ/2)·az, cos(θ/2))` → `q_RH = (−q.X, −q.Y, q.Z, q.W)`

The `ConvertRotation` function at [UnityAnimImporter.cs:29-30](XREngine.Animation/Importers/UnityAnimImporter.cs#L29) implements exactly this — **the formula is mathematically correct** for world-space and root-motion quaternions.

### Two rotation strategies in HumanoidComponent

`ApplyMusclePose` uses two strategies to produce bone rotations from muscle degrees:

**Strategy A** — `ApplyBindRelativeEulerDegrees` ([HumanoidComponent.cs:504-558](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L504)):
```csharp
q = Quaternion.CreateFromYawPitchRoll(-yawDeg * degToRad, -pitchDeg * degToRad, rollDeg * degToRad);
```
Negates yaw (Y-axis) and pitch (X-axis); keeps roll (Z-axis).

**Strategy B** — `ApplyBindRelativeSwingTwistWorldAxes` ([HumanoidComponent.cs:601-658](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L601)):
```csharp
twistLocal     = new(-twistLocal.X,     -twistLocal.Y,     twistLocal.Z);
frontBackLocal = new(-frontBackLocal.X, -frontBackLocal.Y, frontBackLocal.Z);
leftRightLocal = new(-leftRightLocal.X, -leftRightLocal.Y, leftRightLocal.Z);
```
Negates X and Y components of the bone-local axis vectors; keeps Z.

**Both strategies are mathematically equivalent** implementations of the same Z-reflection:
- Strategy A: negate the rotation angle for axes lying in the XY plane → same as `(−q.X, −q.Y, q.Z, q.W)`
- Strategy B: negate the axis vector's X and Y components → `CreateFromAxisAngle((−ax, −ay, az), θ)` → same quaternion

**The LH→RH math is not the bug.** The issue lies in what the axes and degree values represent for each bone in the imported rig.

---

## Incorrect Or Risky Assumptions

### 1. The runtime assumes Unity humanoid clips can be reproduced without Unity avatar data — but for a default humanoid setup, this is mostly fine

Verified:
- The `.anim` importer only reads curve names and scalar/vector data.
- There is no Unity `Avatar`, `HumanDescription`, per-bone pre-rotation, twist distribution, or Mecanim retarget data imported anywhere in this path.
- `HumanoidComponent` reconstructs pose from discovered bone names, bind matrices, heuristics, and hard-coded ranges (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1116-1326`).

**Revised conclusion** (accounting for revised scope):
- For an avatar created in Unity with the **default humanoid setup**, Unity's muscle ranges are well-documented public constants (e.g., Spine Front-Back ±40°, Arm Down-Up −60..100°, etc.).
- Our hardcoded defaults in `HumanoidComponent` are taken from those published Unity defaults and should be correct for this case.
- The hard-coded ranges are **not** the primary source of error for a default-setup avatar.
- The remaining mismatch is the rig-space and axis-direction assumptions, not range amplitudes.

### 2. The bone-axis profile data does not actually drive most of the visible humanoid pose

Verified:
- `AvatarHumanoidProfileBuilder.BuildProfile()` profiles spine, chest, neck, shoulders, arms, legs, feet, and fingers into `HumanoidSettings.BoneAxisMappings` (`XRENGINE/Scene/Components/Animation/AvatarHumanoidProfileBuilder.cs:76-155,162-199`).
- `HumanoidComponent` only consults `GetBoneAxisMapping()` for:
  - elbows
  - wrists
  - knees
  - feet
  - toes
  - fingers
  (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:303-350,423-442,445-450`)
- Spine, chest, upper chest, neck, head, jaw, shoulders, upper arms, and upper legs do **not** use `BoneAxisMappings` at application time (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:167-214,283-323`).

Why this matters:
- The code computes profile confidence for the whole skeleton, but the bones that define most of the visible silhouette (spine chain, upper arms, upper legs) still use generic/default axis assumptions.
- A "good" profile score does not mean the runtime is actually using those mappings where pose accuracy matters most.

### 3. "Profile confidence" is being used as if it were IK-space calibration

Verified:
- `BuildProfile()` sets `Settings.ProfileConfidence`.
- If confidence is `>= 0.6`, it also sets `Settings.IsIKCalibrated = true` (`XRENGINE/Scene/Components/Animation/AvatarHumanoidProfileBuilder.cs:110-114`).
- `HumanoidSettings` documents `IsIKCalibrated` as "avatar-space -> world-space conversion" validity for animation IK goals (`XRENGINE/Scene/Components/Animation/HumanoidSettings.cs:236-240`).
- Animated IK goal application is gated by `ShouldApplyAnimatedIKGoal()` using that same flag (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1637-1678`).

Why this matters:
- Axis-confidence and avatar-space IK calibration are not the same problem.
- A rig can have confident local bone-axis guesses and still have no valid mapping from Unity avatar-space IK goal coordinates into engine world space.

Conclusion: the current meaning of `IsIKCalibrated` is semantically incorrect.

### 4. Upper-limb and upper-leg motion assumes the hips bind basis is the avatar body basis

Verified:
- `ApplyLimbMuscles()` derives `bodyRight` and `bodyForward` from the hips bind matrix only (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:271-279`):

```csharp
Vector3 bodyRight   = GetBodyAxisWorld(Vector3.UnitX);   // hips local +X → world
Vector3 bodyForward = GetBodyAxisWorld(-Vector3.UnitZ);  // hips local −Z → world (engine forward = −Z)
```

- Shoulders, upper arms, and upper legs are then driven from hips-derived body axes, the bone-to-child direction as twist axis, and fixed Euler composition order (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:283-323,601-657`).

Why this matters:
- Imported FBX rigs frequently have hip pre-rotation, bone roll, or helper-node conventions that do not make the hips local X/Y/Z axes equal to the anatomical right/up/forward axes.
- This can produce a result that is "very close" but not quite the same as Unity's Mecanim solve.

**This is the strongest candidate for the remaining discrepancy with Sexy Walk.**

Additional issue — the side-axis direction for the right limb:

```csharp
Vector3 sideAxisWorld = Vector3.Normalize(bodyForward * sideMirror);
// Left:  sideAxisWorld =  bodyForward = −Z_world (engine forward)
// Right: sideAxisWorld = −bodyForward = +Z_world (engine backward)
```

This `sideAxisWorld` becomes the rotation axis for the arm/leg **Down-Up** muscle. For the left arm in T-pose (pointing along +X):
- Rotating +X around −Z (engine forward) with positive angle → arm moves toward −Y (downward)
- Muscle range `(−60, 100)` makes positive = arm up, which requires the rotation to go in the positive (upward) direction

For the right arm in T-pose (pointing along −X):
- Rotating −X around +Z (engine backward) with positive angle → arm also moves toward −Y (downward)

Whether the sign convention of the Down-Up muscle is identical between left and right in Unity's humanoid system is a **specific assumption that needs verification**. If Unity defines positive Down-Up as "arm raises" for both sides with the same sign, then the current `sideMirror` applied to `sideAxisWorld` may produce the correct behavior. If Unity mirrors the sign between sides, this is correct. This needs to be cross-referenced against Unity's humanoid muscle documentation.

### 5. Torso and head still use generic Euler assumptions, not rig-specific mappings

Verified:
- Spine, chest, upper chest, neck, head, and jaw are all applied with `ApplyBindRelativeEulerDegrees(...)` without any `BoneAxisMapping` (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:167-210`).
- The non-mapped path assumes:
  - yaw around local Y (twist)
  - pitch around local X (front-back)
  - roll around local Z (left-right)
  - negate Y and X for LH → RH
  (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:509-552`)

Why this matters:
- That is a generic coordinate assumption, not a verified property of the imported rig.
- The profile builder already knows the runtime does not have universal bone-axis conventions, but the torso/head path still behaves as if it does.
- For a Unity default humanoid FBX imported through Assimp with standard settings, the spine bones **should** have local Y pointing up (along the spine), X to the right, and Z forward. But this depends on how Assimp handles the FBX coordinate space conversion, and whether any pre-rotations from the FBX are baked into the bind pose or left as node transforms.

### 6. Root motion is assumed to be a direct hips bind-relative offset, with no clip rebasing

Verified:
- `RootT` and `RootQ` are imported and routed to `HumanoidComponent.SetRootPosition()` / `SetRootRotation()` (`XREngine.Animation/Importers/UnityAnimImporter.cs:271-321,631-646`).
- `SetRootPosition()` does `tfm.Translation = tfm.BindState.Translation + position` (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1687-1701`).
- `SetRootRotation()` directly calls `SetBindRelativeRotation(rotation)` (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1705-1715`).
- In `Sexy Walk.anim`, root channels are not zero-based at frame 0: `RootT.x` starts around `1.005`, and `RootQ` is not identity at time 0.

Why this matters:
- The code assumes Unity root curves are already bind-relative deltas for this rig.
- That is a stronger assumption than the clip format guarantees.
- Even if the direction conversion is right, body-center/root orientation is not guaranteed to equal the imported hips local frame.

Inference: root application is likely still approximate, even if it is no longer catastrophically wrong.

### 7. Forearm Stretch and Lower Leg Stretch inversion is a hardcoded assumption

Verified ([HumanoidComponent.cs:266-267](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L266)):
```csharp
float forearmStretchMuscle = -GetMuscleValue(forearmStretch);
float lowerLegStretchMuscle = -GetMuscleValue(lowerLegStretch);
```
Comment states: "positive values trend toward extension (straighter limb), not deeper flexion."

Why this matters:
- This inversion was added as a fix to a prior wrong assumption. The assumption it encodes — that Unity Forearm/LowerLeg Stretch `+1` = fully extended — is the correct Unity behavior per documentation.
- However, this was added as a deliberate reversal of a previous reversal. If that previous reversal was correct in some context, the current double-negation is wrong.
- For a **default humanoid setup**, Unity's documentation confirms that Forearm Stretch `+1` = elbow nearly straight (extended), so the inversion is likely correct.
- Risk level: **low** for default avatars, but the reasoning chain should be explicitly verified once against Unity's actual muscle output.

### 8. If animated IK goals are enabled later, they are treated as world-space targets

Verified:
- The importer converts and stores `LeftFootT/Q`, `RightFootT/Q`, `LeftHandT/Q`, `RightHandT/Q` (`XREngine.Animation/Importers/UnityAnimImporter.cs:324-396,583-629`).
- `SetAnimatedFootPosition()` and `SetAnimatedHandPosition()` call `SetFootPosition()` / `SetHandPosition()` when allowed (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1658-1678`).
- `SetFootPosition()` and `SetHandPosition()` store `(null, Matrix4x4.CreateTranslation(...))` — the target is a raw matrix, not a transform-relative offset (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1548-1593`).
- `GetMatrixForTarget()` uses `target.offset * (target.tfm?.RenderMatrix ?? Matrix4x4.Identity)`, so `tfm == null` means the animated target is interpreted directly in world space (`XRENGINE/Scene/Components/Animation/HumanoidComponent.cs:1046-1047`).

Why this matters:
- Unity humanoid IK goal curves are avatar-space/humanoid-space data, not guaranteed world-space targets.

Important for the current test: `AddCharacterIK = false`, so this is **not** the current visible bug. It is still an incorrect assumption in the end-to-end humanoid path.

---

## What's Definitively Not the Bug

- **A missed Z-flip on import-time transform curves**: Sexy Walk is a muscle clip (Path A). The `ConvertRotation` path at importer line 261 is not exercised for its primary pose data.
- **Animation tick order**: verified correct — muscle values sampled in Animation order, applied in Scene order.
- **Generic failure to read the file or deliver muscle values**: `SetValue` diagnostic logs confirm values are being delivered.
- **The LH→RH math in both rotation strategies**: proven mathematically correct and equivalent (both produce `q_RH = (−q.X, −q.Y, q.Z, q.W)` relative to LH input).
- **Hard-coded muscle degree ranges** (for a default humanoid avatar): the ranges in `HumanoidComponent` are taken from Unity's published defaults and should be correct for this specific case.

---

## Most Likely Cause Of "Very Close, But Not Correct"

Ranked by likelihood given the confirmed default-humanoid-setup context:

1. **The imported humanoid float curves are not the same data Unity reports through `HumanPose.muscles`** — the engine currently treats the `.anim` channels as final normalized muscles, but the audit shows that Unity's post-solve humanoid pose lives in a different semantic space for this clip. This is now the primary root-cause candidate.

2. **Hips bind-pose basis not aligning with anatomical body axes** — if the imported hips bind matrix has any unexpected pre-rotation (from Assimp's FBX coordinate conversion, or baked rig offsets), then `bodyRight` and `bodyForward` are wrong for all limb swing-axis calculations. This remains the strongest application-stage candidate once the input-space mismatch is solved.

3. **Spine/head bones have non-standard local axes after import** — Assimp's handling of Unity FBX files may produce spine bones with local coordinate frames that don't follow Y-up/X-right/Z-forward. The generic Euler convention (Y=twist, X=pitch, Z=roll) applied without `BoneAxisMapping` would then map the wrong muscles to the wrong axes.

4. **`BoneAxisMapping` is not used for the most visually important bones** — the profiler builds per-bone axis data for the entire skeleton but it is only consumed for terminal joints (elbows, wrists, knees, feet, fingers). The spine chain and upper limbs ignore it. Extending axis-mapping usage to those bones, or verifying the generic assumptions hold for the specific imported rig, is necessary.

5. **Right-limb Down-Up sideAxis direction** — the sign of `sideAxisWorld` is inverted for the right side (via `sideMirror`). Whether this matches Unity's muscle sign convention for the right arm/leg Down-Up channel needs explicit verification.

6. **Root-motion body center vs bind-pose offset** — `RootT/Q` are applied as bind-relative offsets to the hips, but the clip's frame-0 values are not identity. The body may be slightly offset or rotated relative to the true bind-relative rest position.

---

## Bottom Line

The current code path is coherent enough to explain why the pose is close, but the new audit evidence changes the priority order. The first question is no longer just "are our bone axes wrong?" It is "what do Unity's humanoid `.anim` float curves actually represent relative to `HumanPose.muscles`?" Until that input-space mismatch is resolved, tuning the bone application stage can only produce partial improvements.

Once the imported channel semantics are understood, the next questions remain the rig-space ones: whether the imported FBX skeleton's hips and spine bones have the local coordinate orientations that `HumanoidComponent` assumes (Y along the bone, X to the right, Z forward for spine bones; local −Z = world forward for the hips), and whether the generic Euler convention (Y=twist, X=pitch, Z=roll) with the current LH→RH sign flip is producing the correct rotation axes for each spine/head bone on this specific imported model.
