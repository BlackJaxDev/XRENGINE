# S14f: keep object identity across play-mode snapshots

Status: Validated (September 27, 2026). A round trip keeps the captured
sharing, the capture is 76% smaller and later restores 80 to 86% faster, and
restored shaders keep their sources; see [Disposition](#disposition). Global
identity lookups after a restore remain as described below.

Gate record for
[S14f](../../progress/rendering/vulkan-stall-remediation-results.md#play-transition-results)
under the todo document's one-by-one protocol. Opened by S14a's validation;
see the [S14a record](2026-09-27-s14a-play-transitions.md#snapshot-identity-and-cost-opened-as-s14f).
Evidence root: `Build/_AgentValidation/20260927-095558-s14-core-update/`
(ignored and disposable; findings are copied here).

## Entry evidence

- The cooked binary format has no shared references: an object reachable
  twice is written twice, and a restore returns one copy per occurrence.
- In the S13a fixture 393 submeshes share 25 materials. The first capture
  sees 888 distinct inline assets (1 model, 393 submeshes, 393 meshes, 25
  materials, 25 rendering parameter sets, 4 shaders, 2 generated textures);
  every capture after a restore sees 2,150 (393 materials, 393 parameter
  sets, 393 shaders, 143 textures), all 393 materials carrying the same 25
  identities.
- After S14b a capture is 160 MB; a process's first entry restore takes 2.2
  to 8.8 s and later restores 1.4 to 2.3 s.
- The unsharing feeds S14d: 393 materials mean 393 distinct programs and
  binding snapshots to prepare after each transition, and about 20,000
  separate uniform-name strings.

## Design

Two options were listed: shared references inside a snapshot, or sharing
unmodified assets with the live world. Sharing with the live world would let
play-time changes to inline assets survive the exit, which the snapshot
exists to prevent, and deciding reliably that a mesh or material was not
modified would need revision tracking of all of their data, which the engine
does not guarantee. This item takes the first option, which changes no
play-mode semantics.

- **Format.** `CookedBinarySerializationCallbacks.ShareReference` selects values
  that one serialization writes once. The first complete occurrence of such a
  value is written as a definition (a new marker, an identity and the value);
  later occurrences are written as a reference to that identity (a second
  marker and the identity). An occurrence met while the value itself is still
  being written, a cycle, is written as before. The size pass makes the same
  decisions, and the reader registers each definition after reading it and
  resolves references to it; a reference to an unknown identity fails the
  read. Nothing changes when the option is absent, so cooked assets on disk
  and their readers are unaffected.
- **Snapshots.** The play-mode snapshot selects every `XRAsset` for sharing:
  models, submeshes, meshes, materials, shader objects, rendering parameters
  and generated textures. File-backed assets are already written as
  references to the loaded instance and are unchanged.
- **Semantics.** A restore returns one object per captured object, with
  sharing as captured: 25 materials stay 25, and each identity belongs to one
  object again. Play still runs on a copy and exit still restores a copy of
  the edit state.

## Hypothesis and acceptance (declared before the change)

Hypothesis: the duplicates are the material side of the scene (materials,
their shader objects, rendering parameters and generated textures), and
writing each once restores sharing and removes their share of capture size
and restore time.

Acceptance, on the S13a fixture over three round trips:

- every capture after a round trip sees the same distinct inline assets as the
  first (888: 1 model, 393 submeshes, 393 meshes, 25 materials, 25 rendering
  parameter sets, 4 shaders, 2 generated textures);
- the capture payload is at least 10% smaller than 160,250,128 bytes, and
  restores after the first are at least 10% faster than 1.4 to 2.3 s;
- each play round trip keeps the scene, updating world and tick membership
  (the S14a gate).

Falsifier: different counts after a round trip (sharing not restored), or a
payload or restore reduction under 10%, which would mean per-mesh data
dominates the snapshot and only sharing meshes with the live world would
shrink it further.

## Change 1: shared references

`CookedBinarySerializationCallbacks.ShareReference` and two markers,
`SharedDefinition` (62) and `SharedReference` (63). The writer, the size pass
and the schema inspector each number shared values with a
`CookedBinarySharedValueTracker` in the order their definitions begin, so the
three passes make the same decisions; the reader keeps a
`CookedBinarySharedValueTable`. A definition met where its occurrence is
skipped (an unknown member, or an object that cannot be created) is still read
in full, since later references need it, and a reference to an unknown or
unfinished identity fails the read with `InvalidDataException`. Sharing is
gated on the callbacks of the call, not on the writer alone: custom
serializers write and size their payloads through callback-free entry points,
so both passes keep writing those by value. The snapshot serializer shares
every `XRAsset`.

## Change 1: result

Fresh Release session on the S13a fixture, three observed round trips
(`reports/s14d-observe-s14f`; diagnostics log of the session). The previous
build's session (`reports/s14d-observe-change5`) is the baseline.

| | Before (change-5 build) | After |
| --- | --- | --- |
| First capture payload | 160,368,940 bytes | 38,487,454 bytes |
| Later capture payloads | 160,251,871 bytes | 38,487,317 bytes |
| Capture time | 1.19 s first, 0.73 s later | 0.48 s first, 0.10 to 0.12 s later |
| First entry restore | 3.34 s | 2.40 s |
| Later restores | 1.50 to 1.90 s | 0.26 to 0.30 s |

Asset decisions per capture (inline and reference):

| | First capture | Later captures, before | Later captures, after |
| --- | --- | --- | --- |
| Model | 1 | 1 | 1 |
| Submeshes | 393 | 393 | 393 |
| Meshes | 393 | 393 | 393 |
| Materials | 25 | 393 | 25 |
| Rendering parameters | 25 | 393 | 25 |
| Shaders | 4 | 393 | 4 |
| Generated textures | 2 | 143 | 2 |
| Scene, settings, game mode | 3 | 3 | 3 |
| Referenced textures | 38 | 38 | 38 |
| Referenced shader sources (`TextFile`) | 4 | 0 | 0 |

- **Sharing is restored.** Every capture after a round trip writes the same
  846 inline assets as the first, with the same breakdown by type. (The
  earlier "888" counted the 42 references too.)
- **Payload and restore time fall far past the 10% bar.** The payload is 76%
  smaller, which also means the unshared material side was most of it: the
  first capture already wrote each of the 393 occurrences in full. Restores
  after the first are 80 to 86% faster, the first entry restore 28% faster,
  and captures after the first 84 to 86% faster.
- **One difference remains, and it predates this change:** the 4 shader
  source references of the first capture are missing from every later capture,
  on both builds. See the finding below.
- **The S14a gate holds** (`reports/s14f-gate.out`, fresh process, four
  cycles of editor activity and 15 s of play): after every exit the editor was
  in edit mode with 34 nodes and 27 probes, the world performed 180 updates in
  2 s, and tick membership equalled its state before the first entry. The
  world now pauses for 0.56 to 1.39 s on entry (2.6 to 10.3 s on the S14a
  build) and 0.54 to 0.78 s on exit (1.6 to 2.7 s). No transition failed.

## Finding: a restore loses every inline shader's source

The 4 inline shaders (generated per material, no file path) hold source
`TextFile`s that carry the path of `UberShader.frag`, so the snapshot writes
each source as a reference. On restore, `SnapshotAssetReference.Resolve()`
finds no asset with the source's identity and falls back to the asset loaded
at its path, which is the engine's `XRShader` for `UberShader.frag`. It
returns that without checking the type; the diagnostics log records
"loaded-by-path: hit type=XREngine.Rendering.XRShader" for every source
reference. The reader's conversion of an `XRShader` to `TextFile` throws, the
reader swallows it and sets `XRShader.Source` to null, so after the first
restore every material's shader has no source, which is why later captures
write no source reference. The scene still renders, so these runs never
recompiled a shader from its source; a shader edit, or any recompile, would
find none. Resolving by path is also unsafe for the fix it seems to invite:
`AssetManager.Load(path, type)` evicts a cached asset of another type at that
path, so loading the source through it would drop the engine's `XRShader`
from the cache.

## Change 2 predeclared

The 4 sources are generated uber-variant sources
(`UberShaderVariantBuilder.CreateVariantShader`): generated text in a
`TextFile` that keeps the canonical file's path for include resolution and
program identity. A reference cannot restore them, since the file on disk
holds different text, and asset serialization leaves an asset's path out
(`FilePath` is `[YamlIgnore]`), so writing the `TextFile` itself by value
would drop the path.

1. **Text files the asset manager does not hold are written by value with
   their path.** A reference can only resolve to an asset the asset manager
   holds, so a `TextFile` that is not that very instance is written through a
   snapshot record carrying its identity, name, path, text and encoding, and
   restored as a new `TextFile`. The uber source is 84 KB, so 4 sources add
   well under 1 MB to a 38 MB capture.
2. **Each asset is prepared once per capture.** The snapshot keeps the
   reference or record it made for an asset for the rest of the capture and
   shares it like an inline asset (change 1), so it is written once, and
   restored or resolved once, instead of once per occurrence (2,138 reference
   occurrences in a capture of this scene).
3. **A reference resolves only to an asset of its own type.** The by-identity
   and by-path routes accept a loaded asset only when it is an instance of the
   referenced type. A path loaded as another type is not loaded again (that
   would evict the other asset); the reference fails and says why.

Hypothesis: the source is lost only through the wrong-typed resolution of a
source that a reference cannot represent.

Acceptance, on the S13a fixture over three round trips: every capture writes
the 4 shader sources by value (850 inline decisions and 38 references in
every capture, where the first capture before this change wrote 846 and 42),
no restore logs a resolve of one type to another, and the S14a gate holds.

Falsifier: later captures still write no source, which would mean something
else drops it.

## Change 2: result

Fresh Release session, three observed round trips (`reports/s14d-observe-s14f-c2`;
diagnostics log of the session).

| | Change 1 build | Change 2 build |
| --- | --- | --- |
| Inline decisions per capture | 846 (first), 846 (later) | 850 (every capture) |
| Reference decisions per capture | 42 (first), 38 (later) | 38 (every capture) |
| Shader sources per capture | 4 references (first), none (later) | 4 by value (every capture) |
| Capture payload | 38,487,454 / 38,487,317 bytes | 39,502,532 / 39,503,862 bytes |
| First entry restore | 2.40 s | 0.75 s |
| Later restores | 0.26 to 0.30 s | 0.25 to 0.33 s |
| Resolves per restore | one per reference occurrence | 38, all resolved |
| Reference records per capture | 2,138 | 38 |

- **Met.** Every capture writes the 4 generated sources by value and the same
  850 inline and 38 reference decisions, and no restore resolves a reference
  to another type (38 resolves per restore, none failed). The sources add
  1 MB (generated text with includes resolved).
- **Each referenced asset is now written and resolved once**, which took the
  first entry restore from 2.40 s to 0.75 s.
- **The S14a gate holds on the final build** (with S14d's change 6,
  `reports/final-gate.out`): four cycles, and after every exit edit mode, 34
  nodes, 27 probes, 180 to 181 world updates in 2 s and tick membership equal
  to before the first entry; entry paused the world 0.8 to 1.4 s and exit
  0.5 to 0.8 s; every restore after the first released the copy it replaced
  (424 render assets, no failure); no transition failed.

## Remaining: global identity lookups after a restore

Restored objects carry their captured identities, but `XRObjectBase`'s global
object cache is keyed by identity and keeps the object that registered it
first. A restore creates each object under a fresh identity, then sets the
captured one; the cache still holds the original world's object under that
identity, so the restored object is left unregistered
(`XRObjectBase.SetObjectID`). After the first play entry,
`XRObjectBase.ObjectsCache` therefore resolves scene identities to the
detached original objects, for the rest of the session. Editor tooling already
works around this where it matters: `EditorMcpActions.FindComponent` prefers
the component on the specified live node
(`EditorMcpComponentResolutionTests.FindComponent_PrefersSpecifiedLiveNodeWhenSnapshotIdentityIsDuplicated`).
Other identity lookups, such as persistent event calls and ID-based editor
drag and drop, still resolve to the detached objects. Fixing it means a
restore taking over the identities of the content it replaces, which changes
object-cache ownership rules outside the snapshot and is not part of this
item.

## Disposition

- **Gate met.** A round trip preserves object sharing (every capture after a
  round trip writes the same assets as the first: 25 materials, not 393), the
  capture is 76% smaller (160.3 MB to 38.5 MB, 39.5 MB with the shader
  sources) and restores after the first are 80 to 86% faster (1.5 to 1.9 s
  to 0.25 to 0.33 s); the first entry restore went from 3.3 s to 0.75 s.
- **Changes kept:** 1 (shared references in the cooked format, enabled for
  every snapshot asset) and 2 (text files the asset manager does not hold are
  written by value with their path, each asset is prepared once per capture,
  and references resolve only to assets of their own type), which restores
  the generated shader sources a restore used to drop.
- **Consequence for S14d:** with sharing restored and each copy released by
  S14d's change 6, a transition no longer leaves a scene's meshes, materials,
  renderers and descriptor sets registered; see the
  [S14d record](2026-09-27-s14d-post-exit-publication.md#change-6-result).
- **Remaining:** global identity lookups resolve to the original world's
  detached objects after the first entry (above); not changed here.
