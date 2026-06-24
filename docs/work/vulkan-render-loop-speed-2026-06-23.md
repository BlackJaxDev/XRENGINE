# Vulkan Render Loop Speed Investigation - 2026-06-23

## Problem

Improve Vulkan render-loop CPU time in the editor and restore GPU timing samples for Vulkan commands when render stats and GPU pipeline profiling are enabled.

## Baseline Evidence

- Source run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-23_10-34-51_pid39624/`
- CPU frame dump: `profiler-cpu-frame-2026-06-23-10-39-09-596-9ba76c05.log`
- Frame dump summary:
  - TotalThreadMs: 81.743 ms.
  - Render thread latest: 42.063 ms, average 43.673 ms.
  - Collect-visible thread latest: 39.268 ms, average 37.703 ms.
  - `Vulkan.FrameLifecycle.RecordCommandBuffer`: 4.474-6.600 ms.
  - `Vulkan.RecordCommandBuffer.RefreshFrameData`: 3.588-5.620 ms.
  - `Vulkan.FrameLifecycle.WaitFrameSlot`: 2.064-2.384 ms.

## Findings

- Vulkan command timing was hard-disabled by `EnableVulkanGpuProfilerCommandBufferInstrumentation = false`, so the GPU timing panel could only report the quarantine status instead of command samples.
- Reusable command-buffer frame-data refresh sorted frame ops again even though callers already sorted and split them during command-buffer normalization.
- Vulkan timestamp scopes were rebuilt only while recording command buffers. If command timing is enabled, forcing a rerecord every profiled frame restores samples but makes profiling much more expensive than necessary.

## Attempted Solutions

### 1. Restore Vulkan command timing without forced per-frame rerecords

Implemented change:
- Enable Vulkan command-buffer timestamp instrumentation.
- Persist recorded timestamp scope metadata on each command-buffer cache variant.
- On clean command-buffer reuse, restore the saved scope metadata for the current submit so query samples can resolve for the reused command buffer.

### 2. Remove duplicate refresh sort

Implemented change:
- Stop sorting frame ops inside `TryRefreshReusableCommandBufferFrameData`; callers already provide normalized sorted ops.

### 3. Stabilize reusable Vulkan mesh draw descriptor state

Implemented changes:
- Cache the prepared `VkRenderProgram` and program identity in `PendingMeshDraw`, then use that captured program for full command-buffer recording and clean frame-data refresh.
- Include the captured program identity in frame-op signatures and command-chain structural signatures so command buffers recorded for one material/program variant are not refreshed as another.
- Capture program binding snapshots for Vulkan mesh draws that bind descriptor resources through the program, even when the mesh does not request full `CaptureUniformsOnRender`.
- Warm descriptor sets for the actual draw uniform slot during captured-program recording so draws that enqueue but do not reach `BindDescriptorsIfAvailable` still have reusable frame data.
- Remove `SkinnedOutputVersion` from descriptor/resource fingerprints; the buffer identity is stable and data updates inside the same buffer should not force descriptor-set or command-buffer churn.
- Add descriptor-allocation metadata so clean refresh can reuse the active descriptor allocation for snapshotted resources without recomputing the full descriptor resource fingerprint.

Rejected change:
- Tried an already-sorted fast path in `VulkanRenderGraphCompiler.SortFrameOps`; validation showed it was a loss when the op stream was not already sorted, so that patch was backed out.

## Validation

Run root: `Build/_AgentValidation/20260623-104523-vulkan-render-loop-speed/`

Build:
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`: succeeded with 0 warnings and 0 errors after stopping the task-owned editor process that was locking output DLLs.

Editor iteration:
- Launched unit-testing editor with MCP and Vulkan, enabled render statistics and GPU render-pipeline profiling, confirmed the profiler was unpaused.
- Captured viewport screenshot: `Build/_AgentValidation/20260623-104523-vulkan-render-loop-speed/mcp-captures/Screenshot_20260623_105158.png`.
- Fresh measured run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-23_11-00-26_pid43380/`.
- Mirrored key artifacts under `Build/_AgentValidation/20260623-104523-vulkan-render-loop-speed/logs/`.

GPU timing validation:
- `get_render_profiler_stats` reported `enabled=true`, `supported=true`, `timings_ready=true`, `backend=Vulkan`, and an empty status string.
- DefaultRenderPipeline GPU dump reported `backend: Vulkan`, `frames_tracked: 285`, `timer_samples_tracked: 19261`.
- The dump contains Vulkan command rows for `MeshDraw`, `ComputeDispatch`, `Blit`, and `Clear`, confirming command timings are populated again.
- DefaultRenderPipeline after first 50 frames: average 6.550 ms, p50 6.508 ms, p95 6.979 ms, max 7.354 ms.

CPU timing validation:
- Latest CPU frame dump: `profiler-cpu-frame-2026-06-23-11-00-48-201-393fa6fd.log`.
- TotalThreadMs dropped from 81.743 ms baseline to 66.102 ms in the sampled post-change dump.
- Render thread latest dropped from 42.063 ms to 34.893 ms.
- Collect-visible thread latest dropped from 39.268 ms to 30.696 ms.
- `Vulkan.FrameLifecycle.RecordCommandBuffer`: 3.159-3.732 ms in the sampled post-change dump.
- `Vulkan.RecordCommandBuffer.RefreshFrameData`: 2.440-2.949 ms in the sampled post-change dump.
- `Vulkan.FrameLifecycle.WaitFrameSlot`: 0.031-0.036 ms in the sampled post-change dump.

Frame-stats capture:
- `profiler-render-stats.ndjson` captured 1482 rows, 1399 after the 8-second steady-state cutoff.
- Steady-state command buffer counters: 1395 rows clean-reused command buffers with no records; 4 rows recorded command buffers; 0 rows had `profiler_dirty_count` nonzero.
- Steady-state Vulkan phase medians: frame total 5.713 ms, record command buffer 4.837 ms, sample timing queries 0.058 ms, wait fence 0.044 ms, submit 0.335 ms, present 0.075 ms.

Residual notes:
- `RenderThreadMinusPipeline` remains significant in the GPU dump. After first 50 frames, DefaultRenderPipeline render-thread average was 23.964 ms versus 6.550 ms GPU pipeline average, so further CPU wins likely live in render-thread scheduling/backpressure and render-command execution rather than GPU shader time.
- The launch-time profile capture produced `profiler-render-stats.ndjson` but did not leave a `profiler-capture-summary.json` in this run; the direct MCP CPU/GPU dumps and NDJSON were still available.

## Follow-up Validation - Descriptor Reuse Fix

Run roots:
- `Build/_AgentValidation/20260623-124629-vulkan-render-loop-speed-descriptor-snapshot/`
- `Build/_AgentValidation/20260623-125427-vulkan-render-loop-speed-refresh-fastpath/`

Build:
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`: succeeded with 0 warnings and 0 errors.

Key evidence:
- Before the descriptor snapshot fix, steady-state failed clean reuse with `frame-data: ... descriptors ... pool-miss`, typically on unnamed full-screen mesh/material quad draws in GTAO/light-combine style passes.
- The allocation comparison showed the same descriptor schema existed but the resource fingerprint never matched (`sameS > 0`, `sameR = 0`), which pointed to live program-bound resources changing between enqueue/record/refresh when no `ProgramBindingSnapshot` was present.
- After capturing descriptor-resource bindings, steady-state samples reported `cleanReuse=1`, `recordCount=0`, `record_allocated_bytes=0`, and empty dirty summaries.
- Representative clean samples:
  - `20260623-124629.../sample1..5`: `record_command_buffer_ms` approximately `3.0-3.6 ms`, no command-buffer records, no allocations, GPU dumps written for Vulkan pipelines.
  - Later run with material-binding diagnostics enabled was noisier (`record_command_buffer_ms` around `4.5-5.1 ms`) and occasionally caught forced startup/resource-growth frames, so use it mainly as correctness evidence, not a best-case timing number.
- GPU dump files now include `backend: Vulkan` and command rows such as `MeshDraw[...]` and `ComputeDispatch[...]`, confirming Vulkan command timings are tracked again.

Remaining CPU targets:
- Clean reuse still spends measurable CPU in `Vulkan.RecordCommandBuffer.RefreshFrameData`, `DrainFrameOps`, `NormalizeFrameOps`, and `ResourcePlan`.
- CPU frame dumps can catch a different frame than the immediate stats sample; when they disagree, command-buffer cache counters (`cleanReuse`, `recordCount`, dirty summary, allocated bytes) have been more reliable for reuse state.
- Next likely wins are reducing per-frame frame-op drain/sort/planner work on unchanged structural signatures and slimming/pooling program binding snapshots so descriptor correctness does not add avoidable allocation pressure.
