# Unity Humanoid `.anim` Auto-Playback Plan

## Goal

Make Unity Humanoid `.anim` clips (for example `Assets/Walks/Sexy Walk.anim`) play correctly **without per-clip manual setup** by matching Unity’s humanoid muscle-data workflow.

Unity auto-generates and stores humanoid muscle channels for an avatar. Our runtime should consume those channels as first-class data and auto-calibrate to the target avatar/skeleton so imported clips work by default.

## Product Intent

- User imports a Unity humanoid clip.
- Engine detects it as humanoid-muscle animation.
- Engine binds it to a humanoid avatar mapping automatically.
- Muscle values drive bones with correct axis/range conventions for that avatar.
- Optional IK/root channels are applied safely (or adapted) with no manual patching.

## Current Gaps (Observed)

1. Clip format is muscle-space (not per-bone local rotations), but failures can still happen from avatar axis/range mismatches.
2. Some channels (`RootT/Q`, IK goals) are currently disabled by importer flags, which can degrade perceived pose quality.
3. Per-bone axis calibration exists but is not auto-generated for newly imported avatars.
4. Runtime correctness depends on assumptions that may not match every rig.

## Known Code-Level Bugs (from codebase analysis)

These are concrete issues discovered in the current implementation that must be addressed alongside the pipeline changes above.

### Bug A — Eye rotation: degrees passed where radians expected

**File:** `HumanoidComponent.cs`, `SetValue()` eye cases (lines ~59–100)

`Interp.Lerp(Settings.LeftEyeDownUpRange.X, Settings.LeftEyeDownUpRange.Y, t)` produces a value in **degrees** (e.g. ±30), which is then passed directly to `Quaternion.CreateFromYawPitchRoll()` — a function that expects **radians**. The torso/limb paths all multiply by `degToRad` before creating quaternions, but the eye path does not. This causes wildly over-rotated eyes.

**Fix:** Multiply yaw/pitch by `MathF.PI / 180.0f` before `CreateFromYawPitchRoll`, or store the range values in radians.

### Bug B — `ByNameContainsAll` is actually `ByNameContainsAny`

**File:** `HumanoidComponent.cs`, line ~1661

The method uses `.Any()` instead of `.All()`, despite its name. This means `ByNameContainsAll("pinky", "1")` matches a node containing *either* "pinky" or "1" — so a bone named `"Bone1"` would incorrectly match the finger predicate. This can cause wrong bone mapping, especially for finger joints on skeletons with numbered bone names.

**Fix:** Either rename to `ByNameContainsAny` (if the OR logic is intentional for alternative naming) or fix to use `.All()` and restructure callers. For alternative-name patterns like `("UpperChest", "Upper_Chest")`, keep using `.Any()` but rename the method.

### Bug C — Swing-twist composition order may not match Unity

**File:** `HumanoidComponent.cs`, both `ApplyBindRelativeEulerDegrees` and `ApplyBindRelativeSwingTwistWorldAxes`

The quaternion composition is `frontBack * leftRight * twist` (twist innermost). `System.Numerics.Quaternion` uses column-vector convention where `A * B` means "apply B first." This puts left-right swing *between* twist and front-back, which differs from standard YXZ or ZYX Euler decompositions. If Unity's humanoid muscle system expects a different order, this produces visible rotation differences — especially at large angles on multi-axis joints like hips and shoulders.

**Investigation needed:** Confirm Unity's exact swing-twist decomposition order. Likely candidates: `swing * twist` where swing = single quaternion from combined front-back + left-right, or `twist * leftRight * frontBack` (reversed order).

### Bug D — Body-space swing axes not orthogonal to limb twist axes

**File:** `HumanoidComponent.cs`, `ApplyLimbMuscles()`

The swing axes for limbs are derived from the hips bind pose (`bodyRight`, `bodyForward * sideMirror`), while the twist axis comes from the bone→child direction (e.g., shoulder→elbow). These aren't guaranteed orthogonal — if the T-pose arms aren't perfectly horizontal or the hips bind pose has any roll, the swing axes won't lie in the plane perpendicular to the twist axis. This causes DOF coupling: a "Front-Back" muscle value inadvertently induces some twist.

**Fix:** Orthogonalize the swing axes against the twist axis using Gram-Schmidt before axis-angle construction. E.g.:
```csharp
frontBackLocal -= Vector3.Dot(frontBackLocal, twistLocal) * twistLocal;
frontBackLocal = Vector3.Normalize(frontBackLocal);
// then recompute leftRightLocal = cross(twistLocal, frontBackLocal)
```

### Bug E — `ComputeAutoAxisMappings` fallback produces wrong rotations

**File:** `HumanoidComponent.cs`

When `ComputeAutoAxisMappings()` fails to detect a bone's twist axis (e.g. near-zero parent→child distance), no `BoneAxisMapping` is stored. `ApplyBindRelativeEulerDegrees` falls through to `Quaternion.CreateFromYawPitchRoll`, which assumes Y=twist, X=front-back, Z=left-right. This is wrong for any bone whose local axes don't match that convention (common in VRM/glTF rigs where bone-local X may be the twist axis).

**Fix:** The `AvatarHumanoidProfileBuilder` (proposed in §2) should guarantee an axis mapping for every mapped bone, using skeleton-geometry heuristics with a confidence score. If confidence is below threshold, use the `BoneAxisMapping.Default` but emit a diagnostic.

## Proposed Automatic Pipeline

## 1) Detect Unity Humanoid clips at import time

Add explicit classification in `AnimYamlImporter`:

- `AnimationClipKind.GenericTransform`
- `AnimationClipKind.UnityHumanoidMuscle`

Detection heuristics:
- `classID == 95` + empty `path`
- attributes matching known humanoid names (`Spine Front-Back`, `Left Lower Leg Stretch`, etc.)
- presence of `RootT.*` / `RootQ.*` / `LeftFootQ.*` style channels

Output metadata on the clip:
- `ClipSource = Unity`
- `ClipType = HumanoidMuscle`
- list of present channel groups (muscles, root, IK)

## 2) Build/attach an Avatar Humanoid Profile automatically

When a humanoid clip is loaded on a model:

- Auto-run a profile builder for that skeleton:
  - bone role map (hips/spine/chest/neck/head/limbs/fingers)
  - per-bone axis mapping (`TwistAxis`, `FrontBackAxis`, `LeftRightAxis`)
  - per-muscle degree limits/ranges
- Store result as avatar profile data (cache by model GUID/hash).
- Feed that profile into `HumanoidComponent.Settings` automatically.

This avoids manual axis tuning per character.

### Axis mapping must be guaranteed for every bone

The profile builder must produce an axis mapping for *every* mapped bone — not just ones where geometry detection succeeds. The current `ComputeAutoAxisMappings()` silently skips bones where the parent→child direction is near-zero, leaving those bones to fall through to `CreateFromYawPitchRoll` with hardcoded Y/X/Z assumptions. The profile builder should:

1. Attempt geometry-based detection (bone→child direction = twist axis).
2. If the bone→child distance is near-zero, infer from parent's axis mapping or use the skeleton chain convention.
3. Attach a per-bone confidence score. Low-confidence bones get an explicit `BoneAxisMapping.Default` plus a diagnostic flag.

### Swing-axis orthogonalization

For limb joints that use `ApplyBindRelativeSwingTwistWorldAxes`, the profile builder should pre-compute orthogonalized swing axes per-bone (Gram-Schmidt against the twist axis) and store them in the profile — rather than deriving them at runtime from hips body-space axes which aren't guaranteed orthogonal to each limb's twist axis.

## 3) Auto-bind muscle channels by enum (never by ad-hoc string at runtime)

Importer should always normalize Unity attribute strings to `EHumanoidValue` once, then animate via `SetValue(int,float)`.

This keeps playback deterministic and avoids localization/name drift issues.

## 4) Root and IK channels should be policy-driven, not hard-disabled

Replace static booleans with auto policy:

- `HumanoidRootPolicy`: `Ignore | ApplyToHipsBindRelative | ExtractToCharacterMotion`
- `HumanoidIKGoalPolicy`: `Ignore | ApplyIfCalibrated`

Default behavior for MVP auto-play:
- root: `ApplyToHipsBindRelative` (safe local effect)
- IK goals: `ApplyIfCalibrated`, else ignore with debug warning

This preserves automatic playback while preventing broken full-body IK from bad-space targets.

## 5) Add one-click fallback behavior

If auto-calibration confidence is low:

- still play muscle channels
- disable IK goals
- use conservative root policy
- emit a single diagnostic warning with avatar profile confidence score and suggested fix

Result: clip still works “automatically” instead of fully failing.

## Implementation Notes (Code Targets)

Primary files:

- `XREngine.Animation/Importers/UnityAnimImporter.cs`
  - clip classification metadata
  - channel group extraction metadata
  - root/IK policy plumbing

- `XRENGINE/Scene/Components/Animation/HumanoidComponent.cs`
  - consume profile-generated axis/range mapping
  - calibrated muscle-to-rotation application
  - policy-gated root/IK application

- `XRENGINE/Scene/Components/Animation/HumanoidSettings.cs`
  - profile application API
  - confidence/fallback flags

Suggested new component/service:

- `AvatarHumanoidProfileBuilder` (new)
  - derives humanoid mapping + axis conventions from skeleton bind pose
  - caches reusable profile per avatar

## Suggested Runtime Diagnostics

On first playback of a Unity humanoid clip, log one compact line:

- clip type
- avatar profile source (cached/generated/manual)
- calibration confidence
- IK/root policy selected

This makes auto behavior understandable without noisy logs.

## Acceptance Criteria

1. `Sexy Walk.anim` plays on supported humanoid avatars with no manual settings edits.
2. Knees/elbows/torso motion looks anatomically plausible on at least two distinct rigs.
3. No required per-clip hardcoded axis overrides.
4. Root/IK behavior is stable and policy-driven.
5. Re-import + replay is deterministic across sessions (profile cache hit).

## Validation Matrix

- Avatar A (current Mitsuki path)
- Avatar B (different local-axis convention)

For each avatar:
- play `Sexy Walk.anim`
- verify legs, arms, torso
- verify root behavior (no teleport/floating)
- verify IK does not destabilize when calibration is low

### t=0 bind-pose sanity check (automated)

The fastest diagnostic for rotation correctness: at t=0 most muscle values should be near zero, meaning all bones should be at or very close to bind pose. Add a validation step:

1. Apply the clip at t=0.
2. For each mapped bone, compute the angular difference between the resulting rotation and the bind-pose rotation.
3. Flag any bone where the deviation exceeds a threshold (e.g. >5°).

If bones are visibly rotated at t=0, the axis mapping or bone discovery is wrong (not the muscle data). If they only diverge during animation, the issue is composition order or swing-axis orthogonality.

### Per-bone axis mapping dump

Log the auto-detected `BoneAxisMapping` for each bone alongside the bind-pose bone→child direction. This makes it trivial to spot cases where the twist axis was detected as (0,0,0) or the axes are non-orthogonal.

## Implementation Priority Order

Based on impact-to-effort ratio:

| Priority | Item | Impact | Effort |
|----------|------|--------|--------|
| **P0** | Fix Bug A (eye deg→rad) | Eliminates wildly broken eye rotation | ~5 lines |
| **P0** | Fix Bug B (rename/fix `ByNameContainsAll`) | Prevents wrong bone mapping on some skeletons | ~10 lines |
| **P1** | Fix Bug D (orthogonalize swing axes) | Eliminates DOF coupling on limbs — most visible on walk cycles | ~15 lines |
| **P1** | Implement §2 guaranteed axis mapping | Eliminates silent fallback to wrong Y/X/Z assumption | ~60 lines |
| **P1** | Implement §4 root policy `ApplyToHipsBindRelative` | Restores hip sway/bob from `RootT/Q` | ~30 lines |
| **P2** | Investigate Bug C (composition order) | May fix subtle multi-axis joint errors | Research + ~10 lines |
| **P2** | Implement §1 clip classification metadata | Enables auto-detection pipeline | ~40 lines |
| **P2** | Implement §4 IK goal policy | Re-enables foot/hand IK when calibrated | ~50 lines |
| **P3** | Implement full `AvatarHumanoidProfileBuilder` | Complete auto-calibration for any rig | ~200 lines |
| **P3** | Implement §5 confidence scoring + fallback | Polish: graceful degradation with diagnostics | ~80 lines |

## Non-Goals (for this pass)

- Perfect parity with every Unity Mecanim edge case.
- Retargeting between fundamentally incompatible skeleton topologies.
- Full runtime animation retarget editor UI.

## Why this matches Unity-style workflow

Unity’s humanoid path is avatar-driven and muscle-based. The right equivalent in XRENGINE is:

- treat muscle channels as primary
- auto-generate avatar calibration
- apply policy-based root/IK behavior
- avoid requiring manual per-clip fixes

That provides the “import and it just works” behavior expected from Unity humanoid `.anim` clips.
## Appendix: Code-Level Analysis Notes

### What the `.anim` file actually contains

`Sexy Walk.anim` has `m_RotationCurves: []` and `m_EulerCurves: []` — there are **no** direct per-bone rotation curves. All bone motion is encoded as ~95 float curves in `m_FloatCurves` with `classID: 95` (Unity Animator) and empty `path`, using Unity's Mecanim muscle names.

Channel groups present:
- **Muscle channels:** ~70 curves (`Spine Front-Back`, `Left Upper Leg Front-Back`, `Left Lower Leg Stretch`, `LeftHand.Index.1 Stretched`, etc.)
- **Root motion:** `RootT.x/y/z`, `RootQ.x/y/z/w` — currently **silently dropped** (`ImportHumanoidRootMotionCurves = false`)
- **IK goals:** `LeftFootT/Q`, `RightFootT/Q`, `LeftHandT/Q`, `RightHandT/Q` — currently **silently dropped** (`ImportHumanoidIKGoalCurves = false`)
- **Blend shapes:** `blendShape.Blink`, `blendShape.Nagomi`, etc. on `Body` node (classID 137) — these import correctly

### Current import path (working correctly)

1. `AnimYamlImporter.Import()` reads `m_FloatCurves`.
2. Muscle curves are detected by `IsHumanoidMuscleCurve()` (classID==95, empty path, attribute matches known name).
3. `TryMapUnityHumanoidAttributeToValue()` maps string → `EHumanoidValue` enum.
4. `AnimMemberBuilder.AddHumanoidValueAnimation()` builds a reflection-based path: `SceneNode → GetComponentInHierarchy("HumanoidComponent") → SetValue(enumInt, float)`.
5. At runtime, `SetValue(int, float)` stores in `_muscleValues` dictionary.
6. `ApplyMusclePose()` (called per frame) reads all stored values and applies via `ApplyBindRelativeEulerDegrees` or `ApplyBindRelativeSwingTwistWorldAxes`.

### Where rotations go wrong

The import and routing path is correct — muscle values reach `HumanoidComponent` accurately. The problems are all in the **application** stage:

1. **Axis mapping gaps** — bones without auto-detected axes use `CreateFromYawPitchRoll` with wrong axis assumptions.
2. **Non-orthogonal swing axes** — body-space axes used for limbs aren't orthogonal to the limb twist axis.
3. **Composition order uncertainty** — `frontBack * leftRight * twist` may not match Unity's muscle→rotation decomposition.
4. **Potential bone mis-mapping** — `ByNameContainsAll` using `.Any()` can pick wrong bones.