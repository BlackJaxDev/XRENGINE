# Surface Detail Import And Forward Shadow Debugging

> Status: **implemented, with follow-up cleanup possible**

## Scope

This note captures two rendering issues that were debugged together:

1. Imported materials that provided a height map instead of an RGB normal map were not producing surface-detail normals in the forward or deferred shading paths.
2. Directional shadows in the forward path could render and bind correctly but still appear to have little or no visible effect on receiving surfaces such as the Sponza lion, arches, and floor.

These were separate root causes in different parts of the rendering stack, but they overlapped during investigation because both symptoms showed up in the same scene and both touched forward shading.

## Problem 1: Imported Height Maps Did Not Behave Like Normal Maps

### User-visible symptom

Some imported OBJ or MTL materials shipped with `TextureType.Height` rather than `TextureType.Normals`. The engine's forward and deferred material setup treated those materials as if they had no usable surface-detail normal input, so the resulting shading stayed too smooth.

### Root cause

The importer path only recognized normal textures directly. It did not provide a fallback from `TextureType.Height` to the same shader slot, and the fragment shaders expected a tangent-space normal map decode rather than a grayscale height reconstruction path.

### Implemented design

The fix treats imported "surface detail" as a mode rather than as a single texture type.

#### Import-side changes

- `ModelImporter` now resolves a shared surface-detail texture slot.
- The importer prefers `TextureType.Normals`.
- If no normal map exists, it falls back to `TextureType.Height`.
- The importer appends shader parameters that describe how the shader should interpret `Texture1`.

Current parameters:

- `NormalMapMode = 0` means `Texture1` is an RGB normal map.
- `NormalMapMode = 1` means `Texture1` is a grayscale height map.
- `HeightMapScale` controls finite-difference reconstruction strength for imported height maps.

#### Shader-side changes

- A shared snippet, `SurfaceDetailNormalMapping.glsl`, reconstructs a world-space normal from either source mode.
- Forward and deferred fragment shaders now call the same helper instead of each keeping separate inline normal decoding logic.

### Why this design was chosen

This approach keeps import flexibility without forking the material model.

- The texture slot layout stays stable.
- The material still exposes one surface-detail texture.
- The distinction between normal-map and height-map interpretation moves into explicit shader parameters.
- Forward and deferred paths share one implementation instead of drifting apart.

### Files touched

- `XRENGINE/Core/ModelImporter.cs`
- `Build/CommonAssets/Shaders/Snippets/SurfaceDetailNormalMapping.glsl`
- Forward and deferred fragment shaders that previously decoded `Texture1` inline

## Problem 2: Forward Directional Shadows Looked Like They Were Not Being Received

### User-visible symptom

In the Sponza-style test scene, the lion and surrounding architecture could look almost unshadowed even though:

- the directional light was shadow-casting,
- the engine was rendering a directional shadow map,
- and the forward path reported that it had a bound shadow texture.

This was especially obvious when using the forward lit textured shader variants.

### What was ruled out

The investigation explicitly ruled out several incorrect explanations.

#### Not simply "outside the directional shadow box"

That was an early hypothesis, but it did not explain the observed case once scene coverage was checked.

#### Not a missing shadow map bind

Runtime logs showed all of the following:

- shadow map rendering was occurring,
- the directional light had an active shadow camera,
- and the forward path reported `ShadowMapEnabled=True` with a valid `XRTexture2D` shadow texture.

#### Not imported mesh metadata disabling shadows

`RenderInfo3D` defaults both `CastsShadows` and `ReceivesShadows` to `true`, so imported static scene geometry was not being excluded by default.

### Root causes found

Two forward-shadow problems were identified.

#### Root cause A: shadow caster variants inherited source material face culling

The shadow pass does not always draw the original material directly. In shadow passes, mesh renderers swap to `ShadowCasterVariant` materials. Those variants were cloning the source material's cull mode.

That meant single-sided surfaces could disappear from the shadow map when seen from the light's back side, even though the directional light's own shadow framebuffer material disabled culling.

This matters for floors, ceilings, and arch surfaces when the light direction moves to the opposite side of the authored face orientation.

#### Root cause B: forward directional shadow receive used oversized hardcoded bias values

The forward lighting snippet used hardcoded bias values:

- `maxBias = 0.04`
- `minBias = 0.001`

Those values were much larger than the engine's normal shadow bias controls used elsewhere. With a large orthographic depth range, that bias was sufficient to skip real occluders and make receiving shadows look washed out or absent.

The lightmapping path already used the engine's configured bias parameters and a small PCF kernel, which made it a better reference implementation than the forward snippet.

### Implemented design

#### Shadow caster change

`ShadowCasterVariantFactory` now forces shadow caster variants to render with `CullMode = None`.

This ensures a mesh that is supposed to cast into the shadow map is not silently removed just because the shadow pass is viewing the back side of a one-sided material.

#### Forward receiver change

The forward shadow sampling path now uses the same bias controls already owned by `LightComponent`.

The snippet now consumes:

- `ShadowBase`
- `ShadowMult`
- `ShadowBiasMin`
- `ShadowBiasMax`

It also uses a 3x3 PCF read similar to the lightmapping path instead of a single hard-shadow sample.

This makes forward receive behavior consistent with the rest of the engine's directional shadow tuning instead of depending on a separate hardcoded bias regime.

#### Matrix plumbing note

During investigation, the forward path was also updated to pass separate primary directional-light view/projection data rather than relying only on a precombined matrix.

That change was useful for reducing ambiguity during debugging, but the dominant visual fix came from correcting the forward bias model. If future cleanup work wants to simplify the forward path again, that matrix split should be re-evaluated against a direct render test rather than kept by assumption.

### Files touched

- `XREngine.Runtime.Rendering/Shaders/ShadowCasterVariantFactory.cs`
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `XRENGINE/Rendering/Lights3DCollection.cs`

## Debugging Lessons

### Lesson 1: logs can prove existence, not correctness

The runtime logs were enough to prove that:

- the shadow map pass executed,
- the directional light had a live shadow camera,
- and the forward shader had a bound shadow texture.

They were not enough to prove that the receiver path was producing a meaningful occlusion term.

### Lesson 2: compare against the engine's known-good path

The most productive comparison was not another speculative shader rewrite. It was checking what the engine already did in `LightmapBakeManager` and asking why forward differed from that path.

That exposed the hardcoded forward bias values quickly.

### Lesson 3: shadow generation and shadow receiving fail independently

The same scene had both of these at different times:

- geometry that could fail to cast because shadow variants inherited face culling,
- geometry that could still fail to look shadowed because the forward receiver bias was too large.

Treating those as separate failure planes was necessary.

## Current Result

### Surface-detail import

Imported materials can now use either:

- a normal map directly, or
- a height map reconstructed into a normal at shading time.

This behavior is unified across forward and deferred shader variants through a shared snippet.

### Forward directional shadows

Directional shadows in the forward path now use:

- uncullable shadow caster variants,
- engine-configured bias uniforms,
- and 3x3 PCF sampling.

This materially improves visible shadow receive on scene surfaces that previously looked effectively unshadowed.

## Remaining Questions And Follow-up

1. The forward path still has some debugging-oriented matrix plumbing that may be more complex than necessary. It should be reviewed after the visual regression is confirmed closed.
2. If directional shadow quality still looks fragile in extreme light orientations, the next diagnostic tool should be a temporary forward-shader visualization mode that outputs the sampled shadow factor directly.
3. If one-sided materials should only sometimes cast two-sided shadows, the global `CullMode = None` rule for shadow caster variants may eventually need to become a material-level option instead of a universal default.
