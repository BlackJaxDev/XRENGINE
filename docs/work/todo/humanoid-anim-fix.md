# Unity Humanoid .anim Import — Bug Fix TODO

## 1  Problem Summary

Unity `.anim` files exported for **Humanoid** avatars use **muscle-space** animation, not per-bone transforms. The importer (`AnimYamlImporter`) and the runtime muscle applicator (`HumanoidComponent.ApplyMusclePose`) have several bugs that cause visibly broken animation when playing clips like `Sexy Walk.anim` on an imported humanoid model (e.g. Mitsuki).

### Symptoms

| Observable problem | Root cause |
|---|---|
| Legs stay nearly straight during walk | "Stretch" muscles (knee/elbow bend) applied as ±3% bone Y-scale instead of rotation |
| Elbows don't flex | Same as above — Forearm Stretch treated as scale |
| Feet slide / plant incorrectly | IK goal curves (`LeftFootT/Q`, `RightFootT/Q`, etc.) silently dropped by importer |
| Model displaced / floating | `RootT.y ≈ 1.0` applied as absolute SceneNode transform, overriding scene placement |
| Upper body roughly OK | Spine/arm/head muscle curves ARE correctly imported and applied as rotations |

### Affected files

| File | Role |
|---|---|
| `XREngine.Animation/Importers/UnityAnimImporter.cs` | YAML importer — builds animation member tree |
| `XRENGINE/Scene/Components/Animation/HumanoidComponent.cs` | Runtime muscle applicator + bone mapping |
| `XREngine.Data/Animation/EHumanoidValue.cs` | Muscle value enum (stable, no changes expected) |
| `Assets/Walks/Sexy Walk.anim` | Primary test clip |
| `Assets/UnitTestingWorldSettings.json` | Test toggle (`AnimationClipAnim: true`, `AnimClipPath`) |

---

## 2  Phased Plan

### Phase 1 — Fix "Stretch" muscles (knee & elbow bending) ★ highest impact

**Goal**: Knees and elbows actually bend during walk animations.

- [x] **1.1** Research Unity's muscle definitions for `Left/Right Lower Leg Stretch` and `Left/Right Forearm Stretch`. Confirm they map to joint flexion (knee pitch, elbow pitch) with range approximately [-1, 1] → [full extension, full flexion].
- [x] **1.2** In `HumanoidComponent.ApplyLimbMuscles()`, replace the two `ApplyBindRelativeStretchScale()` calls per side with `ApplyBindRelativeEulerDegrees()` for the Elbow and Knee nodes using appropriate pitch-degree ranges:
  - Knee (Lower Leg Stretch): default range approximately `[-10°, 130°]` (extension → deep flexion).
  - Elbow (Forearm Stretch): default range approximately `[-10°, 145°]` (extension → deep flexion).
- [x] **1.3** Make the degree ranges configurable via `HumanoidSettings.MuscleRotationDegRanges` so models with different joint limits can override.
- [x] **1.4** Consider removing or repurposing `ApplyBindRelativeStretchScale()` entirely (no remaining callers after this change).
- [x] **1.5** Validate with `Sexy Walk.anim` — knees should visibly bend during the stride.

**Risk**: Low — localized change to two method calls in `ApplyLimbMuscles`.

---

### Phase 2 — Route IK goal curves through HumanoidComponent

**Goal**: Foot and hand IK targets animate correctly, producing proper foot planting.

- [x] **2.1** In `AnimYamlImporter`, add recognition for `LeftFootT`, `LeftFootQ`, `RightFootT`, `RightFootQ`, `LeftHandT`, `LeftHandQ`, `RightHandT`, `RightHandQ` prefixes in `TryMapTransformComponent()` (or a new mapping function).
- [x] **2.2** Decide application strategy:
  - **Option A**: Build combined Vector3/Quaternion animations and route them via `AnimMemberBuilder` to `HumanoidComponent.SetFootPositionAndRotation()` / `SetHandPositionAndRotation()`.
  - **Option B**: Add new `EHumanoidValue` entries for IK goals and handle them in `SetValue()`.
  - **Recommended**: Option A — keeps IK goals separate from muscle channels.
- [x] **2.3** Implement the chosen strategy in `AnimMemberBuilder` (new method like `AddIKGoalAnimation`).
- [x] **2.4** In `HumanoidComponent`, ensure the IK solver consumes animation-driven targets when present (check that `LeftFootTarget` etc. update from the clip).
- [x] **2.5** Validate — feet should plant properly and not slide during the walk cycle.

**Risk**: Medium — requires coordination between importer, animation tree, and IK solver. Need to verify IK target space (local vs world) matches what the .anim stores.

---

### Phase 3 — Fix RootT/RootQ application (root motion)

**Goal**: Root motion doesn't override the model's scene position.

- [x] **3.1** Determine the correct semantic for `RootT`/`RootQ` in Unity humanoid clips:
  - They represent the **body center** (hips) position/rotation, not a world-space transform.
  - `RootT.y ≈ 1.0` is hip height off ground, not an absolute Y position.
- [x] **3.2** Change `AnimMemberBuilder.AddTransformPropertyAnimation()` for RootT/RootQ so they apply to the **Hips bone** as bind-relative offsets instead of overwriting the SceneNode's absolute Transform.
  - For RootT: use `SetBindRelativeX/Y/Z` or route through `HumanoidComponent.SetRootPositionAndRotation()`.
  - For RootQ: use `SetBindRelativeRotation()` on the Hips node.
- [x] **3.3** Add an option for true root-motion extraction (delta position/rotation per frame applied to a character controller), gated behind a toggle.
- [x] **3.4** Validate — model should not teleport or float; hip sway/bob should be visible.

**Risk**: Medium — root motion semantics vary between "root motion enabled" and "in-place" clips. May need a per-clip or per-component toggle.

---

### Phase 4 — UpperChest as independent bone

**Goal**: If a model has a separate UpperChest bone, it gets its own rotation instead of being summed into Chest.

- [x] **4.1** In `HumanoidComponent.SetFromNode()`, add detection for an UpperChest bone (child of Chest with name containing "UpperChest" or "Upper_Chest").
- [x] **4.2** Add `BoneDef UpperChest` property to `HumanoidComponent`.
- [x] **4.3** In `ApplyMusclePose()`, apply UpperChest muscles to the UpperChest node independently when present; fall back to summing into Chest when absent.
- [x] **4.4** Validate with a model that has an explicit UpperChest bone.

**Risk**: Low — additive change; fallback preserves current behavior.

---

### Phase 5 — Rotation axis mapping robustness

**Goal**: Support models with non-standard bone local-axis conventions.

- [x] **5.1** Audit common VRM/FBX/glTF bone axis conventions to determine if the current yaw/pitch/roll mapping to twist/frontBack/leftRight is universally correct.
- [x] **5.2** If not universal, add per-bone axis configuration in `HumanoidSettings` (e.g., which local axis is "bone axis", which is "forward", which is "right").
- [x] **5.3** Update `ApplyBindRelativeEulerDegrees` to use the configured axis mapping.
- [x] **5.4** Validate with at least two models with different bone orientations.

**Risk**: Low-medium — may need broader testing matrix. Can defer if current models work with Phases 1–4.

---

## 3  Validation Plan

| Phase | Test method |
|---|---|
| 1 | Load `Sexy Walk.anim` on Mitsuki model; visually confirm knees/elbows bend |
| 2 | Same clip; confirm feet plant on ground during stride, no sliding |
| 3 | Same clip; confirm model stays at its scene-graph position, hips bob naturally |
| 4 | Load on a model with UpperChest bone; confirm upper torso moves independently |
| 5 | Load on two different skeleton conventions; confirm correct rotation axes |

All phases should be tested with `AnimationClipAnim: true` and `AnimClipPath` pointing to `Assets\Walks\Sexy Walk.anim` in `Assets/UnitTestingWorldSettings.json`.

---

## 4  File Change Summary (estimated)

| Phase | Files modified | Lines changed (est.) |
|---|---|---|
| 1 | `HumanoidComponent.cs` | ~30 |
| 2 | `UnityAnimImporter.cs`, `HumanoidComponent.cs` | ~80–120 |
| 3 | `UnityAnimImporter.cs` | ~40–60 |
| 4 | `HumanoidComponent.cs` | ~40 |
| 5 | `HumanoidComponent.cs`, `HumanoidSettings.cs` | ~60 |

Total estimated: ~250–310 lines across 3–4 files.
