# Poiyomi Material Runtime Architecture

This document records the render-state, pass-set, module, and binding contracts
implemented for Poiyomi Toon 9.3.64 imports.

## Pass Set

One `XRMaterial` owns parameters, textures, and authored uber feature state.
`MaterialPassSet` adds immutable pass definitions for EarlyZ, depth-normal,
shadow, velocity, transform ID, picking, reflection, base color, and inverse-hull
outline work. Definitions carry pass-specific shaders and fixed-function state
without duplicating the authored material.

Pass order is deterministic:
| Order | Identity |
| ---: | --- |
| 100 | Early depth |
| 200 | Depth-normal |
| 300 | Shadow |
| 400 | Velocity |
| 410 | Transform ID |
| 420 | Picking |
| 430 | Reflection |
| 500 | Base color |
| 600 | Outline |

Every definition carries the same position/opacity state hash and the complete
alpha, dissolve, UV-discard, vertex-deformation, and culling coverage contract.
Renderable meshes submit an enabled outline definition as a second,
CPU-orchestrated mesh command sharing the source renderer and authored material
state. The canonical vertex and fragment variants provide inverse-hull
extrusion after deformation and reuse the base alpha/dissolve coverage path.
The pass identity, render pass, position/opacity hash, and pass macros all
participate in prewarm keys. Enabled definitions are copied to caller-owned
storage without steady-state allocations.

Poiyomi's `ForwardAdd` pass is folded into the Forward+ base pass. Its authored
additive state is retained for diagnostics. Disabling the source Add pass
suppresses any separate compatibility pass while ordinary Forward+ base-pass
lighting remains active.

## Presets And Fixed State

The importer maps all nine serialized `_Mode` values independently. Render queue
and queue priority are preserved separately from transparency classification.
RGB and alpha blend factors and operations, depth write/test, culling, color
mask, polygon offset, alpha-to-coverage, fog opt-out, and common/front/back
stencil state are converted from their serialized Unity enum values. Outline
blend, depth, stencil, and cull state is independent from the base pass.

Verified locked source toon 9.3.9 and 9.3.63 Basic outlines use the canonical mesh
deformation before outline expansion. The source branch samples the outline
mask at mip 0, applies its authored width and distance factor to the deformed
normal, and clips fragments with the source width-mask threshold. It keeps the
authored outline blend, depth, stencil, and queue state. The importer requires
the supported local-space, UV0, red-channel mask subset. An external mask must
use a linear-repeat sampler; an absent source mask uses the white default.
The importer also requires neutral outline theme, color adjust, and AudioLink
width controls. Other engine outline materials keep their existing expansion
rules.

The outline, the second rim light, the stylized specular lobe, and the cubemap
are optional features. When one of them has an active source option without an
exact Uber conversion, the importer turns off only that feature and keeps the
Uber conversion for the rest of the material. The conversion report records the
refused option as a `POI0007` warning. Other unconvertible options still fail
the Uber conversion, and the material then uses the generic fallback. A theme
color index from 1 to 4 on the stylized specular tint converts to the constant
theme color when that theme has no hue, saturation, value, or hue-speed
adjustment.

Newer source shaders blend matcap global masks with their 20-entry color blend
list. The importer reads the list from the source shader property declaration
and maps normal, darken, multiply, lighten, linear dodge, subtract, and divide
to the native mask blend modes. Other color blend modes still fail the
conversion.

A material that uses the source procedural eye shader converts to a native Uber
material with procedural mode 6 (`_ProceduralEyeParams0` to
`_ProceduralEyeParams3`). The forward Uber, native Uber, and splat paths draw
the same unlit layered parallax iris, smoke rings, glare, and dithered vignette.
A nonzero vignette transparency makes the material masked.

Imported source materials apply `_AlphaForceOpaque` after decal, flipbook, and
dissolve effects, before the final alpha cutoff. Dissolve can still discard a
fragment. Native visibility, masked shadow, and native shading use the same
coverage rule. The authored Uber path retains alpha before the override for
effects such as GrabPass and applies the override to final coverage and output.

Verified source surfaces with a queue above 2500 use the ordered late pass.
The base draw keeps its authored blend, depth, and stencil state, and its sort
priority is the source queue minus 3000. An active, verified named GrabPass uses
the `_SourceSceneColor` array sampler. The pipeline copies linear HDR scene
color before the first such draw and binds the matching view layer. The shader
divides absolute framebuffer coordinates by the full copy extent, including
views with a nonzero viewport origin. It uses source alpha and Fresnel before
the late opaque override, blends scene
color into base color before decals, matcaps, and rim, and then lights the
result. The blend uses the source shader blend factor table: `_GrabSrcBlend` and
`_GrabDstBlend` map to `_SourceGrabSrcBlend` and `_SourceGrabDstBlend` (indices
0 to 10). Source toon refraction maps to `_SourceRefraction*`. The shader moves the
scene-color sample by the view-space normal, multiplied by `(index - 1)`,
`(1 - N.V)`, half the vertical projection scale, and the inverse camera distance
(at least 1). Chromatic aberration samples green at `offset * (1 - aberration)`
and blue at `offset * (1 + aberration)`. The view-space up direction follows the
projection sign and `FramebufferTextureYDirection`.

The importer does not reject a GrabPass material for an unsupported option. It
drops each option that has no mapping and reports it as a `POI0007` warning.
These options are blur, hue shift, pixelation, color ramp, saturation and
vibrance, mask textures, vertex-color masks, global masks, chromatic-aberration
blur, and alpha distance, angle, and AudioLink masks. Out-of-range values are
replaced by the source shader defaults or clamped. An unverified GrabPass shader still
uses the scene-color mapping with a warning. A GrabPass with a queue of 2500 or
less, or with an outline, EarlyZ, or alpha-to-coverage pass, renders without its
scene-color sample. The ordered route keeps the `NativeUberV1` material identity.
The verified Grab lighting divisor uses the primary directional light. The
import report flags source vertex-light and additional-light responses for live
validation.

## Uber Helper Audit

`UberHelperModuleAudit` is the authoritative, testable inventory.

- Active canonical includes: `common.glsl`, `uniforms.glsl`, `parallax.glsl`,
  `dissolve.glsl`, `glitter.glsl`, and `flipbook.glsl`.
- Active companion path: `XRENGINE_OUTLINE_PASS` variants of the canonical
  mono, OpenVR/OpenXR, and fragment shaders.
- Reusable reference implementations whose live behavior remains inline:
  `backface.glsl`, `details.glsl`, `emission.glsl`, `matcap.glsl`, `pbr.glsl`,
  `specular.glsl`, and `subsurface.glsl`.
- Inline surface-extension contracts own four-slot decals alongside
  `decals.glsl` reference helpers.
- Reusable legacy references: `outline.vert` and `outline.frag`.
- Obsolete and superseded by the canonical pass branch: `outlines.glsl`.

A helper file is not evidence that a feature is supported. Support requires a
reachable canonical render-pass path. The variant builder prunes disabled
feature guards and sampler declarations and computes transitive feature
dependency closure before generating the variant.

## Locked Shader Reconciliation And Cooked Textures

`SourceToonLockedShaderReconciler` reconciles locked source toon materials against
their compiled base-pass HLSL before material conversion:

- Matches `[ThryToggle(KEYWORD)]` and `[ThryToggle(KEYWORD, true|false)]`
  attribute forms so locked pass defines update section toggles and keywords.
- Strips samplers whose `PROP_*` define is absent under `OPTIMIZER_ENABLED` and
  applies inline baked scalar and vector constants unless the property remains
  animated.
- Preserves Unity `.meta` mipmap generation (`SmallestAllowedMipmapLevel` set
  to `1000` when mipmaps are enabled) and trilinear filter state when cooking
  hydrated prefab textures into `.pak` archives.
- Rebinds pathless cooked `NativeUberV1` fragment shader copies to the canonical
  host `UberShader.frag` during `XRMaterial.EnsureUberStateInitialized` and
  `UberShaderVariantBuilder` compilation when host shader files are accessible,
  so cooked `.pak` materials use current engine shader sources without
  re-cooking.

## Resource Limits And Binding Ladder

The portable baseline is 16 fragment samplers, 16 sampled images, and 16 KiB of
uniform storage for both OpenGL 4.6 and Vulkan 1.0; Vulkan also records the
128-byte minimum push-constant budget.

`UberMaterialBindingPlanner` selects the first faithful option:

1. Direct samplers within the active backend limits.
2. Texture arrays only when dimensions, formats, and sampler behavior match.
3. The engine material texture table when its backend capability is active.
4. Bindless/descriptor-indexed resources when supported.
5. A precise unsupported result when none can represent the material.

The planner never silently drops a texture. `UberSamplerFallbacks` supplies
role-aware color, normal, white/black mask, zero-data, neutral-height, and black
emission defaults.

`GaussianAvatarSplatMaterialTable` packs `NativeUber` material records for live
Gaussian avatar surface splats within a 128-slot 2D texture table. It skips
sampler roles whose owning Uber feature bit is disabled and reuses existing
1-based slot indices when multiple materials or roles reference the same
`XRTexture2D` or source file path.

## Validation

Use the same camera, pose, render time, exposure, texture resolution, and
anti-aliasing settings for source and native image comparisons. Compare the HDR
scene output with the final output before changing material conversion. FXAA
can soften thin texture marks and outlines that remain intact in the HDR image.

`PoiyomiMaterialArchitectureTests` covers every preset, authored fixed state,
queue and pass enable state, deterministic ordering, companion coverage keys,
outline state, allocation-free enumeration, transitive dependencies, slot
schemas, binding rungs and failures, and semantic fallbacks.
