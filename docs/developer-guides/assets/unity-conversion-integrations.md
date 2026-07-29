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

## Project And Dependency Resolution

One `UnityProjectImportContext` is shared by the entry prefab, nested prefabs,
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

FBX `.meta` data supplies material import mode, external-object remaps, and
relevant `ModelImporter` settings. Renderer material resolution follows this
precedence:

1. model-importer external material remap;
2. model-embedded material;
3. prefab `m_Materials.Array.data[n]` override.

Prefab modifications also cover local transform, active state, enabled state,
material slots, root order, and blendshape defaults. Stale source-object
modifications are diagnosed and ignored.

## Animation Clip Import

Unity YAML `.anim` files are registered as third-party `AnimationClip` sources. The importer reads clip metadata, curve bindings, root-motion channels, humanoid muscle channels, IK goal channels, blendshape curves, and authored keyframes where those channels are present.

Current limits are important: the `.anim` path imports serialized clip curves, but it does not import Unity `Avatar`, `HumanDescription`, Mecanim retarget data, per-bone pre-rotations, or avatar-specific twist distribution. Default humanoid clips can be useful, but exact Mecanim playback parity is still a validation target rather than a guaranteed behavior.

## Material Conversion

Generic Unity materials import their common base color and main texture data when standard properties such as `_BaseColor`, `_Color`, `_BaseMap`, and `_MainTex` are present.

Recognized Poiyomi Toon 9.3.64 and lilToon materials are converted to XRENGINE's forward-plus Uber shader path. The converter maps supported texture slots, scalar properties, color properties, feature toggles, transparency mode, alpha cutoff, culling mode, and texture transforms into engine material parameters.

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

### Lossless Material Metadata

`UnityMaterialDocumentParser` is reusable by any Unity shader converter. It preserves shader references, render queues, old and new keyword layouts, disabled passes, override tags, texture transforms, scalar/vector/string properties, and unrecognized serialized fields. `UnityTextureImportDocumentParser` preserves sampling metadata from TextureImporter `.meta` files, including color space, normal/alpha interpretation, wrapping, filtering, mips, anisotropy, and 2D/array/cube shape.

`UnityMaterialImportResult` exposes the parsed `SourceDocument`, resolved `ShaderAsset`, and normalized `PoiyomiDescriptor` for diagnostics and future reconversion. Texture arrays and cubes are resolved and retained as their native shapes; conversion phases that do not yet bind those shapes report them rather than flattening them.

## Poiyomi and lilToon Detection

Poiyomi Toon conversion is pinned to the cataloged 9.3.64 shader. Unlocked materials are recognized by the pinned shader GUID or exact source evidence. Optimizer-generated materials can be recognized through their `OriginalShaderGUID` override tag even when the generated shader has a different path. Renamed animated properties retain both their serialized generated-shader name and original semantic binding.

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
- avatar descriptor view position, lip-sync mode, viseme blendshapes, eye-look
  metadata, playable animation layers, and custom animation layers;
- Animator controller references needed for inspectable imported state.

Pipeline-manager components are intentionally ignored. Unknown
`MonoBehaviour` documents are preserved as structured metadata containing
their script GUID, local file ID, source path, enabled state, and raw
properties. Unsupported behavior is therefore inspectable and non-executable
rather than silently lost.

## Manifest, Reimport, And Failure Policy

Every imported Unity prefab embeds a `UnityPrefabImportManifest`. It records the
entry source, Unity project root and editor version, completion tier,
SHA-256-fingerprinted reached dependencies, GUID/local-file-ID identity,
dependency kind, referring property, timestamps, source length, conversion
outcome, native output path, diagnostics, unsupported behaviors, and owned
output paths.

The dependency monitor hashes only manifest entries. A change to the entry
prefab or a reached dependency requests reimport; an unrelated Unity project
change does not. Reimport reuses deterministic output paths and removes stale
owned outputs only after a successful replacement is ready. The native root and
its sibling assets are committed transactionally, so a failed conversion
restores the last valid files byte-for-byte.

Completion tiers distinguish hierarchy-only, visual, and visual-plus-avatar-
behavior results. Diagnostics identify the dependency, source object, target
node/component, override property, conversion phase, and severity when those
fields are available. Missing optional expression/menu assets remain non-fatal;
missing required models, materials, or textures are errors.

## Related Documentation

- [Model Import](model-import.md)
- [Poiyomi Toon Material Conversion](../rendering/poiyomi-toon-material-conversion.md)
