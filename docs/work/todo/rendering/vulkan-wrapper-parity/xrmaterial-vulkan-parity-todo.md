# XRMaterial Vulkan Parity TODO

Last Updated: 2026-06-05
Status: Active.

## Goal

Make `VkMaterial` provide the same engine-facing material behavior as
`GLMaterial`: parameter binding, texture binding, render options, engine
uniforms, shadow binding sources, material hooks, diagnostics, and descriptor
fallback behavior.

## Source Inventory

OpenGL:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs`

Vulkan:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Uniforms.cs`

## Current Parity Already Present

- Vulkan has a material wrapper.
- `VkMaterial` tracks texture collection changes and material property changes.
- Descriptor sets are created per Vulkan program and frame.
- Material-owned uniform buffers are supported for a limited set of scalar,
  vector, and matrix parameter types.
- Image descriptors can use placeholders instead of failing the whole set.

## Generation Contract

`IsGenerated` must not be used as a proxy for descriptor-set readiness or valid
material data. OpenGL generation only means the API object exists and has an ID
where an API object exists; material uniforms/textures are made valid by later
binding calls. Vulkan should mirror that split: wrapper/cache generation is
separate from descriptor allocation, descriptor writes, uniform uploads, and
per-program material readiness.

## Missing Parity TODO

1. Define ownership between `VkMaterial` and `VkMeshRenderer`.
   - [ ] Decide which wrapper owns engine uniforms, material parameters,
         textures, shadow source material binding, and custom setting hooks.
   - [ ] Remove duplicated or divergent descriptor-resolution rules between
         `VkMaterial` and `VkMeshRenderer.Descriptors`.
   - [ ] Document the final binding flow in `vulkan-renderer.md` or a material
         binding policy doc.

2. Match OpenGL texture binding semantics.
   - [ ] Resolve textures by shader sampler name before numeric binding index.
   - [ ] Support `XRTexture.ResolveSamplerName(...)` and indexed `TextureN`
         aliases consistently.
   - [ ] Keep null texture slots stable so material texture index and descriptor
         index do not shift.
   - [ ] Fold program-bound samplers into descriptor resource fingerprints.
   - [ ] Keep bindless material array behavior explicitly separate from classic
         sampler-name binding.

3. Port shadow binding plans.
   - [ ] Support `ShadowBindingSourceMaterial` for shadow-pass parameters.
   - [ ] Support `ShadowBindingSourceMaterial` for shadow-pass textures.
   - [ ] Match OpenGL's active-uniform filtering so only parameters/textures
         required by the shadow program are bound.
   - [ ] Invalidate cached plans when source material parameters, textures, or
         active program bindings change.

4. Port shadow/custom material hooks.
   - [ ] Call `ShadowUniformSourceMaterial.OnSettingShadowUniforms` when set.
   - [ ] Call `ShadowBindingSourceMaterial.OnSettingShadowUniforms` when it has
         shadow handlers.
   - [ ] Fall back to `OnSettingUniforms` only when OpenGL would do the same.
   - [ ] Call `Data.OnSettingShadowUniforms` during shadow passes when the
         material itself supplies shadow handlers.
   - [ ] Apply scoped program bindings after hooks if Vulkan keeps an
         equivalent program-level hook.

5. Match render option handling.
   - [ ] Ensure material `RenderOptions` affect Vulkan pipeline state and
         dynamic state exactly once per draw.
   - [ ] Verify local render-options overrides take priority over material
         options like OpenGL.
   - [ ] Include render-option state in pipeline keys when it affects immutable
         Vulkan pipeline state.

6. Expand supported material parameter types.
   - [ ] Audit `ShaderVar.SetUniform` coverage in OpenGL.
   - [ ] Add Vulkan serialization for every shader var type that OpenGL can
         bind and that Vulkan shaders can legally consume.
   - [ ] Include arrays, non-`mat4` matrices, booleans, integer vectors,
         unsigned vectors, and future struct/block-backed parameters.
   - [ ] Align std140/std430 layout packing with the reflected descriptor block
         layout instead of hardcoding only type sizes.

7. Match engine uniform behavior.
   - [ ] Compare OpenGL `SetEngineUniforms` requirements with Vulkan auto
         uniform block generation.
   - [ ] Bind camera, stereo, lighting, AO, viewport, render-time, and material
         required engine uniforms through one consistent Vulkan path.
   - [ ] Ensure material required engine uniforms are honored even when the
         program does not expose a reflected auto-uniform block.

8. Match finalization diagnostics.
   - [ ] Add Vulkan equivalent of fallback sampler binding diagnostics.
   - [ ] Warn when a material program has no parameter or sampler bindings after
         descriptor resolution.
   - [ ] Add rate-limited texture-risk diagnostics equivalent to OpenGL's
         runtime material texture checks.

## Validation

- [ ] Source test: sampler-name texture binding wins over numeric texture slot.
- [ ] Source test: `TextureN` aliases resolve consistently for OpenGL and
      Vulkan.
- [ ] Source test: shadow binding source material supplies parameters and
      textures for a shadow program.
- [ ] Source test: unsupported material parameter types produce diagnostics,
      not silent zeros.
- [ ] Hardware: compare ordinary, shadow, depth-normal, FBO/post, and
      bindless-material paths against OpenGL.
