# Vulkan Uber Pipeline Stall And Black Recovery

Status: shader reload and same-generation restart repaired and live-validated;
collectible Vulkan module replacement remains unsafe at the process-global
NVIDIA Streamline boundary.

## Problem

The Vulkan editor froze on the last completed frame while loading Sponza with
the Uber material path, then became permanently black after a window resize.
The process remained alive and presentation continued to return success.

During the first repaired run the editor remained visually responsive, but cold
pipeline compilation reduced the frame rate to an unusable level. Later shader
reload attempts exposed stale program/pipeline state and crashed in Vulkan draw
recording. Structural `Build and Reload Renderer` subsequently exposed a
separate native Streamline lifetime defect.

## Evidence

The failing run is:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-27_10-24-33_pid18660/log_vulkan.log`

- At `10:25:28`, the first `UberShader.frag` graphics-pipeline misses caused
  primary recording to return before `vkBeginCommandBuffer`.
- The renderer reused the last completed swapchain content, which appeared as
  a frozen frame.
- No Uber fragment pipeline completed. The async queue eventually reported
  `capacity (16; active=16, completed=0)`.
- A resize recreated the swapchain. With no completed content in the new
  images, rejected-frame recovery submitted the initialization clear, which
  was black.
- `vkQueuePresentKHR` continued succeeding, and there was no contemporary
  device-loss or Windows GPU-reset event.

Source inspection found that shared graphics-pipeline libraries were checked
under a lock but created outside it. Multiple full-pipeline variants could
therefore enter the driver concurrently for the same expensive shared Uber
fragment library; deduplication happened only after native creation.

The first repaired live run exposed a separate responsiveness problem in:

`Build/_AgentValidation/mcp-sessions/pipeline-stall-fix-live-0727/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-27_11-03-28_pid46480/log_vulkan.log`

- The queue started four cold compiler workers and processed 514 pipeline
  jobs, totaling 179.86 worker-seconds.
- Five Uber pipelines each spent 34.0-37.1 seconds inside the NVIDIA driver.
  Three of those expensive native calls overlapped.
- Frame diagnostics fell to roughly 0.2-3 FPS during the overlapping cold
  compiles, then recovered to roughly 57-150 FPS as they completed.
- Queue waiters used synchronous semaphore waits on thread-pool jobs, while
  every deferred draw was reconsidered and logged every frame.

A steady-state run after the cold compilation work completed is recorded in:

`Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-27_14-33-06/summary.json`

- render time p50 was 29.997 ms and p95 was 33.170 ms;
- Vulkan submission time p50 was 2.191 ms;
- command recording was 1.695 ms and the GPU command-buffer sample was
  3.687 ms;
- all 173 sampled command buffers were reused; and
- collection was 3.146 ms while the measured render wait was 26.607 ms.

This separates two performance problems. The extreme 0.2-3 FPS behavior was
cold NVIDIA pipeline compilation amplified by excessive concurrency and retry
work. The remaining roughly 30-33 FPS steady state is dominated by engine
CPU/frame coordination rather than Vulkan submission. Runs with
`XRE_VK_TRACE_DRAW=1` and standard validation are diagnostic stress runs and
must not be treated as representative frame-rate measurements.

## Implemented Repair

1. Shared graphics-pipeline library creation now reserves its exact library key
   before entering Vulkan. Sibling creators defer and retry instead of invoking
   the driver concurrently.
2. The async compile queue permits only one cold full-pipeline job per shader
   program at a time and, by default, only one cold native compile globally.
   The global gate is asynchronous, so queued work does not occupy thread-pool
   threads, and the compiling thread runs at below-normal priority. The
   `XRE_VK_PIPELINE_COMPILE_WORKERS` environment variable remains available
   for explicit diagnostic overrides.
3. A two-stage queue watchdog reports jobs older than two seconds and marks
   jobs older than ten seconds as quarantined. Vulkan's synchronous native
   pipeline call is not cancelled; affected draws remain deferred.
4. Pipeline prewarm failure now defers only the affected mesh or indirect draw.
   The primary command buffer still records and submits the rest of the frame.
   Deferred operations are tracked by object identity so render-graph sorting
   cannot redirect a deferral to a different operation. Known-deferred
   requirements are reused until compile or shared-pipeline publication
   activity changes, avoiding per-frame retry storms.
5. Rejected desktop frames can record the current ImGui snapshot after their
   recovery transition and submit both command buffers in order. A recovery
   clear uses a dark purple diagnostic background rather than an
   indistinguishable black frame.
6. Pipeline cache-miss and deferred-draw diagnostics are rate-limited and
   aggregated rather than emitted once per draw and pipeline key.
7. Vulkan shader invalidation is marshalled to the render thread and batches
   all dependent stages at one legal mutation boundary. Shader/program native
   mutation blocks new pipeline builds and drains already-started builds before
   replacing modules or layouts.
8. Pipeline build requests capture the shader, program-layout, and module
   generation they were compiled against. Obsolete results are rejected rather
   than published after a reload.
9. `VkRenderProgram.LinkGeneration` advances on invalidation and successful
   relink. Mesh renderer prepared state, command-recording dependencies,
   full-pipeline cache keys, and graphics-pipeline-library keys include that
   generation. A mesh therefore cannot reuse a pipeline or descriptor layout
   from the previous native program interface merely because the managed
   `VkRenderProgram` reference stayed the same.

## Hot Reload Findings

Disabling Vulkan command chains did not avoid the shader-reload failure. The
`vulkan-hot-reload-nochains-0727` run crashed while recording a draw into the
primary command buffer, ruling out secondary-command-buffer reuse as the root
cause. The missing dependency was mesh-local program generation: relinking
replaced native shader/layout state without changing the managed program
object's identity.

After adding program-generation invalidation, the
`vulkan-hot-reload-linkgen-0727` run completed both supported fast paths:

- `reload_renderer_shaders` invalidated 79 loaded shaders and rendering
  continued;
- `restart_renderer` recreated Vulkan generation 0 in the same editor process;
  and
- baseline, post-shader-reload, and post-restart captures remained valid scene
  frames rather than recovery clears or stale black output.

The three captures are:

- `Build/_AgentValidation/mcp-sessions/vulkan-hot-reload-linkgen-0727/mcp-captures/Screenshot_20260727_165855_425_661ea71edab743dab4ea2c1692757317.png`
- `Build/_AgentValidation/mcp-sessions/vulkan-hot-reload-linkgen-0727/mcp-captures/Screenshot_20260727_165934_888_7c7c2a06f8874dd287967038695b3a48.png`
- `Build/_AgentValidation/mcp-sessions/vulkan-hot-reload-linkgen-0727/mcp-captures/Screenshot_20260727_170017_791_c271ea30efe14b14a58d78e994227214.png`

### Structural Vulkan module replacement

`build_and_reload_renderer` remains unsafe for Vulkan when NVIDIA Streamline
has initialized. In the same live session, the old renderer and device retired
and the candidate generation began Streamline initialization. Streamline
reopened `sl.log` at 17:01:10 and initialized its plug-in manager; Windows then
terminated the editor at 17:01:12 with BEX64 fast-fail `0xc0000409` in
`ntdll.dll`. There was no managed exception or stderr output.

The collectible Vulkan assembly gives each generation independent managed
Streamline statics and callback/function-pointer fields, while
`sl.interposer.dll` and its SDK state are process-global. The existing module
unload path shuts down and frees that library before a candidate generation
loads and initializes it again. The native SDK does not currently survive that
unload/reload sequence reliably.

Until ownership is moved behind a stable-host process-lifetime broker, or the
operation is rejected before teardown with an actionable diagnostic, do not use
`Build and Reload Renderer`/`build_and_reload_renderer` for structural Vulkan
edits. Use:

1. shader reload for GLSL/include changes;
2. `dotnet watch` for compatible C# method-body changes;
3. same-generation renderer restart for device/swapchain/resource recreation;
4. a full editor restart for structural Vulkan C# changes.

The historical OpenGL collectible-generation validation is unaffected by this
Vulkan/Streamline limitation.

## Validation

- `rdc doctor`: passed for desktop Vulkan capture and replay support.
- Targeted Vulkan project build: passed with zero errors. The only warnings
  were pre-existing Magick.NET vulnerability advisories.
- Focused shader dependency, Vulkan pipeline-compilation, and command-recording
  dependency checks: 49 passed, zero failed.
- The first isolated MCP repair run produced two non-black viewport captures
  from different camera positions, proving that frames continued to render
  rather than sampling stale content. The user then closed that editor
  normally; the session bootstrap recorded `Engine.Run returned normally`.
- Shader reload and same-generation Vulkan restart were live-validated in one
  editor process after the link-generation repair.
- Collectible Vulkan module replacement failed at the documented native
  Streamline boundary and is not claimed as complete.

## User Report

The original behavior was reported as a graphics freeze followed by a
permanent black window. During the first repaired live run, the user reported
that the frame rate was unacceptably slow and closed the editor. The compile
scheduler and retry behavior were adjusted in response. No additional live
launches or tests were performed after the request to wrap up this
investigation.

## Remaining Work

1. Move Streamline runtime/library/callback ownership into a non-collectible
   stable-host service, or explicitly reject structural Vulkan reload whenever
   Streamline has been initialized.
2. Re-run consecutive collectible Vulkan generations only after that boundary
   is fixed; require valid scene captures and zero unload leaks.
3. Profile and reduce the roughly 26.6 ms steady render-wait/CPU coordination
   cost independently of Vulkan submission.
