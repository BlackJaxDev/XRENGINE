# Vulkan desktop camera motion, stale frames, and CPU scaling

Opened: 2026-08-10
Last updated: 2026-08-17
Status: desktop input/cadence correctness and the render-thread prepared-cohort
bridge are implemented and live-validated; the persistent template/bin
migration, user acceptance, and regression-test authorization are pending

This is the canonical incident guide for Vulkan desktop symptoms where camera
input appears to gate rendering, the FPS overlay disagrees with visible motion,
camera movement causes black/partial output, or full-scene CPU time scales
poorly. Directional-shadow quality and OpenXR performance have separate owners
listed under [Related ownership](#related-ownership).

## Executive summary

The desktop Vulkan callback was never intentionally input-driven in this
reproduction. With render-on-demand disabled, it continued through acquire,
prepare/reuse, submit, and present every frame.

The input-demand appearance came from two bugs:

1. clean-primary reuse could consume frame-data refresh requests left in
   thread-local scratch by an earlier recording, sometimes for another
   swapchain image; and
2. the debug overlay averaged instantaneous `1 / delta` samples, which
   over-reported workloads alternating long scene frames with short UI/present
   frames.

Primary reuse now requires an exact current-frame refresh cohort, and the FPS
overlay reports frame count divided by summed frame duration. A no-input final
validation interval advanced 176 rendered frames in 3.134 seconds (56.1 Hz),
while the profiler reported 56.5 Hz.

Full Sponza still exposes a separate CPU scaling limit. Command buffers are
reused correctly, but the producer/consumer boundary historically rematerialized
the current visible draw stream and refresh cohort before reuse could be proved.
The 2026-08-17 bridge retains an exact render-thread prepared cohort, patches
current dynamic draw data, and appends it directly to the numeric operation
stream. In the final Debug run, 566 of 625 mesh requests reused the retained
recipe and 59 conservative holes were materialized normally. Frame-op
preparation fell from the earlier dense-view 19--25 ms range to a warmed median
of 13.111 ms, but this remains well above the final CPU budget. The remaining
architectural work belongs to the
[command-recording optimization TODO](../../todo/rendering/optimization/vulkan-command-recording-architecture-optimization-todo.md),
not to another input or presentation workaround.

The previously observed approximately 140 FPS view is not a dense-scene result:
it was looking out at a wall with very few meshes visible and is excluded from
all Sponza-center comparisons.

## Reproduction matrix

Use a named isolated MCP editor session and rebuild after every source change.
Do not use `Start -NoBuild` unless the isolated artifacts already contain the
exact source under test.

| Question | Scene configuration | What it isolates |
| --- | --- | --- |
| Does the loop advance without input? | Sponza off, both directional lights off, `EditorCameraRenderOnDemand=false` | Timer, focus, suppression, callback, submit, and present behavior |
| Does work scale with scene size? | Deferred Sponza on, both directional lights off | Mesh producer, frame-data publication, plan/schedule, and reuse cost |
| Is lighting an amplifier? | Same Sponza view with the directional light and cascades enabled | Cascade collection/publication and shadow command invalidation |
| Is the failure generation-dependent? | Startup, maximize, resize, minimize/restore | Swapchain image completion, resource generation, pass order, and layout state |
| Is the image stale while UI remains live? | Move between two deterministic camera views while sampling output state | Scene-primary/frame-data freshness versus overlay-only rendering |

For the latest dense-scene validation, the deferred Sponza import was enabled,
both directional lights were disabled, and render-on-demand was disabled.

## Measurement rules

1. Warm imports, texture streaming, and required pipelines before taking a
   steady-state cohort.
2. For clean timings, disable detailed draw/recording diagnostics, especially
   `XRE_VK_TRACE_DRAW`. Diagnostic logs can dominate the frame they describe.
3. Do not trust the overlay alone. Sample `frame_outputs.frame_id` before and
   after a measured wall-time interval with no input. Require the desktop scene
   output to report `scene_rendered=true`, `work_disposition=FreshRender`, and
   `skipped=false`.
4. Record p50/p95/p99 and exact work counts. Average FPS hides cadence-bimodal
   recording and tail spikes.
5. Treat `Present` and `RecordCommandBuffer` profiler wrappers as engine
   lifecycle scopes. Inspect native `present_ms`, wait/acquire time, frame-data
   refresh, manifest, reuse, and encoding children before blaming
   `vkQueuePresentKHR` or native command recording.
6. Exclude MCP screenshot/readback windows from cadence measurements. A
   full-resolution Vulkan readback synchronously waits for GPU completion and
   performs substantial CPU conversion.
7. Use `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0` to isolate primary reuse and
   `XRE_VULKAN_COMMAND_CHAINS=0` to isolate hybrid secondary scheduling. These
   are diagnostic topology changes, not production fixes.
8. Inspect at least two camera-dependent images. A capture that does not change
   with the camera points to stale data or the wrong capture surface.

## Resolved defect ledger

| Symptom | Root cause | Durable correction |
| --- | --- | --- |
| FPS text remains high while the 3D view advances only after input | Commit `ec0efb261` moved primary reuse before current-frame frame-data registration; old thread-local requests carried stale camera/view data. The FPS overlay also used a biased arithmetic mean of instantaneous rates. | Build and stamp the current refresh cohort before reuse; aggregate FPS as frame count over elapsed duration. |
| Descriptor/frame-data slots intermittently cannot reopen or refresh the wrong image | Desktop descriptor slots are acquired swapchain-image slots, but completion was checked only against the frame-in-flight timeline. | Frame slots use the frame-slot timeline; desktop descriptor slots use the desktop image timeline; OpenXR retains external ownership. Refresh writes use `frameDataImageIndex`, while command-artifact ownership uses the acquired command image. |
| Camera visibility changes rerecord hundreds of otherwise stable chains | Commit `16047d7e4` applied the whole-frame resource-version signature, including the visible operation stream, to every chain. | Schedule identity keeps the complete signature; per-chain shared-resource invalidation uses resource-allocation generation plus exact packet/descriptor/prepared dependency checks. |
| A dense pass renders only partially or falls back halfway through | A contiguous mesh run mixed scheduled and inline operations but was treated as one reusable run. | Split runs whenever scheduled membership changes. |
| Stable reuse still pays to seal the entire frame plan | Reuse was attempted only after `FramePlan.BuildAndSeal`. Dynamic UI also rejected an unsealed operation before checking for an exact reusable secondary. | Resolve exact schedule identity, project current operations through the recorded order, prepare the current cohort, and attempt primary reuse before sealing. Exact dynamic-UI reuse is checked before the sealed-operation requirement. |
| Stable command reuse still reconstructs hundreds of mesh operations | Native command artifacts were cached after per-visible-draw materialization, `MeshDrawOp` rental/queueing, resource-use lowering, and object-to-numeric-stream conversion. | Retain a bounded exact prepared cohort on the render thread, always drain current raw requests, patch current dynamic data, materialize unsafe holes, and append the completed transaction directly to `FrameOperationStream`. |
| A retained cohort can use stale bindings or an incomplete resource DAG | A renderer's active-program slot is not authoritative for every captured material/program variant, and resource uses lowered before swapchain-context coalescing can describe the wrong framebuffer/resource generation. | Revalidate immutable artifacts through the exact captured program's generation-owned cache; finalize pass identity and relower descriptor/attachment uses only after context coalescing. Snapshot/callback/explicit-target cases without the required lifetime contract remain holes or use the legacy path. |
| Viewport becomes black after maximize/resize although the skybox draw exists | Pass sorting mixed ranks from different render graphs, allowing `Background` to run before a later ForwardPass clear/light-combine blit erased it. | Sort from each operation context's complete pipeline metadata and declare the light-combine-to-`Background` dependency explicitly. |
| Resize/minimize recovery strands mapped data | Swapchain recreation discarded the strongest completion value from retired images. | Seed replacement-image completion authority from the strongest retired completion value. |
| Full-resolution work inherits an internal-resolution viewport/scissor after secondaries | State cached on the primary survived `vkCmdExecuteCommands`, although most primary bind/dynamic state becomes undefined across secondary execution. | Invalidate the primary bind-state cache after every secondary execution batch. See the historical July camera-motion investigation. |
| Directional-camera motion produces global artifact rebuilds | Cascade publication changed shader-visible light state for nearly every mesh; texel-snapped cascade hashes could also look stable while the source camera was moving. | Preserve one coherent atlas generation during motion and use the actual source-camera pose for settle detection. Shadow quality and final light-on acceptance remain in the directional-light investigation. |

## Command-reuse invariants

The complete reusable-command contract is in
[Vulkan Primary Command-Buffer Reuse](../../../architecture/rendering/vulkan-primary-command-buffer-reuse.md).
The incident-specific rules most likely to regress are:

- A reusable native command artifact owns command structure, not current frame
  data. Current data must be published before reuse is accepted.
- A refresh cohort is valid only for its exact frame-plan generation, render
  frame ID, and frame-data image index. Thread-local scratch from a previous
  recording is workspace, never authority.
- Acquired command-image identity and frame-data image identity are distinct
  concepts even when desktop currently maps them to the same integer.
- Camera, transform, animation, material values, and stable-capacity buffer
  contents are data changes. They must not broaden into cache-wide structural or
  resource-allocation invalidation.
- A successful queue submission is the publication boundary for recorded image
  state. Rejected, abandoned, or failed attempts publish nothing.
- Volatile ImGui, dynamic text, profiler, streaming, and debug work remains
  isolated from stable scene artifacts.

## 2026-08-11 dense-scene result

The final `vk-sponza-no-dir-final-20260811` session used Debug Vulkan, full
deferred Sponza, and no directional lights.

- 398 renderables were tracked.
- The focused exterior view scheduled and reused all 725 command chains; zero
  chains were recorded.
- The primary was reused and not recorded.
- All 835 program-binding artifacts were reused; none were built.
- Vulkan validation errors and descriptor binding failures were zero.

Ten warmed exterior samples had these medians:

| Stage | Median |
| --- | ---: |
| Whole frame | 22.136 ms |
| Frame-op preparation | 13.224 ms |
| Packet construction | 0.529 ms |
| Primary handling | 3.200 ms |
| Frame-data manifest | 2.307 ms |
| Submission | 0.896 ms |

Nested stages are attribution, not additive totals. Packet construction fell
from roughly 2.5--3.2 ms before early exact reuse to about 0.5 ms. The remaining
dominant cost is producer/materialization work needed to rebuild the current raw
draw stream and refresh cohort. It is not native primary or secondary encoding.

## 2026-08-17 prepared-cohort result

The final `sponza-package-v10-0817` session used the same Debug Vulkan,
directional-light-off Sponza configuration and the actual dense center view at
camera position `(-11, 6, 0)` looking toward `(-28, 3, 0)`.

- The current producer still emitted all 625 raw mesh requests every frame.
- A stable hit reused 566 prepared recipes and rematerialized 59 explicit safety
  holes. The holes cover callbacks, mutable publishers/snapshots, shadows,
  external targets, order-preserving scopes, and other inputs without a proven
  cross-frame ownership contract.
- Across 48 observed stable hits, the counters advanced by exactly 27,168
  reused operations and 2,832 hole materializations. Cohort builds and full
  materializations did not advance.
- Vulkan runtime error count, skipped draws, and dropped frame operations were
  zero. The Khronos validation layer was not enabled for this performance
  session, so the required validation-enabled acceptance cohort remains open.

Twelve warmed samples over rendered frames 3301--3349 had these medians:

| Stage | Median | Observed range |
| --- | ---: | ---: |
| Whole frame | 21.951 ms | 20.801--23.773 ms |
| Scene command-buffer lifecycle | 20.152 ms | 19.165--21.826 ms |
| Frame-op preparation | 13.111 ms | 12.260--14.839 ms |
| Packet construction | 2.812 ms | 2.613--4.810 ms |
| Frame-data manifest | 2.814 ms | 2.563--3.091 ms |
| Primary handling | 3.302 ms | 3.051--3.712 ms |
| Reported GPU command-buffer time | 3.767 ms | 3.104--4.087 ms |

Nested stages are attribution, not additive totals. This instrumented Debug/MCP
cohort confirms that current CPU preparation, not native command encoding, is
the largest remaining local cost. It is not a 200 Hz result: a 5 ms total frame
budget cannot accommodate a 13 ms preparation stage.

An immediate move to `(-20, 8, 0)` looking toward `(-50, 6, 0)`, followed by a
return to the dense center pose, caused one intentional full materialization and
one cohort build per distinct view. Both resulting screenshots contained the
correct camera-dependent geometry. This rules out stale replay and reproduces
the expected limitation of an exact whole-cohort key: a membership/order change
invalidates the whole cohort. The cohort is therefore a safe bridge, not the
final granularity. The target architecture is persistent per-draw templates,
compact current-frame instance records, numeric pipeline/material bins, and
multi-draw/indirect submission where supported.

## Do not repeat these experiments

The following were measured and reverted or rejected:

- broad mixed-program main-view packets: one visibility change invalidated a
  large packet and made sustained motion worse;
- batching pipeline/camera scopes and invariant materialization facts: no
  measurable improvement;
- an all-direct-owner prepass that omitted refresh requests: slower overall;
- reusing unpinned `MeshDrawOp` plan objects: retained stale-lifetime risk and
  did not reduce packet cost;
- bulk producer-queue draining: first changed queue semantics, then remained
  neutral/slower after correction;
- a global buffer-readiness epoch: dynamic buffers change every frame, so it
  would continuously invalidate; missing one asynchronous upload/delete edge
  would make it incorrect;
- refreshing one directional cascade per frame: spread a global binding
  invalidation across several frames and was worse than preserving one coherent
  generation;
- disabling skybox depth testing or unconditionally transitioning sampled depth
  that is also an attachment: both treated symptoms and caused new output
  failures.

## Triage guide

- **Frame ID does not advance without input:** inspect render-on-demand,
  `Suppress3DSceneRendering`, focus/unfocused target FPS, window minimization,
  callback entry, and frame pacing before command reuse.
- **Frame ID advances but scene content is stale while UI is live:** compare
  primary reuse on/off, cohort frame/image stamps, descriptor-slot completion,
  and the scene output's `FreshRender` state.
- **All chains and the primary reuse but CPU remains high:** inspect producer
  draw preparation, binding publication, frame-data manifest/refresh, and
  descriptor validation. More recording workers will not remove this work.
- **Only directional-light motion is bad:** use the
  [Directional Light Vulkan Stability Investigation](directional-light-inspector-shadow-2026-08-03.md).
- **Only resize/maximize is bad:** inspect output/resource generations, retired
  completion inheritance, per-context pass order, and image entry/exit state.
- **Output occupies the upper-left/internal-resolution region:** invalidate
  primary state after secondary execution and inspect viewport/scissor authority.
- **Profiler says `Present` or `RecordCommandBuffer` is huge:** expand the nested
  lifecycle stages before attributing the time to native Vulkan calls.

## Validation evidence

Final live artifacts:

- exterior:
  `Build/_AgentValidation/vulkan-phase42-43-final-20260810/20260811-descriptor-reuse-scaling/mcp-captures/Screenshot_20260811_151055_077_201e73d5c9a94a188b61963500b2492d.png`;
- interior after an immediate camera move:
  `Build/_AgentValidation/vulkan-phase42-43-final-20260810/20260811-descriptor-reuse-scaling/mcp-captures/Screenshot_20260811_151138_810_fa8d573ea3a94fb6a4b1cc07f9e3fb17.png`;
- final session logs:
  `Build/_AgentValidation/mcp-sessions/vk-sponza-no-dir-final-20260811/logs/`.
- 2026-08-17 dense center after a camera move and return:
  `Build/_AgentValidation/20260817-101407-sponza-center-fps/mcp-captures/after-package-v10-dense-return/Screenshot_20260817_123039_084_1b11a6cd5d924c6cb7ab4dde61113fbe.png`;
- 2026-08-17 alternate interior pose:
  `Build/_AgentValidation/20260817-101407-sponza-center-fps/mcp-captures/after-package-v10-second-pose/Screenshot_20260817_123016_283_63681a4504bf4020be1104a214d63c68.png`;
- 2026-08-17 final session logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260817-122536-sponza-package-v10-0817/logs/`.

The images show materially different scene views while the primary and all
scheduled secondaries remain reusable. The final log contains no Vulkan VUID,
validation error, descriptor/binding failure, primary-reuse rejection,
dynamic-UI unsealed-operation rejection, device loss, or fatal exception.
The 2026-08-17 performance session did not request the Khronos validation layer;
its zero runtime error/VUID count is not a substitute for the separate
validation-enabled acceptance gate.

The Vulkan leaf build passes with zero warnings and errors. The isolated editor
build passes with zero errors and the nine pre-existing OscCore submodule
warnings. No regression tests were added or run before user acceptance, per
repository policy.

## Related ownership

| Topic | Canonical document |
| --- | --- |
| Primary/secondary state, cache identity, and current-data reuse | [Vulkan Primary Command-Buffer Reuse](../../../architecture/rendering/vulkan-primary-command-buffer-reuse.md) |
| Final render-loop ownership and CPU contract | [Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md) |
| Remaining prepared-data/producer optimization | [Vulkan Command Recording Architecture Optimization TODO](../../todo/rendering/optimization/vulkan-command-recording-architecture-optimization-todo.md) |
| Post-change correctness, performance, and soak validation | [Vulkan Core Hardening And Recording Testing TODO](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md) |
| Directional cascades, atlas stability, and light-on acceptance | [Directional Light Vulkan Stability Investigation](directional-light-inspector-shadow-2026-08-03.md) |

### Historical evidence index

These records preserve measurements and narrow fixes; they do not own current
work.

| Evidence | Use it for |
| --- | --- |
| [June Render-Loop Speed](../../vulkan-render-loop-speed-2026-06-23.md) | Initial clean-reuse timing, descriptor snapshots, duplicate refresh sorting, and reused-command GPU timing metadata |
| [Vulkan CPU Framerate Regression](archive/vulkan-cpu-framerate-regression-2026-07-09.md) | Reuse-disabled baseline and CPU/GPU attribution taxonomy |
| [Vulkan Camera-Motion Black Frames](archive/vulkan-camera-motion-black-2026-07-10.md) | Inline-primary camera black frames and the first-stable-frame stop-boundary guard |
| [CPU Async-Query Occlusion During Camera Motion](archive/cpu-query-camera-motion-2026-07-20.md) | Occlusion recovery, sparse frame-data reservation growth, and direct-present orientation |
| [Vulkan Camera-Motion Framerate Regression](archive/vulkan-camera-motion-framerate-regression-2026-07-21.md) | Variant caching, exact uniform-slot mapping, secondary-state invalidation, and camera-motion work |
| [Continuous Window Resize Frame Lifecycle](archive/continuous-window-resize-frame-lifecycle-2026-07-23.md) | Win32 border-drag callback behavior; distinct from ordinary input-demand symptoms |
| [Current JSONC Framerate](archive/current-jsonc-framerate-2026-07-26.md) | CPU/GPU attribution and screenshot/readback overhead |
| [Vulkan Framerate Root Cause](archive/vulkan-framerate-root-cause-2026-07-28.md) | Primary image-state ownership and zero-readback evidence |
| [Vulkan Editor Steady-Frame CPU Cost](archive/vulkan-editor-frame-time-spikes-2026-07-30.md) | Binding and frame-data publication decomposition |

Archived investigations retain point-in-time evidence only. New desktop
camera/input/cadence findings should update this guide; new architecture work,
test requirements, or shadow-specific issues should update their owners above.
