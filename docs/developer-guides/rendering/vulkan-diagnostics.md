# Vulkan Diagnostics

This guide tells you how to isolate Vulkan rendering failures. It lists the diagnostic environment variables, a black-frame triage procedure, GPU capture recipes, the pipeline prewarm workflow, and the lifecycle evidence collector.

Architecture: [Vulkan Renderer](../../architecture/rendering/vulkan-renderer.md), [Vulkan Pipeline Compilation](../../architecture/rendering/vulkan-pipeline-compilation.md).

The constants for all variables are in `XREngine.Data/Environment/XREngineEnvironmentVariables.cs`. Several variables also seed an editor preference (`EditorPreferences`) and a `RenderDiagnosticsFlags` value. The environment variable sets the start value for the process.

## Diagnostic Environment Variables

| Variable | Purpose |
|---|---|
| `XRE_VULKAN_VALIDATION=1` | Enables `VK_LAYER_KHRONOS_validation`. It is off by default because it increases primary recording cost. |
| `XRE_VULKAN_SYNC_VALIDATION=1` | Enables synchronization validation. Use it with `XRE_VULKAN_VALIDATION=1`. The engine also sets the layer setting `syncval_shader_accesses_heuristic`; see [Synchronization Validation Coverage](#synchronization-validation-coverage). |
| `XRE_VULKAN_SYNC_SHADER_ACCESSES=0` | Does not set `syncval_shader_accesses_heuristic`. Synchronization validation then checks only transfer, attachment and layout-transition accesses. |
| `XRE_VULKAN_GPU_ASSISTED_VALIDATION`, `XRE_VULKAN_BEST_PRACTICES` | Enable the GPU-assisted and best-practices validation layers. |
| `XRE_VULKAN_DIAGNOSTIC_PRESET`, `XRE_VULKAN_DIAGNOSTIC_FLAGS` | Select a diagnostic preset or explicit diagnostic flags. |
| `XRE_VULKAN_CRASH_BREADCRUMBS`, `XRE_VULKAN_DEVICE_FAULT` | Enable crash breadcrumbs and `VK_EXT_device_fault`/`VK_KHR_device_fault` reports on device loss. |
| `XRE_VULKAN_RENDERDOC_FRIENDLY` | Selects a capture-friendly device configuration. |
| `XRE_FORCE_SWAPCHAIN_MAGENTA=1` | Clears the swapchain to magenta after main composition. If magenta shows, presentation works and scene output is the suspect. |
| `XRE_DEBUG_RETRY_RECOVERY_CLEAR=1` | Shows a dark purple recovery clear, with only the editor overlay, for each retried desktop frame. By default a retried frame shows the last completed frame again. |
| `XRE_SKIP_IMGUI=1` | Removes the ImGui overlay from the frame. |
| `XRE_SKIP_UI_PIPELINE=1` | Removes screen-space UI pipeline operations from the frame. |
| `XRE_VK_TRACE_DRAW=1` | Logs every Vulkan draw, including FBO-targeted UI batches. Very heavy. |
| `XRE_VK_TRACE_SWAPDRAW=1` | Logs swapchain-targeted draws only. |
| `XRE_VK_TRACE_PIPECREATE=1` | Logs each graphics pipeline creation with stage and format details. |
| `XRE_VULKAN_RECORDING_DIAG` | Logs command-recording diagnostics. |
| `XRE_VULKAN_MATERIAL_BINDING_DIAG` | Logs material descriptor resolution and auto-uniform writes. |
| `XRE_VULKAN_DESCRIPTOR_TRACE`, `XRE_VULKAN_TARGET_TRACE`, `XRE_VULKAN_INDIRECT_TRACE` | Log descriptor, render-target, and indirect-draw activity. |
| `XRE_VULKAN_FRAMEOP_TRACE=1` | Retains frame-operation traces for the MCP `get_vulkan_frame_op_trace` tool. |
| `XRE_VULKAN_FINAL_PRESENT_LEDGER` | Enables the final-presentation ledger. |
| `XRE_VK_PIPELINE_PREWARM_CAPTURE=1` | Writes observed pipeline permutations to `%LOCALAPPDATA%\XREngine\Vulkan\PipelinePrewarm\prewarm_*.json` at shutdown. |
| `XRE_VK_RENDER_TARGET_MODE=Auto\|DynamicRendering\|LegacyRenderPass` | Overrides the persisted render-target mode for the process. |
| `XRE_VK_OBS_HOOK=Auto\|Disable\|Require` | Controls OBS Vulkan hook compatibility. See [Vulkan OBS Hook Compatibility](vulkan-obs-hook-compatibility.md). |
| `XRE_VK_CAPABILITY_TIER`, `XRE_VK_DESCRIPTOR_BACKEND`, `XRE_VK_PROGRAM_BINDING_BACKEND`, `XRE_VK_FOVEATION_BACKEND`, `XRE_VK_RAY_TRACING_BACKEND` | Request a capability tier or backend explicitly. An unsupported explicit request fails startup with a diagnostic. |
| `XRE_VK_DESCRIPTOR_SIGNATURE_EPOCH=0\|1\|witness` | Texture binding signature cache. `0` (default) keeps a signature for one render frame. `1` keeps it while the descriptor generation and the texture readiness epoch stay the same. `witness` also computes each signature again, uses the new value, and writes mismatches to `descriptor-signature-witness.log`. |
| `XRE_OPENXR_MESH_COHORT=0\|1\|witness` | OpenXR eye mesh-operation reuse. `1` (default) keeps the materialized mesh operations of each eye capture and, when the next frame's requests match, rebuilds them from the kept recipes with the current per-frame values. Each reuse reacquires the persistent binding artifact and requires the same object. `0` materializes every eye request each frame. `witness` materializes each reused request again, draws the fresh operation, and writes structural differences to `openxr-mesh-cohort-witness.log`. |
| `XRE_ADVANCED_TYPED_POSTPROCESS_PUBLISHERS=0` | Publishes the Advanced pipeline AO finalize, post-process and final post-process values with uniform callbacks instead of typed binding publishers. For A/B checks only. |
| `XRE_VULKAN_BINDLESS_MATERIAL_MODE` | Selects the bindless material mode. |

## Synchronization Validation Coverage

- The validation layer records the buffers and images that shaders access through descriptors only when its setting `syncval_shader_accesses_heuristic` is on. The layer default is off. When `XRE_VULKAN_SYNC_VALIDATION=1`, the engine turns the setting on through `VkLayerSettingsCreateInfoEXT` at instance creation. Without it, a run with no errors proves nothing about storage-buffer or storage-image barriers between draws and dispatches.
- The layer finds the accessed bindings by static analysis of the SPIR-V. It treats a binding that the shader can write as written in every dispatch, also when a runtime branch skips the write. Read such a report against the shader before you add a barrier.
- The layer does not check bindings that are arrays (descriptor count greater than 1), also with the setting on.
- The layer tracks whole image subresources and buffer ranges. It cannot see that dispatches write disjoint pixels or disjoint records. These reports in the Advanced native shading pass are known false positives:
  - `SYNC-HAZARD-WRITE-AFTER-WRITE` between the per-kernel indirect shading dispatches, on the HDR, velocity, reactive, shading diagnostics and DDGI output bindings. Each pixel belongs to one kernel, so the dispatches have no barrier between them. The DDGI bindings hold the HDR image when DDGI outputs are off, and a runtime flag stops the stores.
  - `SYNC-HAZARD-WRITE-AFTER-WRITE` between the overflow repair dispatch and the main kernels. The classification overflow flag lets only one of them write in a frame.
  - On OpenXR eye outputs without a shading debug view, the shading diagnostics binding (18) holds a dedicated 1x1 placeholder image (`AdvancedShading.ShadingDiagnosticsPlaceholder`), and the push flag `ShadingDiagnosticsDisabledFlag` stops every store to it. Earlier builds bound the metadata image there, which produced a `SYNC-HAZARD-READ-AFTER-WRITE` and a `SYNC-HAZARD-WRITE-AFTER-READ` (0x376BC9DF) on bindings 1 and 18. Those two reports must not appear any more; if they do, check that the placeholder was declared.
- The layer stops a message ID after `duplicate_message_limit` reports (default 10). The known false positive above uses up the limit for `SYNC-HAZARD-WRITE-AFTER-WRITE` in the first frame. To list every hazard site, raise the limit with `VK_KHRONOS_VALIDATION_DUPLICATE_MESSAGE_LIMIT` and group the messages by site.
- MCP `get_render_profiler_stats.vulkan.validation.lifetime_error_count` counts all validation errors of the process. `error_count` counts only the last frame.

## Swapchain And Overlay Isolation Flags

Use these flags when the final image is black, stale, or different from the scene target. They seed `EditorPreferences` and `RenderDiagnosticsFlags` at process start. Change the editor preference for live toggles after startup.

| Variable | Code owner | Behavior | Use when |
|---|---|---|---|
| `XRE_FORCE_SWAPCHAIN_MAGENTA=1` | `RenderDiagnosticsFlags.ForceSwapchainMagenta`, `VulkanRenderer.CommandBufferRecording.Primary.Finalization` | Clears the Vulkan swapchain to magenta after main composition. The log records the forced clear. | Confirm that acquire, final layout, submit, and present reach the swapchain. |
| `XRE_DEBUG_RETRY_RECOVERY_CLEAR=1` | `RenderDiagnosticsFlags.DebugRetryRecoveryClear`, `EditorPreferences.DebugRetryRecoveryClear`, `VulkanFrameLoop.ResolveRejectedDesktopRecoveryPolicy` | When a desktop frame must retry, the recovery frame clears to dark purple (0.06, 0.015, 0.08) and draws only the overlay. When it is off, the recovery frame shows the last completed scene again. When no valid completed scene exists, it clears to the startup presentation color instead of purple. | Count or see the retried desktop frames while you remove their causes. |
| `XRE_SKIP_IMGUI=1` | `RenderDiagnosticsFlags.SkipImGui`, `EditorPreferences.Vulkan.SkipImGui` | Skips Vulkan ImGui overlay recording. | Isolate overlay command recording from scene output faults. |
| `XRE_SKIP_UI_PIPELINE=1` | `RenderDiagnosticsFlags.SkipUiPipeline`, `EditorPreferences.Vulkan.SkipUiPipeline` | Skips screen-space UI pipeline operations on the Vulkan command buffer. | Isolate UI pipeline commands from scene and ImGui commands. |

Do not use these flags as a fallback for production rendering. Remove them before you compare performance or visual parity.

## Black-Frame Triage

1. Set `XRE_FORCE_SWAPCHAIN_MAGENTA=1`.
2. If magenta shows, presentation is alive. Examine the frame diagnostics for zero scene swapchain writers, dropped `MeshDrawOp` or `IndirectDrawOp` operations, descriptor failures, shader or pipeline creation failures, and missing pass metadata.
3. If magenta does not show, examine acquire, record, submit, and present, the swapchain layout transitions, command-buffer recording failures, and the device-lost state.
4. Isolate the overlays with `XRE_SKIP_IMGUI=1` and `XRE_SKIP_UI_PIPELINE=1`.
5. Capture the profiler Vulkan frame diagnostic bundle and the first validation-layer message.

## Profiler Symptom Map

| Symptom | Where to look |
|---|---|
| Missing swapchain writes | Vulkan frame diagnostics: scene, overlay, and diagnostic writer counts and the frame-operation list. |
| Dropped frame operations | Dropped-operation counters and the first failure pass, target, material, shader, and exception. |
| Descriptor fallback rendering | Descriptor fallback and failure summaries and validation messages for the same frame. |
| Oversynchronization | Barrier planner counters, queue ownership transfer count, and GPU capture wait and flush events. |
| Bandwidth-heavy load and store choices | GPU capture attachment load and store operations, MSAA resolves, the post-process chain, and tiler memory events. |
| Pipeline compilation hitches | Pipeline cache miss summaries, `XRE_VK_TRACE_PIPECREATE=1`, driver pipeline cache warm bytes, and prewarm manifest coverage. |

## GPU Capture Recipes

For RenderDoc, use the [RenderDoc skill and editor workflows](../ai/agent-editor-workflows.md).

Nsight Graphics:

1. Start the editor from Nsight with Vulkan selected and validation layers enabled.
2. Capture a frame after the scene renders for at least two frames.
3. Examine the queue submission list for acquire, render, optional compute or transfer work, and present.
4. Make sure that swapchain image layout transitions end at `PresentSrcKhr`.
5. Find pipeline creation events and compare each hitch with the profiler pipeline miss summaries.
6. Examine the descriptor sets on a suspect draw when the fallback counters are not zero.

Radeon GPU Profiler and Radeon GPU Analyzer:

1. Capture the same scene that you use for the OpenGL comparison.
2. Examine barrier cost, wait time, cache flushes, render-pass load and store behavior, and async compute overlap.
3. Look for long pipeline creation or shader compilation gaps during camera movement or UI interaction.
4. Compare Sync2 and legacy sync captures on the same workload.
5. Record whether queue overlap decreases frame time or only adds ownership-transfer overhead.

## Pipeline Prewarm Workflow

1. Start the editor with `XRE_VK_PIPELINE_PREWARM_CAPTURE=1`.
2. Use the default world, the Unit Testing World, common editor panels, post-processing modes, UI, capture paths, and representative project scenes.
3. Close the editor normally. The semantic prewarm manifest is written under `%LOCALAPPDATA%\XREngine\Vulkan\PipelinePrewarm\`.
4. Start the editor again. Do not clear `%LOCALAPPDATA%\XREngine\Vulkan\PipelineCache\`, so that the driver cache and the manifest both load.
5. Make sure that the profiler pipeline miss summaries go toward zero after the warm run.
6. Keep only curated manifests that match the intended GPU, driver, and feature profile.
7. Refresh the manifest when shader interfaces, render-pass formats, material variants, MSAA state, descriptor layouts, or feature-profile gates change.

## Lifecycle Evidence Collector

`Tools/Collect-VulkanLifecycleEvidence.py` collects live Vulkan evidence from a named isolated editor session. It uses only the Python standard library. It is a diagnostic harness. It is not a unit test or an acceptance gate.

### Run

Use a **stopped** named session that `Tools/Manage-McpEditorSession.ps1` created. Configure its environment JSON with `XRE_VULKAN_VALIDATION=1`, `XRE_VULKAN_SYNC_VALIDATION=1`, and `XRE_UNIT_TEST_WORLD_SETTINGS_PATH` set to the intended generated fixture. The camera and mutation sequence targets the Sponza fixture. For a different fixture, choose matching targets and camera positions.

```powershell
python Tools/Collect-VulkanLifecycleEvidence.py `
  --session <stopped-isolated-session> `
  --environment <run-output>/scratch/session-environment.json `
  --output <run-output>/reports/lifecycle-evidence
```

- The output directory must not exist.
- The script builds Release through the session manager and records the hashes of the three relevant DLLs.
- It starts the session with `AllowDestructive` for its disposable world snapshot and restore attempt, and stops the same session in `finally`.
- It refuses to attach to a session that is already running.
- Use `--no-build` only when the session binaries exist and you verified them. A stopped-session cleanup can remove them.
- The script never saves scene or material assets.
- It waits up to 180 seconds for the world capability after MCP becomes ready.
- Use `--cases mutations-world` to collect only warmup, reversible mutations, and world restoration. The default is `--cases full`.

### Collected Cases

- Warmup, stationary observation, repeated A/B views, and an unseen view C.
- Reversible Sponza transform and activation changes.
- A reversible scalar material change when the script finds a supported target.
- Shader reload and two consecutive renderer restarts, each with A/B/A views.
- World snapshot and restore when the running build supports it.

Each observation collects periodic frame, resource, and cumulative-validation snapshots, a screenshot, texture-streaming telemetry, and Advanced pipeline diagnostics. Raw requests and replies are numbered under `replies/`. Screenshots are under `screenshots/`. Manager, build, and runtime logs are under `logs/`. `README.md` and `summary.json` give a compact result. A tool failure is recorded as incomplete. An unavailable case never becomes a success.

### Result Meaning

- `TELEMETRY_OK` means only this: the final sampled interval advanced presents, its last frame completed without the checked resource or binding findings, and the requested validation configuration had no cumulative errors or overflow.
- `REVIEW` keeps findings and does not waive known startup errors.
- Neither status proves image correctness, mutation effect, steady-state resource bounds, or a performance improvement.
- Screenshots are taken after each interval, so they do not affect that interval. They can affect the next interval.
- A world snapshot can keep object IDs and undo references. A successful round trip does not prove distinct-world lifetime or collection.
- The collector does not cover simultaneous multiple views, failure injection, in-place buffer replacement, debugger attribution, matched observer pairs, or unit tests.

### Review

Compare the screenshots with frame progress and the pipeline state. Separate old readiness failures from new failure sequences, and startup or teardown validation from steady rendering. Compare resource deltas for each interval after warmup and mutations. Do not compare absolute counts across a device replacement. Release builds can omit text debug logs. The cumulative validation messages in the raw profiler replies are the primary evidence.

### Session Teardown

Stop named sessions with `-StopTimeoutSeconds 90` when teardown evidence is necessary. The editor refuses the first close request and closes about 20 seconds later. The default 15-second timeout stops the process before Vulkan teardown is logged.
