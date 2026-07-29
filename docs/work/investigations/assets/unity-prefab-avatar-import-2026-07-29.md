# Unity Prefab Avatar Import Investigation

- Date: 2026-07-29
- Status: paused at user-requested wrap-up; live visual validation is blocked
  by shader failures
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

## Automated Validation

The redistributable fixture at
`XREngine.UnitTests/TestData/UnityAvatarProject/` is entirely hand-authored
XRENGINE test data. It contains a small ASCII FBX, model-prefab instance,
generation-2 stripped proxies, FBX material remap, prefab material override,
nested prefab, synthetic locked-style Pro material, descriptor metadata, and
an optional missing reference. It includes its own permissive fixture license
and contains no Unity, VRChat, or Poiyomi package source.

Results:

- focused project/dependency/model/avatar/Pro/reimport suite: 13 passed;
- existing Unity scene and Poiyomi Toon regression suite: 7 passed;
- private full-avatar integration: 1 passed in 29 seconds;
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
`logs/private-avatar-probe-17.log`,
`logs/sanitized-fixture-probe-2.log`,
`logs/unity-fileid-probe.log`, and
`logs/unity-nested-fileid-probe.log`.

## Live Editor Validation

The private native asset was instantiated through the ordinary
`XRPrefabSource` API in a named isolated OpenGL MCP session. Character
locomotion and the third-person pawn were disabled; all later validation must
retain that setup and use only the flying camera.

The instance contained 899 nodes and 120 components, including 63 model
components, 14 physics chains, 21 collider-like components, and three
constraints. Three screenshots from different focus/view states were inspected.
They exposed the yellow collider/debug silhouette, but did not yet prove
camera-relative live rendering or show a correctly rendered, textured, skinned
avatar.

The OpenGL editor then crashed in the driver's indexed-instanced draw path after
large imported Uber variants became ready. `MAT FACE 2` separately failed
fragment compilation because the Poiyomi global-mask helper allowed a compiler
to infer a negative vector subscript for an encoded zero channel. The helper was
changed to clamp the decoded index and select RGBA channels explicitly, and a
focused regression was added, but no complete post-fix editor run was performed.

An offline Vulkan probe found 41 bound materials and attempted 37 Uber
variants. Eight compiled and 29 failed; four non-Uber materials were skipped.
Its exception path did not retain actionable backend diagnostics, so extracting
those diagnostics is the first shader-debug step on resume. The exact ordered
handoff and all remaining acceptance boxes are in
`docs/work/todo/assets/unity-prefab-avatar-import-todo.md`.

## Remaining Risks

- Exact Unity/VRChat runtime behavior is not promised for preserved unsupported
  MonoBehaviours.
- Poiyomi Pro conversion is intentionally lossy and is not a Pro parity path.
- Optional stale expression/menu references remain behavior-incomplete by
  design but do not reduce visual completion.
- The 29 Vulkan Uber failures and the OpenGL draw crash block visual acceptance.
- Unchanged generated-subasset reuse is not implemented.
- Generation-2 FBX file-ID correspondence still needs validation across
  multiple independent Unity exports.
