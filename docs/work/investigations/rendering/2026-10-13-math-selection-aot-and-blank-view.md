# Math Selection AOT Exception And Frozen View

Status: The asset, pipeline readiness, and descriptor-retirement fixes are complete. The final desktop build passes live camera and AA changes with draw tracing off and no resize.

## Report

The user selected the Math Intersections world root. The debugger stopped on repeated `AotParityViolationException` throws for `TextFile`. After the user continued, each window resize showed one new scene frame. The scene then stayed frozen until another resize. Resize did not restore continuous rendering.

## Evidence

The inspected run is `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-10-13_19-22-41_pid71392/`. It used Vulkan, `AdvancedRenderPipeline`, `CpuDirect`, TSR, and an attached debugger. Times below are local log times on 2026-10-13.

- `log_general.log`, line 54 onward: selection calls `EditorFlyingCameraPawnComponent.Selection_SelectionChanged`, then `TransformTool3D.GetInstance`. Gizmo construction loads cached `GizmoLine`, `GizmoArrowHead`, stereo variants, and `GizmoTriangle` shaders.
- `SceneNode.Components.cs`: the factory overload of `AddComponent` enters an AOT player scope when the node world has an active play session. The gizmo node belongs to that world through its hidden editor scene.
- `XRAssetYamlTypeConverter.cs`, `TryLoadUntrackedTextAsset`: disk-backed `TextFile` construction reports `ReflectiveFactory` before `Activator.CreateInstance`. It has no direct constructor branch for the exact `TextFile` type. The cooked catalog branch already uses `new TextFile`.
- `AotParityDiagnostics.cs`: Unit Testing mode defaults to `Error`. This mode throws on each fallback attempt. It does not deduplicate throws.
- `AssetManager.Loading.SerializationAndCache.cs`, `TryLoadCachedAsset<T>`: the cache reader catches the exception, logs a failed cache read, deletes that cache entry, and returns a cache miss. This explains why the debugger can stop repeatedly while the editor can continue through source import.
- `log_vulkan.log`, line 321: frame 455 records a 20-second gap at 19:23:34.563. This matches the exception interval. It does not prove a GPU stall.
- `log_rendering.log`, lines 243-248: after that interval, the render loop continues at about 59-72 Hz before resize. The reported draw count remains five, with 804 triangles. Frame activity does not prove correct scene pixels.
- At 19:23:48, resize changes the output from 1920x1080 to 2560x1369. The pipeline replaces a resource generation with 61 textures, 61 framebuffers, and 75 buffers. Vulkan also recreates the swapchain.

## Changes

- `XRAssetDeserializer.TryLoadUntrackedTextAsset` constructs exact `TextFile` references directly. Derived types use the registered runtime factory before the reflective fallback. Strict parity still rejects an unregistered fallback.
- All three `SceneNode.AddComponent` overloads use the owning root's play policy before entering a player scope. Hidden editor roots do not create a new player scope. An inherited player scope remains active.
- `VkFrameBuffer` clears two stack-allocated Boolean spans before collecting attachment flags. Stack allocation does not guarantee zero values.
- Primary inline mesh draws use the established `VkMeshRenderer.RecordDraw` path. The removed resident shortcut did not preserve acquired template lifetime, resource tracking, exact vertex binding slots, or captured-program preparation. It could also report success without a draw. This is a separate correctness fix; the controlled run below does not identify it as the freeze cause.
- Inline draw failures have a throttled diagnostic even when draw tracing is off. Per-call failure results distinguish an unexpected rejection from an intentional empty or excluded shadow draw.
- Pipeline warm manifests and admission records now track the shared graphics-pipeline retirement generation. A recording-time pipeline miss also invalidates the current admission before retry.
- An unexpected inline mesh draw failure rejects the partial frame. Downstream AA cannot publish retained intermediate images as fresh output.
- Command-buffer publication carries a typed retired-dependency result. That result abandons the stale recording and retries a fresh frame. Other lifetime failures retain strict handling.

## Live Evidence

Evidence is under the named session roots in `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/`. Captures and logs are disposable.

### Baseline: `20261013-193656-math-freeze-review`

This build contains the direct text construction fix, but not the later component policy or rendering changes. Root selection succeeds. The view freezes with both FXAA and TSR. Camera local, world, and render transforms change. The UI and frame counter continue to update. Forcing command-chain recording each frame does not restore the scene.

`renderdoc/stale_frame5881.rdc` shows 13 gizmo draws in the forward pass. `PostProcessOutputFBO` and `FinalPostProcessOutputFBO` each begin and end rendering with no draw. TSR then samples retained output. Exported forward and TSR images were viewed. The forward image contains accumulated gizmo orientations; the TSR image retains the earlier camera view.

The frame-operation trace includes both missing fullscreen draws. Shader preparation and enqueue logs report success. The failure therefore occurs after authoring and before the native draw. This explains the one-frame-per-resize symptom: resource replacement can produce a new image, but regular frames do not update these outputs.

### Patched build: `20261013-195127-math-freeze-fixed`, PID 24932

The isolated editor build passed with zero warnings and zero errors. Root selection produced no parity violation under `XRE_AOT_PARITY=error`. The scene image changed after camera movement without resize, under both FXAA and TSR. Screenshots from both camera positions were captured and viewed.

`renderdoc/fixed-selected_frame9180.rdc` contains one draw in each post-process pass and one TSR draw. The post-process and final-post-process target exports were viewed. Their gizmo image matches the current camera. No nonindexed failure diagnostic appeared.

### Resident shortcut comparison: PID 20296

The same build restored only the two resident shortcut call sites for the experiment. The source was restored after compilation. Camera movement still updated the view without resize. This rules out the shortcut removal as a required fix in this reproduction. The shortcut remains removed because the independent review found resource and state correctness defects.

### Framebuffer and combined comparisons: PIDs 27584 and 52644

Removing the two Boolean clears also left camera movement working. Restoring the original component scope, framebuffer flags, and resident call sites together still passed. Each experiment restored the final source after compilation. These runs had `XRE_VK_TRACE_DRAW=1` from startup; the original frozen run enabled draw tracing later. Tracing changes timing. These comparisons therefore do not establish the freeze's source-level cause.

### Normal-speed failure: PID 64620

The final build passed with zero warnings and zero errors. It started with `XRE_VK_TRACE_DRAW=0`. Root selection again produced no AOT violation. With TSR active, the screenshot at 20:04:17 retained the earlier camera view although the camera's local, world, and render transforms had changed. This rejects the rendering fix claim.

At 20:04:25, the renderer then entered a terminal state. Its log reports that command buffer `0x2CBE8E12C00` attempted to record retired `DescriptorSet:0x2B3847639B8`, generation 4458. The owner was `MeshRenderer.DescriptorSet.Frame0`. The following frames discarded scene work. The engine timer still reported no timer terminal fault; renderer failure and timer failure are separate states. This later terminal failure does not by itself explain the earlier frozen screenshot.

Review traced the terminal escalation to `EndPrimaryCommandBuffer` in `VulkanRenderer.CommandBufferRecording.Primary.Finalization.cs`. Dependency validation can return false with a failure string but no recording failure kind. `RecordDesktopFrameCore` then treats that result as terminal. The existing retirement classifiers also expect the text `attempted to record retired Vulkan resource`, while this producer emits `attempted to record retired resource`. Changing only the text classifier would not repair the finalization branch. A focused repair must propagate a typed stale-dependency result, abandon the rejected recording, and retry before submission. Descriptor ownership and retirement timing still need validation.

Runtime trace changes must use `RenderDiagnosticsFlags.SetVkTraceDraw`. Changing the environment override alone did not update the active diagnostic flag in this run. A later restart, PID 43832, confirmed that the setter produces draw trace records.

### Tracing-off follow-up

Session `20261013-201353-math-continuous-render`, PID 70272, started with draw tracing disabled. Root selection succeeded. After the camera changed from `(8, 6, 12)` to `(-8, 6, 12)`, TSR retained the previous gizmo orientation. No resize occurred. `renderdoc/traceoff_frame3515.rdc` contains 13 forward draws, empty post-process and final-post-process scopes, and a TSR draw. The forward and TSR exports were viewed. The forward image contains the new camera view; the TSR output contains the old view.

The follow-up build adds a throttled draw-failure diagnostic that does not require draw tracing. PID 45908 reports repeated failures for both intermediate targets: `graphics pipeline was not present in the prepared cache`. This is a recording-time pipeline-cache miss, not a camera transform failure. The downstream AA draw previously allowed the incomplete frame to appear successful.

Descriptor publication now carries `EVulkanCommandBufferTrackingFailureKind`. A retired dependency produces `RetryFrame` after the rejected command buffer is abandoned. A command buffer that is already queued for submission remains a hard failure. Review confirmed that rejection releases the accepted plan and its leases before the next frame. The pipeline-cache failure is a separate cause of the earlier frozen pixels.

PID 71980 also encountered a native surface report of `1x1` followed by repeated resize-convergence deferrals. That run was stopped. It is not used as continuous-rendering evidence.

### Final validation

The generation-aware build, PID 81448, corrected the reproduced cache-miss failure with tracing off. Opposite camera positions updated the TSR image without resize. No draw-failure warning appeared. `renderdoc/generation-fixed_frame5559.rdc` contains a draw in each previously empty post-process pass. Both target exports were viewed.

The final editor build passed with zero warnings and zero errors. PID 79600 started with `XRE_VK_TRACE_DRAW=0`; tracing was never enabled in this process. It used Vulkan, `AdvancedRenderPipeline`, `CpuDirect`, the ImGui editor, and strict AOT parity. The checks passed:

- Root selection produced no AOT parity exception.
- FXAA updated at `(8, 6, 12)` and `(-8, 6, 12)`.
- TSR updated at `(8, 6, -12)` and `(-8, 10, -12)`.
- Four additional FXAA/TSR switches completed, six seconds apart. Camera movement still updated afterward.
- A four-second camera move produced changing images during the move and the correct final image.
- The window stayed at `1920x1080`. No resize was used to refresh the scene.
- `renderdoc/final-traceoff_frame4214.rdc` contains 13 forward draws and one draw in each post-process, final-post-process, and TSR pass. The post-process target exports were viewed and match the camera.

Screenshots from 20:38:13 through 20:40:28 were viewed. The final process ran for more than three minutes. Logs contain no repeated pipeline-cache rejection, renderer terminal pause, or retired-descriptor publication failure during these checks. The previously observed descriptor race did not recur, so its recovery classification is supported by code review rather than a forced race in the final run.

## Review Limits

The original freeze was a stale pipeline readiness result followed by partial-frame publication. The generation-aware invalidation corrected the tracing-off reproduction. Trace logging changed timing; the exact timing effect is not established.

Review found no blocking issue in the serial desktop path. Concurrent preparation on another recording thread could overwrite an explicit manifest invalidation from an old thread-local admission record. This was not observed here. OpenXR concurrency was not validated by this desktop check.

The resident shortcut removal can increase CPU recording cost. It keeps the Vulkan GPU path. The investigation does not establish unchanged performance. The captured gizmo draws render correctly through the established recorder.

The user has not yet validated the asset fix in their debugger session. No tests were added or modified during the live investigation. Validation used the isolated editor build, live screenshots, logs, and GPU captures. The isolated session and RenderDoc sessions were stopped after validation.

The broker route tool returned deprecated `gpt-5.6-luna` despite the GPT-6 constraint. No paid run was started. Native read-only investigation continued.
