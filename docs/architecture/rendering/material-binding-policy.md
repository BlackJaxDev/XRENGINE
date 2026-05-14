# Material Binding Policy

Last updated: 2026-05-14

## Production Rule

General scene materials use the GPU material table:

- `MaterialTable` stores compact material rows.
- Each material row stores base color/opacity, roughness/metallic/specular/emission constants, and texture handle or descriptor indices.
- `MaterialTextureHandleTable` stores OpenGL bindless texture handles, or maps to Vulkan descriptor-indexing slots.

The current material row is the standard opaque-deferred row. Its canonical contract is `MaterialBindingLayouts.OpaqueDeferred`; `GPUMaterialTable`, the generated material-table draw shader, and the shared GLSL material table header must agree on that layout hash and 48-byte row shape.

The broader upgrade for additional pass-declared layouts and shader annotation-driven conversion is tracked in [Dynamic Indirect Material Bindings](../../work/design/rendering/dynamic-indirect-material-bindings.md) and [Dynamic Indirect Material Bindings TODO](../../work/todo/rendering/dynamic-indirect-material-bindings-todo.md).

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

## Dynamic Layout Compatibility

Shaders can opt into material-table conversion with `//@binding(...)` directives or `indirect=...` keys on `//@property(...)` metadata. The resolver has three outcomes:

- `MaterialTableCompatible` when pass layout, shader metadata, and material state agree.
- `PerMaterialRequired` when a shader has no usable binding metadata or needs a conservative fallback.
- `Invalid` when the shader claims compatibility but uses missing or incompatible material semantics.

Unknown material uniforms must never be silently packed into the material table.

## PSO Families

The GPU-driven path batches by coarse material state, not by material identity. The built-in families are:

- opaque deferred
- opaque forward
- alpha-tested
- shadow
- transparent

Custom PSOs must be deliberate exceptions. Adding a texture-only material must not increase the graphics program or pipeline count.
