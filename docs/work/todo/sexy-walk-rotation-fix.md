# Sexy Walk Rotation Fix — Incremental Test Plan

Each step is one atomic change. After each change I implement it, you run the app, and report what you see. We proceed or backtrack based on results.

Reference clip: `Assets/Walks/Sexy Walk.anim`
Reference model: Mitsuki (`Desktop/misc/Mitsuki.fbx`)
Setting: `AnimationClipAnim: true`, `AnimClipPath: "Assets\\Walks\\Sexy Walk.anim"`

---

## Phase 1 — Diagnostics (no behavioral changes)

These steps add logging only. They tell us which assumptions are wrong before we change anything. The app should look identical to before.

---

### Step 1 — Log hips bind basis axes

- [x] **What I'll do**: In `ApplyLimbMuscles`, add a one-shot log that prints `bodyRight`, `bodyForward`, and the computed `armTwistAxisWorld` / `legTwistAxisWorld` after they are derived from the hips bind matrix. Also log the raw hips bind matrix.
- **What to look for in the console**:
  - `bodyRight` should be close to `(1, 0, 0)` (world right) for a T-pose character
  - `bodyForward` should be close to `(0, 0, -1)` (engine forward = −Z) for a character facing forward
  - If either is far from those values, the hips bind matrix has unexpected pre-rotation → **that is the root cause**
- **Success**: both axes look anatomically correct → body basis is not the bug, continue to Step 2
- **Failure**: axes are wrong/swapped/negated → body basis IS the bug, jump to Step 3

---

### Step 2 — Log key muscle degree values at frame 0

- [x] **What I'll do**: In `ApplyMusclePose`, add a one-shot log (first call only) printing the `pitchDeg`, `yawDeg`, `rollDeg` values for Spine, left upper arm, and left upper leg after `MapMuscleToDeg` is called. Also log the raw muscle values for those same bones.
- **What to look for**:
  - During a sexy walk the spine should be bent slightly forward and twisting left/right — expect pitchDeg around 5–20°, yawDeg oscillating ±10–30°
  - Upper arm Down-Up should be nonzero (arm swings) — expect ±20–60°
  - Values that are 0 everywhere → muscle values not arriving
  - Values that are very large (>100°) → range mapping is wrong
  - Values that are plausible in magnitude but the rotation goes the wrong way → axis sign issue
- **Success**: values look anatomically reasonable → proceed to Phase 2
- **Failure**: values are zero → muscle delivery pipeline is broken (different bug entirely)

---

## Phase 2 — Body Basis Fix

Only do this phase if Step 1 showed the body basis axes are wrong.

---

### Step 3 — Derive body axes from skeleton geometry, not hips local frame

- [x] **What I'll do**: In `ApplyLimbMuscles`, replace the hips-matrix-derived `bodyRight`/`bodyForward` with axes derived geometrically:
  - `bodyUp` = direction from hips world position to spine world position (normalized)
  - `bodyForward` = derived by finding the most forward-facing axis of the hips bind matrix after projecting out `bodyUp`
  - `bodyRight` = cross product of `bodyUp` and `bodyForward`

  This removes the assumption that hips local −Z = engine forward and hips local +X = engine right.
- **What to look for**: limb swing (arm raise/lower, leg swing front/back) should visibly improve or not regress
- **Success**: arm and leg arcs look more correct → continue
- **Failure**: limbs go in even more wrong directions → revert and reconsider

---

## Phase 3 — Spine and Head Axis Mapping

Do after Phase 1 diagnostics confirm the basic body basis is reasonable, or after Phase 2 is resolved.

---

### Step 4 — Use BoneAxisMapping for spine chain bones

- [x] **What I'll do**: Change `ApplyMusclePose` so that the `ApplyBindRelativeEulerDegrees` calls for Spine, Chest, UpperChest, Neck, and Head pass `GetBoneAxisMapping(bone.Node)` as the `axisMapping` argument, the same way Elbow/Wrist/Knee/Foot already do. The profiler already populates these mappings.
- **What to look for**: upper body twist and bend should look more correct. In a sexy walk the spine should visibly rotate and flex.
- **Success**: upper body motion improves → keep this change
- **Failure / no change**: the profiler's axis data for spine bones matches the generic fallback, or the profiler hasn't run — in that case this change is a no-op and harmless; keep it anyway and continue

### Step 4b — Test neck/head nod sign

- [x] **What I'll do**: Invert the applied `pitchDeg` sign for `Neck Nod Down-Up` and `Head Nod Down-Up` only, while keeping the spine/chest front-back sign unchanged. The clip's nod channels are predominantly negative, and the current result is tilting the head the wrong way.
- **What to look for**: the head should pitch downward more naturally during the walk instead of lifting/back-tilting
- **Success**: head orientation looks closer to the Unity reference → keep
- **Failure**: head pitches even farther the wrong way → revert Step 4b only

### Step 4c — Test arm Down-Up and stretch sign

- [x] **What I'll do**: Flip the sign used for shoulder/upper-arm `Down-Up` on the Strategy B path, and stop inverting `Forearm Stretch` / `Lower Leg Stretch`. The latest runtime snapshot showed negative arm Down-Up degrees while the arms were still raised, and positive lower-leg stretch while the stance knee was still bending.
- **What to look for**: arms should drop closer to the hips, and the active/stance leg should stop over-bending at the knee
- **Success**: arm height and knee bend move closer to the Unity reference → keep
- **Failure**: arms or knees get worse → revert Step 4c only
- **Current status (March 3, 2026)**: partially reverted after testing. The stretch-channel fix stays, but the shoulder/upper-arm `Down-Up` sign flip was wrong for the mirrored limb-axis setup and has been removed.

### Step 4d — Remove extra limb handedness axis flip

- [x] **What I'll do**: In `ApplyBindRelativeSwingTwistWorldAxes`, stop negating the X/Y components of the bone-local swing/twist axes after transforming from world bind space. On Mitsuki's arm bind rotations, that extra flip turns an intended world forward/down axis into a very different effective axis, which matches the raised-arm failure more closely than a curve/sign issue.
- **What to look for**: shoulders and upper arms should stop pitching around a skewed axis; arms should drop closer to the hips instead of lifting up/back. Upper legs may also improve if they were suffering from the same extra conversion.
- **Success**: arm and thigh arcs move materially closer to the Unity reference → keep
- **Failure**: limbs get even more chaotic or obviously mirrored wrong → revert Step 4d only

### Step 4e — Re-test stretch sign after limb-axis fix

- [x] **What I'll do**: Invert `Forearm Stretch` and `Lower Leg Stretch` again, but only after keeping the Step 4d limb-axis change. The new visual result is much closer in shoulder/upper-arm placement, and the remaining bad pose is concentrated in elbows and knees with live values of roughly `+49°`, `+41°`, `+90°`, and `-37°`.
- **What to look for**: elbows should open up instead of collapsing inward, and the knees should stop looking crouched/broken.
- **Success**: arm bend and knee bend move materially closer to the Unity reference → keep
- **Failure**: elbows or knees hyperextend / bend the wrong way → revert Step 4e only
- **Current status (March 3, 2026)**: reverted after testing. This pushed the elbow/knee hinges in the opposite direction and matched the new hyper-bent pose.

### Step 4f — Use asymmetric hinge ranges for stretch channels

- [x] **What I'll do**: Keep the raw stretch sign, but replace the symmetric `-80°..80°` defaults with asymmetric hinge ranges. Forearm stretch now defaults to `-10°..70°`; lower-leg stretch now defaults to `-10°..100°`.
- **What to look for**: elbows should keep a natural bend without folding inward, and knees should stay nearly straight on the stance leg while the lifted leg can still bend deeply.
- **Success**: elbows and knees move closer to the Unity reference without re-breaking shoulders/upper arms → keep
- **Failure**: elbows become too stiff or knees still crouch too much → tune the positive limits next

### Step 4g — Flip upper-limb / upper-leg fore-aft phase

- [x] **What I'll do**: Invert the applied `Front-Back` sign for shoulder, upper arm, and upper leg only. The latest pose is close, but the gait phase is still backward: the thigh uses the right bend amount on the wrong half of the cycle, and the arm fore-aft arc is mirrored against the expected walk.
- **What to look for**: the leg should drift back slowly during stance and whip forward during swing; when the thigh comes forward, the knee bend should now read naturally behind the leg. Arms should keep their reduced amplitude but form the opposite L-shape relative to the previous run.
- **Success**: leg swing timing and arm fore-aft pose now match the Unity reference more closely → keep
- **Failure**: leg and arm phase get even more mirrored wrong → revert Step 4g only

### Step 4h — Remove clip baseline from RootQ and clamp knee hyperextension

- [x] **What I'll do**: Treat `RootQ` as a body-center orientation curve relative to the clip's first sample instead of applying the raw quaternion directly to the hips bind pose. Also clamp knee flexion to `0°` if the mapped lower-leg pitch goes negative, and log the first clamp event per side.
- **What to look for**: the torso/hips center should stop carrying a constant backward-rotated offset from the clip's frame-0 root quaternion. If the right knee still tries to bend the wrong way, the log should now emit a `[KneeClamp]` line instead of letting it pass through silently.
- **Success**: the body center looks aligned without re-breaking the leg timing, and the right knee no longer folds forward → keep
- **Failure**: torso orientation gets worse or root sway disappears entirely → revert Step 4h only

### Step 4i — Apply muscles in animated body space, not frozen bind space

- [x] **What I'll do**: Fix two structural issues. First, stop invoking side-effect setter methods during animation-member initialization; that was causing `SetRootRotation()` to see the default identity quaternion before the clip value was ever sampled. Second, rotate the bind-derived body basis and limb twist axes by the current `RootQ`/body rotation before resolving shoulder/arm/leg muscles, matching Unity's documented rule that muscle curves are evaluated relative to the animated Body Transform.
- **What to look for**: the torso/center should stop behaving like a static bind-space frame, and upper arms/thighs should react relative to the animated body orientation instead of a frozen T-pose basis.
- **Success**: body orientation and limb arcs both move closer to the Unity reference without another sign-specific patch → keep
- **Failure**: the clip becomes obviously over-rotated or root motion disappears → revert Step 4i only

---

### Step 5 — Use BoneAxisMapping for upper arm and upper leg (Strategy B bones)

- [ ] **What I'll do**: For shoulder, upper arm, and upper leg bones (currently using `ApplyBindRelativeSwingTwistWorldAxes`), switch them to use the per-bone-axis-mapped version from the profiler data if available, falling back to the current world-axis approach when the profiler hasn't run. Concretely: if `GetBoneAxisMapping(node)` returns a mapping, use `ApplyBindRelativeEulerDegrees` with that mapping and the same degree values; otherwise keep the current Strategy B path.
- **What to look for**: upper arm and thigh rotations should look more anatomically correct
- **Success**: limb arcs improve → keep
- **Failure**: limbs break further → revert Step 5 only, keep Step 4
- **Current status (March 3, 2026)**: reverted after testing. The axis-mapped path changes Euler composition behavior for these bones and breaks the current walk clip.

---

## Phase 4 — Right-Limb Side Axis Direction

---

### Step 6 — Test right-side Down-Up axis sign

- [ ] **What I'll do**: For the right side only, negate `sideAxisWorld` so it uses `bodyForward` instead of `−bodyForward` (remove the `sideMirror` factor from `sideAxisWorld` only, keeping it on the twist/spread channels where it currently lives). Right shoulder and right upper-leg Down-Up will now use the same rotation axis direction as the left side.
- **What to look for**:
  - If right arm was lifting when it should drop (or vice versa), it should now mirror the left arm correctly
  - If it was correct before and now breaks, revert
- **Success**: right-side arm/leg swing now mirrors the left correctly → keep
- **Failure**: right-side gets worse → revert, the current sideMirror on the axis is intentional
- **Current status (March 3, 2026)**: reverted after testing. This did not fix the walk and regressed the arm/leg interpretation for this clip; the mirrored `sideAxisWorld` is back in place.

---

## Phase 5 — Root Motion Alignment

Do this last since root motion doesn't affect the bone rotations themselves, only the hips world position.

---

### Step 7 — Zero-base root motion at clip start

- [ ] **What I'll do**: In `HumanoidComponent.SetRootPosition` / `SetRootRotation`, record the first-received position/rotation as a baseline and subtract/inverse-multiply it from subsequent values. This removes the non-zero frame-0 offset that is currently being applied as if it were a true bind-relative delta.
- **What to look for**: the character's hips should stay near the ground/their start position rather than being offset by ~1 unit at clip start
- **Success**: hips height looks correct during the walk cycle → keep
- **Failure**: hips bob / the character is now at the wrong height → revert

---

## Notes

- Steps can be done in any order within a phase, but Phase 1 diagnostics should always come first.
- Steps 1 and 2 are **zero-risk** (logging only) — always safe to run.
- If any step in Phase 3–4 makes things worse, revert that step alone and continue with the next.
- Keep each change isolated so we can identify the contribution of each fix independently.
- After all phases pass, remove the diagnostic logging added in Phase 1.
