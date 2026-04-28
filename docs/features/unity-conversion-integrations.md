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

Recognized Poiyomi Toon 9.3 and lilToon materials are converted to XRENGINE's forward-plus Uber shader path. The converter maps supported texture slots, scalar properties, color properties, feature toggles, transparency mode, alpha cutoff, culling mode, and texture transforms into engine material parameters.

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
- Outline masks.
- Backface and backlight/subsurface controls.
- Glitter.
- Flipbook textures for Poiyomi where present.
- Dissolve.
- Parallax.

Shader-specific parity is best-effort. Unsupported source properties are ignored, and failed Poiyomi/lilToon conversions fall back to the generic Unity material importer with warnings.

## Poiyomi and lilToon Detection

Poiyomi Toon detection currently targets Poiyomi Toon 9.3 shader paths and shader text, with a property-based fallback for materials that expose the expected Poiyomi property set.

lilToon detection checks canonical `lilToon/Shader/` shader paths, shader text markers such as `_lilToonVersion`, and a property-based fallback for common lilToon feature properties.

Detection depends on shader GUID resolution through `.meta` files. Keep shader package folders and their `.meta` files with the imported content whenever possible.

## Related Documentation

- [Model Import](model-import.md)