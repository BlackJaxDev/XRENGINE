# Humanoid Body/Root Parity and Compensation TODO

Last Updated: 2026-08-26
Owner: Animation / Avatar
Status: In progress; Phase 6 complete root-motion settings and the
Mitsuki/Sexy Walk reference milestone are complete, while the remaining
corpus/persistence gates and Phase 7+ transition/blend parity remain open

Related evidence:

- `docs/work/investigations/avatar/humanoid-body-root-compensation-2026-08-24.md`
- `Assets/Walks/Sexy Walk.anim`
- `XREngine.UnitTests/TestData/SexyWalkHumanoidRawAudit.compact.json`
- Unity Manual, Root Motion: <https://docs.unity3d.com/Manual/RootMotion.html>
- Unity `HumanPoseHandler.GetHumanPose`: <https://docs.unity3d.com/ScriptReference/HumanPoseHandler.GetHumanPose.html>

## Goal

Establish measured Unity-to-XRENGINE parity for imported humanoid body data and
implement one data-driven, XRE-native pipeline that works for any compatible
humanoid model and any Unity `.anim` in the declared supported Unity
serialization versions. Conversion, import, authoring, and playback must never
invoke, embed, or require the Unity Editor/runtime, and must not depend on a
Unity-generated per-avatar or per-clip bake. The runtime and importer must not
contain behavior keyed to a particular model, clip, filename, path, GUID,
sample count, or calibration display name.

The Mitsuki/Sexy Walk pair remains useful historical integration evidence, but
it is not the product contract and must never be required for correct runtime
behavior. A different avatar and clip must work through the same importer,
`HumanoidComponent` avatar definition, solver, root-motion, IK, and
state-machine paths without code changes.

Unity humanoid `RootT`/`RootQ` data represents the retargetable Body Transform,
not an already-projected scene/model Root Transform. XRENGINE must therefore
keep these concepts distinct:

1. raw imported Unity body channels;
2. the converted, normalized humanoid body pose;
3. the final composed Hips local transform;
4. any temporal Root Transform delta published to the character/model root;
5. procedural IK or contact compensation applied after authored pose data.

Observability, deterministic evaluation, the Body-to-projected-root/Hips split,
loop accumulation, initial state-machine quaternion blending, avatar
calibration, and authored IK/contact handling are implemented for the completed
reference milestone. The existing Unity v3 export is historical conformance
evidence, not a production input or required tool. General completion requires
multiple independent avatars, held-out clips, all supported root-motion
settings, transitions/blend trees, and raw `.anim` encoding coverage through
the one native path.

## Unity Contract To Preserve

- Unity documents the Body Transform as the humanoid center of mass and the
  Body Orientation as an avatar-relative average body orientation.
- Body Transform/Orientation are the humanoid clip's world-space curves;
  muscles and humanoid IK goals are relative to the body transform.
- Unity computes the movable Root Transform as a runtime projection of the Body
  Transform, controlled by clip import settings such as Bake Into Pose.
- `HumanPose.bodyPosition` is normalized by `Avatar.humanScale`. Do not assume
  XRENGINE's current average hips-to-feet bind distance is equivalent without
  measured reference evidence.
- Do not treat a first-sample-relative Hips offset as both the absolute humanoid
  body pose and a temporal model-root motion delta. Those are separate states.

## Generality and No-Hardcoding Contract

- Runtime/import behavior must never branch on a fixture's model name, clip
  name, source path, local filesystem location, GUID, or known sample values.
- Clip display names are diagnostic metadata only. They must not select
  calibration, projected-root behavior, coordinate conversion, IK remapping,
  or solver coefficients.
- Avatar transform names may appear only in imported avatar-definition mapping
  data.
  Standard Unity humanoid roles are semantic identifiers; concrete names such
  as a model's Hips transform are not engine constants.
- Human scale, units-per-meter, neutral transforms, body axes, role mappings,
  twist distribution, optional bones, and coordinate conventions must come from
  the versioned `HumanoidComponent` avatar definition and normalized import
  metadata.
- `HumanoidComponent` is the canonical avatar-definition authoring workflow.
  Its current automatic mapping and editor correction must be strengthened and
  made persistable; no parallel mapping/profile truth may be introduced.
- Automatic mapping may propose a definition, but runtime playback must consume
  a finalized, validated definition. Ambiguous required roles, axes, or neutral
  pose data must request editor correction rather than trigger runtime guessing.
- Imported clips, source skeletons, and finalized avatar definitions must use
  content-derived identities for caching and compatibility checks. Renaming or
  moving a source file must not change playback.
- Manual muscle/IK flip toggles may remain diagnostic overrides, but correct
  default behavior must be derived from source and avatar metadata.
- Unsupported `.anim` fields or encodings must produce explicit capability
  diagnostics. Silently dropping data is not support and must not fall through
  to a fitted or fixture-specific fallback.
- There is one production evaluator. During development, an incomplete feature
  fails explicitly; it never routes to a Unity-assisted, baked, calibrated, or
  lower-fidelity backend.
- Fixture-specific names and paths are allowed only in documentation, test
  manifests, and explicit validation-tool inputs. They are prohibited from
  production behavior and generic acceptance criteria.

Acceptance criteria:

- [ ] Renaming and moving a model and `.anim` without changing their contents
  produces the same imported identities and evaluated animation.
- [ ] A new compatible avatar and `.anim` can be imported and played without
  modifying or recompiling XRENGINE.
- [ ] Repository searches find no production branches or constants keyed to
  the historical reference model or clip.
- [ ] Avatar-definition/skeleton incompatibility produces a clear error; it
  never silently remaps bones or substitutes data from another avatar or clip.

## Capability Status After Re-Evaluation

| Capability | Current status | General completion requirement |
| --- | --- | --- |
| Raw Unity `.anim` curves | Partial | Support every claimed binding/encoding for declared Unity versions or reject it explicitly. |
| `HumanoidComponent` avatar definition | Automatic name/position mapping and editor correction exist | Make this the persistent canonical definition; add deterministic topology/axis inference, validation, stable identities, and complete Unity humanoid metadata. |
| Native humanoid pose solve | One fitted reference milestone, not general parity | Replace clip/avatar-fitted behavior with a deterministic solver driven only by the imported clip and finalized target-avatar definition. |
| Root-motion import settings | Parsed, only partly executed | Implement every setting and combination in the native evaluator. |
| State-machine transitions/blends | Direct and one-state evidence only | Evaluate root/body contributions per active motion and validate transitions and blend trees against Unity. |
| Production avatar-definition association | Component mapping plus profile/runtime and Unit Testing World plumbing | Consolidate on the `HumanoidComponent` definition, serialize it normally, and validate it against the active skeleton. |

## Completed Reference Milestone (Not General Parity)

All values and names in this section describe one historical validation fixture.
They are evidence for the architecture, not constants or universal tolerances.

- Scalar `RootT.*` and `RootQ.*` channels are staged in one evaluator-owned,
  allocation-free transaction. A complete sample is committed once, with
  neutral defaults, finite-value diagnostics, and quaternion normalization.
- Direct clips and state machines use complete Body samples. State-machine
  `RootQ` groups are blended as normalized quaternions rather than four
  unrelated scalars.
- The audit samples through a render-thread barrier, derives hierarchy state
  from current local matrices, and restores clocks, bindings, humanoid caches,
  transforms, dirty flags, projected-root state, and IK diagnostics exactly.
- The v3 profile records `humanScale = 0.8116461039`, 22 mapped roles, four
  twist chains, avatar body axes, and coupled muscle models. XRENGINE measures
  `39.370068` units per meter for a Body-motion scale of `31.954561`.
- Projected root XZ, calibrated Y, and yaw are independent validity channels.
  Absolute projected pose, consecutive delta, unwrapped loop pose, explicit
  target application, and external-consumer publication are distinct states.
- Hips and the other calibrated bones are evaluated once from their Unity
  neutral local pose and coupled-muscle models. Body/root projection no longer
  writes an incompatible intermediate Hips pose into hierarchy/render state.
- Across 81 fixed samples, full projected XYZ error is `0.017704` average /
  `0.132401` maximum engine units and projected rotation error is `0.000977` /
  `0.039565` degrees. Hips local error is `0.235714` / `0.932247` engine units
  and `0.005122` / `0.055953` degrees. The worst selected endpoint difference
  is about `1.2794` engine units (`3.25 cm`).
- Direct seek at `t=0.8` is bit-identical when repeated. Forward and reverse
  loops, replay, restart, clip replacement, direct/state-machine handoff, and
  lifecycle activation are stable without accumulated pose state.
- Authored Unity IK goals use calibrated body-to-world conversion, an explicit
  policy, status diagnostics, and one solver. The four intended goals apply;
  conversion error is at most `6e-6` engine units. Optional ground-plane
  compensation can target feet only or feet and hands, and Disabled preserves
  the authored result exactly.
- Fresh FXAA and TSR running sequences show one coherent, non-accumulating
  silhouette from opposing cameras through a loop. The Unity screenshot series
  contained uncleared old silhouettes, so numeric JSON and current-frame
  silhouettes—not those raster remnants—are the parity oracle.
- Ordinary skinned motion vectors are independently confirmed black while the
  mesh deforms. That renderer producer defect is isolated in the linked
  rendering investigation and is not a duplicate-pose or humanoid-compensation
  failure.

## Scope

- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimationClipComponent.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs`
- `XREngine.Editor/ComponentEditors/HumanoidComponentEditor.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimStateMachineComponent.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/UnityHumanoidAvatarProfile*.cs`
  (transitional data to consolidate into the canonical avatar definition)
- `XREngine.Animation/State Machine/AnimStateMachine.cs`
- `XREngine.Animation/Importers/UnityAnimImporter.cs`
- `XREngine.Animation/Importers/UnityHumanoidClipRootMotionSettings.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/Diagnostics/HumanoidPoseAudit*.cs`
- `XREngine.Runtime.Core/Scene/Transforms/TransformDiagnostic*.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/HumanoidIKSolverComponent.cs` after body parity
- `Tools/Unity/HumanoidPoseAuditExporter.cs` and
  `Tools/Unity/HumanoidPoseAuditOverlay.cs` as historical evidence tooling only;
  they are not part of conversion, import, authoring, playback, or CI
- focused humanoid/importer tests after the live path is validated

## Non-Goals

- Do not claim exact private Mecanim equations; use public Unity contracts and
  versioned conformance output. "Exact" in this document means observable
  behavioral parity within ratified floating-point tolerances, not source-code
  identity.
- Do not make a named avatar or clip a supported special case. If a correction
  cannot be derived from the normalized clip data and finalized generic avatar
  definition, it is not a production correction.
- Do not call one avatar/clip comparison general, exact, or perfect parity.
- Do not invoke or require Unity, and do not consume a Unity-generated
  per-avatar/per-clip pose bake as a production input.
- Do not maintain separate "exact" and "retargetable" playback backends. The
  same native evaluator must handle direct playback and cross-avatar humanoid
  playback.
- Do not move the model/scene root merely because a clip contains Body
  Transform curves.
- Do not make ordinary limb muscles invent body translation.
- Do not conflate authored humanoid evaluation with optional post-pose contact
  compensation. Keep their inputs, order, and diagnostics explicit.
- Do not interpret free-running screenshots as skeletal acceptance evidence;
  use paused deterministic samples.
- Track skinned-mesh temporal ghosting separately under rendering/velocity
  history.
- The local `Desktop/Misc/Mitsuki.fbx` is integration evidence, not a
  redistributable automated-test asset.

## Phase 0 - Trustworthy Observability

- [x] After each exact-time clip evaluation, derive a side-effect-free hierarchy
  snapshot from current local matrices before audit capture. Do not publish
  intermediate samples into live world/render transform caches.
- [x] Ensure audit sampling preserves and restores playback time, body/root
  baseline state, and the visible live pose.
- [x] Capture coordinate spaces explicitly from the local-matrix snapshot rather
  than relying on stale cached `WorldTranslation` values or converting transform
  types during diagnostics.
- [x] Record importer-mapped current/canonical Body, converted Body deltas,
  composed Hips local transform, and character-root local/world transforms as
  separately named fields. Unity's exporter separately records projected root
  and root-motion delta; XRENGINE has no published temporal root delta yet.
- [x] Re-run paused fixed-time samples at `t=0`, `0.8`, `1.6`, and `2.4` seconds.
- [x] Confirm Head/Hands/Feet positions vary and match live MCP transform queries
  after conversion into the same space.
- [x] Refresh the matching Unity reference export with the same avatar, clip,
  sample times, coordinate labels, avatar human scale, neutral pose, and all 95
  single-muscle response probes.

Acceptance criteria:

- [x] Repeating an audit does not change subsequent live playback or body/root
  baselines.
- [x] Derived endpoint positions agree with the live hierarchy at each sampled
  time.
- [x] Every reported transform names its source and coordinate space.

## Phase 1 - Transactional Unity Body-Channel Evaluation

- [x] Remove the implicit "Z commits RootT / W commits RootQ" contract.
- [x] Stage scalar RootT/RootQ values and dirty-component masks during property
  evaluation, then finalize each vector/quaternion exactly once after every
  animated member for the pose has been sampled.
- [x] Define stable defaults for missing components instead of reusing stale
  values from another sample or clip.
- [x] Normalize a completed RootQ only during finalization; reject or diagnose a
  non-finite/zero-length quaternion without silently corrupting the pose.
- [x] Define evaluator ownership and baseline behavior for play, replay, stop,
  clip replacement,
  deactivation, loop wrap, negative playback, direct seek, and exact-time audit
  evaluation.
- [x] Ensure a fixed-time result is independent of evaluation history and scalar
  member registration order.
- [x] Keep the transaction allocation-free in per-frame animation ticks.

Acceptance criteria:

- [x] The first completed Sexy Walk sample captures the full RootT and RootQ
  values shown by the raw audit.
- [x] Sampling time B directly produces the same body/Hips state as A-to-B and
  C-to-B evaluation paths.
- [x] Partial channels use documented neutral defaults and cannot fail to
  commit. The synthetic transaction fixtures cover missing and reordered
  components.

## Phase 2 - Body/Root Contract and Unity Parity Evidence

- [x] Implement and compile a reproducible Unity batch exporter for the avatar,
  clip, human scale, clip root settings, Body/Hips/root data, endpoints, muscles,
  and raw curves.
- [x] Run that exporter in a licensed Unity Editor and retain the reference JSON
  as disposable validation evidence plus a compact avatar-profile sidecar.

- [x] Compare, at identical fixed times, Unity raw/body values, XRENGINE imported
  values, composed bone-local rotations, selected limb endpoints, projected
  root, avatar scale, clip metadata, and the extract-only XRENGINE temporal
  delta for consecutive samples.
- [x] Complete mismatch isolation independently. All seven layers are measured
  and have separate diagnostics:
  1. scalar curve import and axis/handedness conversion;
  2. normalized body-position scale;
  3. RootQ coordinate conversion and multiplication order;
  4. body-to-Hips composition;
  5. muscle-to-bone retargeting;
  6. temporal Root Transform extraction;
  7. optional IK/contact compensation.
- [x] Document what `EstimateAnimatedMotionScale()` measures and compare it with
  Unity avatar human scale before retaining or replacing it.
- [x] Verify RootQ sign continuity without confusing `q` and `-q` with a pose
  discontinuity.
- [x] Define loop-pose behavior separately from root-delta accumulation. The
  within-cycle projected pose remains canonical-relative; a cached endpoint
  transform is exponentiated and composed per signed loop count for unwrapped
  placement, while the temporal delta compares consecutive unwrapped poses.

Acceptance criteria:

- [x] Each measured error is assigned to one conversion/composition layer;
  projected pose, unwrapped placement, temporal delta, Hips residual, authored
  IK, and optional contact compensation remain separately observable.
- [x] No scale, quaternion, IK, or body-frame change is justified only by visual
  preference or stale audit output.

## Phase 3 - Implement Only Demonstrated Corrections

The refreshed v3 export justified the completed corrections:

- avatar-specific neutral transforms, body axes, role/twist metadata, and
  coupled-muscle bone models;
- `humanScale * measured units-per-meter` Body-motion scaling;
- preservation and independent evaluation of Unity's XZ, Y, and orientation
  projection metadata;
- quaternion-aware complete Body samples in direct and state-machine playback;
- explicit root-placement ownership, signed loop accumulation, and authored IK
  conversion/contact policies.

With the matching v3 profile loaded, the 81-sample coupled comparison reduces
Hips local rotation error to `0.005122` degrees average / `0.055953` maximum.
Projected yaw is `0.000977` / `0.039565` degrees, and the maximum selected-bone
endpoint difference is approximately `3.25 cm`.

- [x] Keep raw imported body data immutable for diagnostics.
- [x] Expose projected root pose and consecutive temporal root delta as
  allocation-free component-valid outputs. Preserve and restore their exact
  state during diagnostic evaluation and invalidate temporal continuity at
  genuine playback discontinuities.
- [x] Apply the verified normalized Body Transform to the humanoid pose in one
  deterministic Hips/body composition step.
- [x] Keep absolute/in-place body pose state separate from temporal Root
  Transform deltas and model-root publication.
- [x] Define the exact Hips composition order: calibrated Unity neutral local
  transform, coupled authored-muscle residual, hierarchy propagation, then
  optional procedural IK/contact output.
- [x] Evaluate muscle deltas in the avatar's documented bind/body basis and let
  the composed parent body transform propagate them. Do not dynamically rotate
  the muscle basis a second time unless Unity parity evidence proves that is
  required.
- [x] Recompute hierarchy/render/skinning state after composed Hips changes. A
  single Scene-order coupled-pose publication avoids exposing an intermediate
  incompatible Hips transform.
- [x] Introduce a distinct runtime body-frame type only if private staged body
  state plus explicit root-motion output cannot represent the verified
  contract cleanly. `HumanoidProjectedRootPose` provides the distinct public
  placement value; no second mutable body-frame object is needed.

Acceptance criteria:

- [x] Mitsuki/Sexy Walk matches the refreshed Unity fixed-time body, Hips, and
  endpoint evidence within the recorded v3 tolerances.
- [x] Paused and running playback remain stable across loops, seeks, restarts,
  and clip replacement.

## Completed Reference-Pair Follow-up

### IK and contacts

- [x] Decide whether authored Unity IK goals should be evaluated on this runtime
  path and enable them only with calibrated mappings.
- [x] Transform IK goals through the verified body frame and prevent duplicate
  custom-rig solving.
- [x] Keep authored body/root projection separate from post-pose IK body
  compensation; expose their diagnostics independently.
- [x] Add explicit, configurable foot/hand contact compensation only after
  ordinary clip playback matches Unity without it.

### Avatar metadata and retargeting quality

- [x] Formalize required/optional humanoid roles, avatar human scale, body axes,
  twist chains, stretch, feet spacing, and translation DoF in the avatar profile.
- [x] Warn on ambiguous Hips/Spine/UpperLeg mappings and non-finite body inputs.
- [x] Keep runtime role lookup dense and allocation-free.

### Diagnostics and rendering

- [x] Add deterministic zero-muscle and all-95-muscle `-1/+1` response probes
  to both Unity and XRENGINE audit schema 6, with exact diagnostic restoration.
- [x] Extend the audit comparer with worst-time body/Hips phase error and
  per-bone local translation error.
- [x] Extend overlays for body trajectory, projected root, Hips local transform,
  IK goals, and compensation source.
- [x] Create a separate rendering investigation for the observed skinned-mesh
  temporal ghosting/velocity-history failure.

## Completed Reference-Milestone Phase Sequence

1. [x] Prove the semantic `RootT` basis from refreshed Unity projected-root
   samples and the XRENGINE model-root/Hips-parent bases.
2. [x] Add explicit, allocation-free projected root pose and temporal root
   delta outputs. They default to extract-only and never overwrite external
   character placement.
3. [x] Apply the calibrated coupled Hips residual in the same final pose pass
   that exposes projected XZ/Y/yaw.
4. [x] Implement calibrated Y projection for the matching clip/profile while
   keeping XZ, Y, and yaw validity independently diagnosable.
5. [x] Validate fixed times and signed loop accumulation, then replace
   state-machine scalar RootQ blending with quaternion-aware Body groups.
6. [x] Validate authored IK/contact behavior and isolate the unrelated skinned
   motion-vector producer in a rendering investigation.

## Architecture for General Completion

XRENGINE will expose one native Unity-compatible conversion and playback path:

1. Parse the source `.anim` into a lossless, versioned intermediate model that
   preserves generic properties, humanoid muscles, Body/root data, IK,
   clip-settings, events, object references, and interpolation metadata.
2. Build or load the target avatar definition through `HumanoidComponent`.
   Its existing `SetFromNode()`/`FindChildrenFor()` automatic mapping and
   `HumanoidComponentEditor` bone-mapping controls are the foundation to extend,
   not a second avatar system to work around.
3. Validate and compile that definition into a dense runtime description of
   semantic roles, neutral transforms, joint bases/limits, scale, twist,
   stretch, optional bones, translation DoF, and coordinate conventions.
4. Evaluate the normalized clip against that compiled avatar with one
   deterministic native humanoid solver, then apply root projection, animation
   graph blending, authored IK, contacts, events, and generic property bindings
   in their defined order.

The `.anim` does not contain enough information to reconstruct an arbitrary
target skeleton's manually chosen humanoid mapping by itself. That target-side
information comes from the XRE-generated `HumanoidComponent` avatar definition:
automatic mapping supplies the common case and editor correction resolves
ambiguity. Once finalized, the definition is explicit input—not an approximate
runtime guess.

There is no Unity executable in this pipeline, no Unity-evaluated target-pose
artifact, no per-avatar/per-clip calibration, and no second lower-fidelity
backend. Existing Unity captures are static conformance evidence only. While a
feature is unfinished, the native path must fail with a precise capability or
avatar-definition diagnostic rather than silently changing algorithms.

## Phase 4 - Generic Capability and Asset Contracts

- [x] Define a normalized Unity animation import model with distinct domains
  for generic transform/property curves, humanoid muscles, Body channels, IK
  goals, root-motion settings, events, and object-reference bindings.
- [x] Record per-domain capability state as SupportedAndApplied,
  PreservedNotExecutable, or Unsupported. Import diagnostics must identify the
  source binding/encoding and must be available to editor and build tooling.
- [x] Make the avatar data authored by `HumanoidComponent` versioned and
  serializable. The component may own the data directly or reference a reusable
  avatar-definition asset, but there must be one canonical representation and
  one editor workflow.
- [x] Consolidate the useful fields currently split across
  `UnityHumanoidAvatarProfile`, `HumanoidSettings`, runtime-only mappings, and
  Unit Testing World plumbing into that canonical representation, with a
  deliberate migration/removal plan for duplicate state.
- [x] Associate clips, skeletons, and avatar definitions by content-derived
  signatures, not paths or display names. Persist source serialization version,
  source hashes, target skeleton signature, definition revision, and import
  settings hash.
- [x] Reject stale, mismatched, incomplete, or unsupported combinations with
  actionable diagnostics before playback.
- [x] Move coordinate conversion and muscle/IK handedness metadata into the
  normalized source/avatar contracts. Keep manual flip controls as explicit
  debugging overrides only.
- [x] Add an editor-visible import/playback report showing mapping validity,
  the single native path's executed features, and any preserved-but-not-yet-
  executable or unsupported source data.

Phase 4 deliberately records but does not approximate curve semantics assigned
to Phase 8. A weighted tangent, an unimplemented pre/post-infinity mode, or a
nonempty compressed/dense/streamed/constant encoding is retained with its
source payload and binding diagnostics, marks the corresponding domain
`PreservedNotExecutable`, and blocks playback through both direct clips and
state-machine leaves. Phase 8 replaces those temporary capability failures with
complete native execution through the same normalized path.

Implementation evidence from 2026-08-25:

- `Sexy Walk.anim` imports as Unity YAML `serializedVersion: 6` with 155
  path-independent bindings. Generic properties (`33`), humanoid muscles
  (`87`), Body channels (`7`), and IK channels (`28`) are reported as applied.
  Its authored `HeightFromFeet` setting is preserved and blocks playback until
  Phase 6 implements that behavior.
- A byte-identical renamed source produced the same source-content and import-
  settings identities. The cooked-asset round trip retained the manifest, all
  six present domains, and the preserved root-settings payload.
- `XREngine.Animation` and `XREngine.Runtime.AnimationIntegration` build with
  zero warnings/errors. `XREngine.Editor` also builds with project references
  disabled, validating the editor-report integration against the currently
  built dependency graph.
- The concurrent Runtime Modularization Phase-6 bootstrap break has been
  resolved. An isolated editor session now builds and runs successfully. The
  imported avatar reaches a valid compiled definition; the startup clip is
  rejected only for its preserved `HeightFromFeet` setting, which remains the
  explicit Phase 6 capability boundary.

Acceptance criteria:

- [ ] Importing a newly supplied humanoid model and `.anim` requires no source
  edits, name tables, or path-specific configuration.
- [x] Renaming/moving identical inputs does not invalidate compatible cached
  data or alter playback.
- [ ] Production avatar-definition loading works outside Unit Testing World and
  without `XRE_UNITY_HUMANOID_AVATAR_PROFILE`.
- [ ] There is exactly one production conversion/evaluation path, and every
  incomplete capability fails explicitly instead of selecting a fallback.

## Phase 5 - Complete the `HumanoidComponent` Avatar Definition

- [x] Inventory and preserve the current `HumanoidComponent.SetFromNode()`,
  `FindChildrenFor()`, role/finger mapping, bind-pose capture,
  `HumanoidSettings.BoneAxisMappings`, and
  `HumanoidComponentEditor.DrawBoneMappingSection()` behavior before
  consolidating their state.
- [x] Define the complete serialized avatar-definition schema: semantic role to
  stable target-bone identity, required/optional status, skeleton-relative
  path, neutral local TRS, canonical/T-pose correction, pre/post joint basis,
  min/max muscle limits, rotation sign/order, translation DoF, human scale,
  body axes, arm/leg stretch, arm/leg twist, feet spacing, and optional twist or
  helper chains.
- [x] Use stable relative paths plus structural bone signatures so editor
  corrections survive scene reconstruction and model reimport. Runtime scene
  node references are compiled bindings, not the serialized identity.
- [x] Replace name-first runtime discovery with a deterministic auto-mapper that
  combines hierarchy topology, chain lengths/directions, bilateral symmetry,
  bind-pose geometry, joint axes, and aliases. Names and namespaces are useful
  evidence but never the only way an otherwise valid skeleton can map.
- [x] Apply a clear mapping precedence: locked editor correction; trustworthy
  imported semantic metadata when present; topology/geometry/axis inference;
  name aliases. Record the winning evidence and confidence for every role.
- [x] Validate uniqueness, ancestry, chain ordering, left/right symmetry,
  finite/invertible neutral transforms, plausible axes/scale, required roles,
  optional-role dependencies, and T-pose/canonical-pose quality. Do not
  silently accept a high-impact ambiguous mapping.
- [x] Extend the editor to show confidence and validation errors, lock explicit
  corrections, preserve locked bones when auto-map is rerun, preview the
  canonical pose and axes, support undo, and offer direct select/focus for each
  mapped transform.
- [x] Make automatic fixes deterministic and reviewable. Any editor correction
  updates the same serialized definition consumed by import preview, direct
  playback, state machines, and runtime builds.
- [x] Compile a validated definition once into dense role indices, matrices,
  limits, and other derived runtime data. Eliminate periodic per-frame name
  scans or remapping when a finalized definition is available; keep the runtime
  path allocation-free.
- [x] Migrate current v3 profile data through a generic schema migration, then
  remove the duplicated profile-as-alternate-authority behavior and the Unit
  Testing World environment-variable dependency.

Implementation evidence from 2026-08-25:

- Canonical avatar-definition schema v3 now owns semantic bindings, stable path
  and structural identity, neutral/canonical transforms, joint bases and order,
  limits, translation DoF, body/scale/solver settings, twist chains, auxiliary
  bones, source provenance, and content signatures. Imported sources require a
  valid SHA-256 fingerprint; runtime-created skeletons use an explicit no-source-
  artifact provenance. A changed imported fingerprint rejects the stale
  definition until mapping is refreshed and reviewed.
- Automatic mapper v2 combines topology, geometry, joint-axis alignment,
  bilateral symmetry, and alias evidence under the documented precedence. The
  editor reports the winning evidence/confidence, supports locked corrections,
  deterministic remapping, undo, select/focus, and canonical-pose/axis preview.
- Validation covers required/optional roles, uniqueness, ancestry and ordering,
  finite/invertible transforms, scale and axes, symmetry, optional dependencies,
  helpers, twist-chain references/distribution, source identity, and high-impact
  ambiguity. Automatic definitions from an older mapper version are rejected.
- Production pose evaluation now resolves target nodes, neutral transforms,
  axes, limits, solver settings, fingers, eyes, Body/Hips data, reset state,
  auxiliaries, and twist chains from one dense compiled definition. Mutable
  `BoneDef`/settings state and live name discovery remain authoring/migration
  inputs only; no fixture name or path participates in mapping or evaluation.
- The legacy v3 profile migrates into the canonical representation and is then
  cleared as an alternate authority. The profile environment-variable hook is
  gone.
- An isolated editor run imported the configured FBX with no model-specific
  source logic and produced schema 3, mapper 2, `ImportedModel` provenance, a
  64-character source digest, `Valid` status, no diagnostics, 39 resolved bones,
  and no mapping fallbacks. Applying `LeftArmDownUp = 0.6` changed the compiled
  left-upper-arm target quaternion; restoring zero reproduced the original
  quaternion. Scene-integrity validation reported zero errors and warnings.
- `XREngine.Runtime.AnimationIntegration` and the full editor build both finish
  with zero warnings/errors. No tests were added or run because repository policy
  requires explicit user clearance after live feature validation.

Acceptance criteria:

- [ ] Avatars with conventional names, arbitrary names/namespaces, different
  proportions, nonstandard bind axes, and missing optional bones can all reach a
  valid definition through automatic mapping plus editor correction, without
  source changes.
- [ ] Saving, reopening, moving, and reimporting a model preserves explicit
  corrections when the skeleton is structurally compatible and reports a
  precise conflict when it is not.
- [x] The finalized definition is the only target-avatar input to the solver;
  the clip name and historical fixture identity never influence mapping.
- [x] An unresolved required-role, axis, neutral-pose, or limit ambiguity blocks
  parity playback with a useful editor diagnostic instead of producing an
  "approximate" result.

The first two acceptance items remain open as corpus/persistence evidence, not
known implementation defects. Close them only after live runs cover unrelated
avatars with arbitrary names/axes/proportions and an actual save/reopen/move/
compatible-reimport plus incompatible-reimport sequence.

## Phase 6 - Complete Unity Root-Motion Settings

- [x] Implement and validate `OrientationOffsetY`, `Level`, `CycleOffset`, and
  `LoopPose` in the single native evaluator.
- [x] Implement the full Bake Into Pose and Original/Center-of-Mass selection
  semantics for orientation, Y, and XZ rather than treating the flags only as
  channel enable switches.
- [x] Implement `KeepOriginalPositionY` and `HeightFromFeet`, including avatars
  with different proportions, feet spacing, optional toes, and translation DoF.
- [x] Implement true humanoid mirroring across muscles, Body/root trajectories,
  IK goals, contacts, and left/right role mappings. Mirror must not disable root
  channels.
- [x] Define loop-pose seam correction separately from temporal loop-root
  accumulation, including reverse playback and multiple signed loop epochs.
- [x] Define the native evaluation order so every setting is applied exactly
  once across direct playback, state machines, extraction-only output, and
  model-root application.

Acceptance criteria:

- [x] Every field in `UnityHumanoidClipRootMotionSettings` is either executed
  with Unity-reference coverage or rejected as unsupported; none is merely
  stored while parity is claimed.
- [x] Nonzero offsets, mirror, height-from-feet, loop-pose, and representative
  pairwise setting combinations pass on multiple unrelated avatars and clips.

## Phase 7 - Per-Motion State-Machine Root/Body Evaluation

- [ ] Evaluate Body and root projection independently for each active clip or
  blend-tree leaf using that motion's settings, canonical reference, playback
  time, and loop epoch.
- [ ] Blend evaluated Body/root contributions using the actual active weights.
  Zero-weight children must not disable projection or avatar-definition state.
- [ ] Replace the all-contributors-must-share-settings/name rule with a measured
  per-contributor composition contract.
- [ ] Rebase canonical Body and temporal-root baselines at state entry,
  transition start/end, interruption, seek, replay, and evaluator handoff without
  leaking the first sampled state's reference into later states.
- [ ] Extend scalar quaternion grouping and shortest-arc normalization to
  imported IK goal rotations and generic transform quaternion components, not
  only Body `RootQ`.
- [ ] Define additive-layer Body/root behavior explicitly and keep it separate
  from override-layer motion.
- [ ] Validate 1D, 2D, and direct blend trees, two-state transitions,
  interrupted transitions, different clip lengths, different loop states, and
  compatible/incompatible root settings against Unity references.

Acceptance criteria:

- [ ] A state-machine leaf produces the same result as direct playback at the
  same time and weight.
- [ ] Transition/blend output is independent of child registration order and
  contains no discontinuity when a contributor enters or leaves at zero weight.
- [ ] Mixed clip names and renamed clips do not change root-motion behavior.

## Phase 8 - Raw Unity `.anim` Format Completeness

- [ ] Implement Unity weighted tangent semantics using `weightedMode`,
  `inWeight`, and `outWeight`; retain unweighted fast paths.
- [ ] Map all claimed Unity pre/post-infinity modes rather than collapsing every
  value except Loop to Clamp.
- [ ] Import and execute animation events with deterministic ordering across
  forward, reverse, loop, seek, and state-machine playback.
- [ ] Decode and validate nonempty compressed rotation, dense-clip, and
  streamed-clip representations for every declared supported Unity version.
- [ ] Normalize all supported serialized curve families (`m_RotationCurves`,
  `m_CompressedRotationCurves`, `m_EulerCurves`, `m_PositionCurves`,
  `m_ScaleCurves`, `m_FloatCurves`, `m_PPtrCurves`, dense, streamed, and
  constant data) without routing them through different playback semantics.
- [ ] Preserve and execute clip metadata that affects behavior, including sample
  rate, wrap mode, legacy/high-quality flags where applicable, clip bounds,
  loop/time settings, and all humanoid clip settings.
- [ ] Import PPtr/object-reference curves into executable typed tracks with
  asset resolution and missing-reference diagnostics, rather than metadata-only
  preservation.
- [ ] Support generic serialized property bindings used by valid `.anim` files,
  including float, integer, Boolean, enum, vector/component, quaternion/Euler,
  and object-reference targets. Resolve them through a typed XRE binding
  contract; when a Unity-only component has no XRE target, preserve the binding
  and require an explicit adapter instead of reporting successful execution.
- [ ] Preserve quaternion sign continuity and normalize all quaternion binding
  families after interpolation and blending.
- [ ] Publish a versioned `.anim` capability manifest and fixtures for each
  claimed serialization family. Completion of a declared Unity version means
  every behaviorally relevant field is executable through the one path, not
  merely recognized.

Acceptance criteria:

- [ ] Any valid `.anim` within a declared supported Unity version imports every
  behaviorally relevant field. Inputs outside the published version/feature
  contract fail with a precise unsupported-feature diagnostic.
- [ ] Unity and XRENGINE samples agree at keys, frame boundaries, half-frame
  points, randomized times, and infinity/loop boundaries.
- [ ] Events and object bindings are applied, not merely parsed or listed.

## Phase 9 - Replace Fitted Calibration with the Native Humanoid Solver

- [ ] Remove clip-display-name gating from projected root Y and every other
  calibration lookup.
- [ ] Remove learned/fitted, sampled polynomial, and avatar-and-clip calibration
  coefficients from production evaluation. The current
  `UnityHumanoidCoupledBoneModel` result remains historical evidence until the
  native solver supersedes it; it must not survive as a fallback backend.
- [ ] Construct canonical avatar space from the finalized neutral/T-pose,
  semantic joint bases, body axes, and human scale. Convert each normalized
  muscle through its declared asymmetric limits and joint basis rather than a
  fixture-derived response surface.
- [ ] Implement the complete muscle-to-pose order for Hips, spine/chest/neck,
  head/eyes/jaw, shoulders/arms/hands, fingers, legs/feet/toes, optional roles,
  twist distribution, stretch, and translation DoF.
- [ ] Derive Body position/orientation, Hips translation, projected root, and
  feet-based height from the canonical avatar definition and clip settings.
  Ordinary limb muscles must not invent Body translation.
- [ ] Apply authored IK/contact data after the authored humanoid pose and root
  projection in one documented order, with no duplicate custom-rig solve.
- [ ] Normalize quaternion groups, preserve sign continuity, use deterministic
  traversal/composition order, reject non-finite inputs, and keep all per-frame
  structures precompiled and allocation-free.
- [ ] Validate different human scales, proportions, bind axes, twist settings,
  optional roles, missing toes/upper chest, translation DoF, and nonstandard
  concrete bone names.
- [ ] Run the same imported humanoid clip on every target definition in the
  corpus. No target may require regenerated clip data or target-specific code.

Acceptance criteria:

- [ ] No runtime result depends on a calibration clip, captured target-pose
  samples, fitted coefficients, or validation-corpus identity.
- [ ] A previously unseen compatible avatar and clip satisfy the ratified
  Unity-parity tolerances through the same native path without source changes,
  clip-specific setup, or manual coordinate-flip configuration.
- [ ] Missing optional roles degrade predictably and diagnostically; required
  role failures are explicit.
- [ ] Direct clips, state-machine leaves, transitions, and blend trees all call
  the same compiled avatar solver and agree when their sampled inputs agree.

## Phase 10 - Reproducible Single-Path Unity Parity Matrix

- [ ] Check in license-compatible, versioned known-answer conformance fixtures
  for at least three materially different humanoid avatars. XRE import, build,
  editor, tests, and CI must never launch Unity or require a Unity installation.
  Keep the private external avatar as optional integration evidence only.
- [ ] Include conventional and arbitrary bone naming, distinct proportions and
  bind axes, missing optional roles, automatic mappings, and persisted editor-
  corrected mappings in the avatar corpus.
- [ ] Cover every repository walk plus purpose-built clips for in-place motion,
  translation, turns, vertical motion, non-looping motion, mirror, loop-pose,
  authored IK, no IK, weighted tangents, events, PPtr bindings, and supported
  compressed/dense/streamed encodings.
- [ ] Apply every humanoid clip to every compatible target avatar definition,
  including a wholly unseen avatar and clip added only after the solver design
  is frozen. There is no training corpus because production uses no fitted
  model.
- [ ] Cover direct playback, exact seek, reverse, at least ten signed loop
  epochs, one-state playback, transitions, interrupted transitions, and 1D/2D/
  direct blend trees.
- [ ] Compare projected root pose/delta, Body pose, Hips local/world transforms,
  all mapped humanoid bone rotations, selected endpoints, IK goals/contact
  intervals, events, and object bindings.
- [ ] Add rename/move invariance runs and a source scan that rejects production
  references to validation fixture names or paths.
- [ ] Store source Unity serialization/version provenance, reference-schema
  version, source hashes, avatar-definition signature, import-settings hash,
  coordinate spaces, and comparison tolerances with every known-answer fixture.
  CI must reject stale or mismatched references.

Initial numerical gates to ratify before implementation is declared complete:

- Single native path: maximum root translation error `<= 1 mm`, root
  rotation `<= 0.1 deg`, selected endpoint error `<= 2 mm`, bone local rotation
  `<= 0.2 deg`, and ten-loop accumulated drift `<= 2 mm / 0.2 deg`.
- Raw `.anim` import: zero silently ignored behaviorally relevant fields for
  declared supported versions; all unsupported data produces a capability
  failure.

The historical `3.25 cm` maximum endpoint difference does not pass the proposed
native-path gate. It remains useful baseline evidence, not a general completion
result or an acceptable fallback quality level.

## Tests After Live Feature Validation

The historical reference path was validated before its test work began.
Focused redistributable fixtures currently cover:

- atomic RootT/RootQ evaluation and partial channels;
- reset, clip switch, exact seek, loop seam, reverse playback, and scrubbing;
- audit state preservation and transform propagation;
- isolated body translation/rotation;
- combined body motion and muscle pose;
- optional IK goals and explicit contact compensation.

The 2026-08-25 re-evaluation passed the focused Humanoid,
UnityAnimImporterTests, AnimationClipComponentTests, and
AnimStateMachineComponentTests filter: `113/113`, with zero failures. These
tests protect current internal contracts; they do not prove generic Unity
parity.

Retain the historical clip and private local avatar as optional integration
coverage, but do not make automated tests or production behavior depend on the
external FBX, its concrete name, or that clip's identity. New regression tests
must use redistributable, independently authored fixtures and should be added
only after each corresponding live/runtime slice is functionally validated and
the user clears test work.

## Validation Workflow

For every runtime slice:

1. Select an avatar/clip/settings row from the versioned validation manifest.
   Do not encode the row in production source.
2. Verify the checked-in known-answer reference's schema, source hashes,
   coordinate spaces, source Unity version provenance, avatar-definition
   signature, and import settings. The XRE workflow must not launch Unity or
   generate a target-pose bake.
3. Build the editor through a uniquely named isolated MCP session, load the
   model and `.anim` through normal asset import, and use the same serialized
   `HumanoidComponent` avatar definition that a normal project/runtime uses.
4. Validate the automatic mapping or load its persisted editor corrections,
   then pause and sample key times, frame boundaries, half-frame points, random
   times, loop seams, and transition fractions applicable to that row.
5. Compare numeric Body/root/Hips/bone/endpoint/IK/event/binding data first;
   inspect saved PNGs from multiple cameras as secondary visual evidence.
6. Repeat the row after renaming/moving its inputs and repeat the slice on at
   least one previously unseen avatar/clip pair. For mapping work, include one
   arbitrary-name skeleton and a save/reimport cycle with locked corrections.
7. Stop only the named session, inspect animation/rendering logs, and record the
   versioned results before changing the next variable.

The private historical avatar/clip pair may be included as one optional row,
but it is never the default or sole validation workflow.

Narrow build/run validation before tests:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name <unique-session>
pwsh Tools/Invoke-Mcp.ps1 -Session <unique-session> -Method ping
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name <unique-session>
```

Focused tests, only after live validation and explicit clearance:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Humanoid
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~UnityAnimImporter
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~AnimationClipComponent
```

## Completion Checklist

Historical reference milestone:

- [x] Record before/after Unity comparison numbers in the linked investigation.
- [x] Update `docs/user-guide/animation.md` if public body/root behavior or editor
  controls change.
- [x] Update Unit Testing World settings/schema for the root application mode
  and direct/state-machine validation toggle, then regenerate root/server JSONC.
- [x] Add focused regression coverage only after live validation and the user's
  completion directive.
- [x] Verify no new compiler warnings or hot-path allocations in the transaction
  and diagnostic-scope implementation.

General Unity `.anim` and humanoid completion:

- [ ] Ratify one native path's `.anim`, avatar-definition, pose-solver,
  root-motion, IK/contact, property/event, and animation-graph capability
  boundaries plus its numerical gates.
- [ ] Complete Phases 4-10 without production references to a fixture name,
  path, GUID, clip display name, or known sample data.
- [ ] Import and play renamed/moved inputs and previously unseen compatible
  avatar/clip pairs without code changes or manual coordinate-flip setup.
- [ ] Make the existing `HumanoidComponent` automatic mapping plus editor
  correction workflow produce the one complete, versioned, persistent avatar
  definition; consolidate and retire duplicate profile authority.
- [ ] Preserve explicit mapping corrections across save/reload/reimport, compile
  the definition into dense runtime data, and diagnose all incompatible or
  ambiguous mappings before playback.
- [ ] Execute every behaviorally relevant `.anim` field in the declared
  supported Unity versions. Explicit rejection is reserved for inputs outside
  the published version/feature contract and for incomplete development slices.
- [ ] Replace all clip/avatar-fitted and polynomial production behavior with the
  deterministic native solver. Do not retain a calibrated, baked, Unity-
  assisted, or approximate fallback.
- [ ] Pass the full root-settings, direct playback, loop/reverse/seek,
  transition, interruption, blend-tree, event, PPtr, IK/contact, and
  cross-avatar humanoid matrix through that single solver.
- [ ] Pass previously unseen multi-avatar/multi-clip conformance comparisons
  within the same strict gates used for target-avatar playback.
- [ ] Confirm import, editor, runtime, test, and CI workflows have no Unity
  executable/install dependency and consume no Unity-evaluated target-pose
  artifact.
- [ ] Run targeted builds/tests with zero new warnings and confirm all new
  per-frame paths remain allocation-free.
- [ ] Reconcile the startup warning, developer guide, investigation status, and
  this TODO so they advertise the same bounded capabilities.
- [ ] Mark this TODO complete only when the generic matrix passes. Success on a
  named model/clip pair is insufficient.
