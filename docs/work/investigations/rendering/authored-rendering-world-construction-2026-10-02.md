# Authored mapped and deformed world construction

Status: authored source, real generic/Editor cooking and strict hydration checks
pass. Browser and physical desktop pixels remain separate acceptance checks.

## Scope

`Samples/RenderingParity` is a small, ordinary engine project. Its saved XRWorld
contains a static mapped reference, a two-bone mapped ribbon with a `Ripple`
blendshape, a camera/pawn, directional and point lighting, and a serialized
`RenderingParityGameMode`. The marker is authored explicitly; its constructor
value is empty, so a dropped custom field cannot pass by restoring a default.
The game bootstrap loads the same asset path on desktop and browser. Animation
changes the saved scene bones and a cached production XRMeshRenderer's morph
weight. It does not replace the scene with browser DTOs or GPU test geometry.

The shader-preparation wrappers share `Tools/Cook-EngineBrowserShaders.ps1`.
The canonical production recipe inventory excludes only `*-probe` diagnostics.
The helper composes the CommonAssets Slang tree with the backend-owned packed
WGSL skinning kernel in temporary storage, cooks a project-contained manifest,
and removes the temporary source tree. This avoids both a second drifting recipe
list and duplicated tracked shader payloads.

## Detached staging metadata joined the parent ledger

Real construction initially failed when assigning the skinned ribbon's model:

- XRMeshRenderer's constructor opens a deferred render-publication transaction
- Its skinning and blendshape preparation construct private detached staging
  renderers inside that transaction
- The staging owners defer their own registration, but XRAsset's embedded-asset
  EventList and the renderer's submesh EventList previously registered with the
  ambient object-cache batch during field initialization
- Staging cleanup destroys the embedded-asset metadata container before the
  enclosing transaction completes
- The unchanged publication validator correctly rejects that destroyed member

A first-chance inspection of the production allocation ledger found two destroyed
`EventList<XRAsset>` entries, matching the bone and morph staging owners.

The focused repair suppresses object-cache registration only while constructing
those temporary owners. It ends before replacement buffers are created, so those
buffers still join the atomic transaction. The same scope is applied to all three
private renderer staging sites and the two corresponding mesh staging sites.
Publication validation, state installation, rollback and cleanup are unchanged.
The original skinned ModelComponent construction then succeeds and the real
asset manager saves the authored world.

## Restored aggregate skin metadata was stale

The next ordinary YAML reload reached the canonical mesh payload reader but
failed its compute-skinning validation. Measured state at the actual read boundary:

- Six restored vertices and two restored bone references
- Valid 6-by-4 Byte core index and weight buffers, with the required integral and
  normalized flags
- Convenience encoding `Core4NoSpill` and format `Core4x8`
- Aggregate snapshot bone count zero, encoding `None`, format `None`

ReadSkinningData restored individual metadata fields and buffer references, then
validated a stale aggregate snapshot. RegisterCookedDynamicBuffers only swaps
collections; it does not install aggregate metadata. A later convenience-buffer
refresh has that responsibility in other loading paths and cannot be relied on
inside the codec.

The codec now captures and applies the completed restored aggregate before
validation. Present and absent skinning and blendshape branches receive the same
synchronization, including legacy payload restoration, so absent features cannot
retain stale snapshot metadata. No writer, binary layout or payload version is
changed. The mesh remains inside its existing unpublished construction scope
until the complete read succeeds.

## Transform references and runtime aliases

SubMesh.RootBone and RootTransform use the engine's YamlTransformReference
contract. The YAML reader intentionally creates detached identity carriers;
those source properties are not required to be reference-equal to scene nodes.
Qualification must verify their exact serialized identities and then require
strict identity in the production RenderableMesh's resolved root/bounds-root and
its runtime mesh palette. The normal production resolver performs that work.
No fixture-specific or browser-only rebinder is permitted.

The same checks cover the authored pawn/camera component alias and shared
geometry buffers between the source mesh and its production runtime instance.
Semantic and legacy texture occurrences must be exact aliases after the
production material projection and cooked hydration.

The saved-world activation probe reproduced detached runtime roots even after
the real Jolt world reached Playing. The focused ModelComponent world-binding
repair now reruns the existing resolver/clone pipeline at completed world
attachment when different scene references become available. It preserves the
source identity carriers and shared geometry. The narrow built candidate passes
strict root, bounds-root and palette identity against the authored scene nodes.

The focused cold publication and retirement repair retains
failed construction/cleanup ownership, coalesces superseding model changes,
rejects captured callbacks after retirement, and keeps borrowed assets intact.
Production-object probes pass throwing unregister observers and retry, owned
clone veto and retry, captured LOD callbacks, retirement during a renderer mesh
setter, nested model replacement during live bind callbacks, and a finite
eight-attempt churn diagnostic followed by an explicit cold retry. Additional
checks cover pending-Add recovery after a real constructor fault and unchanged
renderer/morph/material state on ordinary incremental removal.

The canonical generic world route is CookedBinarySerializer; the lower-level
RuntimeCookedBinarySerializer directly handles the mesh custom payload. Nested
reflection hydration suppresses property notifications. A focused production
mesh read confirmed skinning and morph flags plus the name `Ripple` were intact
while its name-to-index lookup was missing. The shared lookup builder is now
invoked explicitly by all present/absent current/legacy codec restores and the
existing cold buffer-assignment refresh used by runtime clones. Named lookup
then passes without query-time repair or a fixture fallback.

## Generic transform cache identity

The next generic read retained animation data but lost `SerializedReferenceId`
on both scene-owned transforms and detached source carriers. The real activated
world had neither runtime root alias and zero of two palette aliases. The normal
resolver matches effective serialized identities; no alternate serialized
name/path contract could explain or repair those missing values.

TransformBase now exposes an inherited protected cooked-only identity bridge.
Its getter emits the effective authored identity and its setter restores
SerializedReferenceId without changing the object's runtime/cache ID. The
existing TransformYamlTypeInspector excludes only that declared bridge name,
so standalone and derived non-flat YAML remain byte-identical and the saved
authored world is unchanged. Real generic and production Editor-cooked activation
now preserve exact root, bounds-root and two-of-two palette aliases.

The outer generic cache still uses `CookedAssetFormat.BinaryV1`; its named-member
stream has no separate global version header. A pre-bridge ordinary world cache
is rejected at the existing transform post-cook boundary with
`CookedTransform.ReferenceIdentityMissing`, directing matched-engine recooking.
Old strict readers retain their unknown-member rejection for new caches.
Therefore content must be recooked and paired with the matching engine build;
unchanged outer format/version is not a forward-compatibility claim. Authored
YAML and Rolling Ball's custom v5/v6 codec bytes are not migrated or changed.

## Evidence and remaining gates

Evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/run/rendering-parity/`:

- `sample-project-build.log`: actual sample project, zero warnings/errors
- `shader-prepare.log` and `shader-inventory.log`: 41 production artifacts, 30
  material variants, nine pipeline programs and one packed-skinning kernel
- `rendering-repair-build.log` and `rendering-codec-build.log`: narrow shared
  Rendering builds, zero warnings/errors
- `rendering-lifetime-build.log`: world-binding/retirement candidate, zero
  warnings/errors
- `rendering-names-build.log`, `rendering-recovery-build.log`,
  `data-reference-build.log`, `core-reference-build.log`: final narrow rebuilds,
  zero warnings/errors
- `suppressed-names-baseline.log` and `generic-reference-baseline.log`: original
  production lookup and identity failures
- `authoring-cook.log`: all strict YAML/generic/Editor-cooked activation, aliases,
  cancellation/retirement/reentrant recovery, non-flat YAML shape and old-cache
  rejection checks pass; genuine Editor export/package yields 82 assets and
  40 shaders
- `reference-candidate-binary-sha256.log`: exact Data/Core/Rendering binaries
  used by that production-object driver

Still required: broad integrated publication gate, ordinary published browser
lifecycle and deformation
submission, visible mapped/deformed pixel checks, same-pose desktop comparisons,
and the physical-device/browser matrix. Local Chromium is unavailable under the
current policy; authorized GitHub CI is the live browser route.
