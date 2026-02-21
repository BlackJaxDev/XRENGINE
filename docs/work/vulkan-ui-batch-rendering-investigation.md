# Vulkan UI Batch Rendering Investigation

**Date:** 2026-02-20  
**Status:** In Progress - runtime traces captured, UI batch draw path confirmed active, remaining blockers narrowed

---

## Problem Statement

Native UI GPU-batched rendering for **UI backgrounds** (`UIMaterialComponent`) and **text glyphs** (`UITextComponent`) does not produce visible output when rendering with the Vulkan backend. The same UI elements appear correctly in OpenGL. The user suspects issues with SSBOs, multi-draw calls, or instancing.

---

## Architecture Overview

### UIBatchCollector

`UIBatchCollector` (`XRENGINE/Rendering/UI/UIBatchCollector.cs`, 688 lines) implements a double-buffered batch collection system. It collects material quads and text glyphs from the scene graph, uploads per-instance data into SSBOs, and issues instanced draw calls.

- **Material Quads** use 3 SSBOs: `QuadTransformBuffer` (binding 0), `QuadColorBuffer` (binding 1), `QuadBoundsBuffer` (binding 2)
- **Text Glyphs** use 4 SSBOs: `GlyphTransformsBuffer` (binding 0), `GlyphTexCoordsBuffer` (binding 1), `TextInstanceBuffer` (binding 2), `GlyphTextIndexBuffer` (binding 3)
- Material settings: `RenderPass = TransparentForward`, `CullMode = None`, `DepthTest = Disabled`, `Blend = EnabledTransparent`
- Vertex shader auto-generation is disabled via `DisableShaderPipelines()`; custom shaders are used exclusively
- Rendering is triggered via `VPRC_RenderUIBatched` pipeline command

### Shaders

| Shader | Path |
|--------|------|
| UIQuadBatched.vs | `Build/CommonAssets/Shaders/Common/UIQuadBatched.vs` |
| UIQuadBatched.fs | `Build/CommonAssets/Shaders/Common/UIQuadBatched.fs` |
| UITextBatched.vs | `Build/CommonAssets/Shaders/Common/UITextBatched.vs` |
| UITextBatched.fs | `Build/CommonAssets/Shaders/Common/UITextBatched.fs` |

### Vulkan Descriptor System

- **4-tier descriptor set layout:** `DescriptorSetGlobals=0`, `DescriptorSetCompute=1`, `DescriptorSetMaterial=2`, `DescriptorSetPerPass=3`
- SSBOs default to `set = 0`
- Auto-uniform blocks (engine-injected UBOs wrapping standalone uniforms) placed at `set = 0, binding = 64`
- `VkMaterial.CanHandleProgramBindings()` has no case for `DescriptorType.StorageBuffer`, so SSBOs fall through to `VkMeshRenderer` descriptor path (expected)

### Shader Compilation Pipeline

1. `ExpandIncludes` - resolve `#include` directives
2. `ResolveSnippets` - expand `$snippet` macros
3. `NormalizeLegacyStereo` - stereo rendering compatibility
4. `Rewrite` (internally runs):
   - `ApplyVulkanSourceFixups()` - replaces `gl_InstanceID` -> `gl_InstanceIndex`, removes float suffixes
   - Auto-uniform processing - wraps standalone `uniform` declarations into a UBO at `set=0, binding=64`
   - `RewriteOpaqueUniformBindings()` - adds `set=2, binding=N` qualifiers to sampler uniforms

### Data Upload Path

SSBOs are created with `EBufferUsage.StreamDraw` and `Resizable = false`. In Vulkan, these use host-visible + host-coherent memory (not device-local). The upload flow:

1. `UIBatchCollector.UploadMaterialQuadData()` writes to `ClientSideSource` (CPU-side staging buffer)
2. Calls `PushData()` (full upload) or `PushSubData()` (partial update)
3. On first frame: `PushData()` fires `PushDataRequested` event, but `VkDataBuffer` may not be subscribed yet (no-op)
4. During `RecordDraw()` -> `EnsureBuffers()` -> each `VkDataBuffer` calls `Generate()` -> `PostGenerated()` -> `AllocateImmutable()` -> `PushData()`, which reads from the client-side source populated in step 1

### Command Buffer Recording

- `MeshDrawOp` is enqueued during pipeline execution via `EnqueueFrameOp()`
- Drained during command buffer recording with per-op try/catch that re-throws exceptions
- `ResetCommandBufferBindState()` called at the start of each frame
- Viewport and scissor set per draw call via `CmdSetViewport`/`CmdSetScissor`

### VkMeshRenderer Creation

- Eager at `BaseVersion.Generate()` time via `GenericRenderObject` constructor -> `GetWrappers()` -> `CreateObjectsForAllWindows()`
- `LinkData()` subscribes to `RenderRequested` and `Buffers.Changed`, then calls `CollectBuffers()`
- `CollectBuffers()` iterates both `Mesh.Buffers` and `MeshRenderer.Buffers`, converting to `VkDataBuffer` via `GenericToAPI<VkDataBuffer>()`

---

## Key Findings

### 1. OpenGL Batch Rendering Was Also Broken (Fixed)

`UIQuadBatched.vs` originally used `gl_InstanceIndex`, which is Vulkan-specific. In OpenGL, shaders failed with:

```text
gl_InstanceIndex requires "#extension GL_KHR_vulkan_glsl : enable"
```

This means visible UI in OpenGL was coming from CPU fallback (`MeshRenderCommands.RenderCPU(pass)`), not the native batch path.

**Fix applied:** Changed `gl_InstanceIndex` -> `gl_InstanceID` in `UIQuadBatched.vs`. Vulkan fixup pipeline converts `gl_InstanceID` back to `gl_InstanceIndex` for Vulkan compilation.

### 2. Per-Item Text Shader SSBOs Work in Vulkan

`Text.vs` (non-batched per-item text) uses `gl_InstanceID` plus SSBOs and renders in Vulkan. SSBO binding mechanism is functional in general.

### 3. Shader Compilation Failures Are Not Silent

Exceptions during shader compilation propagate and can fail frame-op recording. This helps explain hard rendering failures when specific shaders break.

### 4. Silent Failure Paths Exist in VkMeshRenderer Drawing

Some stages return `false` and skip draw submission without throwing:

| Step | Failure Mode | Consequence |
|------|-------------|-------------|
| `EnsurePipeline()` | try/catch + periodic log + `return false` | Draw skipped |
| `BindVertexBuffersForCurrentPipeline()` | `WarnOnce` + `return false` | Draw skipped |
| `BindDescriptorsIfAvailable()` | `WarnOnce` + `return false` | Draw skipped |

### 5. Fallback Descriptor Buffer Path Exists

`TryResolveFallbackDescriptorBuffer` can bind a placeholder zeroed buffer if SSBO resolution fails. This remains possible, but later findings lowered its likelihood as the primary issue.

### 6. `gl_PerVertex` Output Block Was Problematic

Batch shaders declared a `gl_PerVertex` block with `gl_ClipDistance[]` but never wrote clip distances. This was removed from both batch vertex shaders.

### 7. Explicit `set = 0` Added to Batch SSBOs

Added explicit `set = 0` to all SSBO layouts in batch vertex shaders to remove ambiguity in SPIR-V reflection/binding interpretation.

### 8. Auto-Uniform Rewrite Verified Correct

Pipeline rewrite verification confirmed:

- SSBO bindings preserved (`set=0`, expected bindings)
- Auto-uniforms wrapped correctly into UBO at `set=0, binding=64`
- `gl_InstanceID` -> `gl_InstanceIndex` Vulkan fixup working

### 9. Descriptor Set Layout Handles Mixed Types Correctly

Mixed storage buffers and uniform buffers in `set = 0` with non-overlapping bindings are valid and are handled by `BuildDescriptorLayoutsShared`.

### 10. Runtime Trace Confirms Batched UI Draw Submission in Vulkan

Recent retained Vulkan traces show repeated batched UI submissions and real indexed draw recording:

- Run `20260220_125224_41636`:
  - `log_vulkan`: `[UIBatch] RenderMaterialQuadBatch: pass=3, entries=169, capacity=256` (9 occurrences)
  - `log_rendering`: `CmdDrawIndexed(...)` (1549 occurrences)
- Run `20260220_125441_17944`:
  - `log_vulkan`: `[UIBatch] RenderMaterialQuadBatch: pass=3, entries=72, capacity=128` (3 occurrences)
  - `log_rendering`: `CmdDrawIndexed(...)` (11 occurrences)

Conclusion: native Vulkan UI batch draw submission is active; the path is not completely dead.

### 11. No Retained Evidence of Descriptor Fallback Binding for UIBatch

In retained runs, there are no `Using fallback descriptor buffer` messages and no `[WriteDesc] FAILED` / `[BufferResolve]` failures tied to the batched UI pass.

### 12. Persistent Warning: Invalid Render-Graph Pass Metadata

Retained Vulkan runs repeatedly report:

`'MeshDrawOp' emitted with invalid render-graph pass index (pass 4 is missing from metadata). Falling back to pass -1.`

Observed frequency:

- Run `20260220_125224_41636`: 42 occurrences
- Run `20260220_125441_17944`: 11 occurrences

This is now the highest-value runtime anomaly to fix.

---

## Changes Applied

### Shader Fixes

**UIQuadBatched.vs:**
- Added explicit `set = 0` to all 3 SSBO layout qualifiers
- Changed `gl_InstanceIndex` -> `gl_InstanceID` (Vulkan fixup handles conversion)
- Removed unused `out gl_PerVertex { ... }` block

**UITextBatched.vs:**
- Added explicit `set = 0` to all 4 SSBO layout qualifiers
- Removed unused `out gl_PerVertex { ... }` block

### Diagnostic Logging

**VkMeshRenderer.Drawing.cs:**
- Added `XRE_VK_TRACE_DRAW=1` environment variable support for all-draw tracing
- Renamed `[SwapDraw]` trace messages to `[DrawTrace]`
- Trace coverage includes: `EnsurePipeline`, `BindVertexBuffers`, `BindDescriptors`, `CmdDrawIndexed`

**VkMeshRenderer.Descriptors.cs:**
- Expanded `TryResolveBuffer` logging on resolution failures
- Added `WarnOnce` details per failed descriptor write (buffer and image)

**UIBatchCollector.cs:**
- Added Vulkan periodic logging in `RenderMaterialQuadBatch` for pass index, entry count, and capacity

### Build Status

`XREngine.csproj` and `XREngine.Editor.csproj` build with 0 errors.

---

## Remaining Hypotheses (Ordered by Likelihood)

### 1. Render-Graph Pass Metadata Bug (pass 4 missing)

The repeated pass-index fallback warning suggests frame-op pass metadata and emitted draw ops are out of sync.

Potential impact: pass ordering/barrier planning mismatch that can break transparent-pass behavior even while draw calls are submitted.

### 2. Non-Batched Transparent Shader Compatibility in Vulkan Profiles

`Build/CommonAssets/Shaders/Common/UnlitTexturedForward.fs` currently declares:

```glsl
out vec4 OutColor;
```

This is Vulkan-fragile and has previously caused SPIR-V compile errors (`location required`) in Vulkan profile runs that use video/web/viewport materials.

Potential impact: frame-op recording failures and swapchain recovery loops that mask UI batch visibility.

### 3. Render Pass / FBO Target Mismatch During UI Batch Pass

If UI draw ops target an unexpected FBO/render pass state, draws may execute but still be visually absent.

### 4. Camera / Projection Uniform State During `VPRC_RenderUIBatched`

Invalid camera/projection values at submission time can move batched UI geometry off-screen.

### 5. Descriptor Fallback Path (Lower Confidence)

Still possible but currently deprioritized due lack of fallback evidence in retained runs.

---

## Next Steps

1. Fix `Build/CommonAssets/Shaders/Common/UnlitTexturedForward.fs` for Vulkan compatibility (`layout(location=0) out vec4 OutColor;`) and re-run Vulkan profile with streaming/video materials active.
2. Trace emitter/mapping of `MeshDrawOp` pass index `4` vs render-graph metadata and fix pass-index mismatch.
3. Re-run with `XRE_VK_TRACE_DRAW=1` and confirm:
   - no `Frame op recording failed for MeshDrawOp`
   - no invalid pass-index fallback warnings
   - stable repeated `[UIBatch]` submissions with normal present/submit flow
4. Capture screencap after errors/warnings are removed to verify native UI text/background visibility.

---

## Reference: File Locations

| File | Purpose |
|------|---------|
| `XRENGINE/Rendering/UI/UIBatchCollector.cs` | Batch collection, SSBO creation, data upload, instanced draw |
| `Build/CommonAssets/Shaders/Common/UIQuadBatched.vs` | Vertex shader for batched material quads |
| `Build/CommonAssets/Shaders/Common/UIQuadBatched.fs` | Fragment shader for batched material quads |
| `Build/CommonAssets/Shaders/Common/UITextBatched.vs` | Vertex shader for batched text glyphs |
| `Build/CommonAssets/Shaders/Common/UITextBatched.fs` | Fragment shader for batched text glyphs |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Drawing.cs` | Draw command recording |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs` | Descriptor set allocation and writing |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs` | Graphics pipeline creation |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Buffers.cs` | Buffer collection and resolution |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Uniforms.cs` | Auto-uniform buffer management |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs` | Frame op enqueuing, PendingMeshDraw |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs` | Shader linkage, descriptor layout building |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs` | Shader compilation and binding extraction |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs` | GPU buffer allocation and data upload |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs` | Material descriptor handling |
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs` | Shader compilation pipeline, SPIR-V reflection |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | Command buffer recording, frame-op draining |
| `Build/CommonAssets/Shaders/Common/Text.vs` | Per-item text shader (working SSBO reference) |
