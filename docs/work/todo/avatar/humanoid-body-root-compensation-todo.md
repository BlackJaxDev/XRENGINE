# Humanoid Body/Root Parity And Compensation TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Animation API: Humanoid Rigging and IK](../../../developer-guides/animation/animation-api.md#humanoid-rigging-and-ik), [Animation user guide](../../../user-guide/animation.md)
Validation: [Animation Validation](../../testing/animation/animation-validation.md#humanoid-body-and-root)
Investigations: [Body/root compensation](../../investigations/avatar/humanoid-body-root-compensation-2026-08-24.md), [Body frame compensation](../../investigations/avatar/humanoid-body-frame-compensation-2026-08-31.md)

## Current State

One native path imports Unity `.anim` files, compiles the `HumanoidComponent` avatar definition, and solves Body, Hips, projected root, IK, and contacts for direct clips, state-machine leaves, transitions, and blend trees. `UnityHumanoidAvatarProfile` and the `XRE_UNITY_HUMANOID_AVATAR_PROFILE` variable are no longer in code. The conformance runner (`Tools/HumanoidConformanceRunner`) and the manifest (`HumanoidConformanceManifest`, `HumanoidConformanceManifestLoader`) exist with a production-source fixture-identity scan. The last conformance run showed accurate projected root and mapped local rotations, but two open defects: about 15 mm vertical Body/Hips error on the conventional and lean rigs, and about 90 degrees and 124 mm model/root basis error on the arbitrary-name Z-up rig. The three redistributable FBX fixtures, the five walk clips, the 15 schema-7 references, and the manifest were untracked at the last handoff.

## Open Code Items

### Solver

- [ ] Derive and implement the generic absolute Body/Hips vertical allocation for Feet/Y projection from public Unity contracts and avatar data. No clip- or avatar-fitted constants. Done when: the conventional and lean Sexy Walk manifest rows have Body, Hips, and endpoint errors within the ratified gates.
- [ ] Correct static model/root basis composition for arbitrary-Z-up avatars. Done when: the arbitrary-name Z-up manifest row has projected root, model-root and world bone, endpoint, and temporal delta errors within the gates, and mapped local rotations do not regress.
- [ ] Make authored IK goals and foot contacts apply on the direct conformance route. Done when: the direct Sexy Walk observation reports `InverseKinematicsApplied=true` and nonzero foot contacts.

### Conformance corpus

- [ ] Review and commit the three redistributable FBX fixtures, the five walk `.anim` files, the 15 schema-7 references, the manifest, and the runner files. Done when: the files are tracked.
- [ ] Add conventional and arbitrary bone naming, distinct proportions and bind axes, missing optional roles, automatic mappings, and persisted editor-corrected mappings to the avatar corpus. Done when: the manifest covers each corpus dimension.
- [ ] Add purpose-built clips and manifest rows for in-place motion, translation, turns, vertical motion, non-looping motion, mirror, loop pose, authored IK, no IK, weighted tangents, events, PPtr bindings, and supported compressed, dense, and streamed encodings, on each compatible avatar and each root setting. Done when: the manifest declares the rows with hashes, provenance, coordinate spaces, and tolerances.
- [ ] Add manifest rows for externally referenced state, transition, interrupted transition, and 1D, 2D, and direct blend trees. Done when: each route has a declared reference row.
- [ ] Add one avatar and one clip that were not used during solver design. Done when: the unseen avatar and clip pass the same native path and strict gates without source changes, clip-specific setup, or manual coordinate-flip configuration.

### Editor persistence

- [ ] Preserve explicit avatar mapping corrections across save, reopen, move, and reimport when the skeleton is structurally compatible. Report a precise conflict when it is not compatible. `HumanoidComponentEditor`, import mapping persistence. Done when: persisted corrections survive compatible asset changes and incompatible changes fail clearly.

### Documentation

- [ ] Reconcile the startup warning text, `docs/developer-guides/animation/animation-api.md`, `docs/user-guide/animation.md`, and the investigation status with the bounded capabilities in code. Done when: all four state the same supported features.

## Decisions Needed

- [ ] Ratify the capability boundaries of the native path (`.anim` fields and versions, avatar definition, pose solver, root motion, IK and contacts, properties and events, animation graph) and the numerical gates. Proposed gates: root translation at most 1 mm, root rotation at most 0.1 degrees, selected endpoint at most 2 mm, bone local rotation at most 0.2 degrees, ten-loop drift at most 2 mm and 0.2 degrees, and zero silently ignored behavior-relevant `.anim` fields. Owner: animation.

## Out Of Scope

- Claims of exact private Mecanim equations. Parity means observable behavior within ratified tolerances.
- Named avatar or clip special cases. A correction that cannot be derived from normalized clip data and the finalized avatar definition is not a production correction.
- Any Unity executable, install, or Unity-generated per-avatar or per-clip bake in import, editor, runtime, tests, or CI.
- A second "exact" or "retargetable" playback backend, or any fitted, calibrated, or approximate fallback.
- Moving the scene root only because a clip has Body Transform curves.
- Skinned-mesh temporal ghosting. It is tracked as a rendering investigation.
