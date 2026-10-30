# Unity Conversion Integrations

XRENGINE includes interoperability paths for importing content authored in Unity projects. These integrations are format converters and material mappers; they do not make XRENGINE a Unity runtime, and they do not imply affiliation with or endorsement by Unity Technologies.

Unity is a trademark of Unity Technologies. XRENGINE uses the name only to identify compatible source formats and workflows.

## Editor Workflow

Use the ImGui editor's `Tools > Import External Files...` command and select a
Unity `.prefab`. The dialog separates two locations:

- **Unity Project Root** is the original Unity project that supplies GUID and
  dependency context. It is detected by climbing from the selected prefab to
  the nearest directory named `Assets`; its parent is the project root. The
  detected path can be corrected explicitly when the prefab is not below its
  original `Assets` tree.
- **Destination Folder** is inside the current XRENGINE project's `Assets`
  directory and receives the generated native `.asset` plus its externalized
  sibling assets.

The source prefab is read directly from its original project. It is not copied,
rewritten, touched, or placed in the XRENGINE project. The importer resolves
only the selected prefab's reached dependency closure and writes native engine
assets. The existing copy-then-import workflow remains in place for other
external file types and folder imports.

The editor job shows determinate phases for project discovery, dependency
resolution, source conversion, externalized sub-asset writes, root
serialization, and finalization. Completion reports distinguish copied files
from generated native assets. The MCP `import_third_party_asset` tool uses the
same direct conversion behavior for an external `.prefab`.

After conversion, the result is an ordinary `XRPrefabSource`. Drag it from the
Asset Explorer or instantiate it through the normal prefab workflow; there is
no Unity-specific spawn path.

## Unity Reference Capture Without Desktop Interaction

Use the source project's Unity version in GPU-enabled batch mode for offline
reference images. Supply a temporary Editor script through `-executeMethod`.
The script must load the saved scene or instantiate the prefab, render a camera
to a `RenderTexture`, read the pixels, and write a PNG plus capture settings.
Use `-batchmode -quit` with an explicit graphics backend, such as
`-force-d3d11`. Omit `-nographics`; it disables the required graphics device.
See [Unity's command-line reference](https://docs.unity3d.com/2022.3/Documentation/Manual/EditorCommandLineArguments.html).

Close only an owned Unity process before opening the same project in batch
mode. In the disposable batch process, load each capture scene with single-scene
loading and use Editor scene APIs. Do not save temporary cameras, lighting, or
prefab instances to source assets. Remove the temporary script after capture.
Check source hashes before and after the run.

Record camera transforms, field of view, image size, lighting, HDR, anti-aliasing,
post-processing, and color encoding. View every output image. A raw camera render
with custom lighting or post-processing is a controlled reference, not proof
that the saved Game view matches. These images avoid desktop screenshot capture
and its display color conversion. Desktop access is not required for this path.

## Unit Testing World Startup

Set `ModelsToImport[].Path` in `Assets/UnitTestingWorldSettings.jsonc` to a
`.unitypackage`, `.unity`, or `.prefab` file. Relative paths are checked in the
working directory, Desktop, and engine assets. For example:

```json
{
  "Enabled": true,
  "Kind": "Static",
  "Path": "misc/Avatar.unitypackage",
  "SourceProjectRoot": null,
  "MaterialMode": "Uber",
  "AllowPartialImport": true,
  "Scale": 1.0,
  "Dependencies": [
    {
      "Source": "https://poiyomi.github.io/vpm/index.json",
      "PackageId": "com.poiyomi.toon",
      "Version": "9.3.64"
    },
    {
      "Source": "https://vcc.vrcfury.com",
      "PackageId": "com.vrcfury.vrcfury",
      "Version": "1.1434.0"
    },
    {
      "Source": "https://vrchat-community.github.io/vpm-listing-curated/index.json",
      "PackageId": "com.llealloo.audiolink",
      "Version": "3.1.2"
    },
    {
      "Source": "https://Adjerry91.github.io/VRCFaceTracking-Templates/index.json",
      "PackageId": "adjerry91.vrcft.templates",
      "Version": "7.0.5"
    },
    {
      "Source": "https://vrchat-community.github.io/vpm-listing-curated/index.json",
      "PackageId": "vrchat.blackstartx.gesture-manager",
      "Version": "3.9.9"
    },
    {
      "Source": "https://vrchat.github.io/packages/index.json",
      "PackageId": "com.vrchat.base",
      "Version": "3.10.5"
    },
    {
      "Source": "https://vrchat.github.io/packages/index.json",
      "PackageId": "com.vrchat.avatars",
      "Version": "3.10.5"
    }
  ]
}
```

Package extraction runs on an engine worker. The complete source project is
stored under `<asset-cache>/UnityPackages/<content-hash>`. The cache key includes
the package SHA-256 hash and extraction format version. Extraction keeps
asset paths and `.meta` files. A completed cache entry can be reused. A changed
package gets a new entry. Extraction does not change the source package or
copy its files into the game assets directory.

Package and combined-project caches publish through a final directory rename.
On Windows, publication retries transient access and sharing errors for up to
about two seconds. The complete staging directory remains intact during retries.
A persistent error still fails import; the retry does not change permissions.

Each `.unity` and `.prefab` file becomes a separate `XRScene` in the startup
world. This also applies to loose scene and prefab targets. The scene name uses
the source filename, with a numeric suffix for duplicate names. Imported roots
are not children of Main Scene. A source scene with several roots gets one
parent root in its own scene. Static instances stay in that scene. Each scene
can be shown or hidden independently in the hierarchy panel. `.unity` scenes
start checked. Separate `.prefab` scenes start unchecked, so they do not render
alongside the scenes that contain their instances. This also applies to loose
prefab targets. Hidden prefab scenes do not contribute startup light-probe bounds.
Nested prefabs still resolve
in their source hierarchy and also get their own scene when present in the
package. Startup completion and static light-probe bounds wait for all entries.
An empty package or failed entry reports an error; it does not block completion
of the other entries.

Startup converts one root from each package at a time. The next root starts
after the previous root is attached to its scene or reports an error. This
limits concurrent native model decoding and temporary hierarchy allocations.
Staged renderer activation can continue while the next root converts. The
dependency graph scans each reached file once and retains each source reference,
including cycle and missing-reference diagnostics.

Within one root conversion, successful texture decoding is recorded by full
path, byte length, and last-write time. Repeated references to that revision
reuse the validation result without retaining decoded pixels. A new root, a
changed revision, or a failed decode requires validation again.

Required texture validation uses `IRuntimeImageCodec.Validate(Stream)`. It
checks the full decoded image without exporting RGBA pixels that import would
immediately discard. The stream and native decode state are released after
each image. Deferred texture registration still loads pixels when rendering
needs them; validation does not change texture quality or resolution.

Coordinate conversion streams packed blendshape targets and keeps only a
compact array of base attributes. Unity model import disables early native
deformation preparation through `PrepareGpuDeformationOnImport`; conversion
also releases any existing cached input before changing geometry. The renderer prepares current
input when needed; source mesh buffers remain owned by their meshes.

One root import owns a `FbxSourceDocumentCache` for parsed FBX data. The cache
holds at most one source path, file revision, and semantic policy. Coordinate
checks, material-slot resolution, native mesh construction, and animation
conversion reuse those source documents. Related documents are returned as
one coherent snapshot. Strict Unity animation parsing has a separate entry
from permissive native parsing. Consumers must not modify cached collections.
The importer clears the cache before final mesh cooking and disposes it on
exit, including failure. Runtime hierarchies, mesh buffers, bone bindings,
and animation clips remain independent for each avatar instance.

Use `Kind: Static` to retain each converted hierarchy without the unit-world
avatar setup. `Kind: Animated` adds that setup to prefab roots. Scene files do
not receive avatar setup on their scene parent. Independent prefab scenes do
not bind to the Main Scene pawn or its locomotion setup. Scale, rotation, translation,
static instance count, and static collider options apply to each entry.

Loose scenes and prefabs can use `SourceProjectRoot` to select their Unity
project or `Assets` directory. Package imports always use the extracted
project and ignore that setting. Required dependencies must be present in the
package or its companion sources. Missing models, materials, textures, or required shader evidence are
reported by the same converter used for loose prefabs.

Set `AllowPartialImport: true` to keep available content when visual dependencies
are missing. Missing material references use a lit magenta fallback in the same
renderer slot. Missing prefab instances are skipped. Material overrides without a drawable
FBX slot are reported and omitted; supported slots remain intact. Missing textures use the
material converter's default input. FBX prefab instances with unsupported coordinate
settings are omitted before geometry import, with a source diagnostic. Supported sibling
models remain available. Strict imports still reject these coordinate settings.
Diagnostics keep the source GUID and
property, and the manifest reports `PartialVisual` instead of complete visual
conversion. The default is `false` for strict imports.

Poiyomi Pro materials use the existing lossy Uber conversion when source shader
or serialized `OriginalShader` evidence identifies Pro. Partial imports can also
use consistent GUID/name evidence from other materials in the same project.
The evidence files are included in the import manifest. This preserves common
Toon features and reports unsupported Pro features. It does not reconstruct
missing material files or run Unity editor scripts.

`Dependencies` applies to a `.unitypackage` target. Each `Source` can be a local
`.unitypackage`, a VPM `.zip`, an extracted package folder with `package.json`,
a Unity project or its `Assets` folder, a direct HTTPS archive URL, or a VCC/VPM
repository URL. VCC `vcc://vpm/addRepo?url=...` links also work. For a repository
with several packages, set `PackageId`. Set `Version` to select an exact release.
An empty version selects the highest stable numeric semantic version on the
first download and retains that selection in the cache. Prereleases require an
exact version. Version constraints and automatic transitive package downloads
are not supported. Declared VPM dependencies are reported; add their sources
explicitly when required by the import.

Downloads and repository indexes remain in `<asset-cache>/UnityDownloads`.
Startup checks cached SHA-256 values and the repository's `zipSHA256`, when
present. The archive manifest must match the selected package ID and version.
Downloads use public HTTPS, bounded sizes, and checked redirects. To refresh an
index or an unpinned selection, stop the editor and clear `UnityDownloads`.
An exact version is recommended for repeatable imports.

Companion assets join the primary assets in a staged project under
`<asset-cache>/UnityProjects/<content-hash>`. VPM packages use
`Packages/<package-name>`. Folder sources keep their metadata. Project sources
copy `Assets` and embedded `Packages`; they do not copy `Library/PackageCache`.
Project manifests are excluded so their `file:` references cannot escape the
source snapshot. Archive extraction limits each archive to 100,000 entries,
 2 GiB per entry, and 8 GiB of expanded data.
Conflicting files or duplicate GUIDs fail with source diagnostics. Only the
primary package supplies startup roots. Companion prefabs are available for
nested references but do not create extra startup entries.

VRCFury and VRCFT package assets use the same supported component conversion as
other source assets. Unity editor build scripts do not run. AudioLink package
availability does not provide its Unity runtime or guarantee audio-reactive
shader parity. Unsupported behavior remains visible in import diagnostics.

All these source formats use the Unity material converter. Recognized Poiyomi
materials use the native Uber shader regardless of `MaterialMode`. Existing
version limits, Poiyomi Pro downgrade diagnostics, and unsupported behavior
reports still apply. Package scripts and assemblies are not executed.

## Project And Dependency Resolution

One `SourceProjectImportContext` is shared by the entry prefab, nested prefabs,
models, materials, textures, animation assets, and supported serialized
`.asset` files. It provides a single GUID index, reached-file cache, parsed
document cache, imported-object cache, diagnostics, and output ownership
record. The index scans these roots once per project snapshot:

1. `<project>/Assets`;
2. embedded packages under `<project>/Packages`;
3. installed packages under `<project>/Library/PackageCache`.

Project assets take precedence over packages. Duplicate GUIDs, missing assets,
and ambiguous package candidates remain visible as diagnostics instead of
being selected silently. Normalized manifest paths use portable `Assets/...`
and `Packages/...` forms where possible.

The dependency graph recursively follows references reached through prefabs,
scenes, materials, model importer metadata, animation controllers and clips,
supported serialized assets, shaders, textures, and nested prefabs. Cycles are
reported and terminated without dropping other valid edges. Optional
behavior-only references may remain missing; required model, material, and
texture references fail the visual import instead of being replaced by an
invisible placeholder.

## Supported Source Assets

The Unity-oriented conversion paths currently include:

- `.unity` scene files to `XRScene` assets.
- `.prefab` files to `XRPrefabSource` assets.
- `.mat` material files to `XRMaterial` assets.
- `.anim` animation clips to `AnimationClip` assets.
- Serialized Unity `.asset` mesh files for common uncompressed mesh layouts used by imported scene and prefab renderers.

Material, texture, prefab, and scene references are resolved through Unity
`.meta` GUIDs from the detected source project. A single prefab selection is
therefore sufficient when its dependencies remain in that project.

## Model-Backed Prefab Composition

A `PrefabInstance` is dispatched by the resolved source asset type. Unity YAML
prefabs use hierarchy composition, while `.fbx`, `.obj`, `.dae`, `.gltf`, and
`.glb` sources use the engine model importer and converge on
`XRPrefabSource`. Binary FBX data is never parsed as YAML.

Unity model object identity is reproduced from the source model GUID, local
file ID, object kind, and hierarchy path. The importer implements Unity's
generation-2 model file-ID hashing and consumes stripped GameObject,
Transform, and renderer proxy records from the outer prefab. This preserves
duplicate-name hierarchies, root parenting, nested insertion order, skinned
renderer ownership, and transform/renderer overrides.

Nested prefab instances also register inherited local file IDs when the YAML
omits a stripped proxy. Outer scene overrides can therefore target those
objects. Explicit proxy mappings retain authority, and conflicting inherited
identities fail import.

FBX `.meta` data supplies material import mode, external-object remaps, and
relevant `ModelImporter` settings. Renderer material resolution follows this
precedence:

1. model-importer external material remap;
2. model-embedded material;
3. prefab `m_Materials.Array.data[n]` override.

Prefab modifications also cover local transform, active state, enabled state,
material slots, root order, and blendshape defaults. Stale source-object
modifications are diagnosed and ignored.

### Blendshape Normals

`tangentSpace.blendShapeNormalImportMode` in the `ModelImporter` metadata sets `ModelImportOptions.ImportBlendShapeNormals`. Mode 2 ignores blendshape normals: shapes then move positions only and keep the base normals and tangents. Other modes use the authored normal deltas; mode 1 does not recalculate them. Both FBX producers honor this setting, and it is part of the model cache identity.

### Forced Anisotropic Filtering

When the current quality level in `ProjectSettings/QualitySettings.asset` sets `anisotropicTextures: 2`, the importer raises mipmapped, non-point textures to at least anisotropy level 9. A texture with level 0 keeps anisotropy off.

### Source Rim Blur

A material with `_RimBlur` uses the linear rim ramp of the source. A material without it keeps the older `_RimSharpness` edge.

### FBX Units And Skin Binds

The Unity model entry supports metre and centimetre FBX charts with the declared
source axes. Centimetre models require file-scale import. Geometry, local
translations, inverse binds, and source animation use the same metre chart
before prefab overrides apply. Native skin vertices need the unit scale;
Assimp skin vertices already contain the mesh bind and unit conversion.
The importer adjusts each palette product with the inverse geometry conversion
and retains the bind-root basis. A prefab scale override does not replace the
file-unit conversion.

Euler rotation orders and pre/post rotations use the shared FBX transform
composer. Pre/post rotations use XYZ order. The row-vector matrix factors follow
the reverse order of the [FBX column-vector formula](https://help.autodesk.com/cloudhelp/2020/ENU/FBX-Developer-Help/files/nodes_and_scene_graph/fbx_nodes/FBX_Developer_Help_nodes_and_scene_graph_fbx_nodes_computing_transformation_matrix_html.html).
The Unity entry still rejects pivot offsets, nonidentity geometric transforms,
unsupported inheritance, and unmatched producer hierarchies. In particular,
retained Assimp helper nodes can fail the source-hierarchy check when
`preserveHierarchy` is enabled. The importer does not approximate these cases.

## Animation Clip Import

Unity YAML `.anim` files are registered as third-party `AnimationClip` sources. The importer reads clip metadata, curve bindings, root-motion channels, humanoid muscle channels, IK goal channels, blendshape curves, and authored keyframes where those channels are present.

Current limits are important: the `.anim` path imports serialized clip curves, but it does not import Unity `Avatar`, `HumanDescription`, Mecanim retarget data, per-bone pre-rotations, or avatar-specific twist distribution. Default humanoid clips can be useful, but exact Mecanim playback parity is still a validation target rather than a guaranteed behavior.

## Material Conversion

Generic Unity materials import their common base color and main texture data when standard properties such as `_BaseColor`, `_Color`, `_BaseMap`, and `_MainTex` are present.

Recognized Poiyomi Toon 9.2.66, 9.2.79, and 9.3.64 and lilToon materials are converted to XRENGINE's forward-plus Uber shader path. The converter maps supported texture slots, scalar properties, color properties, feature toggles, transparency mode, alpha cutoff, culling mode, and texture transforms into engine material parameters.

Supported conversion categories include:

- Main color and main texture.
- Normal map and normal strength.
- Alpha masks and transparency mode.
- Color adjustments.
- Stylized shading, shadow colors, material AO, and shadow masks.
- Emission.
- Matcap.
- Rim lighting.
- Specular and smoothness controls.
- Detail textures and detail normals.
- Outline authoring is preserved and reported as unsupported until the inverse-hull pass is implemented.
- Backface and backlight/subsurface controls.
- Glitter.
- Flipbook textures for Poiyomi where present.
- Dissolve.
- Parallax.

Shader-specific parity is still incremental. Failed Poiyomi/lilToon conversions fall back to the generic Unity material importer with warnings. The ingestion layer now retains the exact Unity YAML, unknown fields, unresolved references, and unsupported texture shapes in source metadata and structured diagnostics instead of silently discarding them.

The Poiyomi baseline keeps toon ramps, first/second shade maps, metallic/smoothness data, rim color/masks, and dissolve noise/mask/edge inputs in independent sampler slots. UV0-UV3 are available to the uber path. If a mesh lacks an authored UV channel, conversion reports the mismatch and rendering falls back to UV0, or (0,0) when the mesh has no UVs. TextureImporter color-space, normal/data role, alpha, wrapping, filtering, mip, bias, and anisotropy settings are carried into runtime textures. Explicitly disabled Poiyomi sections stay out of the compiled uber variant even when Unity retains dormant texture assignments.

### Source Color And Cubemap Composition

Poiyomi conversion reads the source project's color-space setting. In a Linear
project, ordinary `Color` properties use the source sRGB-to-linear transfer.
`HDR` colors stay linear unless they also have `Gamma`. Ordinary colors with
`Gamma` stay unchanged. Alpha, `Vector`, and scalar values do not use this
transfer. A Gamma project keeps all authored color values. Exact shader property
declarations override the pinned catalog, including properties declared as
vectors. The catalog supplies declarations omitted from a locked shader.

`SourceToonColorSpaceNormalizer` changes a conversion copy. Raw YAML, source
values, and descriptor metadata remain available for diagnostics. The bounded
opaque Standard converter also converts its ordinary `_Color` RGB tint in a
Linear project.

The importer sets `_SourceCubeMap` for enabled source cubemaps. Both native
Advanced shading and canonical Uber shading apply that cubemap to base color
before diffuse lighting and PBR evaluation. Its emission remains separate and
is added once. The source light-mask blend uses `min(mask, lightMask)`. Generic
engine-authored Uber materials keep their existing cubemap composition.
Source overlays run in this order: matcap, cubemap, then view rim. A replacing
cubemap must not overwrite a rim that the source shader applies afterward.

Source matcap slots use linear texel filtering, trilinear mip filtering when
mips are enabled, and repeat wrap, as required by the source shader's inline
sampler. Their texture cache partition is separate from the default sampling
policy. The material sampler name stays unchanged. Masks and other texture
roles retain their imported sampling settings.
Procedural source matcap coordinates convert V to the imported texture origin
after the source texture transform and panning. Mesh-UV masks keep their existing
imported coordinates. Native Advanced and canonical Uber use the same conversion.
Source matcap mode 1 also reverses the horizontal cross-product axis to account
for the source Z reflection. This basis conversion occurs before transform and
panning. Generic engine matcaps keep their existing projection.

Environmental rim conversion uses the source `_EnableEnvironmentalRim` flag.
When it is disabled, the converted `_RimEnviroIntensity` is zero. A saved
intensity can remain in the source material after its module is disabled;
enabling another shared PBR feature must not activate that value.
Enabled source rim uses `_SourceEnviroRim` and the selected reflection probes.
It samples the prefiltered probe in the world-normal direction without box
parallax correction. The source blur maps to the native prefilter mip range.
The source view rim, brightness threshold, base color, mask channel, mask UV,
and intensity control the added light. Native Advanced and canonical forward
Uber use their existing probe selection and normalization. They do not use the
material cubemap for this source effect. Disabled and zero-intensity rim skips
the additional probe samples.
The current avatar control covers blur 1 and an absent mask. Lower blur values
need a comparison of the probe prefilter distributions. An authored source mask
can share the main-texture sampler in Unity; the native mask currently uses its
own imported sampler. These cases need separate parity validation.

### Source Emission Fluorescence

Each source emission slot stores `_EmissionFluorescence` in
`_EmissionSlotSamplingN.w`. Native Advanced and canonical Uber multiply that
slot's emission by `mix(1, clamp((1 - direct) * sqrt(direct), 0, 1), strength)`.
`direct` is the source primary light color after minimum brightness,
monochromatic, cap, multiplier, and addition controls. It does not include
shadow visibility or ambient occlusion. Zero strength retains the previous
emission result. This operation does not change other emission sources.

The serialized prefab cache version invalidates output that lacks this mapping.

### Source Hair Anisotropy

Enabled Poiyomi `_EnableAniso` uses dedicated source parameters. It does not
reuse the engine's `_Anisotropy` PBR lobe. The importer carries two strand
strengths, powers, offsets, direction switches, tints, and theme indices. It
also carries base-color replacement, additive strength, normal blend, shadow
mask, light color mix, and base color mix. Native Advanced and canonical Uber
apply the source base-color replacement before diffuse and PBR shading. They
add the strand light after the source BRDF. The imported coordinate frame
reflects the source Z axis, so the strand cross products use the corresponding
reversed signs.

An unassigned anisotropy color map uses the source fallback `(1, 1, 1, 0)`.
Assigned anisotropy maps, global masks, and toon edges fail source conversion
with a diagnostic until those paths have an exact GPU implementation. The
serialized prefab cache version invalidates output without this source stage.

### Authored Source Ambient Lighting

`WorldSettings.AmbientSphericalHarmonicsEnabled` makes authored SH authoritative
for source stylized diffuse lighting. Both native Advanced and canonical Uber
apply those coefficients through the source lighting model. They do not add
probe diffuse a second time. Enabled zero coefficients mean black authored
ambient. The enable flag controls this rule, not the coefficient magnitude.

Probe specular reflections, clearcoat, and environmental rim remain active.
With authored SH disabled, source materials retain engine probe diffuse.
Generic PBR, including lighting mode 6, retains its probe lighting behavior.
Use a matching environment, camera, light, and post-processing setup when
comparing an imported material against Unity.

### Lossless Material Metadata

`UnityMaterialDocumentParser` is reusable by any Unity shader converter. It preserves shader references, render queues, old and new keyword layouts, disabled passes, override tags, texture transforms, scalar/vector/string properties, and unrecognized serialized fields. `UnityTextureImportDocumentParser` preserves sampling metadata from TextureImporter `.meta` files, including color space, normal/alpha interpretation, wrapping, filtering, mips, anisotropy, and 2D/array/cube shape.

`UnityMaterialImportResult` exposes the parsed `SourceDocument`, resolved `ShaderAsset`, and normalized `PoiyomiDescriptor` for diagnostics and future reconversion. Texture arrays and cubes are resolved and retained as their native shapes; conversion phases that do not yet bind those shapes report them rather than flattening them.

## Poiyomi and lilToon Detection

Poiyomi Toon conversion accepts verified 9.2.66 and 9.2.79 sources and the
cataloged 9.3.64 source. The two 9.2 releases share a shader GUID, so their exact
source version marker is required. An `OriginalShaderGUID` override tag supplies
the original identity for an optimizer-generated shader. A 9.2 GUID without a
version marker cannot select the ambiguous 9.3 signature path. An explicit
unsupported version remains unsupported even when its GUID matches.

The 9.2 sources use the common native Uber mappings. Color conversion reads
their source shader declarations instead of the 9.3 catalog's color flags.
The descriptor and conversion report retain the exact source version. Active
9.2 controls without a mapping produce diagnostics. This does not accept every
version in the advertised legacy bundle. Renamed animated properties retain
both their serialized generated-shader name and original semantic binding.

lilToon detection checks canonical `lilToon/Shader/` shader paths, shader text markers such as `_lilToonVersion`, and a property-based fallback for common lilToon feature properties.

Detection resolves shader GUID metadata before choosing a shader-specific converter. Keep shader package folders and their `.meta` files with imported content whenever possible; missing references remain visible in the import report.

Poiyomi Pro-authored materials use a separate, explicitly lossy path. Unlocked
and optimizer-locked Pro signatures are classified, normalized to the common
Toon property surface, and reported as `Downgraded`. Active Pro-only Grab Pass,
refraction, blur, touch-effect, Pro integration, Pro vertex, and Pro authoring
groups are discarded with `POIPRO0002` warnings. This is a migration aid, not
Poiyomi Pro support or a parity claim. See
[Poiyomi Toon Material Conversion](../rendering/poiyomi-toon-material-conversion.md#lossy-poiyomi-pro-downgrade).

## Avatar Component Conversion

The avatar import path recognizes the following behavior metadata without
loading or executing Unity, VRChat, or package assemblies:

- VRChat PhysBone chains, including root selection, endpoint, pull, spring,
  stiffness, gravity, immobile, radius, limits, curves, collider lists, and
  referenced transforms;
- PhysBone sphere, capsule, and plane colliders;
- position, rotation, and scale constraints with source weights and affected
  axes;
- avatar descriptor view position, lip-sync mode, viseme blendshapes, and
  eye-look metadata;
- animator controller, layer, mask, expression-menu, and parameter references
  needed for inspectable import-side state.

Only behavior with independent engine semantics is published as runtime
components. Animator settings and avatar animation layers remain in the import
manifest until they can be compiled into one executable native
`AnimStateMachineComponent`; expression menus and parameter networks use the
same future graph boundary. They are not represented by placeholder runtime
components.

Physics chains retain their engine-owned transform, collider, limit, and
simulation behavior. Source-specific grabbing, posing, solver, collision
policy, and local/remote authority flags are diagnosed as intentional loss.
Future interaction support should feed one native physics-chain interaction
and authority contract so local and replicated commands do not create separate
simulation models.

Imported chains retain their saved enabled state and can simulate in edit mode.
The unit-world physics-chain fixture switch does not disable imported chains.
Restoring an avatar root transform does not reset particle history. For static
image comparisons, save and disable the imported chains, take a new baseline,
then restore their enabled states after the comparison.

Pipeline-manager components are intentionally ignored. Unknown
`MonoBehaviour` documents are summarized with script identity, local file ID,
source path, enabled state, bounded field names, byte count, and a SHA-256
digest. Raw YAML and opaque source properties are not copied into runtime
assets. Unsupported behavior remains inspectable and non-executable without
creating a source-runtime emulation layer.

## Manifest, Reimport, And Failure Policy

Every imported Unity prefab embeds a `SerializedPrefabImportManifest`. It records the
entry source, Unity project root and editor version, completion tier,
SHA-256-fingerprinted reached dependencies, GUID/local-file-ID identity,
dependency kind, referring property, timestamps, source length, conversion
outcome, native output path, diagnostics, unsupported behaviors, and owned
output paths. Schema 2 also records animator and avatar-animation graph evidence
without publishing it as runtime behavior.

The dependency monitor hashes only manifest entries. A change to the entry
prefab or a reached dependency requests reimport; an unrelated Unity project
change does not. Reimport reuses deterministic output paths and removes stale
owned outputs only after a successful replacement is ready. The native root and
its sibling assets are committed transactionally, so a failed conversion
restores the last valid files byte-for-byte.

Completion tiers distinguish hierarchy-only, visual, and visual-plus-avatar-
behavior results; the last tier means converted native behavior, not source-
platform runtime parity. Diagnostics identify the dependency, source object, target
node/component, override property, conversion phase, and severity when those
fields are available. Missing optional expression/menu assets remain non-fatal;
missing required models, materials, or textures are errors.

Imported animation callbacks cross the runtime boundary only through the
explicit native event allowlist and typed `IImportedAnimationEventReceiver`
dispatch. The allowlist is initially empty. Unmapped source callback names are
recorded as non-blocking intentional conversion loss and are never resolved to
runtime methods by reflection. Animation manifest schema 3 likewise stores
only bounded digests for opaque source payloads.

## Related Documentation

- [Model Import](model-import.md)
- [Poiyomi Toon Material Conversion](../rendering/poiyomi-toon-material-conversion.md)
