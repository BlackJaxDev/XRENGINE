# Vulkan Default Pipeline Issue Plan

Status: analysis and proposed fixes only. No implementation is included in this note.

## Source Run

Use the last Vulkan run, not the later OpenGL comparison run:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-04_16-48-19_pid12752/log_vulkan.log`

Observed symptoms:

- `DefaultRenderPipeline` presents a magenta background even when scene content is toggled off.
- `DebugOpaqueRenderPipeline` presents black instead of magenta, but transform gizmo geometry, debug lines, and debug points are still exploded or malformed.
- Instanced debug points look like partially correct quads: one triangle/half appears plausible while the other half has incorrect color or shape.
- Instanced debug lines are much too wide/long and appear to radiate from a center point.
- The editor UI continues responding while the rendered scene can stall or stop updating.
- Native FPS/debug text is still missing.

## Log Findings

The Vulkan log contains repeated validation errors from depth readback:

- `vkInvalidateMappedMemoryRanges(): pMemoryRanges[0] size (64) + offset (0) exceed the Memory Object's upper-bound (4)`
- Later reports also show invalidation sizes like `16` against a 4 byte memory object.
- The stack goes through `VulkanRenderer.InvalidateBuffer`, `TryMapReadbackMemory`, `GetDepth`, `XRViewport.GetDepth`, and `EditorFlyingCameraPawnComponent.PostRender`.

This is a concrete Vulkan bug and likely contributes to stalls/freezes when editor camera depth probing runs.

The log also shows:

- The skybox pipeline is being created, so absence or corruption of the skybox is likely downstream of bad descriptors, bad vertex input, or bad uniform state rather than the draw being completely absent.
- Repeated fallback descriptor buffer warnings for unresolved storage buffers in deferred lighting. These should be reduced, but they do not directly explain the magenta default-pipeline background.
- A text pipeline validation warning: vertex attribute location `2` is not consumed by the vertex shader. This may be harmless, but should be checked after the vertex-input fix because missing native text can also come from mismatched attributes/descriptors.

## Primary Suspects

### 1. Vulkan Vertex Input Location Mapping

OpenGL binds vertex attributes by shader attribute name. Vulkan currently builds vertex input descriptions mostly by buffer enumeration order in `VkMeshRenderer.Pipeline.cs`.

That is fragile because generated mesh shaders expect a semantic order:

1. `Position`
2. `Normal`
3. `Tangent`
4. optional skinning attributes
5. texcoords
6. colors
7. optional blendshape count

But mesh buffer initialization can store color streams before texcoord streams. OpenGL survives this because it asks the program for each attribute location by name. Vulkan can bind the wrong data to a valid location.

Proposed fix:

- Make Vulkan resolve vertex input locations by semantic attribute name for known mesh attributes.
- Preserve explicit `AttribIndexOverride` and `BindingIndexOverride`.
- Keep a safe fallback for custom/non-mesh attributes, but prevent fallback locations from colliding with resolved semantic locations.
- Do not change OpenGL code or shared shader declarations for this fix.

Expected impact:

- Fixes texture/color stream swaps.
- May fix or reduce exploded mesh/gizmo/debug geometry if incorrect locations are feeding position-like data into shaders.
- Keeps existing OpenGL behavior intact.

### 2. Image Descriptor Resolution Uses Descriptor Binding As Texture Slot

The magenta background is likely the Vulkan placeholder texture. Vulkan uses a 1x1 magenta placeholder when an image descriptor cannot be resolved.

Current Vulkan descriptor paths resolve material textures by numeric descriptor binding, for example:

`material.Textures[(int)binding.Binding + arrayIndex]`

That is not robust after SPIR-V reflection/rewriting. Descriptor binding numbers are shader bindings, not material texture slots. This is especially risky for default-pipeline fullscreen/FBO passes where textures are bound by sampler name through `program.Sampler(name, texture, unit)`.

Proposed fix:

- Track samplers in `VkRenderProgram` by name as well as by texture unit.
- Descriptor resolution should prefer:
  1. exact sampler name from `DescriptorBindingInfo.Name`
  2. texture unit fallback
  3. material texture with matching `SamplerName` or texture name
  4. old numeric index fallback
  5. magenta placeholder with a clear diagnostic
- Include program-bound sampler resources in descriptor resource fingerprints so descriptor sets are rewritten when FBO textures/views/samplers change.
- Preserve bindless array handling by array index.

Expected impact:

- Default pipeline post/FBO quads should sample the intended render targets instead of the magenta placeholder.
- Skybox/default background should no longer be hidden by a magenta fullscreen fallback.
- OpenGL is unaffected because it already binds samplers by name.

### 3. Readback Memory Invalidation Overruns Small Allocations

The validation errors show Vulkan invalidating a non-coherent mapped memory range larger than the memory allocation.

Proposed fix:

- Clamp `NormalizeMappedMemoryRange` to the tracked allocation bounds for buffers and images.
- Make readback staging allocations reliably tracked before `TryMapReadbackMemory` calls `InvalidateBuffer`.
- For tiny readback allocations, avoid expanding an invalidate range past the allocation end when non-coherent atom alignment is larger than the allocation.
- Add a diagnostic if a mapped range cannot be associated with a tracked allocation.

Expected impact:

- Removes the repeated validation errors.
- Reduces editor-camera depth readback stalls/freezes.
- Does not affect OpenGL.

### 4. Projection/View/VRMode Sanity Check

The screenshots look like positions may be reaching clip space incorrectly, or the shader may be taking a non-projected path.

One specific risk is the generated vertex shader's `VRMode` branch. If Vulkan writes `VRMode` incorrectly for a non-stereo editor pass, the shader can output model/world space instead of projected clip space.

Proposed fix/check:

- Add temporary Vulkan diagnostics for the auto-uniform values used by generated mesh shaders: `VRMode`, `ModelMatrix`, `ViewMatrix_VTX`, and `ProjMatrix_VTX`.
- Verify `VRMode == false` for non-stereo editor rendering.
- Do not apply a broad matrix transpose change unless the diagnostic proves the matrix layout is wrong. That would be high risk because OpenGL currently renders correctly.

Expected impact:

- Confirms whether the explosion is from uniform state or vertex input.
- Keeps the fix targeted instead of guessing at shared math conventions.

### 5. Debug Lines, Points, And Triangles

The debug primitive symptoms can come from either bad projection/uniform state or bad SSBO/geometry-shader data interpretation.

Proposed sequence:

- First fix semantic vertex locations, sampler descriptor resolution, and readback invalidation.
- Then retest debug primitives.
- If points still render as two differently colored/positioned triangles, inspect the instanced point SSBO struct layout and the geometry shader expansion path.
- If lines still explode, inspect line endpoint packing, width units, and Vulkan-only clip/depth conventions.
- Keep any shader-side changes behind `#ifdef XRENGINE_VULKAN` where behavior must diverge from OpenGL.

Expected impact:

- Avoids breaking the OpenGL debug shaders that already work.
- Narrows any remaining debug primitive work to Vulkan SSBO packing or Vulkan-specific clip conversion.

### 6. Shader Rewriter And Cache

The Vulkan path should not load OpenGL binary program caches. The more likely cache risk is that generated shader/SPIR-V cache keys do not include all Vulkan-specific inputs.

Proposed check/fix:

- Confirm Vulkan shader cache keys include backend, stage, source after Vulkan define injection, rewrite version, and relevant feature flags.
- Ensure `XRENGINE_VULKAN` define injection is included in the cached source identity.
- Add a diagnostic line when Vulkan loads a cached SPIR-V/pipeline entry so stale cache reuse is visible.
- Do not reuse OpenGL program binary cache artifacts in Vulkan.

Expected impact:

- Prevents stale GLSL rewrite output from masking real fixes.
- Makes future Vulkan shader-cache problems easier to identify in logs.

## Native FPS/Text Follow-Up

After the vertex input and descriptor changes:

- Recheck `UIBatchTextMaterial:UIBatchTextQuadMesh`.
- The current warning about vertex attribute location `2` not being consumed may be harmless, but if text is still missing, verify the text mesh's explicit `BindingIndexOverride` values match the text shader's declared locations.
- Check that the font atlas texture resolves by sampler name and does not hit the magenta placeholder path.

## Suggested Implementation Order

1. Add or tighten Vulkan diagnostics for sampler fallback, vertex input mappings, and auto uniforms.
2. Fix Vulkan vertex input semantic location mapping.
3. Fix Vulkan sampler/image descriptor resolution by name/unit and update descriptor fingerprints.
4. Fix mapped readback invalidation range clamping/tracking.
5. Retest default pipeline, debug opaque pipeline, debug primitives, skybox, and native text.
6. Only then inspect debug primitive SSBO/shader packing if lines/points remain wrong.

## Validation Plan

Run:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanShader" --logger "console;verbosity=minimal"
```

Manual Vulkan validation:

- Launch editor in Vulkan with `DefaultRenderPipeline`.
- Confirm background is no longer magenta when scene content is disabled.
- Confirm skybox appears when enabled.
- Confirm transform gizmo geometry no longer explodes.
- Confirm instanced debug points render as full points with consistent color.
- Confirm instanced debug lines render as normal avatar skeleton/debug lines, not radiating wedges.
- Confirm native FPS/debug text appears.
- Confirm the Vulkan log no longer contains `vkInvalidateMappedMemoryRanges` allocation-bound errors.
- Confirm magenta placeholder fallback diagnostics do not fire for default-pipeline FBO/post-process samplers.

OpenGL regression check:

- Run the same editor scene in OpenGL.
- Confirm existing working model, debug line, debug point, skybox, ImGui, and native text behavior is unchanged.
