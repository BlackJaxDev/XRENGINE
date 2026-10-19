# Unity Package Startup Import

Last updated: 2026-10-19
Related: [Unity Conversion](../../../developer-guides/assets/unity-conversion-integrations.md#unit-testing-world-startup)

## Problem

The Unit Testing World accepted loose Unity prefabs, but sent `.unitypackage`
files to the generic model importer. Package startup needs a complete source
snapshot before the converter can resolve GUID references.

Exact source inventories, missing GUIDs, package paths, and run records are
kept in local accessory notes. They are not required to understand this fix.

## Findings And Changes

`UnitTestingWorld.Models` originally selected Unity conversion only for
`.prefab`. `SerializedSceneImporter.ImportPrefabWithManifest` can also convert
scene YAML through the same hierarchy, material, behavior, and mesh path.
Archive extraction alone does not perform this conversion.

`UnityPackageImportSource` validates archive paths, extracts the source tree,
and identifies cached content by the package SHA-256 hash. Folder records can
have metadata without an asset payload. Startup registers each primary scene
and prefab before it releases the package completion slot.

`ModelImportSettings.Dependencies` accepts local companion sources and direct
HTTPS or VCC repository URLs. Download checks cover hashes and package identity.
Composition mounts VPM packages under `Packages` in the source snapshot.
Companion scenes and prefabs do not become startup roots.

Review corrected asset/metadata copy conflicts, project-manifest references
that escaped the snapshot, unbounded archive expansion, and failure to retry
another public DNS address. Package identity validation uses the root manifest;
an embedded dependency manifest must not replace the archive identity.

## Missing Source References

Complete archive and metadata scans distinguished missing source assets from
extraction loss. Dependency resolution supplied some references but did not
supply every required material, shader, or nested prefab. A material name cannot
be recovered reliably from a missing GUID alone. Strict conversion rejected
entries with missing visual dependencies before main-avatar material conversion.
A successful dependency download therefore does not establish source closure
or visual parity.

The available source set has no complete replacement project. Exact missing
references and dependency versions remain in the local accessory notes.

## Partial Import And Separate Scenes

`AllowPartialImport` retains supported content and reports `PartialVisual`.
Unresolved material references use lit magenta markers. Missing prefab
instances are skipped. Drawable FBX slots remain when an override has no
matching source slot. Failed material conversions retain a failed source
outcome and use cached fallbacks. Strict import remains the default.

Poiyomi Pro identification uses serialized `OriginalShader` evidence. Partial
import can also use consistent shader GUID evidence from other source materials.
Evidence assets enter the dependency graph and content fingerprint. Supported
properties use the Pro-to-Uber conversion path. Unsupported features can still
require a generic fallback. Partial import does not reconstruct missing assets
or execute Unity editor scripts.

Each primary `.prefab` or `.unity` entry creates an independent `XRScene`.
Imported roots and static instances belong to that scene. Scene names use source
filenames with duplicate suffixes. Standalone animated prefab roots do not bind
to the Main Scene pawn. Static probe sources register only after attachment
succeeds. Failed attachment preserves runtime-owned scenes for cleanup.

Startup sets `XRScene.IsVisible` before publication. Unity scenes start visible;
separate prefab scenes start hidden. Prefab instances inside a visible scene
remain visible through that scene. Hidden standalone scenes do not supply
startup probe-placement bounds. The hierarchy checkbox can show them later.

## Validation And Limits

Editor, settings generator, and isolated editor builds passed with zero warnings
and errors. Settings and schema were regenerated. No test source changed.
Warm startup reused extraction and dependency caches.

Strict OpenGL runs completed supported entries and rejected incomplete ones.
Viewed captures confirmed that dependency resolution alone did not produce a
complete avatar. Partial runs then published the supported avatar geometry and
separate scenes. Scene visibility controls worked. Captures still contained
missing materials and bright regions, so they do not establish Unity parity.

The existing targeted tests retained three FBX coordinate-basis failures:

- `DuplicateSiblingExport_UsesSourceNamesAndPersistentGenerationTwoIdentities`
- `ExternalImport_WritesStableOwnedClosureAndRollsBackFailedReimport`
- `SanitizedFixture_ComposesModelProxiesNestedPrefabOverridesAndManifest`

These failures do not enter package extraction or dependency download. Exact
run counts and source-specific results remain in local accessory notes.
Full appearance acceptance still requires source closure and support for the
reported shader features. See the separate shader compiler and rendering
investigations for later findings.
