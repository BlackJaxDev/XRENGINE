# Unity Package Startup Import

Last updated: 2026-10-15

## Problem

The Unit Testing World accepted loose Unity prefabs, but sent `.unitypackage`
files to the generic model importer. The requested source is
`<desktop>/misc/Aryia_By_Mimiiu_V1.3.unitypackage`.

## Findings

- `UnitTestingWorld.Models` selected the Unity converter only for `.prefab`.
- `SerializedSceneImporter.ImportPrefabWithManifest` can convert scene YAML
  through the same hierarchy, material, behavior, and mesh-cooking path.
- The archive UI extractor does not convert extracted files. Startup needs a
  complete source project before it resolves GUID references.
- Aryia contains 1,751 pathname and metadata pairs and 1,542 asset payloads.
  Folder records can have metadata without an asset payload.
- The optional API security review failed because the API project reached its
  enforced spend limit. It returned no review evidence.
- The supplied archive contains 26 entry files: 5 scenes and 21 prefabs.
- A complete scan of all 7,284 archive entries found none of the 91 GUIDs from
  the failed import reports. They are absent from both archive folder IDs and
  metadata GUIDs. All 1,751 metadata files were extracted. This rules out
  extraction loss and an index omission for those references.
- Both main scenes contain unresolved nested prefab references. Examples are
  `2cd7c2d73a12a214b930125a1ca4ed33` and
  `b022bab8112640045a1d4c5c7ba78fac`. Required assets must be supplied before
  these scenes can produce a correct avatar result.

## Change

`UnityPackageImportSource` validates package paths, extracts the complete source
tree, and publishes a cache entry identified by the package SHA-256 hash.
Startup registers every scene and prefab as an independent import before it
releases the package completion slot. Each entry uses the existing Unity
converter and Poiyomi material validation.

`ModelImportSettings.Dependencies` now supplies local companion sources or
direct HTTPS/VCC repository URLs. The generated schema includes the source,
package ID, and version fields. The downloader checks hashes and archive
identity, then composition mounts VPM packages under `Packages` in the source
snapshot. Companion scenes and prefabs do not become startup roots.

Review found and corrected an asset/metadata pair conflict during the first
copy, project-manifest references that could escape the source snapshot,
unbounded archive expansion, and failure to retry another public DNS address.
The final scoped review found no remaining blocker in these corrections.

See [Unity Conversion](../../../developer-guides/assets/unity-conversion-integrations.md#unit-testing-world-startup)
for settings and cache behavior.

## Validation

The editor, settings generator, and isolated editor builds passed with zero
warnings and errors. The settings and schema were regenerated. No tests were
added or changed.
The local JSONC selects the Aryia package with `Kind: Static` and scale 1.
Previous model entries remain present and disabled.

The isolated OpenGL editor extracted the package and attempted all 26 imports.
It published 19 independent roots. Two prefabs and all five scenes failed with
missing visual dependencies. The main avatar did not reach material conversion.
Two viewport captures from different camera positions were inspected. They
confirmed the 19 roots in the hierarchy and no imported avatar geometry.
The session was stopped after inspection.

A warm restart of the original package-only import reused extraction and
prepared the source in approximately 2.35 seconds.

The first companion run downloaded and combined these packages successfully:

| Package | Version | Previously missing GUIDs supplied |
|---|---|---|
| `com.poiyomi.toon` | `9.3.64` | 23 |
| `com.vrcfury.vrcfury` | `1.1434.0` | 0 |
| `com.llealloo.audiolink` | `3.1.2` | 0 |
| `adjerry91.vrcft.templates` | `7.0.5` | 1 |

Jerry's package supplies `b022bab8112640045a1d4c5c7ba78fac` at
`Prefabs/VF_UE_TongueSteps_VRCFT.prefab`. A read-only ZIP scan confirmed that 67
of the original 91 missing GUIDs are still absent from all four companions.
The combined import still stops at required visual dependencies. This is not
evidence of Poiyomi visual parity or execution of VRCFury editor scripts.

The remaining references are not all stale metadata. The breath prefab has a
direct missing material on its `Smoke` particle renderer. `Open Me.unity` has
missing material-slot overrides against the present `ARYIA.fbx`. The scene
under `Scene Lighting` also references absent Yuno avatar and SFX prefabs.
These observations rule out claiming a complete conversion from this source
set. The later partial import mode records these losses explicitly.

The user identified Gesture Manager in the curated repository and asked whether
the VRChat SDK supplies the other omissions. SHA-256-checked SDK and Gesture
Manager archives establish the following matches:

| Package | Version | Missing source assets supplied |
|---|---|---|
| `vrchat.blackstartx.gesture-manager` | `3.9.9` | `GestureManager.prefab`, GUID `2cd7c2d73a12a214b930125a1ca4ed33` |
| `com.vrchat.base` | `3.10.5` | `VRChat-Mobile-StandardLite.shader` and `VRChat-Mobile-ToonLit.shader` |
| `com.vrchat.avatars` | `3.10.5` | No matches in the failed visual-reference set |

These three sources were added to the local dependency list. SDK Base contains
an embedded UniTask `package.json`; archive identity validation was corrected
to ignore such nested library manifests and validate the root package.

The source comparison leaves 61 missing material GUIDs, one shader GUID, and
two source-prefab GUIDs. Material names cannot be recovered reliably from GUIDs
alone. Representative source uses are:

| Missing GUID | Source use |
|---|---|
| `3a5c0783daf1d174494000d2c4063075` | Renderer material slots 0 and 1 on instances of the present `ARYIA.fbx` in `Open Me.unity` |
| `9e211833582cdc1498791752019305b4` | The `Smoke` particle renderer in `Updated Basic Breath Smoke by Raivo.prefab` |
| `98584cfa315fae24594232c9d3bcfab3` | Material slot 2 on `GoBone_Coat.fbx` instances in GoGo demo scenes |
| `77f45810ea400c74e929db1f8ea09c47` | Poiyomi Pro, identified by `OriginalShader` and `OriginalShaderGUID` tags in the supplied materials; also a direct shader reference in `Fishnet Top.mat` and other materials |
| `d564b8a6f57e9da4baec3fdb03e899a0` | Four Yuno avatar variants in the lighting scene |
| `2ad4a8337b7c43c45aaf22a13b83df31` | Four SFX prefab instances in the lighting scene |

The current SDK archives do not supply these materials, Poiyomi Pro, or the
two remaining prefabs. The user has no further source project or package.

The strict isolated OpenGL run resolved all seven dependency sources and
prepared exactly 26 primary entry files. It completed 20 imports and rejected
six entries with the 64 missing GUIDs identified above. `DirectDemo.prefab`
now imports six models and two materials after the SDK shaders are available.
All five scenes and the breath prefab still fail. The main avatar does not
reach Poiyomi conversion. Two viewport captures were viewed from different
camera positions. They did not show the avatar. The job list was empty after
all imports, and the named session was stopped.

The final isolated build passed with zero warnings and errors. The repeated
targeted test run retained the same result: 36 passed and three FBX basis
validation failures. The downloaded cache was reused during subsequent
startup runs; the seven cached sources resolved in approximately 1.43 seconds.

The targeted existing test run passed 36 of 39 tests. The three failures are
`DuplicateSiblingExport_UsesSourceNamesAndPersistentGenerationTwoIdentities`,
`ExternalImport_WritesStableOwnedClosureAndRollsBackFailedReimport`, and
`SanitizedFixture_ComposesModelProxiesNestedPrefabOverridesAndManifest`.
Each fails in the existing Unity FBX coordinate-basis validator. These tests
do not enter package extraction or dependency download. No test code changed.

Earlier failed entries, before all seven sources were available, relative to
`Assets/Aryia by Mimiiu/`:

- `3.0/Breath Prefab - Raivo/Updated Basic Breath Smoke by Raivo.prefab`
- `3.0/Menus/GoGo/Demo/Toggle Direct BLT/DirectDemo.prefab`
- `3.0/Menus/GoGo/Demo/Hands in pockets/DemoHandsInPockets.unity`
- `3.0/Menus/GoGo/Demo/Toggle Direct BLT/Demo1DIrect.unity`
- `3.0/Menus/GoGo/GoModel/GoBone/Avatar GoBone.unity`
- `3.0/Scene/Scene Lighting/! Open Me !.unity`
- `Open Me.unity`

## Partial Import And Separate Scenes

The user requested partial import after confirming that no other source assets
are available. `AllowPartialImport` now retains available content and records
`PartialVisual`. It uses lit magenta materials for unresolved material references,
skips absent prefab instances, and retains drawable FBX slots when an override
has no matching source slot. Failed material conversions use cached fallbacks
and retain a failed source outcome. Strict imports remain the default.

Poiyomi Pro identification now uses serialized `OriginalShader` evidence. Partial
imports can also use consistent shader GUID evidence from other project materials.
The evidence assets enter the dependency graph and content fingerprint. The
existing Pro downgrade converter maps supported properties to Uber. Unsupported
features can still force a generic material fallback.

Each primary `.prefab` or `.unity` file now creates an independent `XRScene`.
Loose serialized targets use the same path. Imported roots and static instances
belong to that scene. Scene names use source filenames and a suffix for duplicates.
Standalone animated prefab roots do not bind to the Main Scene pawn. Scene
publication registers static light-probe sources only after attachment succeeds.
Failed attachment preserves any scene still owned by the runtime for cleanup.

The isolated OpenGL run completed all 26 entries: 21 prefab scenes and five Unity
scenes, plus the existing Main Scene. `Open Me.unity` retained 125 models and 35
materials. Eleven Poiyomi Pro materials reached Uber through the downgrade path.
The scene tree contains all four Aryia variants. Independent scene visibility
was exercised by hiding the other imported scenes. Two camera positions showed
avatar geometry. The captures also show incomplete materials and strong bright
regions; they do not establish visual parity with Unity.

The first partial run exposed unsupported extra renderer material slots on
`H - Bangs` and GoBone `Body`. Partial recovery now reports these overrides and
keeps supported slots. This removed the remaining scene-wide import failures.
The current source still lacks 61 material GUIDs, one shader GUID, and two nested
prefab GUIDs. Partial import does not reconstruct them or execute Unity scripts.

The existing targeted test run passed 40 of 43 tests. The same three tests listed
above still fail in the FBX coordinate-basis validator. No test code changed.
The final editor build passed with zero warnings and errors. The settings schema
and examples were regenerated, and the local Aryia package
entry has `AllowPartialImport: true`. The isolated session was stopped.

Evidence is stored under the existing task run:
`Build/_AgentValidation/20261014-122330-merge-and-startup/`.
The runtime log is `logs/unitypackage-startup/partial-separate-scenes-meshes.log`.
The two viewed captures have timestamps `20261015_123131` and `20261015_123143`
in `mcp-captures/unitypackage-startup/`.

## Startup Scene Visibility

The user requested visible `.unity` scenes and hidden separate `.prefab` scenes.
Startup now sets `XRScene.IsVisible` before scene publication. Unity scenes start
checked; prefab scenes start unchecked. Prefab instances inside a visible Unity
scene remain part of that scene. Hidden standalone prefab scenes do not supply
startup light-probe placement bounds. The hierarchy checkbox can show them later.

The isolated editor build passed with zero warnings and errors. Live scene
inspection confirmed five visible `.unity` scenes, 21 hidden `.prefab` scenes,
and the existing Main Scene. `DirectDemo.prefab` could be shown and hidden again.
Two viewport captures were viewed and showed the unchecked prefab scene entries.
The final scene inventory is `mcp-output/prefab-visibility.json` under the task run.


## Remaining Limits

Full material fidelity requires the missing source assets and support for the
reported shader features. A separate rendering investigation is needed to
classify the bright regions visible in these captures. The partial import result
must not be used as proof of Poiyomi visual parity.
