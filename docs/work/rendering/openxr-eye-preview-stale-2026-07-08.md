# OpenXR Vulkan Eye Preview Stale/Partial Render - 2026-07-08

## Problem

The OpenXR Vulkan eye previews still showed stale/partial output after the preview-copy allocator gate was fixed:

- Left eye: only a small top-left region of the fresh render was visible over stale frame contents.
- Right eye: black.

Follow-up after the safe-path post-process fix:

- Left eye renders correctly.
- Right eye still flickers black.
- Skybox also intermittently flickers black, but less often.
- Disabling directional-light shadow-map atlasing makes the black flicker much less frequent.
- The process crashed with `0xC0000005` in `Vk.BeginCommandBuffer` while recording a command-chain secondary for an OpenXR eye.

Follow-up after the atlas/secondary-lifetime fix:

- Right eye still flickers black, but much less often.
- The skybox still flickers black frequently, only in the right eye.

Follow-up after the atlas-toggle cache invalidation fix:

- The same issue still occurs.
- Bloom can capture a stale startup frame of the skybox and keep applying it even after the camera moves or new scene content renders.

Follow-up after the publish-context fix:

- Bloom updates correctly again.
- The right eye flickers black frequently again.
- The left-eye preview can regress to showing only the top-left part of the fresh eye image over stale previous contents.

Follow-up after the per-eye direct-depth fix:

- Bloom is missing from the main editor view again.
- Both eye preview renders are only visible in the top-left/corner region, not just the left eye.

Follow-up after the framebuffer bind-stack isolation fix:

- Both eye previews could still render only in the top-left region.
- Bloom was present again, but could sample stale contents.
- The exact symptom kept changing between runs, indicating more shared render-scoped state was still leaking across Vulkan/OpenXR recording threads.

Follow-up after the first render-state isolation fix:

- Bloom could disappear from the main editor view.
- TSR appeared inactive even when enabled.
- The right eye still flickered black.
- The VR pickup camera preview in the native UI presented black instead of the pickup camera's scene view.

Follow-up after runtime pass-kind isolation:

- Bloom returned in the main editor view.
- The left eye still only drew a top-left region over stale contents.
- The right eye still flickered black, including intermittent skybox black flicker.

Follow-up after per-thread Vulkan FBO binding isolation:

- The top-left eye preview bug still occurred, but the user observed it only appeared after moving the editor view camera away from the origin.
- The bug could then affect both eyes.
- That implicated editor-camera-driven pipeline state, especially AA/upscale internal-resolution requests, rather than only raw Vulkan FBO binding state.

## Evidence

Fresh logs from `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-08_18-20-14_pid33484` showed that the earlier copy-deferral symptom was gone:

- No repeated `Deferring Vulkan eye mirror copy` / `DeferCopy` warnings.
- `OpenXRPreviewLeftEyeColor` and `OpenXRPreviewRightEyeColor` were both created at `2688x2688 R8G8B8A8Srgb`.

The remaining signal was resource-plan churn in the per-eye external swapchain pipelines:

- `DefaultRenderPipeline#18` and `#19` were generated with OpenXR Vulkan desktop safe-path features.
- Both eye pipelines logged descriptor parity mismatches for `AtmosphereColor` and `VolumetricFogColor`: the declared resources were neutral 1x1 sampled fallbacks, but command-owned passes later materialized full-size `Rgba16f` color attachments with the same names.
- The Vulkan log then repeatedly released descriptor references and force-flushed retired resources with reason `ResourcePlanReplacement`.

Later logs also showed atlas-adjacent churn while OpenXR eye rendering was running:

- Repeated physical allocations for `DirectionalShadow.<id>.Cascade.Hmd.RasterDepthArray`.
- Repeated descriptor releases with reason `ResourcePlanReplacement` and non-zero `commandChainSecondaries`.
- Repeated messages that the OpenXR renderer skipped retired-resource draining because the frame slot was still pending.

The crash stack pointed at `TryExecuteMeshCommandChainSecondaryRun` beginning a command-chain secondary command buffer.

For the remaining right-eye-only skybox flicker, the relevant path was the default pipeline's shared-depth forward pass:

- `DeferredGBufferFBO` writes `DepthStencilTexture`.
- `ForwardPassFBO` shares the same `DepthStencilTexture`.
- In the non-MSAA OpenXR safe path, `ForwardPassFBO` clears color but deliberately does not clear depth so the background skybox can depth-test against G-buffer geometry.

The Vulkan dynamic-rendering recorder correctly asks `QueryCurrentAttachmentLayouts` before binding the FBO so the forward pass can load the G-buffer depth. However, that query only trusted the exact command-buffer subresource layout dictionary. The FBO/texture attachment source and physical image group also track layouts, and those are the state updated by FBO begin/end paths. If the exact dictionary missed the right-eye depth subresource, the forward pass planned `initialLayout=Undefined`, making the dynamic rendering transition discard depth. The skybox then rendered at far depth against undefined/discarded depth and could reject the whole background, leaving the black clear/lighting result visible.

The stale bloom symptom pointed at the same mechanism on a more fragile resource:

- `VPRC_BloomPass` writes and samples different mips of the same `BloomBlurTexture`.
- Downsample passes use `DontCare`, while upsample passes use `Load` so they can blend into the already-written target mip.
- That is only correct if dynamic rendering transitions the exact mip/layer from its true previous layout, not from a guessed whole-image or `Undefined` layout.

Source inspection found that `QueryCurrentAttachmentLayouts` now correctly resolved fallback layouts from the attachment source/physical group, but `TransitionFboAttachmentsForDynamicRendering` threw that answer away when the global exact tracker missed. On begin-rendering transitions, it used `Undefined` instead of the already-resolved fallback old layout. That can discard bloom mips, shared depth, or stereo-eye layers even though another tracker already knew their current layout.

Latest logs from `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-08_20-24-48_pid38132` showed that the bloom publish failure was gone. The active path was still OpenXR Vulkan direct per-eye swapchain compatibility rendering:

- Left/right eye pipelines were generated at the external swapchain extent (`2688x2688`).
- The renderer recorded both direct eye command buffers before a single submit/fence wait.
- Source inspection found both direct eye command buffers still used the same cached OpenXR depth image and image view from `_openXrCachedDepthTarget`.

Latest logs from `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-08_20-36-17_pid55716` changed the shape of the failure:

- Left/right eye pipelines and preview copy textures were all still `2688x2688`.
- The main editor pipeline was `1920x1080` with full post-process features enabled, while the VR preview/overlay pipeline was `300x170`.
- The eye safe path still used intentional 1x1 fallback bloom resources, but the desktop pipeline still declared full bloom resources.

That ruled out small eye image allocation as the current cause. The remaining plausible failure was draw-state contamination: a small offscreen FBO/preview/bloom pass could leak its current draw target extent into command recording for a full-size eye or desktop pass.

Latest source inspection found the direct OpenXR present command does select the external eye region before drawing the final present quad. Mesh/indirect OpenXR swapchain writers are also already validated against the full eye viewport/scissor. The remaining hole was below that pipeline logic:

- `VulkanRenderer.ActiveState` is thread-local during OpenXR eye capture/recording.
- `XRFrameBuffer` bind stacks are thread-local after the earlier fix.
- Vulkan's own `_boundDrawFrameBuffer`, `_boundReadFrameBuffer`, and `_readBufferMode` were still renderer instance fields.
- `ResolveCurrentDrawTargetExtent()` falls back to the Vulkan bound draw FBO when no scoped render-target binding or `XRFrameBuffer.BoundForWriting` is active.

That meant a desktop/native preview, pickup camera, or small post-process helper could still race the OpenXR worker and make the final present quad snapshot a viewport computed against the wrong FBO size. A wrong viewport updates only the corner of the full eye image, leaving stale pixels elsewhere.

## Root Cause

`DeclarePostProcessResources` correctly declares 1x1 sampled fallback textures for `AtmosphereColor` and `VolumetricFogColor` in the OpenXR Vulkan desktop-safe path.

The active command chain already skips `AppendPostProcessCompositeInputDefaults` in that safe path, but `ShouldRunAtmosphericScattering` and `ShouldRunVolumetricFog` did not check the same safe-path guard. When those settings were active, their full effect chains replaced the fallback textures with full-resolution render targets, forcing descriptor/resource-plan replacement during eye rendering.

For the remaining flicker, the directional-light Vulkan atlas path still let HMD cascade shadow code lazily create and replace the legacy per-light receiver array. Atlas pages should own Vulkan cascade shadow rendering, but the legacy receiver was still being requested by binding/compatibility paths. That produced repeated shadow-array allocation/replacement while OpenXR eye command buffers and command-chain secondaries were alive.

The access violation was a separate lifetime bug made easier to hit by the churn: `DestroyCommandChainSecondaryCommandBuffer` freed secondary command buffers and destroyed their owned command pool immediately. A submitted primary can still reference those secondaries until its frame slot retires, so later command-buffer recording could touch invalid Vulkan command-pool state.

The stale-bloom/right-eye recurrence after those fixes was a dynamic-rendering layout bug:

- FBO layout querying had two sources of truth: the global exact image-subresource dictionary and the attachment/physical-image-group tracker.
- The render-pass signature used the attachment fallback, but the explicit dynamic-rendering barrier did not.
- If the exact dictionary missed a bloom mip or stereo layer, the barrier used `oldLayout=Undefined`, discarding contents or creating a layout mismatch before a pass that expected `Load`/sampled contents.
- Cached primary command-buffer reuse also did not include physical image-group subresource layouts in its start-state signature, so a primary could look reusable when the whole-image layout marker matched but individual mips/layers did not.

The renewed right-eye/preview flicker had a separate direct OpenXR target bug:

- Direct per-eye swapchain rendering created unique color swapchain targets for each eye, but reused one singleton `_openXrCachedDepthTarget` for both eyes.
- The left and right command buffers are recorded independently and then submitted as a batch. Sharing the depth attachment makes depth contents and layout state cross-eye mutable state.
- Resource churn from shadow atlasing or descriptor/resource replacement made the shared-depth hazard easier to see as right-eye black flicker and stale/partial preview copies.

The latest top-left eye previews and missing desktop bloom were a render-state isolation bug:

- `XRFrameBuffer.BoundForReading`, `BoundForWriting`, and `CurrentlyBound` were backed by process-global static stacks.
- Vulkan command recording already uses thread-local render/viewport state for OpenXR eye work, but `ResolveCurrentDrawTargetExtent()` consulted `XRFrameBuffer.BoundForWriting`.
- If another render path had a small offscreen FBO bound, such as the 300x170 VR preview/overlay or a post-process helper, the Vulkan recorder could snapshot viewport/scissor/target extent from that stale global FBO instead of the active pipeline render target.
- This explains why resources were correctly allocated full-size while only the top-left of the image updated: the draw area, not the image, was wrong.

The remaining run-to-run instability had the same root shape in higher-level render state:

- `Engine.Rendering.State.RenderingPipelineStack` and `TransformIdStack` were process-global static stacks.
- `Engine.Rendering.State.RenderingCameraOverride`, `IsLightProbePass`, `IsSceneCapturePass`, `MirrorPassIndex`, `ReverseWinding`, and `ReverseCulling` were render-scoped values stored globally.
- `Engine.Rendering.State.CurrentRenderGraphPassIndex` was `[ThreadStatic]`, but relied on a static constructor to initialize `int.MinValue`. Static constructors only run once per type, not once per thread, so fresh worker threads could see `0` as an apparently valid pass index.
- `RuntimeEngine.Rendering.RuntimeRenderingState` kept render-graph pass, camera override, transform id, and mirror pass stacks as instance fields, so all runtime rendering threads shared them.
- `RuntimeEngine.Rendering.State.CurrentRenderingPipeline` fell back to the host's current render pipeline context even on non-render worker threads. A Vulkan worker should only see a pipeline when command recording explicitly pushes one.

Those leaks explain why each run appeared to break something slightly different. The full-size eye images and bloom textures were often allocated correctly, but worker command recording could inherit a camera, pass index, transform id, mirror state, or small render-area context from another render path depending on timing. The visible result then varied between stale bloom contents, top-left-only rendering, skybox/right-eye black flicker, or apparently recovered frames.

The later bloom/TSR/native-preview symptoms exposed another layer in the runtime facade:

- `RuntimeEngine.Rendering.State.IsSceneCapturePass` and `IsLightProbePass` delegated to instance/global state, not thread-local state.
- `RuntimeEngine.Rendering.State.ReverseWinding` and `ReverseCulling` were process-global static auto-properties.
- Bloom and temporal accumulation explicitly skip work during scene-capture/light-probe passes, so a pickup camera, native UI preview, mirror, or probe render could leave the main editor/eye pass observing capture state.
- The runtime mirror-pass helpers only pushed a mirror index. They did not apply scene-capture/reverse-culling semantics consistently or restore the previous capture state when unwound.

That explains why "no bloom", "no AA while TSR is enabled", black native pickup preview, and eye flicker could trade places between runs: they were all reading pass-kind state that belonged to another render path.

The remaining left-eye top-left symptom exposed a lower-level Vulkan state leak:

- The final OpenXR eye writer is usually a fullscreen present quad, so it snapshots Vulkan viewport/scissor at draw enqueue time.
- The scoped pipeline render-target binding and `XRFrameBuffer` binding stacks had been isolated, but Vulkan's backend `_boundDrawFrameBuffer`, `_boundReadFrameBuffer`, and `_readBufferMode` fields were still shared across threads.
- `ResolveCurrentDrawTargetExtent()` can use `_boundDrawFrameBuffer` to convert engine render areas into Vulkan viewports.
- If a small preview or offscreen FBO updates `_boundDrawFrameBuffer` while an OpenXR eye worker captures the present quad, the eye writer can get a small viewport on a full-size swapchain image. That produces exactly a fresh top-left rectangle over stale old contents.
- Because the race depends on pass timing, enabling/disabling directional shadow atlasing changes the symptom frequency without being the direct cause.

The editor-camera trigger exposed a second top-left mechanism:

- `XRViewport.AllowAutomaticInternalResolution` had been added to keep caller-sized render targets exact, but the OpenXR eye viewports never opted out.
- `EnsureOpenXrViewportExtent(...)` validated the eye viewport and internal resolution before rendering, but `XRRenderPipelineInstance.Render(...)` could later apply TSR/AA/upscale requested internal-resolution scaling while the eye pass was executing.
- Moving the editor camera changes temporal/upscale state and makes this much more likely to activate.
- The eye swapchain image remained full-size, but the internal/post-process chain could render or blit only the scaled region, leaving the rest of the full eye image stale.

The direct preview copy also had a layout stability issue:

- `RecordOpenXrEyeSwapchainPreviewCopy(...)` always transitioned the acquired OpenXR source image from `ColorAttachmentOptimal`.
- Direct eye rendering normally leaves acquired images in `ColorAttachmentOptimal`, but a skipped/failed/stale frame can leave the tracked layout somewhere else.
- A hard-coded source layout can make the preview/mirror path sample stale or black data even after the actual eye render recovers.

## Fix

Updated `DefaultRenderPipeline.CommandChain.cs` so both atmospheric scattering and volumetric fog return `false` in `UseOpenXrVulkanDesktopStartupSafePath`.

Added a source-contract regression in `OpenXrTimingPipelineContractTests` proving that:

- The OpenXR safe eye pass keeps the neutral fallback declarations.
- The atmosphere/fog gates run the safe-path guard before reading post-process state.
- The full atmosphere/fog command chains still create full-size outputs, so the guard remains necessary.

Updated `DirectionalLightComponent.CascadeShadows.cs` so the Vulkan atlas cascade path does not allocate or bind the legacy cascade receiver array:

- `GetCascadedShadowReceiverTexture` returns `null` when Vulkan atlas cascade targets are active.
- `EnsureCascadeShadowResources` releases old legacy receivers in atlas mode and only ensures cascade camera/viewport slots.
- Cascade update code accepts atlas-owned targets without forcing a per-light receiver texture.

Updated command-chain secondary lifetime handling:

- Owned command-chain secondary pools can be marked pending-destroy.
- Secondary command buffers are deferred through the normal frame-slot retired-resource path when an image index is known.
- Pending owned pools are destroyed only after all tracked/deferred secondaries from that pool have been released.
- The command-chain lowering path no longer discards deferred secondaries or destroys the owned pool inline.

Updated `VulkanRenderer.CommandBufferRecording.cs` so `QueryCurrentAttachmentLayouts` still prefers exact tracked image-subresource layouts, but falls back to `ResolveFboAttachmentOldLayout(...)` when the exact dictionary has no entry. That lets the forward pass preserve shared depth from the FBO attachment source/physical image group instead of treating the attachment as undefined and discarding it.

Follow-up for atlas toggle recovery:

- `UseSpotShadowAtlas`, `UseDirectionalShadowAtlas`, and `UsePointShadowAtlas` now call `NotifyRenderResourcesChanged(...)` on the active renderer as soon as the setting changes. Atlas mode changes can swap sampled shadow resources and render targets, so they must invalidate cached command-buffer state before the next OpenXR eye tries to reuse it.
- `ReleaseDescriptorReferencesForPhysicalResourceDestruction(...)` now dirties OpenXR primary command-buffer variants unconditionally after clearing descriptor/framebuffer/layout state. Previously this only happened when command-chain secondaries were counted as invalidated. If a resource-plan replacement cleared descriptors/layouts but found no executable secondary to invalidate, an OpenXR eye primary could still replay against stale resource state until the cache naturally aged out.

Follow-up for stale bloom/right-eye recurrence:

- `TransitionFboAttachmentsForDynamicRendering(...)` now uses `ResolveFboAttachmentOldLayout(..., requestedOldLayout)` whenever the exact global tracker misses, including begin-rendering transitions. It only uses `Undefined` when the requested old layout is also truly `Undefined`.
- Physical image-group layout snapshots now sort subresource entries, and `ComputeImageLayoutStateSignature()` folds each physical group mip/layer layout into the command-buffer reuse signature. Reuse can no longer ignore a per-mip/per-layer layout change just because the whole-image layout marker is unchanged.

Follow-up for direct eye flicker:

- Replaced the singleton OpenXR direct-eye depth cache with two cached depth targets, indexed by OpenXR view.
- `TryPrepareOpenXrEyeSwapchainCommandBuffer(...)` now resolves depth with `request.OpenXrViewIndex`, so the left and right direct swapchain command buffers no longer share a depth image/view.
- OpenXR rendering teardown now destroys both cached depth targets.

Follow-up for top-left eye previews/missing desktop bloom:

- `XRFrameBuffer` read/write/general bind stacks are now thread-local, matching Vulkan's thread-local active render state.
- The Vulkan draw-target resolver now prefers the active pipeline's scoped render-target binding before falling back to direct FBO binds and the backend's last-bound FBO.
- Added `XRFrameBufferBindingStackTests.BoundFrameBuffers_AreThreadLocal` to lock down that a fresh render-recording thread cannot see a framebuffer bound by another thread.

Follow-up for editor-camera-triggered top-left eye previews:

- OpenXR left/right eye viewports now set `AllowAutomaticInternalResolution = false` when created.
- `EnsureOpenXrViewportExtent(...)` reasserts `AllowAutomaticInternalResolution = false` every frame, so existing viewports cannot be resized by camera AA/upscale settings after validation.
- The true single-pass stereo staging viewport also disables automatic internal-resolution scaling.
- Direct OpenXR eye preview-copy plans now capture the source swapchain image's tracked layout and transition from that layout instead of assuming `ColorAttachmentOptimal`.

Follow-up for remaining render-state leaks:

- `Engine.Rendering.State` now stores its active pipeline stack, transform id stack, render-graph pass index, camera override, scene-capture/light-probe flags, mirror index, and reverse winding/culling flags per thread.
- `CurrentRenderGraphPassIndex` now uses a nullable thread-local backing field so a fresh thread correctly reports `int.MinValue` instead of defaulting to pass `0`.
- `PushRenderingPipelineOverride(null)` now has an explicit thread-local "override set" flag, so a deliberate null override is distinguishable from "no override on this thread."
- `RuntimeEngine.Rendering.RuntimeRenderingState` now stores its render-graph pass, camera override, transform id, and mirror pass stacks per thread.
- `RuntimeEngine.Rendering.State.CurrentRenderingPipeline` now prefers thread-local override/stack state and only falls back to the host pipeline context on the render thread. Vulkan worker threads must receive their pipeline through an explicit push.
- Added `RenderStateThreadIsolationTests` to verify both the legacy `Engine.Rendering.State` facade and the runtime rendering-state facade are isolated across worker threads.

Follow-up for capture/probe pass leakage:

- `RuntimeEngine.Rendering.RuntimeRenderingState` now stores scene-capture, light-probe, reverse-winding, and reverse-culling state per thread.
- `RuntimeEngine.Rendering.State.ReverseWinding` and `ReverseCulling` now delegate to the same runtime state object instead of static auto-properties.
- Runtime mirror passes now snapshot the previous capture/reverse-culling state, apply active mirror semantics, and restore the prior state on pop/dispose.
- `RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(null)` now pushes an explicit null override instead of becoming a no-op.
- `RenderStateThreadIsolationTests` now covers runtime pass-kind/reverse flags and mirror-pass restoration.

Follow-up for Vulkan backend FBO binding leakage:

- Vulkan now scopes `_boundDrawFrameBuffer`, `_boundReadFrameBuffer`, and `_readBufferMode` per recording thread when `ThreadRenderStateScope` is active.
- The thread render-state scope also pushes the active renderer into `AbstractRenderer.Current`, so frame-op capture and off-thread command recording resolve the same Vulkan renderer instead of falling back to a desktop/editor renderer.

Follow-up for explicit null camera capture:

- `XRQuadFrameBuffer.Render()` intentionally pushes `PushRenderingCamera(null)` before drawing the fullscreen present quad.
- `VkMeshRenderer` previously treated that explicit null the same as "no camera scope" and fell back to the current pipeline or last scene camera.
- That made the final OpenXR eye writer snapshot scene/eye camera matrices for a clip-space fullscreen quad. Depending on the leaked viewport/target state at that moment, the eye swapchain could update only a top-left region while stale pixels remained elsewhere.
- `RenderingState` now exposes `HasRenderingCameraScope`, and Vulkan mesh snapshot capture uses it to preserve an explicit null camera scope. Right-eye stereo camera fallback is also skipped when the base camera snapshot is null.

Follow-up for command-buffer dirtying during frame-op capture:

- Logs from `xrengine_2026-07-08_21-44-54_pid53224` showed command buffers being marked dirty thousands of times per second while OpenXR rendering was active (`CollectBuffers` and mesh buffer sync reasons dominated).
- `MarkCommandBuffersDirtyForLegacyMeshState(...)` now also suppresses legacy primary invalidation while `t_frameOpCapture` is active. Frame-op capture already records the exact operation/resource signature; dirtying reusable primaries during capture reintroduced timing-dependent reuse and black/stale eye frames.

- Added thread-local Vulkan framebuffer binding state owner/slots for the backend bound draw FBO, bound read FBO, and read-buffer mode.
- `ThreadRenderStateScope` now starts with isolated null FBO bindings and restores the previous thread-local binding state on dispose.
- `BindFrameBuffer`, `SetReadBuffer`, draw-target extent resolution, swapchain extent changes, and readback paths now use the active thread-local binding state when an OpenXR/thread render state scope is active.
- Added `VulkanThreadRenderStateScope_IsolatesFramebufferBindings` to lock down that OpenXR worker render-state scopes cannot inherit small desktop/native-preview FBO bindings.

## Validation

Focused tests:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanOpenXr_EyePreviewCopyUpdatesReadyTargetsDuringAllocatorPressure|FullyQualifiedName~VulkanOpenXr_PreviewTargetFormatFallbackDoesNotRecurse|FullyQualifiedName~VulkanOpenXr_EyeRenderingGateDoesNotInheritDesktopAllocationBackoff|FullyQualifiedName~VulkanOpenXr_SafeEyePassDoesNotReplacePostProcessFallbacksWithAtmosphereFogOutputs" --no-restore -p:BaseOutputPath="<run>\temp-build\bin\"
```

Result: passed, 4/4.

Additional focused tests:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=DirectionalDepthCascadeAtlasFallbacks_KeepReceiverArrayBoundOutsideVulkanAtlas" --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=VulkanOpenXr_SafeEyePassDoesNotReplacePostProcessFallbacksWithAtmosphereFogOutputs|Name=VulkanOpenXr_EyePreviewCopyUpdatesReadyTargetsDuringAllocatorPressure" --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait" --no-restore
```

Result: passed, 1/1, 2/2, and 1/1 respectively.

Right-eye skybox fallback test:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=VulkanFboLayoutQuery_FallsBackToAttachmentSourceTrackedLayout" --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-181208-eye-preview-validation\temp-build\bin\"
```

Result: passed, 1/1.

The same focused test without `BaseOutputPath` compiled but failed during MSBuild copy-to-editor-output because the live editor/debug adapter had locked `Build/Editor/.../XREngine.dll` and `XREngine.Runtime.Rendering.dll`.

A broader source-contract filter was also run and compiled, but failed on unrelated stale assertions outside this fix area, including point-shadow resource names, Monado URL expectations, editor UI z-index expectations, and other existing source-contract checks.

Narrow build:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
```

Result: passed with existing Magick.NET vulnerability warnings.

Vulkan thread-local FBO binding build:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false
```

Result: passed with existing Magick.NET vulnerability warnings.

Vulkan thread-local FBO binding focused tests:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanThreadRenderStateScope_IsolatesFramebufferBindings|FullyQualifiedName~BoundFrameBuffers_AreThreadLocal" --no-restore -m:1 -p:UseSharedCompilation=false -p:BaseOutputPath="Build\_AgentValidation\20260708-213911-vulkan-thread-fbo-state\temp-build\bin\"
```

Result: passed, 2/2.

The same test run with `ExternalSwapchainPlannerExtents_AreAuthoritativeOverDesktopPipelineExtents` included compiled from scratch but failed on an existing stale source-contract path: it expects `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`, while the current file lives under `Types/Default/DefaultRenderPipeline.cs`. A run without `BaseOutputPath` also failed before tests because the live editor/debug adapter had locked normal `Build/Editor/...` output files.

Atlas-toggle cache invalidation tests/build:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=ShadowAtlasSettings_DirtyRendererResourcesForOpenXrPrimaryReuse|Name=VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait|Name=VulkanFboLayoutQuery_FallsBackToAttachmentSourceTrackedLayout" --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-181208-eye-preview-validation\temp-build\bin\"
dotnet build .\XRENGINE\XREngine.csproj --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-181208-eye-preview-validation\temp-build\xrebuild\"
```

Result: tests passed, 3/3. Temp-output `XRENGINE` build passed with existing Magick.NET vulnerability warnings. A regular `dotnet build .\XRENGINE\XREngine.csproj --no-restore` attempt failed only because `VBCSCompiler` held `XREngine.Runtime.Rendering.dll` in `obj`; the temp-output build avoided that local lock.

Stale bloom/dynamic-rendering layout tests/build:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=VulkanDynamicRenderingFboTransition_UsesQueriedAttachmentOldLayoutFallback|Name=VulkanImageLayoutReuseSignature_IncludesPhysicalSubresourceLayouts|Name=VulkanFboLayoutQuery_FallsBackToAttachmentSourceTrackedLayout|Name=ShadowAtlasSettings_DirtyRendererResourcesForOpenXrPrimaryReuse" --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-181208-eye-preview-validation\temp-build\bin\"
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-181208-eye-preview-validation\temp-build\rendering-build\"
```

Result: tests passed, 4/4. Rendering project build passed with existing Magick.NET vulnerability warnings.

Direct eye per-eye depth tests/build:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=VulkanOpenXr_DirectEyeSwapchainsUsePerEyeDepthTargets|Name=VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait" --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-202900-openxr-eye-flicker\temp-build\openxr-eye-contracts\"
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-202900-openxr-eye-flicker\temp-build\rendering-build\"
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Result: tests passed, 2/2. Rendering project build and normal editor build passed with existing Magick.NET vulnerability warnings.

Framebuffer state isolation tests/build:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=BoundFrameBuffers_AreThreadLocal|Name=VulkanOpenXr_DirectEyeSwapchainsUsePerEyeDepthTargets" --no-restore -m:1 -p:UseSharedCompilation=false -p:BaseOutputPath="Build\_AgentValidation\20260708-204200-openxr-eye-corner-bloom\temp-build\tests\"
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -p:BaseOutputPath="Build\_AgentValidation\20260708-204200-openxr-eye-corner-bloom\temp-build\rendering-build\"
```

Result: tests passed, 2/2. Rendering project build passed with existing Magick.NET vulnerability warnings. An earlier parallel test/build attempt failed because `VBCSCompiler` locked `XREngine.Data.dll` in `obj`; rerunning after `dotnet build-server shutdown` and disabling shared compilation produced the clean pass above.

Render-state isolation tests/build:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=RuntimeRenderingState_StacksAreThreadLocal|Name=LegacyEngineRenderingState_StacksAreThreadLocal|Name=BoundFrameBuffers_AreThreadLocal" --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false
```

Result: tests passed, 3/3. Runtime rendering project build passed with existing Magick.NET vulnerability warnings. Earlier temp-output test attempts created large project-local validation outputs and hit disk space; those disposable `Build/_AgentValidation/20260708-204200-openxr-eye-corner-bloom` build folders were cleaned before rerunning with normal build outputs.

Runtime capture/probe isolation tests/build:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=RuntimeRenderingState_StacksAreThreadLocal|Name=RuntimeRenderingState_PassFlagsAreThreadLocal|Name=RuntimeRenderingState_MirrorPassRestoresPreviousCaptureState|Name=LegacyEngineRenderingState_StacksAreThreadLocal|Name=BoundFrameBuffers_AreThreadLocal" --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false
git diff --check
```

Result: tests passed, 5/5. Runtime rendering project build passed with existing Magick.NET vulnerability warnings. `git diff --check` reported only line-ending normalization warnings for touched files.

Explicit null-camera/frame-op capture validation:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=OpenXrFrameOpCapture_RespectsExplicitNullCameraAndScopedRenderer|Name=VulkanThreadRenderStateScope_IsolatesFramebufferBindings" --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false
git diff --check
```

Result: tests passed, 2/2. Runtime rendering project build passed with existing Magick.NET vulnerability warnings. The build recovered from a transient copy retry on `XREngine.Runtime.Rendering.dll` and ended with 0 errors. `git diff --check` passed, with only line-ending normalization warnings.

Attempted editor build:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Result: failed at copy-to-output because the currently running editor/debug adapter locked DLLs in `Build/Editor/...`. The compile path had already been validated by the focused test build and render-project build; close/restart the editor before refreshing the default editor output.

Right-eye black flicker / parallel eye cross-feed pass:

Findings:

- The fixed top-left/stale preview issue was caused by fullscreen present/post-process quads intentionally pushing an explicit null camera while Vulkan mesh draw capture fell back to the scene/eye camera. That made fullscreen quads capture scene-camera state and render like geometry. `RenderingState.HasRenderingCameraScope` now lets Vulkan distinguish intentional null camera scopes from missing camera state.
- The remaining mode-dependent right-eye flicker points at per-eye Vulkan command-buffer/resource identity, not GPU submit ordering. Sequential mode submits and waits each eye independently, while batch and parallel modes record both eyes before one submit; no submit path was found that releases an eye before its command buffer fence completes.
- Direct OpenXR eye rendering used a synthetic swapchain image index for both eyes, but its swapchain target begin layout was still derived from desktop "ever presented" logic. External OpenXR eye images now resolve the actual tracked target image layout at command-buffer record time.
- Scheduled command-chain secondaries were mapped back to frame ops by group/key order. Mixed clear/blit/compute/draw op lists can make that positional mapping stale, so the lookup now maps by each recorded `CommandChain.SourceStartIndex`/`SourceCount` and uses an unmapped sentinel.

Validation:

```powershell
$out = (Resolve-Path 'Build\_AgentValidation\20260708-221711-openxr-eye-flicker\temp-build').Path + '\test-bin\'
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=BuildCommandChainKeysByFrameOpIndex_UsesRecordedSourceIndices|Name=OpenXrDirectEyeSwapchain_UsesTrackedExternalTargetInitialLayout|Name=OpenXrFrameOpCapture_RespectsExplicitNullCameraAndScopedRenderer|Name=VulkanThreadRenderStateScope_IsolatesFramebufferBindings" --no-restore -m:1 -p:UseSharedCompilation=false -p:OutDir=$out

$out = (Resolve-Path 'Build\_AgentValidation\20260708-221711-openxr-eye-flicker\temp-build').Path + '\rendering-bin\'
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:OutDir=$out

git diff --check
```

Result: focused tests passed, 4/4. Runtime rendering project build passed with existing Magick.NET vulnerability warnings. `git diff --check` passed, with only line-ending normalization warnings.

Parallel eye recording target-identity pass:

Finding:

- OpenXR direct eye rendering entered an external swapchain scope with only the target extent. Frame-op capture therefore recorded null framebuffer swapchain writes with target identity `0` unless another field happened to separate the eyes. Command-chain grouping, command-chain fast schedule signatures, scheduling identity, and primary frame-op signatures could all treat left/right external swapchain writes as the same target. That explains why sequential, single-pass, and parallel buffer recording changed the symptom: each mode changed whether stale primary/secondary cache state was reused or re-recorded.

Fix:

- The OpenXR external swapchain scope now carries a thread-local output target identity/name derived from the OpenXR view and acquired image. `FrameOpContext` captures that identity for external targets, includes it in scheduling identity, and command-chain/primary signatures use it when the frame op has no concrete framebuffer target.

Validation:

```powershell
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~NullSwapchainFrameOps_UseExternalOutputTargetIdentity" -p:OutDir=Build\_AgentValidation\temp-build\vulkan-eye-target-identity-single\
dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -p:OutDir=Build\_AgentValidation\temp-build\vulkan-eye-target-identity-rendering-build\
git diff --check
```

Result: focused regression test passed, 1/1. Runtime rendering project build passed with existing Magick.NET vulnerability warnings. A broader `VulkanCommandChainDataModelTests` run compiled but still has unrelated source-contract failures in this working tree; `git diff --check` passed, with only line-ending normalization warnings.

OpenXR external swapchain command-chain bypass:

Finding:

- External OpenXR swapchain targets were still forced through command-chain recording by `CommandChainsEnabledForCurrentRecording`, even when command chains were otherwise disabled. That meant the earlier target-identity fix separated left/right cache keys, but the eyes could still run through the stale secondary-command-buffer path. This matches the mode-dependent behavior: sequential, single-pass, and parallel eye recording changed how often stale secondaries were reused or re-recorded, so the visible issue shifted between right-eye black flicker, top-left partial draws, and cross-eye leakage.

Fix:

- External swapchain targets no longer force command chains. OpenXR eye swapchain recording now stays on the inline primary-command-buffer path until command-chain recording is made explicitly safe for per-eye external targets.

Validation:

```powershell
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=NullSwapchainFrameOps_UseExternalOutputTargetIdentity|Name=OpenXrExternalSwapchainTargets_DoNotForceCommandChains" --no-restore -m:1 -p:UseSharedCompilation=false --logger "console;verbosity=minimal"
dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false --verbosity:minimal
git diff --check
```

Result: focused tests passed, 2/2. Runtime rendering project build passed with existing Magick.NET vulnerability warnings. `git diff --check` reported only line-ending normalization warnings for touched files.

OpenXR swapchain blit target pass:

Finding:

- `RecordCommandBuffer` correctly resolved OpenXR eye command buffers to an external `SwapchainRecordingTarget`, but `RecordBlitOp` still resolved `InFbo == null` or `OutFbo == null` through the desktop swapchain image array. A final/post-process blit captured for an eye could therefore write the desktop swapchain instead of the OpenXR image. The eye image would keep whatever direct draw/clear work happened before the blit, which matches the fresh top-left area over stale pixels and the mode-dependent right-eye black flicker.
- OpenXR external validation also checked only mesh/indirect swapchain writers. Swapchain-targeted `BlitOp`s could carry a desktop/internal destination rectangle into a full-size eye image without being rejected.

Fix:

- `RecordBlitOp` now receives the active `SwapchainRecordingTarget` from `RecordCommandBuffer`, and null-framebuffer blit endpoints resolve against that active target before falling back to the desktop swapchain.
- OpenXR external frame ops now normalize swapchain blit destinations to the full eye extent before planning, and validation explicitly rejects any partial `BlitOp { OutFbo: null }` writer that survives.

Validation:

```powershell
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=SwapchainBlits_UseActiveCommandBufferRecordingTarget|Name=OpenXrExternalSwapchainBlits_AreNormalizedAndValidatedAsFullEyeWriters|Name=OpenXrEyePrimaryRecording_PassesTargetContextIntoCommandBufferRecording" --no-restore -m:1 -p:UseSharedCompilation=false --logger "console;verbosity=minimal"
dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false --verbosity:minimal
```

Result: focused tests passed, 3/3. Runtime rendering project build passed with existing Magick.NET vulnerability warnings.

OpenXR per-eye pipeline command-chain isolation:

Finding:

- The OpenXR eye viewports were using separate `XRRenderPipelineInstance`s, but the external per-eye path still assigned the same OpenXR-owned `RenderPipeline` object to both left and right viewports.
- `RenderPipeline` owns the `CommandChain` and the command objects in that chain. Those command objects are allowed to cache per-view/per-pass helper state while lowering to Vulkan frame ops.
- This explains the remaining symptom shape: moving the editor camera changes frame/pipeline state, and either eye can independently start presenting only the freshly updated top-left region over stale contents. Parallel recording made the leak more obvious because both eyes could execute the same command-chain objects close together, allowing left/right frame-op state to cross-feed.

Fix:

- External OpenXR eyes now keep separate left/right OpenXR `RenderPipeline` objects (`_openXrLeftRenderPipeline` and `_openXrRightRenderPipeline`), so each eye has its own command-chain object graph.
- The shared mesh visibility collection remains shared, but only the immutable/snapshot command list is shared; the render pipeline command objects that emit frame ops are now per-eye.
- The eye cameras now receive their matching per-eye pipeline instead of both pointing at one shared non-stereo pipeline. True single-pass stereo still uses one stereo pipeline intentionally.

Validation:

```powershell
dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:OutDir="Build\_AgentValidation\20260708-235140-openxr-pipeline-split\rendering-bin\" --verbosity:minimal
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~OpenXrExternalEyes_UseIndependentPipelineCommandChains" --logger "console;verbosity=minimal"
```

Result: runtime rendering project build passed with existing Magick.NET vulnerability warnings. The exact new regression test passed, 1/1. A broader `VulkanP1ValidationTests` run is still noisy in this working tree because of unrelated existing source-contract failures; a buildful exact test run also hit a live-editor DLL lock from `.NET Host (63720)`, so the final exact run used `--no-build` after the prior test build had produced the updated test assembly.

OpenXR shared visibility and occlusion ownership:

Finding:

- User repro narrowed the "top-left over stale pixels" bug to moving the main editor camera close to/through a wall, which strongly implicated culling/occlusion instead of swapchain extent math alone.
- The combined OpenXR eye visibility collection was still allowed to be non-authoritative when an independent desktop VR view existed. If the desktop view collected first and set a command's authoritative publish marker, then the OpenXR shared eye collection could swap before the desktop view and skip publishing that command snapshot. The eye would then render from an older snapshot until another ordering happened to repair it. Moving the editor camera through walls changes the desktop visible set, making this race much easier to trigger.
- The external OpenXR shared-visibility path also bypassed normal frustum/BVH culling, but still ran per-eye occlusion refine. That meant a combined stereo visible set could be trimmed using one eye's depth/temporal occlusion state, turning the shared set back into a view-dependent, timing-sensitive set.
- The generic two-pass VR shared command collection had the same non-authoritative desktop dependency, so it was hardened at the same time.

Fix:

- OpenXR shared eye mesh commands are always `IsRenderCommandSnapshotAuthority = true`.
- The generic VR shared stereo command collection is also always snapshot-authoritative; passive/non-authoritative command collections are now documented as inappropriate for independently scheduled VR/desktop views.
- External OpenXR shared-visibility passes now skip per-eye occlusion refinement and record an `ExternalVrSharedVisibility` GPU Hi-Z skip reason instead of mutating the combined stereo visible set.

Validation:

```powershell
dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:OutDir="Build\_AgentValidation\20260709-openxr-shared-visibility-occlusion\temp-build\rendering-bin2\" --verbosity:minimal
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=OpenXrSharedEyeCommands_AreSnapshotAuthority|Name=ExternalOpenXrSharedVisibility_SkipsPerEyeOcclusionRefine|Name=OpenXrExternalEyes_UseIndependentPipelineCommandChains" --no-build --no-restore --logger "console;verbosity=minimal"
```

Result: runtime rendering project build passed with existing Magick.NET vulnerability warnings. The focused eye-path guard tests passed, 3/3. A broader four-test run including `RuntimeVrDesktopView_DoesNotReuseEyeCommandsOrEditorImGuiWhenDesktopEditingDisabled` still fails on an existing `Engine.RuntimeRenderingHostServices.cs` mirror-mode source-contract assertion unrelated to this OpenXR eye ownership fix.

OpenXR/pickup preview render-area empty-stack leak:

Finding:

- The pickup camera preview rendered black on startup with the default pipeline, recovered when switched to `DebugOpaqueRenderPipeline`, and then worked when switched back to the default pipeline until moving the editor camera again.
- That connects the pickup preview and the left-eye top-left crop: the pickup preview is a small offscreen scene-capture/default-pipeline render, while the eye preview is a full external swapchain render that later snapshots Vulkan viewport state.
- The pipeline render-area stack restored the previous render area when nested, but when the stack became empty it only stopped tracking a render region. It did not tell the backend renderer to clear/reset the explicit viewport.
- Vulkan's active state keeps `_viewportExplicitlySet` plus `_viewportRegion`; if no one clears that flag, `GetViewport(...)` continues converting the last tiny offscreen region against whatever target extent is active next.
- Debug opaque avoided enough of the full default scene-capture/post-process path to temporarily mask the leaked viewport. Moving the editor camera changes culling/visibility and causes more frame-op recapture, so the stale tiny viewport could be captured again by the left-eye writer.

Fix:

- Added `AbstractRenderer.ClearRenderArea()` as the explicit "render-area stack is empty" signal.
- `XRRenderPipelineInstance.RenderingState.PopRenderArea()` now calls `ClearRenderArea()` when the render-area stack becomes empty, instead of leaving the backend's last explicit render area alive.
- `PopIndexedViewportScissors()` also restores or clears the normal render area after indexed viewport/scissor state is popped.
- Vulkan overrides `ClearRenderArea()` to clear `_viewportExplicitlySet` and discard `_viewportRegion`, so later draws fall back to the active target's full extent.
- Added `RenderAreaStackClearsVulkanExplicitViewportWhenEmpty` to guard the contract.

Validation:

```powershell
dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -m:1 -p:UseSharedCompilation=false --verbosity:minimal
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=RenderAreaStackClearsVulkanExplicitViewportWhenEmpty|Name=OpenXrSharedEyeCommands_AreSnapshotAuthority|Name=ExternalOpenXrSharedVisibility_SkipsPerEyeOcclusionRefine" --logger "console;verbosity=minimal"
git diff --check -- XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderStateMutation.cs XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs XREngine.UnitTests/Rendering/VulkanP1ValidationTests.cs
```

Result: runtime rendering project build passed with existing Magick.NET vulnerability warnings. Focused tests passed, 3/3. `git diff --check` passed with only line-ending normalization warnings.
