# Humanoid Body/Root Compensation Investigation

Date: 2026-08-24

Status: Paused after Unity reference export, calibrated avatar-response integration, and live motion-scale validation; Body-to-root/Hips decomposition remains open

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
4. a future temporal projected Root Transform and published root-motion delta;
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

The retained conversion is deliberately conservative pending Unity evidence:

- translation: `(currentBody - canonicalBody) * EstimateAnimatedMotionScale()`;
- rotation: `inverse(canonicalBody) * currentBody`, then bind rotation composed
  with the weighted delta.

Those scale and multiplication choices are not yet claimed to match Mecanim.

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

Running frames still show temporal halo/multiple-silhouette trails. That is a
rendering velocity/history issue, not Body/Hips duplication.

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

## Unity Reference and Calibrated Runtime Results

The refreshed Unity reference uses the same Mitsuki avatar and Sexy Walk clip
at 25 Hz / 81 samples. Unity reports:

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

The remaining calibrated maxima are approximately `11.00` degrees on the left
lower leg, `10.10` on the left upper leg, `7.24` on the right lower leg, `7.03`
on the right upper leg, and `5.56` on the left foot. Spine, chest, shoulders,
head, lower arms, hands, and eyes are otherwise sub-degree or close to it.
These residual multi-axis limb errors show that independent single-muscle
responses approximate, but cannot exactly reproduce, Mecanim's coupled swing
solver; any later refinement should use combination probes rather than more
axis guessing.

The imported clip now retains Unity's Body-to-root projection metadata.
Phase-11 live logs confirm `UnitsPerMeter=39.370064` and
`BodyMotionScale=32.062904`, and two camera captures show one coherent animated
pose. The camera-dependent red/blue trails remain a separate temporal-rendering
artifact.

## Remaining Gaps

- Hips is intentionally excluded from profile overrides for now. Its current
  local-rotation error is `12.40` degrees average / `23.46` maximum. The
  measured Hips muscle response predicts about `2.62 / 5.92` degrees, but
  applying it alone would remove the Body yaw before that yaw is published on
  the model root.
- Unity projected-root yaw is almost exactly the first-sample-relative yaw of
  normalized raw `RootQ`; the mean measured discrepancy is about `0.019`
  degrees and the maximum about `0.042`. XRENGINE still folds the complete
  Body rotation into Hips and publishes no projected-root pose or temporal
  delta.
- Unity projected XZ closely follows first-sample-relative Body position after
  canonical-yaw removal and `humanScale`; normalized position error was about
  `5.4e-5` mean / `2.4e-4` maximum. Y projection additionally depends on the
  clip's `HeightFromFeet` policy and must not be guessed.
- Scalar curve values import exactly, but the current semantic `RootT` path maps
  source `(x,y,z)` to XRENGINE `(-x,z,y)` before direct Hips composition. Bind
  evidence makes the Y/Z swap suspicious. It was deliberately left unchanged
  at wrap-up so a basis change cannot silently mix projected-root and Hips
  semantics.
- `AnimStateMachineComponent` still blends RootQ scalar members rather than
  blending complete quaternions.
- The raw startup path disables humanoid IK. Authored goals, foot contact, and
  post-pose compensation remain intentionally unvalidated.
- Missing Mitsuki textures, fallback material bindings, temporal ghosting, and
  the scale-blind skin-explosion threshold are separate rendering/import work.

## Best Path Forward

1. Resolve the semantic `RootT`/RootQ basis from the measured Unity projected
   root and XRENGINE bind/model-root bases before changing another pose axis.
2. Add explicit projected-root pose and temporal-delta outputs. Default to
   extract-only so animation cannot overwrite gameplay/network character
   placement; make scene-root application an explicit policy.
3. Atomically extract yaw/XZ/Y according to clip metadata while applying the
   measured Hips muscle response plus residual Body tilt/roll. Never remove a
   component from Hips without publishing it to root in the same sample.
4. Use feet-bottom evidence for `HeightFromFeet`, and diagnose yaw, XZ, and Y
   projection independently across fixed samples, loop wrap, seeks, and replay.
5. Replace state-machine scalar RootQ blending with quaternion-aware complete
   Body sample blending and repeat the lifecycle matrix.
6. Only after Body/Hips/root parity, enable authored IK/contact handling and
   investigate rendering ghosting separately.
7. Add tests only after the user explicitly clears test work, per repository
   policy.

## Validation Performed

- `dotnet build XREngine.Runtime.AnimationIntegration/XREngine.Runtime.AnimationIntegration.csproj --no-restore`: zero warnings, zero errors.
- Fresh isolated editor builds: zero errors; clean restore builds surface nine
  pre-existing OscCore submodule nullable/unused-member warnings only.
- Phase-11 isolated playback loaded the refreshed compact profile, exported all
  81 samples, logged `UnitsPerMeter=39.370064` and
  `BodyMotionScale=32.062904`, and was inspected from two camera positions.
- Named MCP sessions were stopped through `Manage-McpEditorSession.ps1`; no
  unrelated editor process was stopped.
- Unity exporter standalone compile and licensed isolated Unity 2022.3 batch
  run: zero compile errors and successful batch exit.
- No unit tests were added or run. Repository policy defers test work until live
  feature validation is complete and the user explicitly clears it.

User validation status: work paused at the user's request before the
projected-root/Hips split.
