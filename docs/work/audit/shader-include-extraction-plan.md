# Shader Include Extraction Review

Date: 2026-03-18
Scope: `Build/CommonAssets/Shaders`
Status: Review only. No shader files changed.

## Goal

Identify repeated GLSL code blocks that should be moved into shared `#include`-style `.glsl` files or redirected to existing shared snippets.

## Recommended Review Order

1. Extract `HashColor` into a small shared utility include.
2. Centralize octahedral encode/decode helpers.
3. Centralize the common `Attenuate` helper.
4. Consolidate deferred PBR helper blocks around the existing shared PBR snippet.
5. Decide whether the Uber shader family should stay self-contained.

## Candidate 1: Hash Color Utility

- Priority: High
- Proposed include: `Build/CommonAssets/Shaders/Snippets/HashColor.glsl`
- Canonical source: move the existing implementation from one of the debug shaders unchanged.
- Why: this is exact copy-paste and the safest extraction in the tree.

Representative duplicate definitions:

- `Build/CommonAssets/Shaders/Scene3D/DebugTransformId.fs`
- `Build/CommonAssets/Shaders/Scene3D/DebugSurfelCircles.fs`
- `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/DebugCircles.comp`

Suggested exported API:

```glsl
vec3 XRENGINE_HashColor(uint id)
```

Notes:

- No stage-specific behavior.
- No dependency on other snippets.
- Good first extraction because it is isolated and low risk.

## Candidate 2: Octahedral Encoding / Decoding

- Priority: High
- Proposed include: `Build/CommonAssets/Shaders/Snippets/OctahedralMapping.glsl`
- Alternative: extend `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl` if the project prefers fewer snippet files.
- Why: encode and decode logic is repeated across environment, reflection, and deferred-combine shaders.

Current repeated encode sites:

- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombineStereo.fs`
- `Build/CommonAssets/Shaders/Scene3D/IrradianceConvolutionOcta.fs`
- `Build/CommonAssets/Shaders/Scene3D/OctahedralEnv.fs`
- `Build/CommonAssets/Shaders/Scene3D/PrefilterOcta.fs`

Current repeated decode sites:

- `Build/CommonAssets/Shaders/Scene3D/CubemapToOctahedron.fs`
- `Build/CommonAssets/Shaders/Scene3D/IrradianceConvolutionOcta.fs`
- `Build/CommonAssets/Shaders/Scene3D/PrefilterOcta.fs`
- `Build/CommonAssets/Shaders/Tools/OctahedralImposterBlend.fs` uses the same idea with different naming

Suggested exported API:

```glsl
vec2 XRENGINE_EncodeOcta(vec3 dir)
vec3 XRENGINE_DecodeOcta(vec2 uv)
vec3 XRENGINE_SampleOcta(sampler2D tex, vec3 dir)
vec3 XRENGINE_SampleOctaLod(sampler2D tex, vec3 dir, float lod)
vec3 XRENGINE_SampleOctaArray(sampler2DArray tex, vec3 dir, float layer)
vec3 XRENGINE_SampleOctaArrayLod(sampler2DArray tex, vec3 dir, float layer, float lod)
```

Notes:

- The forward-lighting snippet already contains part of this API.
- The swizzle convention is consistent in the reviewed files and should be kept exactly as-is.
- `OctahedralImposterBlend.fs` should be compared carefully before redirecting because its naming differs even if the math is the same.

## Candidate 3: Shared Light Attenuation Helper

- Priority: High
- Proposed include: `Build/CommonAssets/Shaders/Snippets/LightAttenuation.glsl`
- Alternative: move to `Build/CommonAssets/Shaders/Snippets/MathUtils.glsl` if the project prefers a broad utility snippet.
- Why: the same attenuation formula is duplicated through forward and deferred lighting paths.

Current repeated definition sites:

- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `Build/CommonAssets/Shaders/SNIP_LightingBasic.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingSpot.fs`
- `Build/CommonAssets/Shaders/Scene3D/PBRShaderStart.glsl`
- `Build/CommonAssets/Shaders/Scene3D/Unused/OLD_DeferredLighting.fs`

Suggested exported API:

```glsl
float XRENGINE_Attenuate(float dist, float radius)
```

Notes:

- Most implementations are effectively identical.
- Some call sites scale `dist` and `radius` by brightness before calling; only the helper should be centralized, not the caller-specific preprocessing.
- The voxel cone tracing attenuation functions should stay separate for now because they are simplified and not obviously the same abstraction.

## Candidate 4: Deferred PBR Helper Consolidation

- Priority: High
- Proposed destination: reuse `Build/CommonAssets/Shaders/Snippets/PBRFunctions.glsl`
- Alternative: create `Build/CommonAssets/Shaders/Snippets/DeferredPBR.glsl` if the existing generic snippet is considered too broad or naming-stable to change.
- Why: several deferred shaders duplicate the same GGX, Smith, and Schlick helper blocks.

Current duplicate clusters:

- `Build/CommonAssets/Shaders/Scene3D/PBRShaderEnd.glsl`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir_Enhanced.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingSpot.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombineStereo.fs`
- `Build/CommonAssets/Shaders/Snippets/ForwardLightingPBR.glsl`
- `Build/CommonAssets/Shaders/Meshlets/MeshletShading.fs`
- `Build/CommonAssets/Shaders/Uber/pbr.glsl`

Suggested consolidation target:

```glsl
float XRENGINE_D_GGX(float NoH, float roughness)
float XRENGINE_G_SchlickGGX(float NoV, float roughness)
float XRENGINE_G_Smith(float NoV, float NoL, float roughness)
vec3 XRENGINE_F_Schlick(float VoH, vec3 F0)
vec3 XRENGINE_F_SchlickFast(float VoH, vec3 F0)
vec3 XRENGINE_F_SchlickRoughness(float VoH, vec3 F0, float roughness)
vec3 XRENGINE_CookTorranceSpecular(float D, float G, vec3 F, float NoV, float NoL)
```

Notes:

- This is the biggest code-reduction target, but it also has the most naming drift.
- `ForwardLighting.glsl` contains similar functions with `Spec*` names and precomputed `k`; those may need adapters rather than a direct swap.
- `MeshletShading.fs` and `Uber/pbr.glsl` use the same math but may intentionally remain self-contained depending on shader compilation constraints.

## Candidate 5: Light Struct Reuse

- Priority: Medium
- Proposed destination: reuse the struct definitions already present in `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- Why: the same forward-light structures are redefined in a few places.

Current overlap:

- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `Build/CommonAssets/Shaders/SNIP_LightingBasic.fs`
- `Build/CommonAssets/Shaders/Uber/uniforms.glsl`
- `Build/CommonAssets/Shaders/Uber/outline.frag`

Suggested action:

- Redirect `SNIP_LightingBasic.fs` first.
- Review whether the Uber shader family should keep local copies for self-containment.

Notes:

- Deferred light shaders use trimmed structs that are not one-for-one replacements and should not be folded into the forward structs automatically.

## Candidate 6: Shared Math Constants and Small Utilities

- Priority: Medium
- Proposed destination: extend or standardize on `Build/CommonAssets/Shaders/Snippets/MathUtils.glsl`
- Why: constants and helper functions are partly centralized already, but several shader families still redefine them.

Current overlap:

- `Build/CommonAssets/Shaders/Snippets/MathUtils.glsl`
- `Build/CommonAssets/Shaders/Snippets/PBRFunctions.glsl`
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `Build/CommonAssets/Shaders/Uber/common.glsl`

Possible shared items:

```glsl
PI
TAU
INV_PI
EPSILON
XRENGINE_Saturate
XRENGINE_Remap
```

Notes:

- This is worthwhile, but the gain is smaller than the lighting and octahedral candidates.
- If the Uber family stays self-contained, it may intentionally keep local constants and helpers.

## Candidate 7: Color Conversion Reuse

- Priority: Low to Medium
- Proposed destination: reuse `Build/CommonAssets/Shaders/Snippets/ColorConversion.glsl`
- Why: Uber keeps local sRGB helpers even though a shared snippet already exists.

Current overlap:

- `Build/CommonAssets/Shaders/Snippets/ColorConversion.glsl`
- `Build/CommonAssets/Shaders/Uber/common.glsl`

Suggested exported API:

```glsl
vec3 XRENGINE_SRGBtoLinear(vec3 srgb)
vec3 XRENGINE_LinearToSRGB(vec3 linear)
```

Notes:

- This is a cleanup candidate, not a high-priority dedupe target.
- Tone-mapping helpers in `ToneMapping.glsl` should stay separate because they include fast and precise variants rather than the same API surface.

## Areas That Should Probably Remain Separate

### Uber shader family

Files under `Build/CommonAssets/Shaders/Uber` appear intentionally modular and self-contained. Even when they duplicate engine snippets, keeping them local may simplify graph-style composition and reduce coupling.

### Deferred light structs

Deferred point, spot, and directional light structs are reduced variants tailored to specific passes. Their math helpers should be shared first; the structs themselves should not be unified blindly.

### Specialized attenuation variants

`Build/CommonAssets/Shaders/Scene3D/VoxelConeTracing/voxelization.frag` and `Build/CommonAssets/Shaders/Scene3D/VoxelConeTracing/voxel_cone_tracing.frag` use attenuation-like helpers, but they do not clearly belong to the same abstraction as the forward/deferred light falloff helper.

## Suggested Implementation Sequence

1. Add `HashColor.glsl` and redirect the three debug shaders.
2. Add `OctahedralMapping.glsl` or extend the current octahedral API in `ForwardLighting.glsl`.
3. Add `LightAttenuation.glsl` or centralize `XRENGINE_Attenuate` in `MathUtils.glsl`.
4. Refactor deferred PBR helpers to consume `PBRFunctions.glsl`, starting with `PBRShaderEnd.glsl` and one deferred shader as a validation pass.
5. Decide whether Uber should adopt shared snippets or remain intentionally duplicated.

## Validation Focus When Refactoring Later

- Environment map sampling must preserve the current octahedral swizzle convention.
- Deferred lighting output must be compared visually after any PBR helper unification.
- Shader include order must be checked where constants such as `PI` and `INV_PI` already exist.
- Any change touching Uber should be validated separately because those files may rely on self-contained compilation assumptions.