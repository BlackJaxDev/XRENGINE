# Vulkan Pipeline — To-Fix List

> Re-assessed with logs: `20260213_155838_11808` (Feb 13, 2026)

## Critical (regressed / still failing)

- [ ] **Shader Compilation Failures (Regression)** — Still reproducing repeatedly.
  - `DeferredLightingDir` (`DeferredLightingDir:14`)
  - `PostProcess` (`PostProcess:8`)
  - `LitColoredForward` (`LitColoredForward:12`)
  - `LitTexturedForward` (`LitTexturedForward:10`)
  - `LitTexturedNormalForward` (`LitTexturedNormalForward:10`)
  - `LitTexturedNormalSpecAlphaForward` (`LitTexturedNormalSpecAlphaForward:11`)
  - `LitTexturedSpecAlphaForward` (`LitTexturedSpecAlphaForward:11`)
  - Note: these failures cascade into `Frame op recording failed for MeshDrawOp`.

- [ ] **Missing Descriptor Set Bindings (Regression)** — Still reproducing (`vkCmdDrawIndexed`: descriptor set `0`/`2` never bound; pipeline layout incompatible).

- [ ] **Missing Vertex Buffer Bindings (Regression)** — Still reproducing (`vkCmdDrawIndexed`: required vertex bindings `0`, `1`, `2` not bound).

- [ ] **Image Layout Violations (Regression)** — Still reproducing in two forms:
  - `vkCmdPipelineBarrier`: old layout mismatch (`COLOR_ATTACHMENT_OPTIMAL` vs known `SHADER_READ_ONLY_OPTIMAL`).
  - `vkQueueSubmit`: draw expects attachment/general/read-only layouts while current layout is `VK_IMAGE_LAYOUT_UNDEFINED`.

## High

- [ ] **Render-Graph Pass Index Invalid (Regression)** — Still reproducing every frame group (`Clear`, `MeshDraw`, `DispatchCompute`, `Blit` emitted with invalid sentinel pass index).

- [ ] **SPIR-V Interface Mismatch (Regression)** — Still reproducing (`vertex output location 21` not consumed by fragment stage).

- [ ] **Fragment Output Location Mismatches (Regression)** — Still reproducing (`fragment writes output locations 1/2/3` without corresponding subpass color attachments).

- [ ] **DepthView Missing `VK_IMAGE_USAGE_SAMPLED_BIT` (Regression)** — Still reproducing (`Skipping sampled descriptor bind for texture 'DepthView' ... VK_IMAGE_USAGE_SAMPLED_BIT is not set`).

- [ ] **Transfer Barrier Stage/Access Mismatch (Newly Visible in this run)** — Repeated `vkCmdPipelineBarrier` error where `dstAccessMask=VK_ACCESS_SHADER_READ_BIT` is incompatible with `dstStageMask=VK_PIPELINE_STAGE_TRANSFER_BIT`.

- [ ] **`vertexPipelineStoresAndAtomics` Not Enabled (New)** — Graphics pipeline creation fails because vertex-stage storage buffer usage is writable and not decorated `NonWritable`, while `vertexPipelineStoresAndAtomics` is disabled.

## Medium

- [ ] **Zero-Size Buffer Creation (Regression)** — Still observed: `System.ArgumentException: Buffer size must be greater than zero. (bufferSize)`.

- [ ] **Queue Ownership — Compute/Transfer Underutilized** — Still observed across snapshots: `mode=GraphicsOnly`, `useCompute=False`, `useTransfer=False`, often with `computePasses=2`.

- [x] **`fragmentStoresAndAtomics` Not Enabled** — No `fragmentStoresAndAtomics` validation errors observed in this log; keep as fixed for now.

## Low / Not observed in this log

- [x] **GPU Auto-Exposure Not Supported** — Implemented Vulkan GPU auto-exposure compute path (`SupportsGpuAutoExposure` + `UpdateAutoExposureGpu`) with 2D/2DArray metering support and on-GPU 1x1 exposure texture updates.

- [ ] **Octree Raycast Queue Saturation** — Not observed in this log.

- [ ] **Startup Job Starvation** — Not observed in this log.

## Investigate

- [ ] **Descriptor Type Conflicts** — Not explicitly observed in this run; descriptor-set bind failures dominate. Re-check after shader compile + descriptor bind regressions are fixed.

- [ ] **"Vulkan Fallback" Startup Path** — Not observed in this log; prior finding still needs dedicated startup trace confirmation.
