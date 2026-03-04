# Unity .anim → Humanoid Rotation Pipeline: Full Audit

**Scope**: Deep analysis of the coordinate-system conversion assumptions, routing paths, and rotation-application logic for Unity `.anim` files imported via `AnimYamlImporter` and applied through `HumanoidComponent`. Written to document potential sources of the incorrect bone rotations observed with `Sexy Walk.anim` and `AnimClipPath = true`.

---

## 1. Coordinate System Ground Truth

| System | Handedness | Y | Z |
|--------|------------|---|---|
| Unity | Left-handed | Up | Forward (+Z) |
| Engine (OpenGL) | Right-handed | Up | Toward viewer (−Z is forward) |

The transform between them is a **Z-axis reflection**: `M = diag(1, 1, −1)`.

### Quaternion conversion under M

Given `M = diag(1,1,-1)`, which is an *improper* rotation (det = −1):

```
M · R(â, θ) · M = R(M·â, −θ)
```

Where `M·(ax, ay, az) = (ax, ay, −az)` and the rotation *sense* reverses (`θ → −θ`).

As a quaternion `q = (sin(θ/2)·ax, sin(θ/2)·ay, sin(θ/2)·az, cos(θ/2))`:

```
q_RH = (sin(−θ/2)·ax, sin(−θ/2)·ay, sin(−θ/2)·(−az), cos(−θ/2))
      = (−sin(θ/2)·ax, −sin(θ/2)·ay, sin(θ/2)·az, cos(θ/2))
      = (−q.X, −q.Y, q.Z, q.W)
```

The `ConvertRotation` function in [UnityAnimImporter.cs:29-30](XREngine.Animation/Importers/UnityAnimImporter.cs#L29) implements exactly this:

```csharp
private static Quaternion ConvertRotation(Quaternion q)
    => new(-q.X, -q.Y, q.Z, q.W);
```

**The formula itself is mathematically correct.** The question is whether applying it to specific quaternion data is appropriate.

---

## 2. Two Separate Animation Routing Paths

The importer creates two fundamentally different kinds of animation data from the same `.anim` file. Which path a curve takes depends on its YAML structure.

### Path A — Humanoid Muscle Curves

Scalar float curves with `classID = 95` and an empty `path`, whose `attribute` matches a human-readable muscle name (e.g., `"Spine Front-Back"`).

**Import**: Scalar values are passed through unchanged — no coordinate conversion.
**Runtime**: `HumanoidComponent.SetValue(EHumanoidValue, float)` → `ApplyMusclePose()` → rotations generated from scratch using degree ranges + LH→RH sign conventions.

### Path B — Explicit Transform Curves

Vector/quaternion curves with a `path` (bone name like `"Hips"` or `"Armature/Spine"`), containing `m_LocalRotation.x/y/z/w` components.

**Import**: `ConvertRotation(q)` is applied at line 261, producing `(-x, -y, z, w)`.
**Runtime**: The stored quaternion is written directly to `Transform.Rotation` — no further conversion.

> **Critical distinction**: `AnimClipPath` points to `Sexy Walk.anim`. Whether this clip's curves land in Path A or Path B determines which analysis section is most relevant. A Unity Humanoid-mode export produces Path A (muscle) curves; a Generic-mode export produces Path B (transform) curves. The importer auto-detects via `HasMuscleChannels`.

---

## 3. Path B Analysis — `ConvertRotation` on Explicit Bone Quaternions

### 3.1 The Comment vs Code Contradiction

[UnityAnimImporter.cs:16-19](XREngine.Animation/Importers/UnityAnimImporter.cs#L16):

```
// Assimp's ZAxisRotation=180 applies a global root rotation — it does NOT
// mirror per-bone local transforms. Since the .anim quaternions are in the
// same local bone space as the FBX, they should be passed through as-is.
// Only root-motion translation needs Z-negation (world-space path).
```

But [line 261](XREngine.Animation/Importers/UnityAnimImporter.cs#L261) applies `ConvertRotation` to every explicit bone quaternion, and [line 507](XREngine.Animation/Importers/UnityAnimImporter.cs#L507) does the same for vector-curve rotations.

**The comment says do not convert local bone rotations. The code converts them anyway.** One of these is wrong.

### 3.2 Which Is Correct?

This depends entirely on what Assimp does to the FBX skeleton:

**Scenario A — Assimp applies global root rotation only (as the comment assumes)**
- Each bone's local transform in the engine is still in Unity LH space.
- `.anim` local quaternions are in the *same* LH space.
- Applying `ConvertRotation` would be *wrong* — the quaternion already matches the bone frame.

**Scenario B — Assimp converts every bone's local frame to RH space**
- Each bone's local frame has been Z-reflected.
- `.anim` local quaternions are in Unity LH space.
- Applying `ConvertRotation` is *correct* — it adapts the Unity quaternion to the new RH frame.

**Scenario C — Mixed (e.g., Assimp applies ZAxisRotation=180 + per-bone axis corrections)**
- The correct conversion is more complex than a simple (-x,-y,z,w) formula.

The comment argues Scenario A, but the code implements Scenario B. Without inspecting the actual imported bind matrices of the skeleton, it is unclear which is true. This is **the primary unverified assumption** for Path B.

### 3.3 Double-Conversion Property

Note: `ConvertRotation(ConvertRotation(q)) = q`. If `ConvertRotation` is applied at import time and should *not* be, the net effect is that all bone rotations have the wrong sign on their X and Y quaternion components — a subtle but systematic error that would look "close but not correct."

---

## 4. Path A Analysis — Humanoid Muscle Pipeline

This path does not use `ConvertRotation`. Instead, `HumanoidComponent` generates bone quaternions from degree-mapped muscle values, applying its own LH→RH sign conventions.

### 4.1 `ApplyMusclePose` — Two Strategies

The component uses two distinct methods to construct bone-local rotations from muscle values.

**Strategy A: `ApplyBindRelativeEulerDegrees`**
Used for: Spine, Chest, UpperChest, Neck, Head, Jaw, Elbow, Wrist, Knee, Foot, Toes, Fingers.
[HumanoidComponent.cs:504-558](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L504)

Without axis mapping:
```csharp
q = Quaternion.CreateFromYawPitchRoll(-yawDeg * degToRad, -pitchDeg * degToRad, rollDeg * degToRad);
```

With `BoneAxisMapping`:
```csharp
float twistHandednessSign = m.TwistAxis == 2 ? 1f : -1f;   // Z stays, X/Y negate
float fbHandednessSign    = m.FrontBackAxis == 2 ? 1f : -1f;
float lrHandednessSign    = m.LeftRightAxis == 2 ? 1f : -1f;
```

**Strategy B: `ApplyBindRelativeSwingTwistWorldAxes`**
Used for: Shoulder, upper arm (Arm), upper leg (Leg).
[HumanoidComponent.cs:601-659](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L601)

```csharp
// Transform body-space axes into bone-local space, then apply LH→RH flip:
twistLocal     = new(-twistLocal.X,     -twistLocal.Y,     twistLocal.Z);
frontBackLocal = new(-frontBackLocal.X, -frontBackLocal.Y, frontBackLocal.Z);
leftRightLocal = new(-leftRightLocal.X, -leftRightLocal.Y, leftRightLocal.Z);

Quaternion twist     = Quaternion.CreateFromAxisAngle(twistLocal,     yawDeg   * degToRad);
Quaternion frontBack = Quaternion.CreateFromAxisAngle(frontBackLocal, pitchDeg * degToRad);
Quaternion leftRight = Quaternion.CreateFromAxisAngle(leftRightLocal, rollDeg  * degToRad);
q = leftRight * frontBack * twist;
```

### 4.2 Mathematical Equivalence of Both Strategies

For a rotation of angle `θ` around bone-local axis `(ax, ay, az)`:

- **Strategy A**: angle negated for X/Y axes → same as `CreateFromAxisAngle((ax, ay, az), −θ)` = `CreateFromAxisAngle((−ax, −ay, az), θ)` ✓
- **Strategy B**: negate X/Y of the axis vector → `CreateFromAxisAngle((−ax, −ay, az), θ)` ✓

Both produce `q_RH = (−q.X, −q.Y, q.Z, q.W)` relative to the LH input. They are mathematically equivalent implementations of the same Z-reflection transform. **The LH→RH math is correct in both strategies.**

The rotational correctness therefore depends entirely on whether:
1. The incoming muscle degree values are correctly mapped to anatomical axes
2. The bone-local axis vectors correctly represent the anatomical intent

### 4.3 Assumption: Standard Humanoid Bone Axis Convention

**Strategy A without `BoneAxisMapping`** assumes:
- Bone local **Y** = twist axis (rotation around bone's own long axis)
- Bone local **X** = front-back (pitch/bend) axis
- Bone local **Z** = left-right (lean) axis

This is the Unity default for T-pose humanoid skeletons. However, many models exported from different DCC tools (or imported via Assimp) have different local axis orientations. If a spine bone's local Y points forward rather than up, all three rotations will be on the wrong axes.

The `BoneAxisMapping` system ([HumanoidSettings.cs:297-341](XRENGINE/Scene/Components/Animation/HumanoidSettings.cs#L297)) is intended to fix this, but it is populated by `AvatarHumanoidProfileBuilder.BuildProfile()` and is only applied in the axis-mapped branch. If profile building hasn't run, or if the profiler detects the axes incorrectly, the standard Euler convention is used as a fallback.

**Assumption: `AvatarHumanoidProfileBuilder` correctly identifies all bone axes.** This is unverified.

### 4.4 Assumption: Hips Bind Pose Encodes Body Orientation

[HumanoidComponent.cs:571-577](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L571):

```csharp
private Vector3 GetBodyAxisWorld(Vector3 localAxis)
{
    Matrix4x4 bodyBind = Hips.Node?.Transform?.BindMatrix ?? Matrix4x4.Identity;
    Vector3 axisWorld = Vector3.TransformNormal(localAxis, bodyBind);
    ...
}
```

Strategy B derives body-space axes as:
```csharp
Vector3 bodyRight   = GetBodyAxisWorld(Vector3.UnitX);     // hips +X → world right
Vector3 bodyForward = GetBodyAxisWorld(-Vector3.UnitZ);    // hips −Z → engine forward
```

**Assumption 1**: The Hips bone's local X axis is the body's rightward direction.
**Assumption 2**: The Hips bone's local −Z axis is the body's forward direction (engine convention, where −Z = forward in RH).

For models where the Hips bone faces a different direction at rest (e.g., local +Z = engine backward), `bodyForward` would be inverted. This would silently swap front vs back for all limb swing rotations.

**Assumption 3**: The Hips bind pose is identity or close to it (i.e., the character is standing upright at bind pose with no unusual root rotation). If the model was imported with an unusual root rotation baked into the hips, `GetBodyAxisWorld` would return wrong directions.

### 4.5 Side Axis Derivation for Limbs

[HumanoidComponent.cs:279-280](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L279):

```csharp
Vector3 sideAxisWorld   = Vector3.Normalize(bodyForward * sideMirror);
Vector3 frontBackAxisWorld = bodyRight;
```

Where `sideMirror = isLeft ? 1.0f : -1.0f`.

This means:
- **Left limb**: `sideAxisWorld = bodyForward = −Z_world` (engine forward axis)
- **Right limb**: `sideAxisWorld = −bodyForward = +Z_world` (engine backward axis)

These become the rotation axes for `frontBackAxisWorld` parameter in the `ApplyBindRelativeSwingTwistWorldAxes` call, which drives the Down-Up muscle (arm elevation in the coronal plane).

**Anatomical verification for left arm (T-pose along +X)**:
Down-Up = elevation/depression of arm. Rotating +X toward +Y (raising) in RH corresponds to rotating around the −Z axis (forward). For left: `sideAxisWorld = −Z_world` ✓.
This seems anatomically correct.

**For right arm (T-pose along −X)**:
Down-Up rotation axis = `+Z_world`. Rotating −X toward +Y in RH corresponds to rotating around... `+Z × (−X) = −(Z×X) = −Y`, not +Y. Rotating around +Z: `+Z × (-X) = -(Z×X) = -Y`...

Let `ω = +Z`, `v = −X`. `dv/dt = ω × v = Z × (−X) = −(Z × X) = −Y`. So positive rotation of the right arm (along −X) around +Z pushes the arm toward −Y (downward). For the right side, `muscle = +1 → pitchDeg = +100°` should represent upward motion. But positive rotation around +Z pushes the right arm *down*. This is **inverted** compared to the left side.

**Potential Issue**: The sign convention for the Down-Up muscle on the right limb may be reversed. The `sideAxisWorld` for the right side is negated (via `sideMirror`), effectively reversing the direction of Down-Up rotation. This might be intentional if positive muscle = down on the right side in Unity's convention, or it may be a bug.

### 4.6 The `sideMirror` Factor and Twist Axes

For upper arm twist:
```csharp
yawDeg: MapMuscleToDeg(armTwist, GetMuscleValue(armTwist), new Vector2(-90.0f, 90.0f)) * sideMirror,
```

The twist is negated for the right side. **Assumption**: In Unity, positive Left Arm Twist is in one rotational direction, and the mirrored Right Arm Twist is in the opposite world-space direction. The `sideMirror` factor encodes this. If Unity actually uses the same sign convention for both sides, this would produce inverted twists on the right.

Similar `* sideMirror` factors appear for leg twist and various elbow/wrist/foot channels.

### 4.7 Euler Composition Order

Both strategies use **ZXY Euler order** (intrinsic: Z innermost, then X, then Y outermost) — also written as extrinsic YXZ:

```csharp
q = leftRight * frontBack * twist;  // Ry * Rx * Rz (intrinsic ZXY)
```

`Quaternion.CreateFromYawPitchRoll(y, x, z)` in System.Numerics also produces `Ry * Rx * Rz`. The order is consistent between strategies and matches Unity's documented ZXY Euler convention for humanoid joints. **No issue here.**

### 4.8 Muscle Range Defaults

[HumanoidComponent.cs:167-203](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L167) uses hardcoded ranges like `new Vector2(-40.0f, 40.0f)` for spine muscles.

**Critical assumption**: These defaults match the Unity muscle ranges configured on the avatar that the animation was authored for.

Unity's humanoid muscles are **avatar-specific**. A muscle value of `+1.0` maps to the maximum rotation configured in that avatar's muscle settings. If the animation was made for an avatar with, for example, a Spine Front-Back range of ±60°, but our code uses ±40°, all spine bends will be at 2/3 the correct amplitude.

Conversely, if the range *signs* are wrong (e.g., the range is stored as `(−40, 40)` but Unity's convention expects `(40, −40)` for that muscle), the rotation would be in the opposite direction.

`HumanoidSettings.MuscleRotationDegRanges` allows per-channel overrides, but only if explicitly populated. For a fresh avatar with no calibrated profile, all ranges fall back to the hardcoded defaults.

### 4.9 Forearm and Lower Leg Stretch Inversion

[HumanoidComponent.cs:266-267](XRENGINE/Scene/Components/Animation/HumanoidComponent.cs#L266):

```csharp
float forearmStretchMuscle = -GetMuscleValue(forearmStretch);
float lowerLegStretchMuscle = -GetMuscleValue(lowerLegStretch);
```

The comment explains this is because "positive values trend toward extension (straighter limb), not deeper flexion." This is a deliberate inversion fix.

**Assumption**: This inversion correctly reflects Unity's convention that `Forearm Stretch = +1` means fully extended elbow (not fully flexed). If Unity's actual convention is different (e.g., the neutral pose already includes an offset, or the sign is the opposite), this inversion would cause over-flexion at rest and no flexion at the expected maximum.

---

## 5. Root Motion and IK Goal Paths

### 5.1 Root Motion

`RootT.x/y/z` and `RootQ.x/y/z/w` in a humanoid clip represent the body's center position and orientation in avatar space, not a world-space transform. The code routes these to `HumanoidComponent.SetRootPosition / SetRootRotation` via `AddRootMotionAnimation`.

`ConvertPosition` (Z-negation) is applied to `RootT`. `ConvertRotation` is applied to `RootQ`.

**Assumption**: RootT/Q are in Unity world/avatar space and require the full coordinate system conversion. This seems correct for position (world-space path). For rotation, the same question applies as in Section 3.2.

### 5.2 IK Goal Curves

`LeftFootT/Q`, `RightFootT/Q`, `LeftHandT/Q`, `RightHandT/Q` are also imported and subjected to `ConvertPosition` / `ConvertRotation`. They're gated by `IKGoalPolicy = ApplyIfCalibrated`, meaning they are silently skipped unless the avatar has been calibrated via `AvatarHumanoidProfileBuilder`.

**If IK goals are skipped** (uncalibrated avatar), foot and hand positions will be driven purely by the muscle-pose FK chain. This could contribute to incorrect limb endpoint positions.

---

## 6. `AvatarHumanoidProfileBuilder` Role and Gaps

The profile builder ([AvatarHumanoidProfileBuilder.cs](XRENGINE/Scene/Components/Animation/AvatarHumanoidProfileBuilder.cs)) attempts to auto-detect per-bone axis mappings from the skeleton's bind-pose geometry (bone-to-child vectors). It produces `BoneAxisMapping` entries stored in `HumanoidSettings.BoneAxisMappings`.

**Gaps**:
1. Only called by `SetFromNode()` after bone discovery, and only populates `BoneAxisMappings` — it does not affect the world-axis logic in Strategy B.
2. The profile is only used in the `axisMapping.HasValue` branch of `ApplyBindRelativeEulerDegrees`. Strategy B (`ApplyBindRelativeSwingTwistWorldAxes`) uses world-space body axes directly and does **not** consult `BoneAxisMappings`.
3. Profile confidence is not surfaced in a way that clearly warns when the auto-detection is unreliable.

---

## 7. Summary of Assumptions and Potential Issues

| # | Location | Assumption | Risk |
|---|----------|------------|------|
| A | `ConvertRotation` at line 261 | FBX model's per-bone local frames are in LH space and need Z-reflection | **High** — contradicts comment; if Assimp already converted frames, this double-converts |
| B | `ApplyBindRelativeEulerDegrees` (no mapping) | Bone local Y = twist, X = pitch, Z = roll | **Medium** — model-dependent; may be wrong for non-Unity-standard rigs |
| C | `GetBodyAxisWorld(-Vector3.UnitZ)` | Hips local −Z = engine forward direction | **Medium** — model/import-dependent; breaks all limb swing axes if wrong |
| D | `sideAxisWorld = bodyForward * sideMirror` | Right-side Down-Up rotation is in the opposite direction vs left | **Medium** — may cause right arm/leg to move down when it should move up |
| E | `sideMirror` on twist muscles | Unity Arm/Leg Twist signs are mirrored between left and right | **Low-Medium** — standard humanoid convention, but needs verification |
| F | Hardcoded degree ranges (e.g., ±40°) | Animation was authored for an avatar with exactly these muscle ranges | **Medium** — avatar-specific; wrong amplitude or sign if ranges differ |
| G | `forearmStretchMuscle = -...` | Unity Forearm Stretch `+1` = fully extended | **Low-Medium** — needs re-verification against Unity source |
| H | `IKGoalPolicy = ApplyIfCalibrated` | IK goals will be skipped without calibration | **Low** — limb endpoints driven by FK only, may mismatch Unity's intended foot placement |
| I | Strategy B ignores `BoneAxisMappings` | World-axis approach always correct for limb bones | **Low** — profiler output unused for Strategy B bones |

---

## 8. Primary Suspects for "Very Close But Not Correct" Rotations

The symptom "very close but not correct" suggests a systematic, small-magnitude error rather than a complete approach failure. The most likely individual causes, ranked:

1. **Assumption A** — `ConvertRotation` applied to local bone quaternions (Path B, explicit transforms): if this path is active for `Sexy Walk.anim`, the X and Y quaternion components are negated when they should not be (or vice versa). This would produce rotations that appear qualitatively correct in direction but are mirrored on certain axes.

2. **Assumption D** — Right-side arm/leg Down-Up sign: if the right limb's `sideAxisWorld = −bodyForward` produces a downward bias for what Unity treats as an upward muscle, the right arm lifts down when it should lift up (or vice versa), a subtle per-cycle error that looks "close."

3. **Assumption F** — Muscle degree range mismatch: if the avatar used for the animation has ranges that differ from the code defaults by a modest amount (say 60° vs 40°), the amplitude would be 1.5× too large or 2/3 too small. Combined with a sign difference in one channel, this could produce complex-looking but systematic errors.

4. **Assumption C** — Hips bind pose not identity: if the character is not perfectly upright in bind pose (e.g., slightly rotated due to import), `bodyForward` and `bodyRight` are offset, skewing all limb swing axes by that rotation.

5. **Assumption B** — Bone axis convention mismatch: if the model's spine or limb bones have non-standard local frames, the Euler convention (Y=twist, X=pitch, Z=roll) maps the wrong muscles to the wrong axes.

---

## 9. Verified Correct Elements

- `ConvertRotation` formula `(−q.X, −q.Y, q.Z, q.W)` is mathematically correct for M = diag(1,1,−1).
- `ConvertPosition(v) = (v.X, v.Y, −v.Z)` is correct for Z-reflection of world-space positions.
- Euler composition order (ZXY intrinsic / YXZ extrinsic) is consistent between both strategies and matches Unity's humanoid convention.
- Both strategies (A: angle negation for X/Y; B: axis vector X/Y negation) produce mathematically equivalent quaternion conversions.
- The muscle-to-degree mapping (`MapMuscleToDeg` piecewise-linear through zero) correctly handles asymmetric ranges.
- String-to-`EHumanoidValue` mapping in `TryMapUnityHumanoidAttributeToValue` covers all standard Unity humanoid muscle names.
- `SetValue(int, float)` → `SetValue(EHumanoidValue, float)` cast is safe given enum values are stable.

---

## 10. Recommended Investigation Steps

1. **Determine clip kind**: Add a log after `clip.ClipKind` is set to see whether `Sexy Walk.anim` is `UnityHumanoidMuscle` or `GenericTransform`. This determines which analysis branch (Path A or Path B) applies.

2. **Log Hips bind pose**: Print `Hips.Node.Transform.BindMatrix` and `Hips.Node.Transform.LocalMatrix` to verify that `GetBodyAxisWorld` is receiving correct orientations. Confirm that `GetBodyAxisWorld(Vector3.UnitX)` and `GetBodyAxisWorld(-Vector3.UnitZ)` return plausible world-space right and forward vectors.

3. **Test without `ConvertRotation`** (Path B only): Temporarily remove the `ConvertRotation` call on line 261 and re-import to see if rotations improve or worsen. This isolates Assumption A.

4. **Inspect muscle values live**: Expand the diagnostic logging in `LogMusclePoseSnapshot` to include the computed `pitchDeg`, `yawDeg`, and `rollDeg` values for spine bones mid-animation. Verify the degree magnitudes are anatomically plausible (e.g., spine bent 20–40° during walk).

5. **Verify muscle ranges against Unity**: Open the avatar in Unity and inspect the "Muscles & Settings" tab to see the exact per-muscle ranges. Compare to the hardcoded defaults in `ApplyMusclePose` and `ApplyLimbMuscles`.

6. **Right vs left symmetry test**: Pose the character at a frame where only the left arm moves. If the right arm mirrors correctly (symmetric), Assumption D is likely fine. If the right arm moves in the wrong direction, the `sideMirror` or `sideAxisWorld` sign is the culprit.
