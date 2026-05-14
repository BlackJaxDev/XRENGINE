# Material Binding Policy

Last updated: 2026-05-14

## Production Rule

General scene materials use the GPU material table:

- `MaterialTable` stores compact material rows.
- Each material row stores base color/opacity, roughness/metallic/specular/emission constants, and texture handle or descriptor indices.
- `MaterialTextureHandleTable` stores OpenGL bindless texture handles, or maps to Vulkan descriptor-indexing slots.

The current material row is the standard opaque-deferred row. The proposed upgrade for pass-declared dynamic layouts and shader annotation-driven conversion is tracked in [Dynamic Indirect Material Bindings](../../work/design/rendering/dynamic-indirect-material-bindings.md).

Texture arrays are not the fallback for arbitrary material diversity. They are allowed only for genuinely homogeneous resource classes where every layer has the same semantic, dimensions, format, sampling policy, and lifetime pattern:

- decals
- terrain splat layers
- light cookies and shadow/cookie atlases
- render-target arrays used by stereo, cascades, probes, or post-processing

## Enforcement

`RenderingParameters.TextureArrayPolicy` defaults to `ArbitraryMaterialTextures`. Generic material-table draws reject texture-array inputs unless a material explicitly opts into `HomogeneousClassOnly`. That keeps one-off imported materials on bindless/descriptor indexing and prevents unbounded texture-array variants.

OpenGL `BindlessMaterialTable` uses `GL_ARB_bindless_texture` handles made resident by the renderer. The material table is two-level:

`MaterialEntry -> handle index -> 64-bit OpenGL handle`

Vulkan uses the same material entry indices as descriptor indices into the descriptor-indexed material texture array. Shaders that use that path must read through `nonuniformEXT`.

## PSO Families

The GPU-driven path batches by coarse material state, not by material identity. The built-in families are:

- opaque deferred
- opaque forward
- alpha-tested
- shadow
- transparent

Custom PSOs must be deliberate exceptions. Adding a texture-only material must not increase the graphics program or pipeline count.
