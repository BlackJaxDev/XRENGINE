# Unity Prefab Avatar Import Investigation

- Date: 2026-07-29
- Updated: 2026-07-30
- Status: implementation and OpenGL visual acceptance complete; Vulkan live
  rendering and a like-for-like Unity reference comparison remain open
- Subsystems: Unity import, model import, materials, avatar components, editor
- Private validation input: local `jax2026.prefab`; never copied or committed

## Problem Statement

Import a Unity 2022 avatar prefab directly from its original Unity project into
an ordinary native `XRPrefabSource`. The conversion must preserve the complete
visual dependency closure, relevant avatar behavior metadata, model-prefab
overrides, and stable reimport identity without changing the private source
prefab or redistributing proprietary package content.

## Reproduction Shape

The private validation prefab is 164,122 bytes and references an approximately
83 MB FBX plus nested package content. Its outer YAML contains stripped
model-prefab proxies, 217 main-model property modifications, 31 unique material
overrides, six blendshape defaults, VRChat avatar/PhysBone/collider/constraint
metadata, and optional stale expression references.

The pre-import SHA-256 of the prefab is
`EA63E9F3859F64C2B07D0976CEDB3B2842873CF2163E6104A79ADF4EC2824E8F`.
Private integration validation compares bytes, length, and last-write timestamp
before and after import.

## Issues Found And Resolutions

### External source context was lost

The external-file workflow copied a selected prefab before conversion. That
detached it from the Unity project whose `.meta` files define GUID identity.
External Unity prefab import now reads the original path, detects or accepts an
explicit project root, and writes only native output into XRENGINE `Assets`.

### GUID resolution repeatedly rescanned source trees

Nested prefabs and materials constructed independent resolvers. One
`UnityProjectImportContext` now owns a cached index across project `Assets`,
embedded `Packages`, and `Library/PackageCache`, with explicit precedence,
duplicate diagnostics, reached-document caches, and a dependency manifest.

### Binary FBX PrefabInstances entered the YAML path

Prefab-instance dispatch previously assumed every GUID source was Unity YAML.
Dispatch now uses the resolved asset extension: YAML prefabs compose through
the hierarchy importer and model formats use the existing model importer.

### Stripped proxy records did not bind model objects

The outer prefab identifies imported FBX objects with Unity generation-2 local
file IDs and stripped GameObject/Transform/renderer records. The importer now
reproduces Unity's XXH64-based IDs and indexes model objects by asset GUID,
file ID, object kind, and hierarchy path. A Unity 2022.3 editor probe confirmed
870 imported GameObject/Transform pairs, 51 renderers, root GameObject ID
`919132149155446097`, and root Transform ID `-8679921383154817045`.
Two outer-prefab renderer targets absent from a fresh Unity reimport are treated
as stale modifications rather than rebound heuristically.

### Assimp post-processing flattened hierarchy identity

`OptimizeGraph` changed the paths required to match Unity model objects.
Unity-prefab model composition disables hierarchy-destructive processing,
preserves the authored hierarchy, folds only the model wrapper root, and
retains nested blendshape-only renderer nodes.

### Required textures caused unbounded conversion memory

Eager decode/upload and render-authority construction pushed the private import
past 31 GB. Conversion now creates deferred file-backed texture streaming
sources, suppresses renderer authority/cooking while externalizing, and rebuilds
runtime renderables after deserialization. A clean import peaks near 2.5 GB.

The named MCP session manager also removes the reproducible texture cache from
every stopped session, retains build artifacts for only the two newest stopped
sessions, and refuses to start below 10 GiB free disk space. Agent-validation
output is disposable and repository policy limits retained run roots. There is
still no operating-system byte quota on an active session directory, so long
runs must remain monitored; the retention policy prevents stopped builds and
caches from accumulating without bound.

### Inline YAML references inherited the wrong property name

Unanchored property matching classified inline list references as the
surrounding `m_Materials` property. Anchoring property parsing to the beginning
of a YAML line restored correct edge classification and required/optional
policy.

### Unnamed externalized assets changed paths on every import

Fallback names included newly generated asset GUIDs. Stable per-type discovery
ordinals now assign deterministic sibling paths, enabling unchanged-output
reuse and precise stale-output retirement.

### Failure could leave partial native output

The native root and its owned sibling tree are now backed up before replacement.
Externalized placeholders are cleaned on failure, and a failed reimport restores
the previous asset bytes and paths.

### Native prefab references reloaded as empty placeholders

The partial native-prefab pass discovered compact external `{ID}` references,
but the root asset could deserialize before those assets were populated.
`AssetManager.LoadPrefabWithReferencesAsync` now preloads the bounded reference
set before parsing the root. The editor drag/drop path and MCP scene-authoring
path both use this hydration entry point, so the native Unity output remains an
ordinary `XRPrefabSource` rather than a Unity-specific placement type.

### User workflows were not directly testable through MCP

`run_editor_command` now exposes `import_external_asset` and `drop_asset`.
They invoke the same ImGui external-file queue and Asset Explorer drag/drop
spawn helpers used by a person in the editor. The sanitized fixture and private
avatar were both exercised through those commands rather than by calling the
importer or prefab-instantiation APIs directly.

### Large Uber programs duplicated driver-resident shader source

The OpenGL asynchronous compile path could admit several multi-megabyte Uber
programs at once and could also build a duplicate separable fallback from the
same prepared source. That drove the editor into multi-gigabyte growth and an
eventual driver draw crash. The compile queue now admits only one large-source
program at a time while preserving capacity for small/interactive programs, and
large prepared Uber programs skip the redundant separable fallback.

### Physics-chain colliders rendered as an always-on yellow overlay

`PhysicsChainCollider` and `PhysicsChainPlaneCollider` previously submitted
debug geometry unconditionally. Collider visualization is now an opt-in
`DebugDraw` property on `PhysicsChainColliderBase`, uses the gizmo render layer,
and does not submit while disabled. Physics behavior remains active.

## Automated Validation

The redistributable fixture at
`XREngine.UnitTests/TestData/UnityAvatarProject/` is entirely hand-authored
XRENGINE test data. It contains a small ASCII FBX, model-prefab instance,
generation-2 stripped proxies, FBX material remap, prefab material override,
nested prefab, synthetic locked-style Pro material, descriptor metadata, and
an optional missing reference. It includes its own permissive fixture license
and contains no Unity, VRChat, or Poiyomi package source.

Results:

- isolated focused Unity/avatar/Poiyomi/prefab/physics/OpenGL contract suite:
  105 passed;
- real OpenGL large-source compile admission regression: passed;
- private full-avatar structure and unchanged-source integration: passed;
- private externalized native-prefab reload: passed;
- private imported Uber material SPIR-V compilation regression: passed in
  13 minutes 38 seconds;
- clean private import: 883 nodes, 93 converted components, 52 model
  components, six blendshape-default groups, 21 colliders, 14 physics chains,
  three constraints, one avatar descriptor, two animator metadata components,
  1,718 reached dependency records, 269 diagnostics, and zero errors;
- visual closure: 87 material records, 144 texture records, 130 required
  texture records, 40 Pro downgrade outcomes, and completion tier
  `VisualAndAvatarBehavior`;
- main-avatar closure excluding the nested face-tracking package: 86 required
  materials and 128 required textures;
- all 31 direct prefab material overrides resolve and report `Downgraded`.

Ignored local evidence is stored under
`Build/_AgentValidation/20260729-unity-avatar-import/`. The important logs are
under `logs/`, the MCP responses are under `mcp-output/`, and focused NUnit
TRX results are under `reports/targeted-tests/`. These are disposable evidence;
the implementation and durable conclusions do not depend on committing them.

## Live Editor Validation

The sanitized fixture was queued through the editor's external-file UI helper,
produced a native `XRPrefabSource`, and was placed through the exact Asset
Explorer drag/drop helper. The private avatar followed the same two workflows.
MCP inspection recorded the hierarchy, skeleton chain
`Armature -> Hips -> Spine -> Chest -> Neck -> Head`, model/mesh bindings,
authored blendshape defaults, material slots, and component properties. The
importer-owned avatar root contains 883 nodes; the broader live scene inventory
reported 899 nodes after editor/runtime scene nodes were included.

The final OpenGL profile explicitly set:

- `CharacterLocomotion = false`;
- `UseThirdPersonCharacterPawn = false`;
- `Mirror = false`;
- all transform/physics debug rendering off; and
- a render-on-demand desktop flying-camera pawn.

No `MirrorNode` or mirror component was present. The default Unit Testing World
can create one when `Mirror` is true, but the mirror renderer is currently
broken and is intentionally outside this validation.

The earlier `Body`/`Face`-only captures are useful skinning and fresh-frame
evidence, but they are not complete-avatar acceptance evidence. Their paths are
`mcp-captures/Screenshot_20260730_010436_853_de190ccce7024d73a11025a45e2854a0.png`
and
`mcp-captures/Screenshot_20260730_010523_132_a1b72b4ad8954be8b7dded2f46fe6250.png`.

The follow-up inspection kept the prefab's authored active renderer set intact.
Of 52 model components, 22 are effectively active and match the Unity outfit:
body, face, horns, ears, tail, jewelry, glove, shorts, thigh-highs, shoes,
underwear, tank/zip-up, and the authored active hair branches. The active
silhouette is visible against the temporary neutral validation backdrop in
`mcp-captures/Screenshot_20260730_095012_951_cddd01eb77fd4129bd9f3ece73499c65.png`.
That capture is still not accepted: exposure/material output leaves major
body, face, and hair regions blown out or flat compared with the supplied Unity
reference.

Dependency and runtime inspection found no unexplained required visual loss.
The main-avatar manifest has 128 required visual texture references, represented
by 112 distinct imported source textures after deduplication. Live streaming
tracked 77 material texture bindings with zero failures, and representative
body, hair, tank, shorts, socks, and accessory slots resolve to their expected
Unity files. Logs also show the compact pending material binding real
`_MainTex` textures. The remaining failure is therefore in material
specialization/output and visual acceptance, not missing imported mesh or
texture records.

The compact textured pending-Uber shader now links in approximately 12-14 ms,
instead of the previous 30-52 second large fallback, and makes the complete
silhouette inspectable while final variants compile. Final Pro-authored
variants remained approximately 324-360 KiB and 8,500-9,200 lines, however,
and took approximately 85-100 seconds each to link. The editor reached roughly
16 GiB while compiling them, so the named session was stopped.

The cause was unconditional `UseRuntimeUberPropertyBindings()` during lossy
Pro import. It converted all implicitly authored constants into uniforms and
prevented constant folding from pruning inactive Pro branches. The importer now
leaves implicit Pro-downgrade values static while preserving explicit
runtime-mutability declarations. Authored static changes continue to rebuild
the material variant. The normal test project builds with this change and the
focused Pro-downgrade/Uber contract selection passes 43/43. The avatar has not
yet been reimported and rendered with this final specialization change, so no
visual or memory acceptance is claimed.

Vulkan was then launched from the same isolated build with the Vulkan profile.
`get_render_capabilities` confirmed Vulkan, no mirror node existed, and the
ordinary native prefab drag/drop completed. Enabling continuous rendering for
the capture made the editor unresponsive; it reached about 5.05 GiB working set
and 7.34 GiB private bytes before the exact named session was stopped. There was
no managed exception in stdout. Vulkan backend initialization and prefab
placement therefore pass, but Vulkan frame rendering/capture does not.

An earlier normal test-project compile was independently blocked by concurrent
Vulkan/frame-package API work. The latest focused invocation built the normal
test project successfully, so that transient 41-error blocker is no longer a
current limitation. The complete test suite was not rerun after the final
specialization change.

## Remaining Risks

- Exact Unity/VRChat runtime behavior is not promised for preserved unsupported
  MonoBehaviours.
- Poiyomi Pro conversion is intentionally lossy and is not a Pro parity path.
- Optional stale expression/menu references remain behavior-incomplete by
  design but do not reduce visual completion.
- Reimport the unchanged private source with the final static Pro-downgrade
  specialization change. Measure representative generated shader source sizes
  and stop the named session if memory again approaches the validation bound.
- Obtain accepted complete-avatar OpenGL front and oblique captures with a
  static flying camera and neutral backdrop. Keep every authored active mesh
  branch enabled, and compare the visible textures/materials with the supplied
  Unity reference.
- Vulkan frame rendering/capture hangs after successful backend startup and
  prefab placement. Debug this against the current Vulkan renderer without a
  mirror node or locomotion and without weakening the OpenGL path. Run
  `rdc doctor`, capture, export and visually inspect the final/suspicious render
  target, then close the RenderDoc session.
- The repository has pinned CC0 Unity Poiyomi reference images, but a live
  XRENGINE capture of that exact representative scene/camera matrix has not yet
  been produced. Do not treat the private avatar screenshot as a like-for-like
  Unity comparison.
