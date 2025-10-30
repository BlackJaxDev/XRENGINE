# GPU Render Pass Pipeline Overview

## Stage Sequence
- **PreRenderInitialize**: Allocates SSBOs and parameter buffers, loads the compute shaders required for the pass, and builds the shared `XRMeshRenderer` VAO bound to the scene atlas.
- **ResetCounters**: Dispatches `Compute/GPURenderResetCounters.comp` to clear `_culledCountBuffer`, `_drawCountBuffer`, overflow flags, and stats before work begins.
- **Cull / Passthrough Copy**: Currently uses `Compute/GPURenderCopyCommands.comp` to copy `GPUScene.CommandsInputBuffer` into `CulledSceneToRenderBuffer`, mirroring the command count into `_culledCountBuffer`.
- **PopulateMaterialIDs**: Streams each command's `MaterialID` into `_materialIDsBuffer` to support batching.
- **BuildIndirectCommandBuffer**: Dispatches `Compute/GPURenderIndirect.comp`, converting culled commands and `MeshDataBuffer` entries into `DrawElementsIndirectCommand`s inside `_indirectDrawBuffer` while atomically incrementing `_drawCountBuffer` and updating overflow/stat flags.
- **HybridRenderingManager.Render**: Chooses or builds graphics programs per material, binds the shared VAO, applies engine/material uniforms, and issues `glMultiDrawElementsIndirect` (or the count variant) with `_indirectDrawBuffer` and `_drawCountBuffer`.

## Shaders Utilized
- `Compute/GPURenderResetCounters.comp` – clears counters, overflow flags, and stats each frame.
- `Compute/GPURenderCopyCommands.comp` – current culling path (full copy + visible-count write).
- `Compute/GPURenderIndirect.comp` – generates indirect draw commands and updates draw counts.
- Additional compute stages (`GPURenderCulling.comp`, `GPURenderExtractSoA.comp`, `GPURenderCullingSoA.comp`) load during initialization but remain disabled until true GPU culling/SoA paths are enabled.
- Graphics shaders are supplied per `XRMaterial` (e.g., red box raster shader, skybox shader) and combined by `HybridRenderingManager`.

## Primary Components
- **GPUScene** – Maintains the command buffer, mesh/material ID maps, and mesh atlas buffers consumed by the GPU pipeline.
- **GPURenderPassCollection** – Coordinates buffer lifecycle, culling, indirect command generation, and batching.
- **HybridRenderingManager** – Binds material programs, configures the shared VAO, and dispatches indirect draws (batched or monolithic).

## Potential Issues / Watch Points
- `BuildMaterialBatches` now groups contiguous commands per material and logs the batch summary each frame. Keep an eye on those logs when introducing new materials.
- The passthrough copy shader clamps the reported culled count to the actual copy count. If you still observe zero draws, inspect the copy shader warnings for mismatched counts.
- Graphics programs must include at least one vertex and fragment shader. Missing stages trigger warnings and abort indirect draws.
- The indirect renderer now rebuilds atlas buffers before binding and warns if no index buffer is available; investigate any such warning immediately.
- GPU dispatch sizing uses `VisibleCommandCount`, so verify that value is non-zero before expecting any draw output.
- Overflow and stats buffers are polled after each render. Treat any logged non-zero values as actionable GPU-side issues.
