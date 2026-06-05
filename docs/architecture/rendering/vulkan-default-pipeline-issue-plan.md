# Vulkan Default Pipeline Issue Plan

Status: **fixes #1 and #2 implemented** (2026-06; builds clean). Symptoms were confirmed against the current build by direct observation. Investigation ruled out #4 (matrices) as a bug — both OpenGL and Vulkan upload identical raw row-major bytes (System.Numerics row-major + `transpose = false`), so no matrix change was made. #3 readback was already resolved. #5 debug primitives are to be re-evaluated after retesting with #1/#2 in place.

## Fixes Applied

- **#1 Vertex input by semantic name** (fixes exploded gizmo / over-scaled debug primitives):
  - `VkShader.VertexInputLocations` parses `layout(location = N) in <type> <name>;` from the vertex shader source (cached, reset on recompile).
  - `VkRenderProgram.TryGetVertexInputLocation` / `HasReflectedVertexInputs` expose the map.
  - `VkMeshRenderer.Pipeline.BuildVertexInputState` now resolves each attribute's location by name (override → reflection → legacy sequential fallback when no reflected inputs), mirroring the OpenGL by-name binding path. Previously it used buffer-cache enumeration order, binding the wrong vertex stream to each location.
- **#2 Image descriptors by sampler name** (fixes magenta `DefaultRenderPipeline` background):
  - `VkRenderProgram` now tracks `_samplersByName` and exposes `TryGetSamplerTexture` + `AddSamplerResourceFingerprint`.
  - `VkMeshRenderer.Descriptors.TryResolveImage` prefers the texture bound to the shader sampler uniform `binding.Name`, then falls back to the material-texture binding index, then the placeholder. Named samplers are folded into the descriptor resource fingerprint so sets rewrite on FBO target swap/resize.

The remainder of this document is the original audit/analysis that drove these fixes.

---

Status (original): analysis and proposed fixes. No implementation has been done yet. Symptoms are confirmed against the current build by direct observation. Priority order (highest first): #4 matrices/`VRMode`, #1 vertex input, #2 sampler/image descriptor resolution, then #5 debug primitive packing. #3 readback is resolved (verify-only) and #6 shader cache is hygiene-only.

## Source Run

Most recent Vulkan run (current behavior, post-dates earlier readback work):

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-04_18-02-00_pid12048/log_vulkan.log`

The originally cited run (`xrengine_2026-06-04_16-48-19_pid12752`) has been rotated out, but this audit remains valid: none of the proposed rendering fixes (#1, #2, #4, #5) have been implemented, and the symptoms below are confirmed by direct observation of the current build.

## Confirmed Visual Symptoms (Direct Observation)

These are observed on the current build, not inferred from logs:

- `DefaultRenderPipeline` presents a **magenta** background.
- `DebugOpaqueRenderPipeline` presents a **black** background instead of magenta.
- The **transform tool 3D gizmo renders exploded about the origin**, as if the projection matrix, the view matrix, or both are wrong / not applied.
- **Instanced debug points** render scaled way up and extend past their endpoints on screen, with a gradient fade on the ends that does not make sense.
- **Instanced debug lines** render scaled way up and extend past their endpoints, similar to the points.
- The **overdraw visualization** appears to render only transparent forward meshes correctly; opaque content is missing/incorrect.
- Native FPS/debug text is still missing.

### Symptom-to-Suspect Mapping

The magenta-vs-black split is the strongest single clue:

- `DefaultRenderPipeline` runs FBO/post/composite fullscreen passes that sample render-target textures **by sampler name** (`program.Sampler(name, texture, unit)`). If Vulkan resolves those samplers by numeric descriptor binding instead of by name, the composite/blit quad samples the **1×1 magenta placeholder**, producing a magenta screen. This directly implicates **Suspect #2**.
- `DebugOpaqueRenderPipeline` is a simpler path that clears to black and draws closer to the swapchain without the same name-bound FBO sampling, so it shows black (the clear color) rather than magenta. This is consistent with #2 being the cause of the magenta rather than a missing draw.
- The **gizmo exploding about the origin** points at the vertex transform reaching clip space incorrectly: either wrong auto-uniform matrices / `VRMode` (**Suspect #4**) or position data being fed from the wrong vertex stream due to enumeration-order vertex input (**Suspect #1**). Both must be distinguished with the #4 diagnostics before committing a fix.
- The **debug points/lines scaled up, overshooting endpoints, with nonsensical gradient fade** is consistent with either bad clip-space transform (#4) or misread instanced SSBO / geometry-expansion data (**Suspect #5**). The gradient fade specifically suggests the expansion endpoints or per-vertex interpolants are being driven by wrong data.
- The **overdraw vis only working for transparent forward meshes** suggests the opaque/deferred submission path is where the vertex-input or matrix problem bites hardest, while the forward-transparent path (different shader/attribute wiring) survives.

Because the gizmo explosion implicates matrices, **Suspect #4 is promoted from a diagnostic-only check to a primary suspect** and should be instrumented before #1, since a wrong shared matrix would explain the gizmo, debug primitives, and opaque meshes all at once with a single root cause.

## Log Findings

Note: in the current run (`xrengine_2026-06-04_18-02-00_pid12048`) the `vkInvalidateMappedMemoryRanges` allocation-bound validation errors **no longer appear** (0 occurrences). The readback clamping in `NormalizeMappedMemoryRange` / `TryMapReadbackMemory` (in `VkDataBuffer`) now bounds the invalidate range to the tracked allocation. Suspect #3 below is therefore **verify-only** and should not block the rendering fixes; the magenta background and exploded geometry are unrelated to readback.

Historically the Vulkan log contained repeated validation errors from depth readback:

- `vkInvalidateMappedMemoryRanges(): pMemoryRanges[0] size (64) + offset (0) exceed the Memory Object's upper-bound (4)`
- Later reports also show invalidation sizes like `16` against a 4 byte memory object.
- The stack goes through `VulkanRenderer.InvalidateBuffer`, `TryMapReadbackMemory`, `GetDepth`, `XRViewport.GetDepth`, and `EditorFlyingCameraPawnComponent.PostRender`.

The current run also shows:

- The skybox pipeline is being created, so absence or corruption of the skybox is likely downstream of bad descriptors, bad vertex input, or bad uniform state rather than the draw being completely absent.
- Repeated fallback descriptor buffer warnings, but only for unresolved `LightProbe*` storage buffers (`LightProbePositions`, `LightProbeTetrahedra`, `LightProbeParameters`, `LightProbeGridCells`, `LightProbeGridIndices`) in deferred lighting. These should be reduced, but they do not explain the magenta default-pipeline background.
- No `magenta`/`placeholder` image-descriptor fallback is logged for the composite/post samplers. This is expected: the by-binding image resolution in #2 silently returns the magenta placeholder via `GetPlaceholderImageInfo` and only records a fallback counter, so the magenta path can fire without an obvious per-frame log line. Adding the #2 diagnostic is required to make it visible.
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

### 3. Readback Memory Invalidation Overruns Small Allocations (Verify-Only)

Status: the current run no longer emits the `vkInvalidateMappedMemoryRanges` allocation-bound validation errors. `NormalizeMappedMemoryRange` and `TryMapReadbackMemory` in `VkDataBuffer` already clamp the invalidate/map range to the tracked allocation bounds. This is **not** a cause of the magenta background or exploded geometry. Keep it as a regression check only.

Historical context (the validation errors showed Vulkan invalidating a non-coherent mapped memory range larger than the memory allocation). The clamping that addresses it is already present:

- `NormalizeMappedMemoryRange` clamps `flushEnd` to `allocationEnd` for tracked allocations.
- `TryMapReadbackMemory` clamps `mappedLength` to the available allocation length and warns once via `Vulkan.Readback.ClampMappedRange`.

Remaining verify-only work:

- Confirm readback staging allocations are reliably tracked before `InvalidateBuffer` runs (warn if a mapped range cannot be associated with a tracked allocation).
- Watch for the `Vulkan.Readback.ClampMappedRange` warning during editor-camera depth probing; persistent clamping there indicates a sizing bug upstream.

Expected impact:

- No rendering impact. Confirms the readback path stays clean while the rendering fixes land.
- Does not affect OpenGL.

### 4. Projection/View/VRMode Sanity Check (PRIMARY — promoted)

Promoted to a primary suspect by the current observation that the **transform gizmo renders exploded about the origin**. That pattern means vertex positions are not reaching clip space correctly: positions collapse toward / radiate from the origin as if the projection matrix, the view matrix, or both are wrong, missing, or applied in the wrong space. A single wrong shared matrix would simultaneously explain the gizmo, the over-scaled debug points/lines, and the missing opaque content in the overdraw view.

One specific risk is the generated vertex shader's `VRMode` branch. If Vulkan writes `VRMode` incorrectly for a non-stereo editor pass, the shader can output model/world space instead of projected clip space, which would explode geometry about the origin exactly as observed.

Proposed fix/check (do this first, before the vertex-input change, because it can be the single root cause):

- Add temporary Vulkan diagnostics that dump, for a known gizmo/debug draw, the auto-uniform values used by generated mesh shaders: `VRMode`, `ModelMatrix`, `ViewMatrix_VTX`, and `ProjMatrix_VTX`.
- Verify `VRMode == false` for non-stereo editor rendering, and that the stereo matrix arrays are not being indexed when `VRMode` is false.
- Compare the dumped `ViewMatrix_VTX`/`ProjMatrix_VTX` against the OpenGL values for the same camera. If they match OpenGL but geometry still explodes, the fault is vertex input (#1) or row/column-major upload, not the matrix values.
- Check the matrix upload path for the generated mesh uniform block: confirm column/row-major layout and that the dynamic uniform ring buffer offset for `ModelMatrix`/view/proj is correct (a wrong offset would feed garbage matrices and explode geometry about the origin).
- Do not apply a broad matrix transpose change unless the diagnostic proves the matrix layout is wrong. That would be high risk because OpenGL currently renders correctly.

Expected impact:

- Distinguishes "matrices/uniform state wrong" from "vertex input wrong" before any structural change.
- If matrices are the cause, fixing this single path likely resolves the gizmo, debug primitives, and opaque overdraw together.
- Keeps the fix targeted instead of guessing at shared math conventions.

### 5. Debug Lines, Points, And Triangles

The debug primitive symptoms — points and lines scaled way up, overshooting their endpoints on screen, with a gradient fade on the ends that does not make sense — can come from either bad projection/uniform state (#4) or bad SSBO/geometry-shader data interpretation. The over-scaling and endpoint overshoot are consistent with a wrong clip-space transform; the nonsensical end gradient is consistent with the expansion endpoints or per-vertex interpolants being driven by misread instanced data.

Proposed sequence:

- First confirm/fix the matrix-uniform path (#4) and semantic vertex locations (#1), and confirm sampler descriptor resolution (#2).
- Then retest debug primitives.
- If points still overshoot/scale wrong, inspect the instanced point SSBO struct layout (stride, field offsets, std140/std430 packing) and the geometry/expansion path; verify the point size/width is in the expected units and space.
- If lines still overshoot their endpoints, inspect line endpoint packing, width units, and Vulkan-only clip/depth conventions; verify the endpoint positions are not being expanded in NDC vs. world space inconsistently.
- Investigate the gradient-fade artifact specifically: confirm the per-end interpolant (alpha/color/distance) read by the fragment shader matches the SSBO layout Vulkan is actually binding.
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

1. Add or tighten Vulkan diagnostics for sampler/image fallback (#2), vertex input mappings (#1), and auto uniforms / `VRMode` / matrices (#4). These are cheap and convert the suspects from hypotheses to confirmed/ruled-out.
2. Inspect the #4 matrix/`VRMode` dump for a gizmo draw first. If matrices are wrong, fix that path — it can be the single root cause of the gizmo explosion, over-scaled debug primitives, and missing opaque overdraw.
3. Fix Vulkan vertex input semantic location mapping (#1) if the matrices are proven correct but geometry still explodes.
4. Fix Vulkan sampler/image descriptor resolution by name/unit and update descriptor fingerprints (#2) to remove the magenta default-pipeline background.
5. Retest default pipeline (no magenta), debug opaque pipeline, transform gizmo, debug primitives, skybox, overdraw vis, and native text.
6. Only then inspect debug primitive SSBO/shader packing (#5) if lines/points still overshoot.
7. Keep #3 (readback) and #6 (shader cache) as verify-only/hygiene checks; they are not blocking the visible rendering bugs.

## Validation Plan

Run:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanShader" --logger "console;verbosity=minimal"
```

Manual Vulkan validation:

- Launch editor in Vulkan with `DefaultRenderPipeline`.
- Confirm background is no longer magenta.
- Confirm `DebugOpaqueRenderPipeline` still presents a valid scene (was black background) and now shows correct opaque content.
- Confirm skybox appears when enabled.
- Confirm the transform gizmo no longer explodes about the origin.
- Confirm instanced debug points render at the correct scale without overshooting their endpoints or showing the nonsensical end gradient.
- Confirm instanced debug lines render as normal avatar skeleton/debug lines, not over-scaled wedges overshooting endpoints.
- Confirm the overdraw visualization renders opaque content correctly, not only transparent forward meshes.
- Confirm native FPS/debug text appears.
- Confirm the Vulkan log still contains no `vkInvalidateMappedMemoryRanges` allocation-bound errors (regression check for #3).
- Confirm magenta placeholder fallback diagnostics (once added per #2) do not fire for default-pipeline FBO/post-process samplers.

OpenGL regression check:

- Run the same editor scene in OpenGL.
- Confirm existing working model, debug line, debug point, skybox, ImGui, and native text behavior is unchanged.
