# GPU Record Layouts

Shader-visible advanced and GPU scene records are declared in C# with
`GpuRecordAttribute`. The editor generator writes their GLSL declarations to
`Build/CommonAssets/Shaders/Advanced/Generated/AdvancedRecords.glslinc` and
`GPUSceneRecords.glslinc`. Access helpers and shader sources include those files.
Dynamic material rows use the same GLSL struct writer with their packed members.

Regenerate the checked-in declarations from a source checkout with:

```powershell
dotnet run --project XREngine.Editor/XREngine.Editor.csproj -- --generate-gpu-record-includes <repo-root>
```

The command writes both includes, compiles validation shaders with the existing
Shaderc API at SPIR-V 1.3 for the installed SPIRVCross parser, and compares SPIR-V member names, byte
offsets, matrix strides, and record array strides with the C# fields. Shader
packaging runs the same check before building a common-assets archive; a
mismatch fails the package operation. In Debug editor builds, setting
`XRE_VALIDATE_GPU_RECORD_LAYOUTS=1` runs it at startup as well.

`Matrix4x4` uses `mat4` in row-major `std430` blocks. `Vector2` and `Vector4`
map to GLSL vectors; `ulong` maps to `uvec2`. `Vector3` fields require explicit
padding before they can be marked as GPU records. Nested handles use the
generated GLSL handle type. Vulkan indirect command structures remain governed
by the Vulkan-defined ABI checks.

The common shader resolver tracks included files as dependencies, so an edit
to either generated include invalidates affected shader source caches on reload.
OpenGL and Vulkan consumers load the same declarations and `std430` record
shapes; backend-specific bindings are declared separately.

The generated GPU-scene include currently covers `DrawMetadata`,
`TransformGpu`, `BoundsGpu`, `MaterialStateGpu`, `MeshDataEntry`,
`LODTableEntry`, `GPULodTransitionState`, `GpuMeshletRange`,
`GpuMeshletDescriptor`, `GpuMeshletTaskRecord`, `GPUSortKeyEntry`,
`GPUBatchRangeEntry`, `GPUViewBatchClassification`, `GPUViewDescriptor`,
`GPUViewConstants`, `GPUDrivenBoneMappingData`, and `GPUTextureHandleEntry`.
These C# records live in the rendering commands, GPU-scene resources,
physics-chain dispatch, and material texture table. The last two GLSL names
are `BoneMappingData` and `TextureHandleEntry`. Bone mapping explicitly pads
its `Vector3` to a 16-byte std430 slot. Shader-local storage bindings vary by pass; the view
descriptor and constants arrays use bindings 11 and 12. The constants blocks
explicitly use `row_major` for their matrices. Some passes intentionally
read transform, bounds, and view-mask storage as flat scalar/vector arrays;
those access forms require separate pass-level checks. The former
`GPUSceneLayoutContract` constants are now C# size self-checks.

The advanced include covers the handle, lookup, remap, buffer and texture
reference types plus the draw, instance, geometry, transform, deformation,
render-state, editor-identity, material, material-layout, material-texture,
shading-kernel, view, light, shadow, probe, environment, decal, GI, texture,
sampler, and encoded-reference records. It also covers
`AdvancedSkinInfluence`, `AdvancedSpillInfluence`,
`AdvancedActiveBlendshape`, `AdvancedBlendshapeRange`, and
`AdvancedBlendshapeSparseRecord` for aggregate deformation. Bound tables are declared in the
`Advanced/Access` includes. Their binding names are centralized in
`AdvancedGlobalResourceBindings`; nested references have no independent
binding. Both backends use `XR_ADV_TABLE_LAYOUT` with `std430, row_major`.
`AdvancedShaderRecordLayout` now checks C# interop sizes without duplicating
shader offsets.

Other shader schemas remain distinct from these generated record groups:
dynamic material rows are packed by `MaterialBindingLayout` and
`GPUMaterialTable`; packed deformation vertices and jobs, other physics-chain
buffers, and legacy shader snippets still need a binding-by-binding audit.
Vulkan indirect draw commands use the Vulkan ABI rather than a shader record
block. Pass-level validation of production shader permutations remains open
in the runtime-data-layout TODO.
