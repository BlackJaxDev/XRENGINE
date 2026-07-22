# GPU BVH Math Preview Missing Nodes

## Problem

In the Math Intersections Unit Testing World, GPU Scene BVH showed only the 75
source AABBs and GPU Mesh BVH showed only the wavy grid mesh. Neither preview
rendered the GPU-resident hierarchy comparable to its CPU counterpart.

## Issues Found

- Both components reported ready, passing workloads with the expected topology
  counts: 75 scene nodes and 399 mesh nodes.
- The original queued GPU workloads ran without a valid render-graph pass, so
  Vulkan rejected their BVH build/refit dispatches.
- After correcting workload admission and renderer preparation, Vulkan frame-op
  tracing proved the hierarchy dispatch and draw were valid (the mesh case drew
  4,788 line instances), but they were appended after
  `RenderToWindow_TsrOutputTexture`.
- Those lines targeted `ForwardPassFBO` after the final viewport copy. The next
  frame cleared that target, so they could never appear onscreen.
- The same direct renderer API was used by the Math test, the ImGui model BVH
  preview, and the legacy model preview component.

## Solution

- Keep GPU BVH workload execution in the component render callback under a valid
  pass identity, independently of whether debug visualization is enabled.
- Queue GPU BVH overlay requests per pipeline and deduplicate them by renderer.
- Double-buffer reusable request lists so steady-state rendering does not
  allocate in the per-frame path.
- Drain requests from `VPRC_RenderDebugShapes` inside the real
  `LateDebugOverlay`, before post-processing and the final viewport copy.
- Route the Math test and both model-preview entry points through this queue.
- Prepare the generated line renderer before dispatch/draw and give the compute
  program the diagnostic name `GpuBvhDebugLines`.

## Validation Evidence

Baseline captures:

- `Build/_AgentValidation/20260721-gpu-bvh-preview/mcp-captures/Screenshot_20260721_201141_696_7266a17eb61f42069118c31d8d085641.png`
- `Build/_AgentValidation/20260721-gpu-bvh-preview/mcp-captures/Screenshot_20260721_201244_558_019fcb87ba0449f88c8b469fe5aabb26.png`

Validation completed:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`:
  succeeded with 0 warnings and 0 errors.
- Targeted `GpuMeshBvhPreviewContractTests`: 6 passed, 0 failed.
- A fresh Vulkan GPU Scene capture was produced at
  `Build/_AgentValidation/20260721-gpu-bvh-preview/mcp-captures/Screenshot_20260721_220444_642_60dc8b3e935c456fb365616f6a217adc.png`.

Visual inspection of that final capture and a corresponding GPU Mesh capture
were interrupted when the user asked to wrap up. They remain the next validation
step; the code is build- and test-clean.

RenderDoc tooling passed `rdc doctor`, but the apphost capture loaded a
different desktop-mode scene and the OpenXR launch lacked an available form
factor. Vulkan frame-op tracing supplied the decisive pass-order evidence
instead.

## User Confirmation

Pending.
