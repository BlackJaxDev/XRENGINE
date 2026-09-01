# Pose-dependent humanoid Body frame (Phase 9A)

Date: 2026-08-31

Status: Phase 9A is implemented and accepted under the focused approximate
Unity `2022.3.22f1` gate of `<= 0.25 deg` and `<= 0.5 mm`. The accepted model is
`XRE.PublicHumanoidMassHierarchy.v1`. This does not claim private Mecanim
equations, bit-exact parity, or completion of Phase 10's broader matrix.

Related plan: [Body/root TODO](../../todo/avatar/humanoid-body-root-compensation-todo.md).
This corrects the scope of the older Phase 9 finite-pose probe: solving local
muscles and allocating authored Body/root channels did not implement the
pose-dependent skeleton-to-Body constraint.

## Behavior and ownership

Changing limbs or torso changes a provisional skeleton's weighted center and
hip/shoulder orientation. Holding Body fixed therefore requires a compensating
Hips translation and rotation. That correction is not locomotion, physics, a
contact adjustment, or a new RootT/RootQ sample.

The implementation now evaluates final muscles and translation DoF in scratch,
derives the Body frame, resolves Body/root policy, aligns the skeleton once,
and commits the complete authored pose. Projected root history is published
only after that commit. Authored IK uses the committed pre-IK Body frame and
current model-root world placement; it no longer treats Hips as Body. A
muscle-only edit retains the last committed Body target, including blended
targets that cannot be reconstructed from one state leaf.

Unity's public contracts describe an average human body-part mass distribution
for [Body position](https://docs.unity3d.com/ScriptReference/HumanPose-bodyPosition.html)
and hip/shoulder geometry for [Body orientation](https://docs.unity3d.com/ScriptReference/HumanPose-bodyRotation.html).
These contracts motivate the implementation but do not establish exact Unity
mass fractions, segment centers, landmark weighting, or final pose parity.

## Explicit avatar data

`HumanoidAvatarDefinitionMetadata.BodyDefinition` is additive metadata; the
avatar storage schema remains version 3. It contains an algorithm version,
model ID, weighted segments, four orientation landmarks, and hip/shoulder
orientation weights. A point is a semantic role plus a bone-local position in
native model units. A segment interpolates two such points by a center fraction
and supplies a positive normalized mass fraction. It is not a mesh-bounds or
unweighted-bone average and does not read Rigidbody masses.

The initial authoring preset is `XRE.SkeletalMassApproximation.v1`:

| Segment | Mass fraction | Center along start/end |
| --- | ---: | ---: |
| Torso (hip midpoint to shoulder midpoint) | 0.497 | 0.500 |
| Head (head origin) | 0.081 | 0.000 |
| Each upper arm | 0.028 | 0.436 |
| Each forearm | 0.016 | 0.430 |
| Each hand | 0.006 | 0.506 |
| Each thigh | 0.100 | 0.433 |
| Each shin | 0.0465 | 0.433 |
| Each foot | 0.0145 | 0.500 |

The torso's upper endpoint is attached to the highest mapped torso role
(UpperChest, Chest, then Spine). Head uses a skeletal origin proxy. Hand/foot
endpoints use middle-proximal/toe origins when mapped and otherwise their own
origin. These choices are explicit approximations, not claims about Unity's
private anatomical model. Optional-role choices are made during authoring and
persisted, not rediscovered during playback.

`RefreshAvatarDefinition` creates this preset only if Body data is absent and
the required mapping is complete. Existing explicit data is deep-copied and
preserved. An incomplete mapping cannot persist a half-built NaN preset.
Old finalized definitions without Body data must be explicitly refreshed and
reviewed; playback does not silently invent it. Authoring custom data or
changing its endpoints requires refresh/reconfirmation. To intentionally
regenerate the preset after a remap, clear `BodyDefinition` before refreshing.

All data, model/version identity, landmark offsets, segment order, mass/center
fractions, and orientation weights enter the definition content signature.
Compilation validates role ancestry, finite transforms/points, positive masses
summing to one (tolerance `1e-4`, roundoff-only normalization), and usable
orientation landmarks. Unknown algorithms and missing data reject explicitly.

## Coordinate and solve contract

Matrices use row-vector composition: `M_child = L_child * M_parent`.
Body frames and compensation live in component/model-root coordinates; scene
placement is excluded. Bone-local point offsets inherit the skeleton's actual
scale. Imported normalized Body translation is converted once using the
existing coordinate contract and `HumanScale * ModelUnitsPerMeter`.

For segment `i`, transform its local endpoints by provisional model-root FK,
interpolate its center, then sum `mass_i * center_i`. Body up runs from the hip
midpoint to the shoulder midpoint. The side vector is the weighted sum of
right-minus-left hip and shoulder vectors; cross products construct a proper
orthonormal frame. Orientation is calibrated against immutable zero-muscle
FK, including canonical joint corrections: `R_body = inverse(R_neutral) * R_pose`.
The zero-muscle Body frame is therefore identity rotation plus its weighted
center, not a fixed Hips pivot.

Given provisional Body `Bp`, requested root-relative Body `Br`, provisional
Hips in model-root coordinates `Hm`, and the actual Hips-parent matrix `P`:

```text
C       = inverse(Bp) * Br
Hlocal' = Hm * C * inverse(P)
```

Only Hips local receives the rigid compensation; descendants inherit it.
The scratch result is reevaluated to verify Body alignment before any commit.
All mapped/auxiliary bones must descend from Hips. Compiled hierarchy guards
reject reparenting or edits to fixed helpers below Hips; refresh is required.
Hips ancestors may translate/rotate/scale and are read through a compiled chain.
If nonuniform parent scale would require unrepresentable local shear, exact
TRS reconstruction rejects the whole pose instead of discarding the shear.
Commit preserves concrete transform identities instead of converting transform
types and invalidating those cached guards.

State leaves contribute requested Body targets and projected roots, not
already-compensated Hips. Sidecar Feet/loop FK cannot become the final pose:
the final muscle/TDoF blend is always reevaluated before the one alignment.
Direct clips and leaves derive unit-weight Body/root targets and endpoint
generators; the final target is weighted once. Root loop epochs compose before
that weight. Weighted muscle FK remains independent. This fixes two measured
fractional-weight defects: weight-dependent cached Feet/loop baselines, and
direct allocation of a weighted Body before projection instead of blending the
same projected target as a state leaf.
Current Feet projection includes staged TDoF in both direct/state paths;
the canonical baseline excludes per-frame TDoF. Per-leaf Feet caches have an
allocation-free rollback scope. Component Body/root state, accepted diagnostic
state, and IK acceptance remain unchanged when the final solve rejects.
Required loop endpoint FK/alignment failures reject the evaluator input cache;
the old no-pose endpoint substitute is removed. Avatar rebuilds invalidate
avatar-dependent loop/Feet caches. State leaf preparation happens before any
muscle/TDoF/IK setters, so a preparation failure cannot leak a new muscle pose
to the later scene tick. Root publication requires a newly accepted owned Body
input; a muscle-only recenter does not publish locomotion. Exact-seek epochs
and state continuity rebases commit only after an accepted pose.

## Focused runtime evidence

Disposable recorder and raw JSON are under
`Build/_AgentValidation/20260831-105711-humanoid-phase9a/`:

- `scratch/BodyFrameProbe/` (public runtime API recorder, no test framework).
- `reports/body-frame-probe.json` (raw matrices and independent live-center sums).

Two independently constructed skeleton layouts exercise different scale and
proportions, with and without optional torso/neck/toe roles. Positive/negative
arm controls, torso bend/twist, asymmetric and combined poses, nonzero Body
offset/rotation, repeated application, invalid NaN input, and changed Hips
parent transforms are recorded. For the initial manual matrix, maximum live
weighted-center residuals were `1.1935131e-7` and `2.3888379e-7` model units;
Hips changed in position and orientation while scene-root drift and repeated
pose matrix differences were zero. Warmed manual application measured zero
current-thread allocated bytes over 32 evaluations. This is not a claim of
zero allocations throughout the entire clip/import/IK/editor pipeline.

Both local walk clips (`Sexy Walk.anim` and `Sexy Walk No IK.anim`) run through
`AnimationClipComponent.EvaluateAtTime` and
`AnimStateMachineComponent.EvaluateAtTime` on each layout, with IK absent.
The first five-sample seek/repeat/reverse matrix produced maximum direct/state
Hips element difference below `7.1e-8`, independent Body-center residual below
`4.8e-7` model units, and zero repeated/reverse Hips or scene-root drift.
These measure internal behavioral consistency, not external known answers.

The expanded recorder also verifies:

- Reused and fresh direct weights `0`, `0.5`, and `1`, equivalent state layers,
  and two identical blend-tree leaves. At the recorded half-weight sample,
  requested Body Y is `1.058545` and Hips Y is `1.0761464` in all three paths;
  full weight gives `1.3358235` and `1.3424792`. Zero weight retains the neutral
  Body Y `0.78126645` and Hips Y `0.8`. These are fixture observations, never
  implementation constants.
- Mirror on/off with Feet/Loop Pose and those three weights: 24 accepted
  direct/state snapshots, maximum paired Hips element difference below
  `4.1e-9`, requested Body difference below `2.6e-9`, and live-center residual
  below `2.4e-7` model units. Half-weight repeat/reverse samples reproduce the
  same matrices.
- A Body-space hand goal before/after a torso/arm edit and after translated,
  rotated scene placement. The reported goal matches the independently
  calculated committed-Body-to-world position. The fixed-Body edit changes
  goal Y only by floating-point noise (about `3e-7`), despite Hips compensation.
- Explicit direct root-target publication: a nonuniform-parent seek that
  requires shear retains the accepted target matrix, projected pose, epoch `1`
  and sequence `1`. Restoring the parent accepts the same sample in epoch `2`,
  sequence `1`, without target drift. This is not a state-transition, external-
  consumer, or ten-loop epoch matrix.

Normal animation-integration builds repeatedly passed with zero warnings and
errors. The isolated OpenGL editor build also passed. Session
`phase9a-synthetic` started successfully, but its MCP-created primitive fixture
remained unmapped/invalid after unit-scale and standard-name reconstruction.
Three screenshots from two viewpoints showed black viewport output, not a
usable humanoid result. Logs also contained an OpenGL immutable-buffer error;
no causal rendering diagnosis is claimed here. That editor attempt is not a
visual acceptance pass. Only that owned session was stopped. Its evidence is
under `00000000-000000-shared/mcp-sessions/20260831-111711-phase9a-synthetic/`.

No unit tests were added, changed, or run; test work remains subject to the
repository's explicit post-runtime user clearance.

Reproduce the available numerical evidence from the repository root:

```powershell
dotnet build XREngine.Runtime.AnimationIntegration/XREngine.Runtime.AnimationIntegration.csproj --no-restore -v minimal
dotnet build Build/_AgentValidation/20260831-105711-humanoid-phase9a/scratch/BodyFrameProbe/BodyFrameProbe.csproj --no-restore -v minimal
dotnet run --project Build/_AgentValidation/20260831-105711-humanoid-phase9a/scratch/BodyFrameProbe/BodyFrameProbe.csproj --no-build --no-restore --no-launch-profile
```

The recorder is disposable local evidence, not a required build dependency or
redistributable conformance fixture. The numerical findings above are retained
here because ignored evidence may be pruned. Startup logging and animation
guides explicitly identify the Body model and the unratified parity boundary.

## Remaining acceptance and next evidence

- The previous missing-asset/reference blocker is resolved; see the follow-up
  below. Complete the native/external comparison on those actual inputs. No
  production behavior may key on their names, paths, or hashes.
- Ratify/replace the explicit anatomical preset against those known answers,
  including angular convention and mass-center offsets, then ratify numerical
  tolerances. Symmetric axial twists may legitimately have zero correction.
- Complete mixed-clip/additive/interrupted-transition, signed live-loop epoch,
  contact-solver and remaining settings comparisons against external references,
  plus usable editor visual validation, before checking off Phase 9A acceptance.
- The user has not yet reported whether this implementation fixes their live
  character. There is no Unity parity or full-phase completion claim.

## Acceptance follow-up: all muscles and independent Unity evidence

The compensation stage is shared by every muscle, not an arm/spine/chest
allowlist. A complete mapped avatar was evaluated at `-0.5` and `+0.5` for all
95 controls: 190 recorded poses, no missing role or diagnostic, and a changed
mapped-bone pose for every control. Maximum reconstructed live Body-center
residual was `2.3843677e-7` model units and calibrated orientation element error
was `1.1920929e-7`. Returning to zero reproduced all mapped locals and the scene
root exactly. A control need not move the Body center: whether it does depends
on the affected segment/landmark geometry. This is coverage and internal
constraint evidence, not a Unity-parity result.

Evidence: `reports/phase9a-acceptance-sweep.json` and the
`scratch/BodyFrameProbe/` recorder's `--acceptance-sweep` mode.

### Reference provenance

The real source asset was found at `C:/Users/DavidEddy/Desktop/misc/Mitsuki.fbx`.
It was copied into an isolated ignored Unity project; the original was not
modified. Unity `2022.3.22f1` successfully imported it as Humanoid and generated
fresh references. This replaces the earlier assumption that the historical
model could not be recovered on this machine.

- Source FBX SHA-256:
  `366ea4878acdaf209fc7d72eb9d842cd21b1d465a0f30f78ff4c8ae06d128db8`.
- `unity-reference/body-frame-schema1.json`: SHA-256
  `4f2bfdbb9a41a9d23d11c1c924ac91ea96e2635d9e19508f6cb8f0bb0f6d6830`.
  Imported and independently constructed procedural avatars each have a
  zero-muscle neutral, 190 signed muscle poses, asymmetric/combined poses,
  and Body yaw/pitch poses. The record includes requested/read-back HumanPose,
  bone transforms, concrete bind hierarchy, mappings, public muscle limits,
  human scale, and available serialized avatar diagnostics.
- `unity-reference/SexyWalk-schema6.json`: SHA-256
  `2b9e12425deb0f47623b3a1db32811e230c74e47da6efffaa5f053dd8b19cdba`.
- `unity-reference/SexyWalkNoIK-schema6.json`: SHA-256
  `903c902c8a6e116ad9ac2306098d6236db161689cbd99a51b33f02434efdfd07`.
  Both walk references contain 33 samples at 10 Hz. The older schema-6 muscle
  probes use magnitude **1**, unlike the new fixed-Body sweeps at **0.5**; they
  must not be compared as equal inputs.

All paths above are relative to the existing ignored run root. Private FBX
content and generated samples are not checked in or used by native playback.
`Tools/Unity/HumanoidBodyFrameAcceptanceExporter.cs` is a reference-only public
Unity API recorder; XRE import, build and playback do not launch it. The
independent procedural rig is declared in that recorder, not derived from a
sampled Mitsuki response or fitted profile.

### Earlier external failures (superseded by the closeout below)

Evaluating the current declared `XRE.SkeletalMassApproximation.v1` directly on
Unity's recorded bone transforms isolates the Body model from native muscle FK.
Even after subtracting a neutral center bias, maximum center errors are
`0.00416316 m` on Mitsuki and `0.01207575 m` on the procedural avatar. Thus the
failure cannot be explained solely by the native joint solver or a fixed origin
offset. Hip/shoulder orientation geometry is much closer (maximum element
error below `1.9e-5`), but this does not establish complete pose parity.

The public `HumanTrait.GetBoneDefaultHierarchyMass` data and serialized avatar
mass diagnostics were recorded. Subtracting immediate-child hierarchy masses
gives a total exclusive mass of 82.5; normalized masses match the imported
serialized mass array, including observed optional-role merges. That supports
the weights, not an exact center-location equation. Point-origin and uniform
serialized-axis midpoint candidates both fail the existing external poses.
Discrete endpoint hypotheses remain diagnostic only: no optimized coefficients,
sampled response surface, per-avatar correction, or second playback backend has
been introduced.

Additional capture checks (`reports/unity-geometry-discriminator-probes-schema1.json`)
record complete per-pose hierarchies, both serialized avatars, reordered poses,
and fresh-handler read-backs. Named poses before/after the full sweep are
bit-identical. Initial imported bind and zero-muscle poses are distinct: the
initial HumanPose has nonzero muscles on both avatars. With translation DoF
disabled, moving an isolated non-Hips transform while preserving descendant
world transforms does not materially change `GetHumanPose` Body position.
Consequently those translation probes cannot be used to infer zero mass or fit
mass weights; virtual humanoid FK must be accounted for.

A paired procedural capture with `HumanDescription.hasTranslationDoF=true`
has the same rest Body/human scale and the same non-discriminating Get-only
translation results across 22 mapped joints. Enabling that flag does not make
this particular public-API probe a direct COM-weight measurement. Evidence:
`reports/unity-geometry-translationdof-schema1.json`.

An independent read-only geometry review found that the serialized rest
skeleton agrees with recorded bind positions at sub-micrometre scale. For
selected nonterminal segments, the serialized axis endpoint also agrees with
the concrete child position at sub-micrometre scale. Those differences cannot
explain the millimetre residuals. No candidate center topology is ratified.

### Native Mitsuki joint-pose acceptance is also open

The native public `ModelAssetImporter` FBX path imports the same hashed model
without a rendering device, and both walk clips produce accepted runtime poses.
The recorder reads current local matrices and explicitly recomposes the parent
chain; cached world transforms are not used as evidence of a newly applied pose.

The external comparison uses the schema-1 zero-muscle pose and exact signed
`0.5` inputs, with IK/contact disabled. For global bind positions, the importer
boundary is `Unity = (-native.X, native.Z, -native.Y) * 0.0254`. After removing
each Hips transform, Hips-local positions instead use only X reflection and
the unit factor; applying the global Y/Z conversion again would be incorrect.
Parent/Hips-space quaternion deltas use `Q_now * inverse(Q_neutral)` and one
fixed reflection conjugation, never a per-bone choice of whichever comparison
produces a smaller error. The combined pose uses the same six muscle values
as the Unity record.

Even with those corrections, Spine Front-Back `+0.5` differs by about `40°`
at Spine/Chest, and Left Arm Down-Up `+0.5` differs by about `33.54°` down the
left arm chain. The exact combined input has a worst recorded difference near
`45°`. These are neutral-relative, Hips-removed joint-motion differences, not
Body mass-center residuals. A different COM preset cannot repair them. Earlier
scratch comparisons with mismatched probe magnitudes or coordinate frames are
superseded and are not acceptance evidence.

Default native human scale is `25.481258` model units, while Unity's
`0.8143983 m` converts to `32.062923` native model units. The native authoring
path currently estimates scale from Hips-to-foot distance; this scale convention
also needs ratification instead of a per-avatar value chosen to make a clip fit.

Evidence: `scratch/MitsukiImportProbe/`, `reports/phase9a-native-mitsuki.json`,
and `reports/phase9a-native-unity-muscle-compare.json`. All native applications
in the corrected comparison have an empty playback rejection diagnostic.
The final recorder build used existing dependency outputs with
`--no-restore -p:BuildProjectReferences=false` and passed without warnings or
errors. These findings reopen focused validation of the Phase 9 canonical
joint/bind/scale derivation as a dependency of Phase 9A's full-pose gate; they
must not be hidden by fitting a Body correction to the wrong provisional pose.

### Live sampling defect fixed during acceptance

`AnimLayer.EvaluateFrame` sampled typed animation values before advancing its
active state clocks. Direct clip playback advances first. A state could therefore
report phase `0.53125` while applying the preceding frame at phase `0.5`, despite
exact seeks agreeing. The fix advances active states before sampling; Body/root
sidecars and ordinary properties now observe the same clock.

With equal clips, explicit initial seeks, one shared runtime world, root-motion
extraction only, and IK/contact disabled:

- 80-frame direct/state forward playback: maximum local matrix difference
  `1.3709068e-6`, Body `1.1920929e-7`, projected root `2.3841858e-7`.
- Reverse playback: local `4.172325e-7`, Body/root `1.1920929e-7`.
- An identical single-child direct blend tree agrees over all 80 live frames:
  local `2.1010637e-6`, Body/root `5.9604645e-7`. Forward loop ranges `0..3`
  and reverse ranges `-2..0` match the corresponding direct runs.
- A condition-triggered mixed-clip transition from Sexy Walk No IK to Basic
  Walk reaches the destination and repeats deterministically with zero scene-
  root drift. This is not an external Unity mixed-transition reference or an
  interrupted-transition rejection pass.

Evidence: `scratch/GraphBodyFrameProbe/` and
`reports/phase9a-graph-acceptance.json`. Recorder build/runtime validation passed
with zero warnings/errors. No unit tests were added, modified, or run.

Final targeted builds of `XREngine.Animation` and
`XREngine.Runtime.AnimationIntegration` also passed with zero warnings/errors.
All Unity reference runs and owned editor sessions used for this investigation
have exited or been stopped; no user-owned editor was stopped.

## Phase 9A closeout

The earlier mass, joint-frame and scale failures above were diagnostic history,
not the final state. Closeout replaced the legacy body approximation with
`XRE.PublicHumanoidMassHierarchy.v1`, authored canonical skeletons through full
affine parent transforms, separated imported-model canonicalization from
runtime-authored neutral skeletons, and corrected the Upper Chest, Jaw and toe
range/sign semantics discovered by the independent 95-channel sweep.

The focused contract is approximate Unity `2022.3.22f1` compatibility at
`<= 0.25 deg` joint rotation and `<= 0.5 mm` endpoint displacement:

- Six calibration geometries/24 poses and seven held-out geometries/28 poses
  have worst calibrated Body-center residuals of `2.39e-7 m` and `2.46e-7 m`.
- Imported Mitsuki covers 192 neutral/signed/combined cases at `0.213063 deg`
  and `0.301777 mm`; HumanScale is `0.8143982` versus Unity's `0.8143983`.
- The independently defined runtime-authored procedural avatar covers all 95
  signed muscle pairs at `0.039565 deg` and `0.000598 mm`, with exact neutral
  restoration and no root drift.
- The final graph recorder has zero failures: direct/state local pose differs by
  at most `1.1920929e-7`, Body by `6.00121e-8`, root target by `2.9802322e-8`,
  and condition/mixed-clip transition repeats are deterministic.

Schema 6 persists `XRE.MecanimCanonicalPose.2022.3.v2`. Definitions lacking
that exact marker regenerate generated canonical corrections and require review;
authored solver axes, limits and pre/post data remain preserved when structurally
compatible. Runtime playback rejects stale markers rather than mixing contracts.

These results close Phase 9A, not Phase 10. They establish Mitsuki plus one
independent runtime-authored rig, not bit-exact behavior or a three-imported-
avatar generic conformance matrix. Unit tests were neither added nor run under
the repository's live-validation-first policy.
