# Humanoid Body/Root Compensation Investigation

Date: 2026-08-24

Status: Phase 6 complete; Unity fixed-time, full root-settings matrix, lifecycle,
IK/contact, determinism, and live visual parity passed

Related TODO: `docs/work/todo/avatar/humanoid-body-root-compensation-todo.md`

## Problem Statement

Validate and implement the trustworthy-observation and deterministic body-root
evaluation work needed before changing XRENGINE's humanoid compensation model.
Use the external `Desktop/Misc/Mitsuki.fbx` avatar and
`Assets/Walks/Sexy Walk.anim`, which originated as a Unity humanoid animation.

The investigation must keep five concepts separate:

1. importer-mapped Unity `RootT`/`RootQ` Body data;
2. XRENGINE's converted Body delta;
3. the final composed Hips local transform;
4. the projected Root Transform, unwrapped placement pose, and temporal delta;
5. optional post-pose IK/contact compensation.

## Reproduction and Evidence

Unit Testing World settings were temporarily changed to OpenGL, Mitsuki enabled,
the unrelated Sponza disabled, Sexy Walk enabled, and pose audit enabled. They
were restored to their original Vulkan/model/audit values after validation.

Primary disposable evidence is under:

- `Build/_AgentValidation/20260824-114435-mitsuki-humanoid-audit/`
- `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260824-125904-mitsuki-body-transaction-2/`
- `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260824-164741-mitsuki-unity-parity-11/`

The final 81-sample report is:

`Build/_AgentValidation/20260824-114435-mitsuki-humanoid-audit/reports/phase2-owned-body-transaction.json`

Paused screenshots from two camera angles are under:

`Build/_AgentValidation/20260824-114435-mitsuki-humanoid-audit/mcp-captures/phase2-transaction/`

The current Unity reference, compact avatar profile, phase-11 report, and latest
two-angle captures are:

- `unity-reference/unity-humanoid-pose-schema6-profile-refresh.json`;
- `unity-reference/mitsuki-unity-humanoid-profile.json`;
- `reports/phase11-profile-scale.json`;
- `mcp-captures/phase11-profile-scale/`;

all relative to the primary disposable evidence root above.

Those first-run paths describe evidence from the original machine and are not
present in this checkout's disposable validation tree. Continuation evidence
from the second machine is under:

`Build/_AgentValidation/20260824-continue-humanoid-root/`

The source avatar resolved to `D:\Desktop\misc\Mitsuki.fbx`. The run contains a
fresh Unity export and profile under `unity-reference/`, build and Unity logs
under `logs/`, projected-root comparison output under `reports/`, and live MCP
captures under `mcp-captures/`.

## Root Causes Confirmed

### Scalar body channels were committed before a complete sample existed

The old path treated RootT Z and RootQ W callbacks as commit markers. The first
live evaluation therefore committed incomplete state:

- incomplete RootT: `(0.0234, 0.0000, 1.0055)`;
- complete RootT: `(0.023381634, 0.017171474, 1.0054543)`;
- incomplete RootQ: identity;
- complete RootQ:
  `(-0.0097379815, 0.027661655, 0.020389186, 0.99936193)`.

This made the Hips result depend on scalar member order and evaluation history.

### The original audit read stale descendant world state

The original report showed changing local rotations but zero positional range
for Head/Hands/Feet because it read cached world transforms immediately after
exact-time evaluation. Live MCP queries proved those endpoints moved.

### Restoring a time by evaluating it is not restoring the original pose

Before initial playback, the visible avatar can still be in bind pose while the
clip clock is zero. Calling `EvaluateAtTime(0)` in audit cleanup changes that
pose. Exact observation therefore requires preserving transform frames,
humanoid caches, clip initialization/binding state, clocks, and invalidation
flags—not only the scalar time value.

## Implemented Changes

### Evaluator-owned Body transaction

- `AnimationClipComponent` caches the complete time-zero Body sample and
  identifies all RootT/RootQ members once.
- Every direct evaluation begins an owner-tagged, allocation-free transaction,
  stages all scalar components with a channel mask, and commits once after all
  animated members.
- RootQ is normalized only at finalization. Non-finite position, rotation,
  quaternion length, and weight values receive explicit diagnostics and stable
  fallback values.
- Missing channels begin from a neutral sample rather than stale state.
- Hips translation and rotation are applied atomically with
  `SetLocalTranslationRotation`.
- Canonical references are owner-aware across direct-clip/state-machine
  suspension, deinitialization, stop, and resume.
- One-shot vector setters also use neutral samples and raw Body rotation access
  no longer mutates stored state.

At this first implementation phase, the retained conversion was deliberately
conservative pending Unity evidence:

- translation: `(currentBody - canonicalBody) * EstimateAnimatedMotionScale()`;
- rotation: `inverse(canonicalBody) * currentBody`, then bind rotation composed
  with the weighted delta.

That was a midpoint contract, not a final Mecanim-parity claim; the v3 coupled
profile and projected-root work below supersede it.

### Observational pose audit

- Exact-time evaluation runs at a render-thread barrier so rendering cannot
  consume an intermediate diagnostic pose.
- A transform diagnostic scope suppresses temporary world-dirty queue entries.
- Root/world positions are composed directly from current local matrices; the
  audit does not depend on stale world/render caches.
- The sampler snapshots and restores the exact clip fixed-point clock,
  initialization/binding state, humanoid raw/converted/canonical caches,
  settings values, every descendant transform frame, and all prior dirty flags.
- Cleanup does not re-evaluate the previous time.
- The sampler asserts exact restoration and fails visibly if any captured state
  differs.
- Schema 4 names importer-mapped current/canonical Body, converted Body deltas,
  composed Hips local state, character-root local/world state, and coordinate
  spaces separately.

### Unity reference exporter

`Tools/Unity/HumanoidPoseAuditExporter.cs` now supports a deterministic batch
entry point and records:

- `HumanPose.bodyPosition/bodyRotation`;
- Animator Body position/rotation and `Avatar.humanScale`;
- Animator GameObject local/world transform;
- projected root position/rotation and root-motion deltas;
- Hips local state, feet-bottom heights, selected bone local/root/world data;
- muscles, all 95 isolated `-1/+1` muscle response probes, neutral bone local
  positions/rotations, and raw clip curves;
- serialized clip start/stop, loop, cycle, mirror, Bake Into Pose, original
  orientation/position, feet-height, level, and orientation-offset settings.

It also emits a compact schema-1 avatar profile containing `humanScale`, Unity
`HumanDescription` parameters, neutral pose, and flattened per-bone muscle
responses. It compiles against the installed Unity 2022.3 assemblies with zero
warnings and errors. Once Unity was activated, an isolated batch project
completed successfully; the user's open `My project` editor was neither reused
nor stopped.

## Live Validation Results

### Fixed-time Body and Hips samples

| Time | Imported Body XYZ | Converted translation delta XYZ | Composed Hips local XYZ |
| --- | --- | --- | --- |
| 0.0 s | `(0.023381634, 0.017171474, 1.0054543)` | `(0, 0, 0)` | `(0, 30.648771, 0.8058766)` |
| 0.8 s | `(0.055059746, 0.018136151, 0.9503511)` | `(0.8071983, 0.024581188, -1.4040987)` | `(0.8071983, 30.673353, -0.59822214)` |
| 1.6 s | `(0.06089156, 0.048957802, 1.0207943)` | `(0.9558003, 0.8099559, 0.39088184)` | `(0.9558003, 31.458727, 1.1967585)` |
| 2.4 s | `(0.021739049, 0.018691964, 0.9623104)` | `(-0.041855138, 0.038744014, -1.0993618)` | `(-0.041855138, 30.687515, -0.29348516)` |
| 3.2 s | `(0.023381634, 0.017169299, 1.0054543)` | `(0, -0.000055436263, 0)` | `(0, 30.648716, 0.8058766)` |

The phase-1 and final phase-2 reports have 81 samples each and zero Body/Hips
mismatches.

Corrected root-space endpoint ranges are non-zero. Representative ranges are:

- Hips: approximately `(2.098, 2.913, 1.909)`;
- Head: approximately `(3.462, 2.870, 5.574)`;
- both hands and both feet also move substantially across the clip.

At `t=1.6`, audit Hips state matches the live MCP Hips state exactly. Audit Head
world position `(1.7773817, 47.57093, 2.823702)` matches the live query
`(1.7773814, 47.57093, 2.823702)`, differing by about `3e-7` only on X.

### Determinism and lifecycle

The exact Hips result at `t=1.6` is:

- translation: `(0.9558003, 31.458727, 1.1967585)`;
- rotation: `(0.04161858, -0.10924062, -0.022250298, 0.9928944)`.

It is bit-identical after:

- direct evaluation at B;
- A-at-0 then B-at-1.6;
- C-at-2.4 then B-at-1.6;
- humanoid reset then B;
- stop, play, pause, then B;
- deactivate (which restores bind), reactivate, play/pause, then B.

Deactivation restores the bind Hips translation
`(0, 30.648771, 0.8058766)` and near-identity rotation before playback resumes.

### Visual result

All fixed-time screenshots show one coherent skeleton and plausible walk poses;
the camera-dependent view changes as expected. No mesh explosion, duplicated
skeleton, or cumulative seek/loop drift was visible. The scene has no calibrated
ground plane, and Mitsuki's authored textures are mostly unavailable, so these
captures cannot establish foot contact or material correctness.

Fresh consecutive-frame FXAA and TSR sequences show one coherent,
non-accumulating silhouette through the loop from opposing cameras. The earlier
trail impression was not reproduced as a humanoid pose defect. Resource export
did expose black skinned Velocity while the avatar deforms; that distinct
producer bug is isolated in the linked rendering investigation.

### Startup skinning warnings

Audit-enabled and audit-disabled controls both emit exactly 40
`[SkinExplode] HUGE palette` warnings just after Sexy Walk starts. An earlier
audit-off count of zero was invalid because the search targeted an ignored
directory rather than `log_general.log`; the corrected control reproduces the
same warnings.

The warned bones have ordinary current skeleton positions while the composed
palette translation exceeds a fixed 50-unit threshold. Mitsuki is authored at
centimeter-like scale and remains visually coherent, so this threshold is not a
valid explosion detector for this avatar. It should be recalibrated against
mesh/avatar bounds in a separate rendering task.

## Earlier Single-Muscle Calibration Results (Superseded by v3)

The prior machine's refreshed Unity reference used the same Mitsuki avatar and
Sexy Walk clip at 25 Hz / 81 samples. It reported:

- `Avatar.humanScale = 0.8143980503`;
- arm/leg twist factors `0.5`, arm/leg stretch `0.05`, feet spacing `0`, and
  translation DoF disabled;
- loop enabled, all three Bake Into Pose flags disabled, original XZ retained,
  original orientation/Y disabled, height from feet enabled, and mirror off.

Matching Unity/XRENGINE neutral-bone vector lengths produce a stable median
ratio of `39.370064` XRENGINE units per Unity meter. The resulting verified
Body-motion scale is `0.8143980503 * 39.370064 = 32.062904`; the former
hips-to-feet heuristic was approximately `25.48` for this avatar. A profile is
now preferred when present, with the legacy estimate retained only for
unprofiled avatars.

The runtime can load either the compact profile or a full schema-6 Unity audit.
It applies measured neutral rotations and per-bone muscle responses while
leaving missing roles/fingers on the geometry fallback. The Unit Testing World
accepts `XRE_UNITY_HUMANOID_AVATAR_PROFILE` solely as a reproducible validation
hook.

Representative full-quaternion errors versus Unity are:

| Bone | Geometry path average/max | Calibrated profile average/max |
| --- | ---: | ---: |
| Left lower arm | `94.53 / 105.52` deg | `0.00 / 0.06` deg |
| Right lower arm | `61.47 / 63.44` deg | `0.00 / 0.04` deg |
| Right hand | `58.81 / 71.25` deg | `0.50 / 1.00` deg |
| Left hand | `8.90 / 11.07` deg | `0.80 / 1.18` deg |
| Left upper arm | `46.22 / 70.89` deg | `8.29 / 13.55` deg |
| Right upper arm | `54.17 / 66.22` deg | `4.03 / 6.75` deg |
| Neck | `41.76 / 48.21` deg | `0.58 / 1.12` deg |

At that stage, the remaining calibrated maxima were approximately `11.00` degrees on the left
lower leg, `10.10` on the left upper leg, `7.24` on the right lower leg, `7.03`
on the right upper leg, and `5.56` on the left foot. Spine, chest, shoulders,
head, lower arms, hands, and eyes are otherwise sub-degree or close to it.
These residual multi-axis limb errors show that independent single-muscle
responses approximate, but cannot exactly reproduce, Mecanim's coupled swing
solver; any later refinement should use combination probes rather than more
axis guessing.

The imported clip now retains Unity's Body-to-root projection metadata.
Phase-11 live logs confirmed `UnitsPerMeter=39.370064` and
`BodyMotionScale=32.062904`, and two camera captures showed one coherent
animated pose. The red/blue trail impression at that stage motivated the
separate renderer investigation; the completed fresh sequences below do not
show an accumulating humanoid silhouette.

## Completion Continuation

A fresh Unity 2022.3.22f1 batch import on this machine reported
`Avatar.humanScale = 0.8116461039`. This differs slightly from the prior
machine's generated avatar (`0.8143980503`), so completion used only the matching
v3 Unity export and profile. XRENGINE measured `UnitsPerMeter=39.370068` and
`BodyMotionScale=31.954561` for that pair.

The v3 profile contains 22 humanoid roles, four twist chains, measured body
axes, neutral local positions/rotations, and coupled-muscle models. The runtime
stores role data in dense enum-indexed arrays. Missing optional roles retain the
geometry fallback, while mismatched Hips/Spine/UpperLeg names and non-finite
Body/profile values produce diagnostics.

The raw basis question is resolved at the projection boundary. Unity's first
`RootT` sample is approximately `(-0.023382, 1.005454, 0.017171)`, while the
retained importer diagnostic is `(0.023382, 0.017171, 1.005454)`. Projection
restores semantic model-root axes as `(imported.X, imported.Z, imported.Y)`
without mutating the raw diagnostic sample.

XZ, calibrated Y, and yaw are independently valid projected-root channels.
Direct playback distinguishes the canonical-relative within-cycle pose,
consecutive delta, signed unwrapped loop pose, and placement target. The
application mode is explicit: extract only, apply relative to a named target's
playback-epoch anchor, or publish to an external consumer. It never guesses a
Hips, Armature, or scene-root target.

The calibrated coupled Hips model publishes Unity's final neutral-relative
local position and rotation once in the Scene-order pose pass. The projected
root calculation is a separate value and no longer causes an intermediate,
incompatible Hips write. State-machine layouts recognize complete RootQ groups,
blend them with shortest-arc normalized slerp, and pass compatible clip
projection metadata into the same atomic Body transaction.

## Final Unity/XRENGINE Parity

`reports/coupled-v3-comparison.json` compares all 81 samples at 25 Hz:

| Metric | Average | Maximum |
| --- | ---: | ---: |
| Projected root XYZ | `0.01770426` units | `0.13240078` units |
| Projected root rotation | `0.0009769` deg | `0.0395647` deg |
| Consecutive root translation | `0.01777755` units | `0.12590785` units |
| Consecutive root rotation | `0` deg | `0` deg |
| Hips local position | `0.2357138` units | `0.9322473` units |
| Hips local rotation | `0.0051216` deg | `0.0559529` deg |

The full translation metrics are dominated by the calibrated Y fit. The
separately projected XZ mean/max remain `0.0004466 / 0.0009885` units and yaw
`0.003397 / 0.047324` degrees; consecutive XZ is
`0.0000640 / 0.0002749` units and yaw `0.00175 / 0.03156` degrees. The worst
selected endpoint difference is about `1.2794` engine units, or `3.25 cm` at
the measured scale. These results satisfy the fixed-time parity gate and the
fresh XRENGINE silhouettes match the corresponding current Unity poses.

## Lifecycle, Loops, and IK

- Repeating a direct seek to `t=0.8` produces bit-exact Body, Hips, projected
  root, and hierarchy state.
- Forward and reverse loop accumulation remains stable across multiple cycles.
  Restart, stop/play, clip replacement, activation changes, and direct/state-
  machine handoff do not preserve stale pose or temporal state.
- The direct and one-state state-machine paths produce the same fixed-time
  Body/Hips result. Quaternion float-slot grouping prevents component-length
  collapse during state blending.
- A single `HumanoidIKSolverComponent` consumes animation goals only according
  to its explicit Ignore, calibrated-only, or always-apply policy. The four
  intended foot/hand goals report Applied on the calibrated Mitsuki path.
- Authored body-relative goals convert to the verified world frame within
  `6e-6` engine units. Optional contact correction can constrain feet only or
  feet and hands against a configured plane; Disabled reproduces the authored
  goal exactly. Body projection and post-pose compensation retain independent
  diagnostics.

## Phase 6 Completion: Unity Root-Motion Settings

Phase 6 now executes the complete
`UnityHumanoidClipRootMotionSettings` contract. The serialized DTO remains a
lossless import record, while `UnityHumanoidRootMotionPolicy.TryCreate`
validates and compiles it into the semantic orientation, Y, and XZ bases used
by the evaluator. Non-finite numeric values, invalid source intervals, and the
contradictory Original-Y plus Feet-Y combination are rejected with an explicit
diagnostic instead of entering a partially supported path.

The native evaluation order is now one transaction:

1. derive the signed source time, normalized cycle offset, loop phase, and
   temporal loop epoch from `StartTime`, `StopTime`, `CycleOffset`, and
   `LoopTime`;
2. evaluate curves into a complete canonical muscle/Body sample;
3. apply within-cycle Loop Pose endpoint correction independently from signed
   temporal root accumulation;
4. mirror semantic muscles, Body/root trajectories, left/right roles, IK goals,
   contact roles, and sole offsets when requested;
5. select Original, Center of Mass, or Feet projection independently for
   orientation, Y, and XZ, then apply Bake Into Pose, orientation offset, and
   level exactly once;
6. compose the remaining Body allocation into Hips in the exported
   Hips-parent-to-Animator-root frame and remove the projected root exactly
   once;
7. publish the extracted placement/consecutive delta or apply it to the
   explicitly configured target; and
8. run optional IK/contact compensation after the native pose transaction.

Direct clips and the state-machine path feed the same complete Body transaction.
Extraction-only, explicit-target application, and external-consumer output
share the same projected value, so changing the output mode does not evaluate
the root policy a second time. Restart, direct seeks, forward/reverse playback,
signed multi-loop epochs, and direct/state-machine handoff reset or preserve
only the state their contracts name.

The avatar profile schema is now version 5. It carries the Unity
Hips-parent-to-Animator-root allocation frame and its inverse. This is required
for avatars such as Mitsuki whose imported Armature was normalized to identity
even though Unity evaluated Body channels in a rotated parent frame. Using the
exported frame reduced Mitsuki's former roughly `45-50` engine-unit Hips-axis
error to the coupled-muscle residual below. Older compatible profiles migrate
through the normal avatar-definition pipeline; incompatible or non-finite
frames are rejected by validation and included in the canonical definition
hash.

`HeightFromFeet` first uses the Unity-calibrated projected-Y model. Its
geometry fallback now composes the current Hips-relative foot/toe FK rather
than a neutral world offset, retains the measured sole thickness, supports
missing optional toes, respects authored translation DoF, and mirrors semantic
roles without double-reflecting the already mirrored live pose. Loop Pose
float/vector endpoint deltas and quaternion endpoint corrections are cached at
clip initialization, so the per-frame path performs only allocation-free
lookups and interpolation. The 33-sample Mitsuki Loop Pose audit fell from more
than three minutes to `13.34` seconds with bit-equivalent output.

### Phase 6 Unity-reference matrix

Unity 2022.3.22f1 regenerated schema-5 profiles and fixed-time references for
Mitsuki, Akari, and Jess. XRENGINE runtime coverage used two unrelated avatars
(Mitsuki and Akari), Sexy Walk and Basic Walk, all ten root-policy variants,
and a cross-clip profile run. Representative 33/43-sample results are:

| Runtime case | Root position average/max | Hips position average/max | Hips rotation average/max |
| --- | ---: | ---: | ---: |
| Mitsuki Sexy base | `0.01963 / 0.06289` units | `1.31075 / 2.10900` units | `0.03291 / 0.09691` deg |
| Mitsuki Sexy combined | `0.02250 / 0.05625` units | `2.44424 / 3.13325` units | `0.03359 / 0.09691` deg |
| Akari Sexy base | `0.00044 / 0.00141` units | `0.02785 / 0.04605` units | `0.04870 / 0.11191` deg |
| Akari Sexy combined | `0.00050 / 0.00126` units | `0.05425 / 0.06921` units | `0.04515 / 0.11191` deg |
| Akari Basic Walk | `<0.000001 / <0.000001` units | `0.01862 / 0.02306` units | `0.08063 / 0.13706` deg |
| Akari Basic Walk with the Sexy profile | `0.00805 / 0.01830` units | `0.02114 / 0.03083` units | `2.00313 / 2.55067` deg |

The ten-variant matrices cover nonzero `OrientationOffsetY`, `Level`, and
`CycleOffset`; each Bake flag; Original, Center-of-Mass, and Feet bases;
mirror; Loop Pose; and a combined nonzero/mirrored/Loop-Pose case. Unity also
exported deliberately non-seam references with and without baked XZ so seam
correction remains a separate oracle from loop-root accumulation. The final
Mitsuki first eight variants are under
`reports/runtime-matrix-v55-mitsuki-final-fk/`; final Combined and Loop Pose
are under `reports/runtime-matrix-v56-mitsuki-loop-final/`; the Akari matrices
are under `reports/runtime-matrix-v52-akari-basic-final/`,
`reports/runtime-matrix-v53-akari-cross-clip-final/`, and
`reports/runtime-matrix-v54-akari-sexy-final/`, all relative to
`Build/_AgentValidation/20260825-204254-humanoid-root-phase6/`.

Repeating the hardest Mitsuki Combined case produced byte-identical audit JSON
(`SHA-256 C1EA66254315631F872FC8CA2135B8527C96521AE749A1F387F32EA547BFE1B2`)
and identical comparison metrics; the comparison files differ only in their
recorded output path. Direct fixed-time replay is likewise bit-exact, and the
single-state state-machine result remains identical to direct evaluation.

Mitsuki's remaining base Hips translation average/max is approximately
`3.33 / 5.36 cm` at its measured `39.370068` units per meter. A Unity
full-muscle replay shows about `2.96 cm` of Hips translation that is absent from
the reduced coupled-muscle probe set, explaining this bounded residual. Root
position and Hips rotation remain substantially tighter, the error does not
accumulate, and this is no longer a Body/root axis or allocation defect.

The final-binary live sequence is under
`mcp-captures/mitsuki-phase6-final-runtime/`; the complete earlier 10-frame
loop is under `mcp-captures/mitsuki-phase6-final-loop/`; and the fresh opposite
camera checks are under `mcp-captures/mitsuki-phase6-final-opposite/` and
`mcp-captures/mitsuki-phase6-final-opposite-full/`, relative to the same Phase
6 run root. Inspected frames show one coherent animated silhouette with no
stale duplicate or cumulative root drift. OpenGL readback dropped intermediate
frames in one fresh sequence because its bounded capture queue was saturated;
the completed frames, explicit opposite-camera captures, animation evaluator,
and numerical reports were unaffected.

## Visual and Renderer Isolation

The complete opposite-camera FXAA sequence is:

`Build/_AgentValidation/20260824-continue-humanoid-root/mcp-captures/rendering/baseline-fxaa-opposite/ViewportSequence_20260825_053402_239_18278c0ea6e8448694f33c3c22962e8b/contact-sheet.png`

The complete TSR sequence is:

`Build/_AgentValidation/20260824-continue-humanoid-root/mcp-captures/rendering/baseline-tsr/ViewportSequence_20260825_053637_643_5209abdd190244d2a727d8acf539fb0f/contact-sheet.png`

Both show one coherent, non-accumulating silhouette through a loop. The fixed
Unity screenshot series itself retained older uncleared silhouettes after its
first frame; the refreshed JSON and current-frame silhouettes are therefore the
pose oracle.

Velocity exports are black under both FXAA and TSR while the skinned mesh is
moving. Source inspection traces that to current and previous motion-vector
positions using the same current skinned local position, with no ordinary
previous skin-palette snapshot. This is documented separately in
`docs/work/investigations/rendering/humanoid-skinned-mesh-temporal-ghosting-2026-08-24.md`.
It does not change the passing humanoid animation result.

## Validation Performed

- Licensed isolated Unity v3 exporter run: completed successfully without
  reusing or stopping the user's open Unity project.
- Runtime animation-integration build and focused test builds: zero warnings and
  zero errors. Fresh full editor builds have zero errors; clean builds may show
  the nine pre-existing OscCore nullable/unused-member warnings.
- Regression matrix: focused Body/root suite `24/24`, Humanoid `87/87`, Unity
  importer `11/11`, AnimationClipComponent `21/21`, and AnimStateMachine `8/8`.
- Live fixed-time, direct/state-machine, lifecycle, signed-loop, authored IK,
  contact-mode, FXAA, and TSR validation all ran against
  `D:\Desktop\misc\Mitsuki.fbx` and `Assets/Walks/Sexy Walk.anim`.
- The named `humanoid-temporal-v1` session was stopped through
  `Manage-McpEditorSession.ps1`; no unrelated editor was stopped.
- Unit Testing World settings and schema were regenerated and restored to the
  canonical Vulkan/Jax/Sponza configuration with animation/audit/IK disabled.
- The Phase 6 continuation regenerated the Unity matrix, produced clean
  zero-warning/zero-error animation-integration and isolated full-editor
  builds, repeated the Combined audit byte-exactly, inspected live captures
  from both sides, and stopped only the named
  `humanoid-root-phase6-akari-matrix` session. No tests were added, modified, or
  run during this feature-validation continuation, following the repository's
  required live-path-first sequencing.

User validation status: humanoid Body/root compensation through Phase 6 is
complete and the live XRENGINE animation passes the Unity parity gate. Later
TODO phases remain intentionally open. Missing textures, the scale-blind
skin-explosion warning threshold, and ordinary skinned motion vectors remain
separate rendering/import diagnostics rather than animation-pose gaps.
