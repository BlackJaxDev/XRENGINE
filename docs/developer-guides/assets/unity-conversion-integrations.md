# Unity Conversion Integrations

XRENGINE includes interoperability paths for importing content authored in Unity projects. These integrations are format converters and material mappers; they do not make XRENGINE a Unity runtime, and they do not imply affiliation with or endorsement by Unity Technologies.

Unity is a trademark of Unity Technologies. XRENGINE uses the name only to identify compatible source formats and workflows.

## Editor Workflow

Use the ImGui editor's `Tools > Import External Files...` or `Tools > Import External Folder...` commands to copy sources into the project `Assets/` tree. When `Import after copy` is enabled, files with registered third-party extensions are immediately converted to native `.asset` files.

Folder import is preferred for Unity project exports because it preserves the source layout, `.meta` files, shader files, material files, and texture dependencies needed for GUID resolution.

## Supported Source Assets

The Unity-oriented conversion paths currently include:

- `.unity` scene files to `XRScene` assets.
- `.prefab` files to `XRPrefabSource` assets.
- `.mat` material files to `XRMaterial` assets.
- `.anim` animation clips to `AnimationClip` assets.
- Serialized Unity `.asset` mesh files for common uncompressed mesh layouts used by imported scene and prefab renderers.

Material, texture, prefab, and scene references are resolved through Unity `.meta` GUIDs when the source project layout is available. Importing a whole folder generally produces better results than selecting isolated files.

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

## Related Documentation

- [Model Import](model-import.md)