# Humanoid Avatar Definition Phase 5 Investigation

Date: 2026-08-25

## Problem statement

Phase 5 had to replace mutable, name-oriented humanoid setup with one versioned
target-avatar definition that can support arbitrary compatible skeletons. The
definition must be authored and corrected entirely in XRENGINE, compile once for
runtime use, contain no model/clip fixture logic, and leave later Unity-parity
work on the same native path rather than introducing another backend.

This investigation covers the avatar-definition architecture only. Full root-
motion settings remain Phase 6, raw `.anim` curve-format completeness remains
Phase 8, and complete native muscle-solver semantics remain Phase 9.

## Preserved pre-Phase-5 behavior

- `HumanoidComponent.SetFromNode()` and its old `FindChildrenFor()` role/finger
  discovery established the semantic `BoneDef` assignments used by the solver.
- Bind local/world matrices were captured in component-owned dictionaries and
  refreshed around content-basis changes.
- `HumanoidSettings` carried bone-axis mappings, neutral-pose data, joint/muscle
  ranges, and solver tuning.
- `HumanoidComponentEditor.DrawBoneMappingSection()` exposed manual role
  assignments and the debug skeleton.
- The old name-first mapper remains private implementation history, but
  production initialization no longer invokes it. Aliases are now only one
  weighted input to the generic mapper.

## Issues found

1. The initial automatic body basis treated model +X as anatomical right even
   though this engine's imported humanoid convention required transformed -X.
   Left/right confidence and canonical axes were therefore inverted.
2. A missing UpperChest allowed the optional role to consume Neck, creating a
   role collision.
3. An elbow helper leaf could score as a wrist under name/topology-heavy
   discovery. Wrist selection needed palm/finger-branch evidence.
4. Terminal Head and Wrist bones lacked a child direction, so the first axis
   profile could incorrectly reject an otherwise valid skeleton.
5. Axis-profile results were keyed by display name. Duplicate names could
   overwrite one another and attach calibration to the wrong node.
6. Mapping confidence did not explicitly include bind-space joint-axis evidence.
7. Several runtime pose paths still read mutable `BoneDef`, captured bind state,
   or authoring settings after the definition had ostensibly been finalized.
8. Translation DoF, helper bindings, and twist-chain references were serialized
   but were not all validated and represented in the dense compiled contract.
9. Imported definitions could validate with an empty model-source fingerprint,
   so source changes that preserved skeleton shape could evade stale-definition
   detection.
10. The editor still described a Unity exporter as the way to populate one
    neutral-pose preset, conflicting with the pure-XRE authoring contract.

## Implemented solution

### Canonical schema and identity

- Added avatar-definition schema v3 with stable skeleton-relative paths and
  structural hashes; semantic role requirements; neutral local/world transforms;
  canonical, pre-, and post-rotations; rotation order/sign; joint and muscle
  limits; translation DoF; human/model scale; body axes; solver stretch/twist/
  feet settings; auxiliary bindings; and twist chains.
- Added explicit source provenance. Imported models require a normalized source
  SHA-256 digest. Runtime-created skeletons explicitly declare that no source
  artifact exists. Unknown provenance and malformed/missing imported hashes
  reject playback.
- A newly observed imported fingerprint that differs from the finalized one
  invalidates the compiled definition, preserves corrections for remapping, and
  requires review before confirmation. Provenance and source content participate
  in the definition and clip/avatar compatibility signatures.
- Generic FBX/model startup import now supplies a byte-content digest. Unity-
  prefab conversion computes a path-independent digest from its exact dependency
  manifest; it does not run or consult the Unity editor.

### Mapping and validation

- Automatic mapper v2 combines hierarchy topology, chain directions/lengths,
  bind geometry, joint-axis alignment, bilateral symmetry, and aliases.
- Mapping precedence is locked editor correction, trustworthy imported semantic
  metadata, topology/geometry/axis inference, then aliases. Winning evidence and
  component scores are retained per role.
- Corrected anatomical basis orientation, optional UpperChest behavior, helper-
  leaf wrist selection, palm/finger branch recognition, and terminal Head/Wrist
  axis profiling.
- Profile entries and internal mapping evidence now use `SceneNode` identity;
  display names remain authoring evidence only.
- Validation rejects missing required roles, duplicate semantic nodes, broken
  ancestry/order, left/right conflicts, non-finite or non-invertible transforms,
  implausible scale/axes, optional-role dependency errors, bad helper identities,
  invalid helper/twist ancestry or distribution, stale mapper versions, source
  mismatch, and high-impact ambiguity.

### Editor and runtime contract

- The editor reports status, provenance and hashes, per-role confidence/evidence,
  validation errors, locks, and source-capability diagnostics. It preserves
  locked corrections during remap, supports undo and direct select/focus, and
  previews the canonical pose and axes. Unknown source provenance can be
  explicitly marked runtime-authored; imported provenance must come with a
  fingerprint.
- The neutral-preset message now directs authors to the canonical avatar
  definition and no longer requires Unity-exporter output.
- Valid definitions compile once into dense role-indexed nodes, neutral
  transforms, canonical/pre/post rotations, rotation order, translation DoF,
  axes, limits, muscle ranges, solver settings, twist chains, helpers, and legacy
  migration calibration.
- Pose application, limb and finger solving, Body/Hips samples, eye effects,
  reset, bind access, and calibration now consume the compiled definition. Live
  name discovery, mutable `BoneDef`, and captured bind dictionaries are limited
  to authoring, migration, rebinding, and compilation.
- Legacy v3 profile data migrates into the canonical definition and is cleared as
  an alternate authority. The Unit Testing World environment-variable profile
  hook was removed.

## Live validation evidence

The named isolated session was
`humanoid-phase5-avatar-definition`, rooted at:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260825-140144-humanoid-phase5-avatar-definition/`

Final run evidence:

- Full isolated editor build: zero warnings and zero errors.
- Imported avatar definition: schema 3, automatic mapper 2, revision 1,
  `ImportedModel` provenance, 64-character source digest, `Valid` status, no
  diagnostics, 39 resolved profile bones, zero fallbacks, four limb twist
  contracts, and no auxiliary bones for this skeleton.
- Manual compiled-solver probe on `LeftArmDownUp`:
  - neutral local quaternion:
    `(0.11819269, -0.22368604, -0.042752646, 0.9665233)`
  - value `0.6`:
    `(0.5445448, -0.17213175, -0.2617945, 0.7780137)`
  - restored value `0`:
    `(0.11819269, -0.22368605, -0.042752646, 0.9665233)`
- Scene-integrity validation: zero errors and zero warnings.
- The animation log reports complete required-bone mapping and 95% profile
  confidence. The startup `.anim` is rejected only because its authored
  `HeightFromFeet` setting is preserved but not executable; that is the planned
  Phase 6 gate.
- Viewport evidence is under
  `Build/_AgentValidation/20260825-133442-humanoid-phase5/mcp-captures/`.
  The subject renders very dark because source textures are missing from their
  original external locations; this is unrelated to avatar mapping.
- Direct builds of `XREngine.Runtime.AnimationIntegration` and
  `XREngine.Editor` also completed with zero warnings/errors.

No tests were added or run. Repository policy requires live feature validation
first and explicit user clearance before test work for this integration.

## User-reported outcome

No user visual verdict has been reported for this Phase 5 implementation yet.

## Remaining work

- Run the Phase 5 acceptance corpus with unrelated avatars covering arbitrary
  names/namespaces, different proportions, nonstandard axes, duplicate names,
  and missing optional roles. The implementation is generic, but one live avatar
  does not prove the full corpus.
- Exercise actual save, reopen, move, compatible reimport, and incompatible
  reimport flows to close correction-persistence acceptance.
- Phase 6: execute all root-motion settings, beginning with the observed
  `HeightFromFeet` capability block.
- Phase 8: execute weighted tangents, infinity modes, events/object bindings,
  and compressed/dense/streamed/constant curve families through the same path.
- Phase 9: replace fitted legacy calibration with the complete native humanoid
  solver and execute pre/post basis, rotation order, twist/helper distribution,
  translation DoF, Body projection, and IK/contact order exactly.
