# Unity Prefab Avatar Import TODO

- Created: 2026-07-29
- Owner: Assets / Avatar / Rendering / Editor
- Status: Paused at wrap-up; core import implementation is present locally,
  but visual acceptance and the remaining unchecked items are incomplete
- Primary target: Unity 2022.3 model-backed avatar prefabs imported as
  `XRPrefabSource`

## Wrap-Up Handoff - 2026-07-29

This task was intentionally stopped at the user's request. The checklist is
the authority for remaining scope: 162 of 180 boxes are checked, and none of
the 18 unchecked boxes should be inferred complete from partial import or scene
evidence.

Verified shutdown state:

- No owned avatar shader probe or named MCP editor process remains running.
- The private source `jax2026.prefab` was not modified. Its final size is
  164,122 bytes, its last-write time remains
  `2026-01-29T09:03:38.8537663Z`, and its SHA-256 remains
  `EA63E9F3859F64C2B07D0976CEDB3B2842873CF2163E6104A79ADF4EC2824E8F`.
- The last editor settings had character locomotion and the third-person pawn
  disabled. Future avatar validation must keep both disabled and position only
  the flying camera through MCP.

What the last live run proved:

- The private import produced an ordinary native `XRPrefabSource` and MCP could
  instantiate it through the standard prefab API.
- The instance contained 899 nodes, 120 components, 63 model components, 14
  physics chains, 21 collider-like components, and three constraints.
- Three camera/focus screenshots were captured and inspected. They showed the
  live collider/debug silhouette, but not a correctly rendered, textured,
  skinned avatar. The separate visual-success boxes therefore remain unchecked.
- The OpenGL session crashed in `DrawElementsInstanced` after very large Uber
  variants finished linking. `MAT FACE 2` also reported an out-of-bounds
  Poiyomi mask channel expression. The mask helper now clamps and selects the
  channel explicitly, but that change has not passed a complete editor rerun.
- The final offline Vulkan material probe found 41 bound materials. It attempted
  37 Uber variants: eight passed and 29 failed; four non-Uber materials were
  intentionally skipped. The probe did not preserve useful backend compiler
  diagnostics, so these failures remain an active rendering blocker.
- The new focused shader/private-avatar regressions have not run to completion.
  The current solution build is also blocked by unrelated in-progress advanced
  rendering code where `AdvancedVisibilityPayload` has no `Skinned` parameter.

Resume in this order:

1. Restore a compilable solution/test baseline without changing the avatar
   importer to work around unrelated advanced-rendering failures.
2. Make the material probe retain the real Vulkan compiler diagnostics, resolve
   all 29 Uber failures, and run the focused shader regression.
3. Start a fresh named OpenGL MCP session with locomotion and the third-person
   pawn disabled. Use the flying camera to frame the active `Body` renderer,
   capture at least two materially different views, and confirm camera-relative
   changes plus visible skinning and textures before checking visual criteria.
4. Inspect all 55 FBX remaps, 31 prefab material overrides, six authored
   blendshape defaults, active states, transforms, and renderer bindings against
   the Unity-authored targets.
5. Implement unchanged generated-subasset identity/content reuse and validate
   generation-2 FBX file-ID correspondence against multiple Unity exports.
6. Exercise the sanitized fixture through the external-file UI and actual
   drag/drop path, compare a representative Unity reference, then run the full
   Unity/Poiyomi regression set and reconcile the remaining boxes and docs.

Durable findings and the prior validation counts are recorded in
`docs/work/investigations/assets/unity-prefab-avatar-import-2026-07-29.md`.
Ignored captures, logs, and the disposable probe remain under
`Build/_AgentValidation/20260729-unity-avatar-import/`.

## Decision Summary

- Unity `.prefab` files are first-class third-party prefab sources and must
  instantiate in the editor through the same `XRPrefabSource` path used by FBX
  model imports.
- The importer will detect the Unity project from the nearest ancestor named
  `Assets`, index GUIDs from that project, and import only the dependency
  closure required by the selected prefab.
- The original Unity source path/project root and the XRENGINE output
  destination must remain separate throughout import.
- Poiyomi Pro is not a supported target shader family.
- Materials authored with Poiyomi Pro may be recognized only so they can be
  downgraded to the already-supported Poiyomi Toon 9.3.64 conversion path.
- Losing Pro-only features is acceptable. Pro-only properties, passes, and
  authoring behavior must not be implemented.
- Lossy downgrade must still be reported clearly; it must not masquerade as
  full Poiyomi Pro parity or silently become an unrelated generic material.

## Goal

Import a Unity avatar prefab directly from its original Unity project into a
native XRENGINE `XRPrefabSource`, including:

- the FBX-backed hierarchy, skeleton, meshes, skinning, and blendshapes,
- Unity prefab instance composition and overrides,
- GUID-resolved materials, textures, nested prefabs, and relevant animation
  assets,
- lossy Poiyomi Pro-to-Toon material downgrade,
- useful avatar metadata and supported VRChat component conversion, and
- deterministic reimport when any required Unity dependency changes.

Once generated, dragging or placing the native prefab must use the existing
`XRPrefabSource` preview and `InstantiatePrefab` workflow. This work must not
create a second Unity-specific scene-placement path.

## Analyzed Avatar Fixture

`jax2026.prefab` is a private, local validation asset and must not be committed
to the repository. Its current dependency shape is:

- Unity editor version: `2022.3.22f1`.
- Prefab size: 164,122 bytes.
- Main model source: binary `jax2026.fbx`, 82,937,792 bytes.
- Two PrefabInstances:
  - the main FBX model, and
  - a package prefab used for VRC face-tracking templates.
- Serialized documents:
  - 38 GameObjects, including 33 stripped model-prefab proxies,
  - 27 Transforms, including 22 stripped model-prefab proxies,
  - 39 MonoBehaviours,
  - one stripped SkinnedMeshRenderer, and
  - two PrefabInstances.
- The main FBX PrefabInstance has 217 property modifications, including:
  - 75 material-slot assignments,
  - 52 probe-anchor assignments,
  - 33 active-state overrides,
  - transform overrides,
  - 14 bounds-related fields,
  - six blendshape-weight overrides,
  - name overrides,
  - animator-controller/root-motion fields, and
  - renderer culling fields.
- The FBX `.meta` uses `fileIdsGeneration: 2`, humanoid animation import, blend
  shape import, and 55 external material remaps.
- The prefab contributes 31 unique material overrides. These do not overlap
  the 55 FBX material remaps, producing a visual closure of 86 materials.
- The 86 materials reference 128 resolvable textures:
  - 117 PNG,
  - four JPG,
  - two BMP,
  - two EXR,
  - one GIF,
  - one PSD, and
  - one HDR.
- The 39 direct VRChat MonoBehaviours comprise:
  - 21 PhysBone colliders,
  - 14 PhysBones,
  - two VRChat constraints,
  - one AvatarDescriptor, and
  - one PipelineManager.
- The Unity project embeds Poiyomi Pro package version 9.3.66.
- Of the 31 direct Poiyomi materials:
  - four use the unlocked Poiyomi Pro 9.3.66 shader, and
  - 27 use locked/optimized Poiyomi Pro 9.3.11 shaders, including Pro Grab Pass
    variants.
- Two VRChat expression asset GUIDs are already stale or unresolved in the
  source Unity project. They must generate non-fatal diagnostics unless later
  shown to be required for the visual avatar.

## Current Engine Reality

- `XRPrefabSource.Import3rdParty` recognizes `.prefab` and calls
  `UnitySceneImporter.ImportPrefab`.
- Imported Unity and FBX prefab sources already converge on the same native
  `XRPrefabSource` instantiation path.
- `UnitySceneImporter` already detects the nearest `Assets` ancestor and scans
  `.meta` files below `Assets/` and embedded `Packages/`.
- The external-file import UI currently copies a selected prefab and optionally
  its own `.meta` before reimport. This discards the original Unity project
  context and therefore most GUID dependencies.
- Each Unity material import constructs a separate `UnityAssetResolver`, which
  can rescan the same large Unity project repeatedly.
- Every PrefabInstance source currently recurses through the Unity YAML
  hierarchy parser. A binary FBX source is therefore read as text/YAML instead
  of being dispatched to `ModelImporter`.
- The Unity document-header parser recognizes the `stripped` token but does not
  preserve it. Stripped model proxies are parsed as incomplete standalone scene
  objects.
- Prefab model-component modifications currently cover only enabled state,
  cast-shadows, and receive-shadows.
- MonoBehaviour documents are not parsed or attached.
- The Poiyomi matcher is intentionally pinned to the free Poiyomi Toon 9.3.64
  GUID/version. The analyzed Pro materials are rejected and currently receive
  the generic Unity conversion, which retains only base color and main texture.
- The existing targeted baseline has seven passing Unity scene/material tests,
  but no coverage for binary model PrefabInstances, stripped records, Unity
  model fileIDs, FBX material remaps, or lossy Pro-to-Toon downgrade.

## Scope

- Unity project/root detection and reusable GUID indexing.
- External-source import without losing source-project context.
- Recursive, deterministic dependency-closure discovery.
- Unity YAML prefab composition.
- Model-backed PrefabInstance dispatch and source-object identity.
- FBX `.meta` import settings and material remaps.
- Prefab renderer, transform, material, blendshape, and active-state overrides.
- Existing Poiyomi Toon converter reuse through a lossy Pro-to-Toon input
  normalization layer.
- Supported VRChat avatar metadata, PhysBone, collider, and constraint mapping.
- Import diagnostics, dependency manifests, reimport, tests, and editor
  validation.

## Explicit Non-Goals

- Do not implement Poiyomi Pro shader features.
- Do not add a Poiyomi Pro shader catalog, forward-uber feature set, authoring
  UI, shader variants, or runtime compatibility mode.
- Do not reproduce Pro Grab Pass, refraction, blur, touch effects, or any other
  Pro-only pass. These features may be discarded during downgrade.
- Do not commit or redistribute Poiyomi Pro shader source, package contents,
  materials, or textures.
- Do not describe Pro-authored input recognition as Poiyomi Pro support.
- Do not execute Unity, VRChat SDK, Poiyomi, ThryEditor, or arbitrary
  MonoBehaviour code inside XRENGINE.
- Do not copy an entire Unity project into XRENGINE to make GUID lookup work.
- Do not preserve VRChat upload/pipeline metadata that has no XRENGINE runtime
  meaning.
- Do not create a Unity-specific scene spawn path after native prefab
  generation.

## Import Architecture Invariants

- [x] Introduce one import context that owns the original entry path, detected
  Unity project root, output destination, GUID index, dependency graph,
  diagnostics, and import cache.
- [x] Pass that context through prefab, model, material, texture, animation, and
  nested-prefab import rather than constructing independent resolvers.
- [x] Index `.meta` files once per Unity project snapshot and resolve asset
  contents lazily only when their GUID is reachable.
- [x] Keep source reads separate from native output writes.
- [x] Identify Unity source objects with at least source asset GUID, local
  fileID, and object kind; do not rely on globally ambiguous node names.
- [x] Preserve dependency and conversion diagnostics on the generated prefab.
- [x] Detect cycles and duplicate GUIDs deterministically.
- [x] Do not silently substitute placeholder assets for required visual
  dependencies.
- [x] Optional or behavior-only missing dependencies may remain non-fatal when
  explicitly classified and reported.
- [x] Externalized XRENGINE meshes, materials, textures, models, and animation
  assets must remain deduplicated and use stable paths across reimport.
- [x] Large source files must be streamed or dispatched to the appropriate
  importer; never load binary FBX data through a YAML text path.

## Phase 0 - Contracts And Reproduction Fixtures

- [x] Define `UnityProjectImportContext` and `UnityAssetIdentity` contracts
  before extending individual component parsers.
- [x] Define dependency kinds: required visual, optional visual, animation,
  avatar behavior, editor-only, and unsupported.
- [x] Define native output precedence and stable sibling-asset naming.
- [x] Define import completion tiers:
  - visual prefab completion, and
  - optional avatar-behavior completion.
- [x] Add a small committed, redistributable Unity-project fixture containing:
  - an `Assets` root,
  - a YAML prefab,
  - a model-backed PrefabInstance,
  - stripped proxy records,
  - an FBX `.meta` with external material remaps,
  - nested prefab references,
  - locked-style material metadata, and
  - missing optional dependencies.
- [x] Keep the private `jax2026` avatar as an opt-in local integration corpus.
- [x] Record a deterministic analysis summary for the private fixture without
  storing its proprietary content.
- [x] Capture the current failure mode in a regression test or import-harness
  assertion before implementing the model-prefab fix.

Acceptance criteria:

- [x] Tests can reproduce the binary-model-as-YAML failure without the private
  avatar.
- [x] Fixture licensing permits inclusion under both the XRENGINE Community
  Source License and commercial distributions.
- [x] The import context and dependency classifications are reviewed before
  implementation spreads across importers.

## Phase 1 - Unity Project Detection And Shared GUID Index

- [x] Extract project-root discovery from `UnitySceneImporter` into a shared
  Unity import service.
- [x] Starting at the original selected asset, climb to the nearest ancestor
  named `Assets` and treat its parent as the Unity project root.
- [x] Validate the detected root using `ProjectSettings/ProjectVersion.txt`
  when available.
- [x] If no `Assets` ancestor exists, support an explicit Unity project or
  `Assets` folder selection instead of guessing a broad filesystem root.
- [x] Index GUIDs from:
  - `<project>/Assets/**/*.meta`,
  - embedded `<project>/Packages/**/*.meta`, and
  - package locations resolved through `Packages/manifest.json` and
    `Packages/packages-lock.json`, including `Library/PackageCache` when
    required.
- [x] Cache the index by normalized Unity project root.
- [x] Add invalidation based on relevant directory/file changes or an explicit
  refresh generation.
- [x] Report duplicate GUIDs with every candidate path and deterministic
  precedence.
- [x] Avoid rescanning the project once per material or nested prefab.
- [x] Expose GUID-to-path and path-to-GUID lookup through the shared context.

Acceptance criteria:

- [x] Selecting a prefab anywhere below its original Unity `Assets` tree finds
  its project root automatically.
- [x] Asset and package GUIDs resolve through one shared index.
- [x] Importing 86 materials does not trigger 86 full-project scans.
- [x] Missing, duplicate, and package-resolution failures are actionable.

## Phase 2 - Dependency Closure And External Import UX

- [x] Change external Unity import so conversion reads from the original source
  path while native output is written to the chosen XRENGINE Assets
  destination.
- [x] Do not copy the entry prefab before project-root detection and dependency
  discovery.
- [x] Add a Unity-project import option showing the detected project root and
  allowing correction before import.
- [x] Build a recursive dependency graph by parsing only reached assets.
- [x] Discover GUID edges from:
  - `.prefab` and `.unity` serialized references,
  - `.mat` shader and texture references,
  - `.fbx.meta` `externalObjects` and relevant `ModelImporter` settings,
  - `.controller`, `.overrideController`, `.anim`, and supported `.asset`
    documents,
  - texture `.meta` import settings, and
  - nested prefab sources.
- [x] Track source GUID, local fileID, source path, dependency kind, and
  referring property for every graph edge.
- [x] Detect recursive prefab cycles without dropping unrelated valid
  dependencies.
- [x] Copy raw files only when a native importer requires a local stable source;
  otherwise convert directly from the original project.
- [x] Show dependency discovery and conversion progress in the existing editor
  job UI.
- [x] Produce a summary grouped by converted, downgraded, ignored optional,
  unresolved, and failed dependencies.

Acceptance criteria:

- [x] Importing a single external prefab resolves its model, materials,
  textures, and embedded-package prefab without manually copying folders.
- [x] The source Unity project is not modified.
- [x] The XRENGINE project receives only generated assets and intentionally
  retained source files.

## Phase 3 - Model-Backed PrefabInstance Composition

- [x] Dispatch PrefabInstance sources by actual asset type:
  - `.prefab` and `.unity` use the Unity YAML hierarchy importer,
  - `.fbx`, `.obj`, `.gltf`, `.glb`, `.dae`, and other supported model formats
    use the existing `XRPrefabSource`/`ModelImporter` path, and
  - unsupported source types produce explicit placeholders or failures
    according to dependency classification.
- [x] Never call `File.ReadAllLines` or YamlDotNet for model formats.
- [x] Preserve the `stripped` document-header flag.
- [x] Treat stripped GameObject, Transform, and renderer records as
  correspondence proxies, not standalone nodes/components.
- [x] Represent correspondence between:
  - the local stripped fileID,
  - `m_CorrespondingSourceObject.fileID`,
  - the source model GUID, and
  - the imported XRENGINE node/component.
- [x] Define and implement Unity model fileID generation/mapping for
  `fileIdsGeneration: 2`, or provide an equally deterministic source-object
  identity bridge.
- [x] Do not use node name alone as the identity key; duplicate bone and mesh
  names must remain valid.
- [x] Preserve FBX hierarchy paths during model import so identity mapping can
  be diagnosed and tested.
- [x] Compose added GameObjects and components onto their corresponding model
  nodes.
- [x] Apply removed GameObjects/components after correspondence is established.
- [x] Preserve root parenting and nested-prefab insert order.
- [x] Ensure Unity-to-XRENGINE coordinate conversion is applied exactly once.

Acceptance criteria:

- [x] The sanitized model prefab expands without binary/YAML exceptions.
- [x] No phantom `GameObject 0` nodes are created from stripped proxies.
- [x] Every required override target resolves by stable identity.
- [x] Duplicate node names do not redirect overrides to the wrong object.
- [x] The generated root remains an ordinary `XRPrefabSource`.

## Phase 4 - FBX Metadata, Materials, And Instance Overrides

- [x] Parse the source FBX `.meta` as a `ModelImporter` document.
- [x] Respect relevant settings for:
  - humanoid/animation import,
  - blendshape import,
  - material naming/remap behavior,
  - axis and scale conversion, and
  - fileID generation.
- [x] Apply material precedence in a documented order:
  1. model-embedded/default material,
  2. FBX `.meta` `externalObjects` material remap,
  3. prefab instance `m_Materials.Array.data[n]` override.
- [x] Match material remaps by Unity material identity and imported submesh slot,
  not by first matching display name.
- [x] Import and deduplicate all reached materials and textures.
- [x] Apply prefab instance overrides for:
  - local position, rotation, and scale,
  - active state,
  - name,
  - material slots,
  - blendshape weights,
  - renderer enabled/culling/shadow fields,
  - root-motion/controller references where supported, and
  - authored bounds when they are required and valid.
- [x] Classify probe-anchor fields as supported, native equivalent, or ignored
  with diagnostics; do not pretend they were applied.
- [x] Recompute invalid or unsupported authored bounds from imported geometry
  when that is safer than retaining Unity-specific values.
- [x] Externalize generated materials, textures, meshes, submeshes, and models
  through the existing prefab externalization path.
- [x] Validate BMP, EXR, GIF, PSD, HDR, PNG, and JPG dependencies exercised by
  the analyzed material closure.

Acceptance criteria:

- [ ] The 55 FBX material remaps and 31 unique prefab override materials resolve
  in the intended slots for the private fixture.
- [x] All 128 referenced textures either import or produce a specific
  format/import-setting failure.
- [ ] Six authored default blendshape weights appear on the correct meshes.
- [ ] Active-state and transform overrides reproduce the authored hierarchy.

## Phase 5 - Lossy Poiyomi Pro-To-Toon Downgrade

This phase recognizes Pro-authored inputs solely to route them into the
supported free Toon converter. It must not expand the supported shader target.

- [x] Separate shader-family classification from full-conversion acceptance.
- [x] Add a classification such as `PoiyomiProDowngradeSource`; do not mark Pro
  as an accepted Poiyomi Toon shader match.
- [x] Recognize the analyzed unlocked Pro, locked Pro, and locked Pro Grab Pass
  source identities using shader name/GUID and `OriginalShaderGUID` metadata.
- [x] Record the detected source family, version, and locked state.
- [x] Normalize common serialized properties from Pro 9.3.11 and 9.3.66 into
  the pinned Poiyomi Toon 9.3.64 material schema.
- [x] Reuse the existing Toon-to-forward-uber conversion after normalization.
- [x] Preserve common Toon-compatible behavior when present:
  - base color and main texture,
  - normal maps,
  - render mode, alpha, blend, cull, and queue state,
  - emission,
  - Toon-compatible lighting/shading,
  - matcap and rim,
  - decals,
  - dissolve,
  - glitter,
  - common UV transforms, and
  - other fields already implemented by the pinned Toon converter.
- [x] Deliberately discard Pro-only fields and passes, including:
  - Grab Pass,
  - refraction,
  - blur,
  - Pro-only integrations,
  - Pro-only vertex effects,
  - Pro-only authoring metadata, and
  - any feature without a defined Toon 9.3.64 counterpart.
- [x] For discarded Grab Pass/refraction materials, preserve common
  color/texture/alpha/render-state data so the downgraded material remains a
  basic visible Toon material where possible.
- [x] Emit one concise per-material downgrade summary and structured
  diagnostics for dropped active feature groups.
- [x] Add an explicit conversion outcome such as `DowngradedToPoiyomiToon`
  rather than reporting full conversion or generic fallback.
- [x] Do not add Pro properties to the forward uber shader merely because they
  are present in the source material.
- [x] Do not add Pro assets to the parity corpus or repository licenses.
- [x] Build redistributable tests from synthetic serialized material documents,
  not copied Poiyomi Pro shader/package content.

Acceptance criteria:

- [x] All 31 direct Pro-authored materials in the private fixture route through
  the Toon converter rather than the generic base-color-only fallback.
- [x] Four unlocked 9.3.66 and 27 locked 9.3.11 materials are classified as
  lossy downgrade inputs.
- [x] No Pro-only feature is implemented or claimed.
- [x] Active discarded feature groups are visible in the import report.
- [x] No Poiyomi Pro source or asset is committed or redistributed.

## Phase 6 - Avatar And VRChat Component Conversion

- [x] Parse MonoBehaviour documents generically enough to retain:
  - GameObject attachment,
  - script GUID/local fileID,
  - enabled state, and
  - serialized field data required by registered adapters.
- [x] Add explicit adapters rather than loading or executing Unity assemblies.
- [x] Map supported VRC PhysBone chains to `PhysicsChainComponent`.
- [x] Map PhysBone sphere, capsule, plane, and supported collider shapes to
  engine collision representations.
- [x] Convert root, ignored transforms, endpoints, radius, gravity, pull,
  stiffness, immobility, limits, collision lists, and curves where an engine
  equivalent exists.
- [x] Diagnose parameters whose semantics cannot be reproduced exactly.
- [x] Map supported VRChat parent/position/rotation/scale constraints to native
  transform constraints.
- [x] Map AvatarDescriptor data useful to XRENGINE:
  - humanoid/avatar root,
  - view position,
  - eye-look references and ranges,
  - lip-sync mode and viseme blendshapes,
  - animation layer/controller references, and
  - supported playable/default animation data.
- [x] Treat PipelineManager upload identity/status as intentionally ignored
  editor metadata.
- [x] Expand the nested face-tracking package prefab through the same dependency
  and prefab composition path.
- [x] Preserve unsupported MonoBehaviour metadata for inspection without
  attaching a fake runtime behavior.

Acceptance criteria:

- [x] The analyzed 21 colliders and 14 PhysBones either convert or receive
  parameter-specific diagnostics.
- [x] The two constraints target the correct imported nodes.
- [x] Avatar view, eye, and viseme metadata target the correct skeleton and
  blendshape objects.
- [x] PipelineManager is ignored intentionally and does not block import.

## Phase 7 - Reimport, Diagnostics, And Failure Policy

- [x] Store a dependency manifest beside or inside the generated native prefab.
- [x] Record source GUID, local fileID where applicable, normalized path,
  dependency kind, last-write timestamp, content fingerprint, output asset, and
  conversion outcome.
- [x] Reimport when the entry prefab or any reached dependency changes.
- [x] Do not reimport because an unrelated Unity project asset changed.
- [ ] Reuse unchanged generated sub-assets where identity and content match.
- [x] Remove or retire generated sub-assets only after confirming they are no
  longer reachable and are owned by this import.
- [x] Group diagnostics by:
  - project/root detection,
  - GUID resolution,
  - dependency parsing,
  - model identity,
  - prefab override,
  - material downgrade,
  - texture import,
  - avatar component conversion, and
  - optional unsupported content.
- [x] Fail the visual import for unresolved required model, mesh, skeleton,
  material, or texture dependencies unless an explicit user-approved
  placeholder policy exists.
- [x] Keep known stale expression assets non-fatal when they are behavior-only.
- [x] Ensure failed imports do not overwrite a previously valid native prefab.

Acceptance criteria:

- [x] Editing one referenced material or texture reimports the correct native
  dependency and prefab.
- [x] Unrelated Unity project edits do not invalidate the avatar.
- [x] A failed reimport leaves the last valid prefab recoverable.
- [x] The final report distinguishes successful, downgraded, ignored, missing,
  and failed assets.

## Phase 8 - Tests, Editor Validation, And Documentation

### Unit and integration tests

- [x] Add project-root detection tests for nested `Assets` paths and invalid
  external paths.
- [x] Add GUID-index tests for `Assets`, embedded `Packages`, PackageCache,
  duplicate GUIDs, missing GUIDs, and cache invalidation.
- [x] Add dependency-graph tests for nested materials, textures, model `.meta`,
  cycles, and optional missing assets.
- [x] Add a regression test proving a PrefabInstance model source dispatches to
  the model importer instead of YAML parsing.
- [x] Add stripped GameObject/Transform/renderer proxy tests.
- [x] Add deterministic model fileID/correspondence tests with duplicate names.
- [x] Add FBX external material-remap and prefab override precedence tests.
- [x] Add transform, active-state, material-slot, and blendshape override tests.
- [x] Add Pro-to-Toon downgrade classification tests for:
  - unlocked Pro 9.3.66,
  - locked Pro 9.3.11,
  - locked Pro Grab Pass,
  - common Toon-compatible properties, and
  - active discarded Pro-only feature diagnostics.
- [x] Assert that Pro downgrade does not enable or add Pro-only uber features.
- [x] Add MonoBehaviour adapter tests for PhysBone, collider, constraint,
  AvatarDescriptor, and intentionally ignored PipelineManager data.
- [x] Keep the private avatar integration test opt-in and skip with a clear
  reason when the local corpus path is unavailable.

### Live editor validation

- [x] Start a named isolated MCP editor session according to `AGENTS.md`.
- [ ] Import the sanitized fixture through the same external-file UI path users
  will use.
- [x] Import the private avatar through the same path when locally available.
- [ ] Place the resulting prefab by drag/drop and verify it uses ordinary
  `XRPrefabSource` instantiation.
- [ ] Inspect the generated hierarchy, node/component counts, skeleton, mesh
  bindings, blendshapes, and material slots through MCP.
- [x] Capture the avatar from multiple camera positions and inspect the images.
  The captures documented the rendering failure; successful visual output is a
  separate unchecked acceptance criterion below.
- [ ] Verify that camera-relative visual changes prove live rendering rather
  than stale capture data.
- [ ] Compare a representative Unity reference against the XRENGINE downgrade,
  accepting documented loss of Pro-only effects.
- [x] Review named-session logs for YAML/model import errors, missing assets,
  shader compilation failures, and material downgrade diagnostics.
- [ ] Validate OpenGL first and run the narrowest useful Vulkan check after the
  primary path is correct.
- [x] Record durable evidence under `docs/work/investigations/assets/` if live
  iteration reveals defects.

### Documentation

- [x] Update
  `docs/developer-guides/assets/unity-conversion-integrations.md` with the
  project-root/dependency behavior and supported prefab composition.
- [x] Update the editor external-import documentation with source-project and
  destination semantics.
- [x] Update Poiyomi conversion documentation to describe lossy Pro-authored
  input downgrade without implying Pro support.
- [x] Document supported Unity/VRChat components and the unsupported-component
  diagnostic policy.
- [x] Document dependency manifests and reimport behavior.

Acceptance criteria:

- [ ] Existing Unity scene and Poiyomi Toon tests remain green.
- [ ] New fixtures cover every previously untested blocker found in the private
  avatar.
- [ ] Editor screenshots show a correctly placed, skinned, textured avatar.
- [ ] Any visual difference caused by dropped Pro-only features is expected and
  present in the downgrade report.

## Definition Of Done

- [x] Selecting an external Unity avatar prefab automatically finds its Unity
  project and resolves required GUID dependencies.
- [x] The importer reads dependencies from the original Unity project and
  writes a stable native asset closure to the chosen XRENGINE destination.
- [x] Binary model PrefabInstances never enter the YAML parser.
- [x] Stripped records map overrides/additions to imported model objects without
  creating phantom nodes.
- [x] FBX hierarchy, skeleton, skinning, meshes, blendshapes, material remaps,
  and prefab overrides are present in the generated prefab.
- [ ] The generated asset is an ordinary `XRPrefabSource` and places in the
  scene exactly like an imported FBX prefab source.
- [x] The analyzed 86-material/128-texture closure imports with no unexplained
  required visual dependency loss.
- [ ] Pro-authored materials are visibly usable through a documented lossy
  conversion to the supported Poiyomi Toon path.
- [x] Pro-only features are dropped, diagnosed, and not implemented.
- [x] Supported avatar metadata, physics chains/colliders, and constraints bind
  to the correct imported nodes.
- [x] Known stale optional VRChat expression references do not block the visual
  avatar.
- [x] Reimport reacts to reached dependency changes without rescanning or
  rebuilding unrelated project assets.
- [x] No proprietary Poiyomi Pro content is committed or redistributed.
- [ ] Tests, editor validation evidence, and user-facing documentation describe
  the shipped behavior accurately.

## Likely Implementation Touchpoints

- `XRENGINE/Scene/Prefabs/XRPrefabSource.cs`
- `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs`
- `XREngine.Editor/IMGUI/EditorImGuiUI.ExternalFileImport.cs`
- `XREngine.Editor/IMGUI/EditorImGuiUI.ModelDropSpawn.cs`
- `XREngine.Editor/Importers/UnitySceneImporter.cs`
- `XREngine.Editor/Importers/UnitySceneImporter.Components.cs`
- `XREngine.Editor/Importers/UnityAssetResolver.cs`
- `XREngine.Editor/Importers/UnityMaterialImporter.cs`
- `XREngine.Editor/Importers/Poiyomi/PoiyomiShaderMatcher.cs`
- `XREngine.Editor/Importers/Poiyomi/MaterialConversionReport.cs`
- `XREngine.UnitTests/Scene/UnitySceneImporterTests.cs`
- `XREngine.UnitTests/Rendering/UnityPoiyomiMaterialImporterTests.cs`
- new focused Unity project-context, dependency-graph, model-identity, and avatar
  component test files under `XREngine.UnitTests/`

## Highest-Risk Open Decisions

- [ ] Choose a reliable Unity `fileIdsGeneration: 2` mapping strategy for FBX
  nodes/components and validate it against multiple Unity exports.
- [x] Decide how package-cache paths are normalized so manifests remain portable
  across machines.
- [x] Decide whether dependency fingerprints use content hashing everywhere or
  timestamp/size fast paths with selective hashing.
- [x] Define the exact basic Toon fallback surface state for discarded Pro Grab
  Pass/refraction materials.
- [x] Define how much AvatarDescriptor controller/playable-layer behavior is
  required for initial completion versus a later avatar-runtime phase.
- [x] Define PhysBone parameter mappings whose Unity/VRChat semantics do not
  have an exact `PhysicsChainComponent` equivalent.
