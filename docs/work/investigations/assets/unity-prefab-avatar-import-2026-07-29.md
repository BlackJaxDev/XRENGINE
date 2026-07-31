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

The unchanged private source was reimported after the final specialization
change. Its pre- and post-validation identity remained:

- length: 164,122 bytes;
- last-write UTC: `2026-01-29T09:03:38.8537663Z`; and
- SHA-256:
  `EA63E9F3859F64C2B07D0976CEDB3B2842873CF2163E6104A79ADF4EC2824E8F`.

The accepted OpenGL captures use bloom disabled and manual exposure `0.5`.
This removes the default low bloom threshold as a confounding factor without
changing the imported material. The static flying camera was placed on engine
negative Z and looked toward positive Z for the front view. This is the
exact-once mapping of Unity positive-Z-forward content to XRENGINE
negative-Z-forward content; no compensating avatar rotation was added.

Accepted complete-avatar evidence is under
`Build/_AgentValidation/20260729-unity-avatar-import/mcp-captures/opengl-final-coherent/`:

- front:
  `Screenshot_20260731_023624_551_c03b7aa8ab6f41e580598158720e1c67.png`;
- front-right oblique:
  `Screenshot_20260731_023642_814_d68e4a7a59cf4729a2f7ac8a5e8e3bb6.png`;
- rear:
  `Screenshot_20260731_023700_336_aed315fcaede48ae9dbc6fe78791f7ac.png`.

These images show the authored active set with textured skin, face, outfit,
thigh-highs, shoes, hair, tail, and accessories. Camera-relative silhouettes
change as expected, so the result is live rendering rather than a stale
readback. An isolated Body capture at
`mcp-captures/opengl-body-final-coherent/Screenshot_20260731_023722_201_fe5e92a9432d4394ae42e7fb3a862edb.png`
shows the complete correctly skinned/textured torso, arms, hands, legs, and
feet. The apparent gaps in the dressed avatar are authored masking, including
Body blendshape index 1 at weight 100, rather than missing imported geometry.

Dependency and runtime inspection found no unexplained required visual loss.
The main-avatar manifest has 128 required visual texture references, represented
by 112 distinct imported source textures after deduplication. Representative
body, hair, tank, shorts, socks, and accessory slots resolve to their expected
Unity files. The fresh generated closure contained 350 files before it was
deleted through the asset API at the end of validation.

The earlier specialization failure was caused by unconditional
`UseRuntimeUberPropertyBindings()` during lossy
Pro import. It converted all implicitly authored constants into uniforms and
prevented constant folding from pruning inactive Pro branches. The importer now
leaves implicit Pro-downgrade values static while preserving explicit
runtime-mutability declarations. Authored static changes continue to rebuild
the material variant. A new regression proves dormant emission and matcap
textures cannot enable those features when Poiyomi explicitly authors their
feature toggles as zero. The focused Unity model/Poiyomi/Pro selection passes
9/9. The generated `MAT_BODY 2` material therefore keeps both emission and
matcap disabled, matching the source. A raw Body HDR capture had maximum RGB
`0.7534` and no non-finite pixels, which rules out the reported whole-model
emission; the earlier glow was post-process bloom.

The supplied Unity image is a useful representative comparison, but it is not
the raw prefab's authored default state. It shows a runtime-selected loose
black/cyan hair variant, while the raw prefab keeps multiple hair branches,
including the braid, active. XRENGINE does not execute the unsupported
VRCFury/menu toggle behavior that selects that runtime outfit state. The
remaining material differences are also expected from the documented lossy
Poiyomi Pro-to-supported-Uber conversion: Pro-only grab-pass, layered,
special-effect, and exact outline/matcap behavior is diagnosed and dropped.
The supported base textures, colors, alpha modes, normal inputs, and explicit
emission state are visibly usable in the accepted OpenGL images.

The narrow Vulkan check then used the same native prefab closure, static camera,
no locomotion, and no mirror. `get_render_capabilities` and texture residency
confirmed Vulkan, native prefab placement succeeded, 56 tracked textures
settled with zero pending transitions, and MCP returned an
`R16G16B16A16Sfloat` GPU readback. The resulting image at
`mcp-captures/vulkan-final-coherent/Screenshot_20260731_024123_428_6f3123baf2264b9e97463c4807f86b3b.png`
is vertically inverted by the current Vulkan screenshot-readback path and only
shows the Body/opaque subset. This is a backend/readback limitation, not an
import-closure failure, because the same ordinary prefab renders completely in
OpenGL.

`rdc doctor` passed for RenderDoc 1.44 and the registered Vulkan layer. A
bounded RenderDoc launch confirmed Vulkan, but attaching reported an empty
capture API and its preflight MCP image was black; `capture-trigger` produced
no `.rdc`. The exact launched editor process was stopped. This records the
current RenderDoc/Vulkan integration limitation without weakening the accepted
OpenGL importer path or introducing a mirror.

## Remaining Risks

- Exact Unity/VRChat runtime behavior is not promised for preserved unsupported
  MonoBehaviours.
- Poiyomi Pro conversion is intentionally lossy and is not a Pro parity path.
- Optional stale expression/menu references remain behavior-incomplete by
  design but do not reduce visual completion.
- Vulkan screenshot readback still needs a vertical-origin fix, and the
  forward/masked avatar passes need backend-specific renderer investigation.
  This is separate from the completed Unity-prefab import path.
- RenderDoc 1.44 currently launches the Vulkan editor but does not expose the
  active API to `rdc-cli` for capture. Resolve that renderer/tool integration
  before relying on Vulkan GPU captures for avatar-material diagnosis.
- A true like-for-like Unity comparison would require the same camera,
  lighting, post processing, and VRCFury runtime-selected outfit state. The
  private screenshot should remain a representative, not pixel-parity,
  reference.
