# OpenXR Monado OpenGL Rendering - 2026-06-25

## Problem

OpenXR Monado testing on the OpenGL path showed several symptoms:

- The editor could freeze or stall during the run.
- The editor render and Monado eye renders did not match; Monado eyes were missing the deferred-lit final image.
- Shadow mapping appeared absent in the visible output.
- The editor eye preview overlay was vertically squished.

The Vulkan run after this was intentionally ignored. The relevant OpenGL run was:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_10-30-51_pid7504/`

## Evidence

- Monado reported portrait eye swapchains: `OpenXR view[0/1] recommended size: 896x1007`.
- The preview UI forced every eye texture into a `300x170` region, causing the visible squish.
- The OpenGL log repeatedly reported:
  - `GL_INVALID_VALUE error generated. <texture> is not the name of an existing texture.`
  - `GL_INVALID_FRAMEBUFFER_OPERATION error generated. Framebuffer bindings are not framebuffer complete.`
- The OpenXR frame stack pointed at `OpenXRAPI.RenderViewportsToSwapchain`, where the per-frame blit reattached the runtime swapchain texture handle to a utility draw FBO.
- The OpenXR OpenGL swapchain image array was allocated with unmanaged memory and only the `Type` field was initialized before `xrEnumerateSwapchainImages`, leaving `Next` and other struct fields dependent on stale memory.
- `VPRC_RenderToWindow` always unbound to the default backbuffer before drawing the final image. During OpenXR eye rendering the pipeline was rendering into `_viewportMirrorFbo`, so the deferred/post final image could be sent to the editor backbuffer instead of the mirror texture that gets copied to Monado.
- Shadow logs showed shadow atlas work was occurring and shadow textures were present (`ShadowMapEnabled=True`, `directionalAtlasSampleable=True`), so the most direct visible-output issue was the final image not landing in the eye mirror target.
- OpenGL shader async logs showed very slow shared-context links, including 50s+ links. That can still cause startup pressure, but the repeated GL debug errors and incomplete FBOs were the hard failure loop fixed here.
- Follow-up OpenGL run:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_10-59-22_pid10860/`
- The follow-up run confirmed OpenXR OpenGL is rendering left/right eyes in two-pass mode, with a separate desktop render path (`VRState callbacks: CollectVisible=CollectVisibleTwoPass, SwapBuffers=SwapBuffersTwoPass, Stereo=False, Runtime=OpenXR`).
- Runtime swapchain FBO usage was healthy after the first fix, but preview blits still produced invalid texture/FBO errors against the editor eye-preview texture. The failing lines were the preview texture attachment/blit, not the acquired OpenXR swapchain framebuffer.
- The eye final-output command path could still select `VPRC_VendorUpscale` on OpenGL, while the external swapchain render scope needs the direct `VPRC_RenderToWindow` path so the full deferred/post image lands in the eye mirror FBO before the OpenXR swapchain blit.
- The pose root selection still depended on `RuntimeEngine.VRState.IsInVR`. In editor/OpenXR startup timing, the VR rig nodes and eye cameras may already exist while `IsInVR` is not true, causing `_openXrLocomotionRoot` to fall back to the source desktop camera transform instead of the headset's playspace parent.
- Imported/sparse texture streaming records per-material usage during main scene collection. The registry aggregates all usage for the same collect frame by taking the minimum distance, maximum projected pixel span, maximum screen coverage, maximum UV density hint, and unioned sparse page selection. Because desktop viewport collection and OpenXR two-eye collection run before the global `PostCollectVisible`, residency policy sees the maximum demand across desktop, left eye, and right eye in the same frame.
- Later OpenGL run:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_11-23-24_pid30716/`
- The later run still showed unlit-looking Monado eyes. The root cause was that the direct-present branch check for external OpenXR swapchain rendering ran while constructing the render command chain, before `EnterOpenXrExternalSwapchainRenderScope(...)` was active. The OpenGL eye command chain could therefore still select the vendor-upscale path and skip the final deferred-lit output path needed for the eye mirror FBO.
- Native UI text diagnostics showed glyph geometry, material selection, UVs, and atlas creation were correct, but the GL bind-time progressive uploader exposed only the smallest mip of the very large bitmap font atlas. Sampling that mip makes glyph coverage essentially uniform, so text appears as colored quads instead of shaped glyphs until high mips upload.
- The right eye preview remaining landscape until any layout edit was an invalidation issue: the preview node size could change after the texture appeared, but no explicit layout invalidation was forced after the aspect correction/material texture update.
- Follow-up OpenGL run:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_12-09-38_pid12224/`
- That run still showed sparse texture transitions in the OpenGL log, e.g. `GLTexture2D.Sparse`, so sparse streaming had not been globally disabled. A too-broad synchronous upload experiment was removed from imported textures; the synchronous path remains scoped to bitmap font atlases.
- The remaining deferred-eye lighting issue likely involved render-area state rather than swapchain validity. Deferred lighting shaders derive G-buffer UVs from `ScreenOrigin`, `ScreenWidth`, and `ScreenHeight`, which are populated from the currently pushed render area. The OpenXR external swapchain render scope tracked the correct `0,0,eyeWidth,eyeHeight` region, but `VPRC_PushViewportRenderArea` only used the viewport's region/internal-resolution rectangles.
- Later OpenGL run:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_12-45-08_pid31184/`
- That run showed the deferred shaders becoming ready early, including `DeferredLightCombine.fs`, `DeferredLightingPoint.fs`, `DeferredLightingSpot.fs`, and `DeferredLightingDir.fs`; missing shader compilation was not the current blocker.
- The same run showed repeated `[GLMeshRenderer] Inline Generate fallback` stacks from `VPRC_LightCombinePass.RenderLight(...)` for the deferred light-volume renderers. The `OpenXRViewportMirrorFBO` incomplete warnings appeared only during shutdown/teardown, so they are not treated as the live lighting failure.
- Root cause found: `VPRC_LightCombinePass` is a command object shared by multiple `XRRenderPipelineInstance`s, but it cached a single set of light renderers by G-buffer texture references. The two OpenXR eye viewports have separate pipeline instances and separate G-buffer textures, so left-eye and right-eye execution repeatedly invalidated and recreated each other's deferred light renderers before they could stabilize.
- The eye cameras appearing under the editor view were owned fallback OpenXR cameras. When no VR rig root was available, fallback eye transforms were parented to the source editor camera transform. Once the scene VR rig existed, the code reused VRState eye cameras but did not detach those fallback transforms, leaving transform-only children under the editor view.
- The hierarchy `(2)` with an empty expansion was the UI counting raw transform children, including transform-only helpers with no `SceneNode`, while the expansion renderer only displayed scene-node children.
- The native hierarchy context menu was created as a child of the hierarchy panel but was shown using canvas coordinates. The menu translation is parent-local, so the first right-click could place/size the popup incorrectly until a later layout edit invalidated it.
- Follow-up after point/spot deferred lighting worked in the OpenXR eyes: the directional deferred shader bound `DirectionalShadowAtlas` on texture unit `30`, but OpenGL sampler reservation already treats unit `30` as `ForwardContactDepthViewArray`. The backend reserves unit `9` for `DirectionalShadowAtlas`. This collision is directional-only and is most visible in the eye pipeline where deferred lighting and forward-contact resources are both active.
- Follow-up after directional deferred lighting worked in the OpenXR eyes: bloom still did not appear in Monado because both default render pipeline variants explicitly disabled `ShouldUseBloom()` whenever `IsRenderingExternalSwapchainTarget()` was true.
- The remaining eye-rooting path still had a registration-order hole: frame collection could infer the OpenXR locomotion root from `HMDNode`, but not from already-registered `VREyeTransform` eye cameras. When the HMD node reference was late or absent, the code could fall back to the desktop/editor camera tracking root even though real scene eye cameras were available.
- Follow-up after bloom worked: ambient occlusion still did not appear in Monado because both default pipeline variants explicitly returned `AmbientOcclusionDisabledMode` for external swapchain targets.
- Motion blur was similarly prevented from running in OpenXR eyes because `ShouldUseMotionBlur()` reused the helper that disables shared history/temporal effects for external swapchains and two-pass VR.
- Depth of field was already eligible in the runtime condition, but the older `DefaultRenderPipeline` branch did not explicitly create its DoF copy FBO in the conditional pass chain, unlike `DefaultRenderPipeline2`.
- The VR eye cameras were backed by raw `VREyeTransform` children created by `VRHeadsetComponent`, so they did not appear as real `SceneNode` children under `VRHeadsetNode`. The editor stereo preview also used generic child node names `Left Eye` and `Right Eye`, making those UI preview nodes easy to confuse with the render eye cameras in the hierarchy.
- Follow-up run:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_15-31-55_pid42196/`
- The follow-up run confirmed the remaining mesh popping/culling issue was real: OpenXR was still running separate left-eye and right-eye `CollectVisible(...)` calls, producing two independent command buffers instead of the intended combined stereo visibility set.
- The same run showed repeated first-chance `InvalidOperationException` and logged `AggregateException` failures during collect visible. The inner exception was `Nullable object must have a value` from `XRCameraParameters.GetUntransformedFrustum()`, called by directional cascade preparation. The getter verified the projection under one lock and then read the nullable frustum under a second lock, so an OpenXR eye FOV/projection invalidation between those two locks could clear `_untransformedFrustum`.
- Follow-up run after the shared OpenXR collection change:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_15-51-26_pid27424/`
- That run rendered black Monado eyes while the eye pipelines still executed. The regression was that the OpenXR shared command collection was populated and swapped through `MeshRenderCommandsOverride`, but mesh render commands still read `XRRenderPipelineInstance.MeshRenderCommands` directly. The private per-eye collections were now intentionally empty, so the render passes drew no scene geometry.

## Changes

- Zero-initialize OpenXR OpenGL swapchain image structs before enumeration, check enumeration results, validate returned GL texture handles, and validate precreated swapchain FBO completeness.
- Track the acquired OpenXR swapchain FBO during `RenderEye`, then blit the eye mirror texture into that FBO directly instead of reattaching the OpenXR texture handle every frame.
- Added an OpenGL OpenXR external-swapchain render scope, matching the existing Vulkan concept closely enough for pipeline decisions and target-region resolution.
- Updated `VPRC_RenderToWindow` so explicit external/offscreen output FBO targets remain bound for final output instead of being unbound to the editor backbuffer.
- Updated the VR stereo preview overlay to fit eye textures inside the existing preview bounds while preserving each texture's aspect ratio.
- Forced `DefaultRenderPipeline` and `DefaultRenderPipeline2` external swapchain renders to use the direct final-present command path instead of the vendor-upscale branch.
- Guarded the OpenXR eye-preview blit so invalid preview GL texture names are skipped with a throttled warning rather than producing `GL_INVALID_VALUE` and poisoning the draw FBO state.
- Changed OpenXR rig detection to prefer `VRState.ViewInformation` world, HMD node, and eye cameras whenever they are present, rather than requiring `VRState.IsInVR`. This anchors the OpenXR eye poses to the headset/playspace rig when the scene provides one.
- Changed both default render pipelines so the final-present choice evaluates the external OpenXR swapchain condition at command execution time, while the eye render scope is active.
- Added a per-texture `PreferSynchronousGpuUpload` flag and applied it to bitmap font atlases so native UI text glyph coverage is available on the first sampled frame instead of going through smallest-mip progressive upload.
- Forced VR stereo preview layout invalidation when eye preview sizes are aspect-corrected or when preview materials/textures are swapped.
- Made the deferred light-combine fullscreen blit explicitly target `ForwardPassFBO` in all default pipeline command builders. This removes the runtime/render-graph ambiguity from the previous "draw into current FBO" behavior and keeps OpenXR external-swapchain eye renders on the same deferred-lit composition path as the desktop view.
- Added OpenXR OpenGL texture-handle repair for the mirror/preview textures: if a cached GL wrapper reports a name that `glIsTexture` rejects in the current context, the wrapper is destroyed and regenerated before the blit is attempted.
- Strengthened the stereo preview layout refresh so activation and texture/material replacement invalidate the root, left eye, and right eye transforms, including measure and arrange, instead of only updating when width/height numerically changed.
- Kept imported texture streaming on the sparse/progressive residency path. The `PreferSynchronousGpuUpload` escape hatch is intentionally applied to bitmap font atlases only, because glyph coverage must be available on the first sampled frame.
- Changed imported texture stuck-pending recovery from global-idle-only to per-important-texture recovery. Visible, recently bound, or promotion-pending textures can now clear and requeue stale transitions even while unrelated uploads are still active.
- Changed `VPRC_PushViewportRenderArea` so any render pass executing inside an external swapchain scope pushes the external target region. For OpenXR OpenGL eye renders this makes deferred light accumulation, deferred combine, post-processing, and final output all see the acquired eye image dimensions.
- Changed `VPRC_LightCombinePass` to keep deferred light renderer resources per `XRRenderPipelineInstance` using a weak table. Left and right OpenXR eyes no longer churn one shared renderer cache by swapping G-buffer texture references every eye pass.
- Marked point, spot, and MSAA deferred light-volume renderers as render-pipeline generation-priority resources and pre-created their render-pipeline versions, matching the directional fullscreen light renderer path.
- Changed OpenXR fallback eye-camera ownership: scene VRState eye cameras are reused without reparenting, while owned fallback cameras live under a private tracking root that mirrors the source camera instead of becoming children of the editor view. Owned fallback transforms are detached when real VR rig cameras are selected.
- Updated native and ImGui hierarchy child counts to count only real child `SceneNode`s and ignore transform-only/self helper children.
- Updated native hierarchy context-menu spawning to convert the cursor from canvas coordinates into hierarchy-panel local coordinates. The context menu now clamps to its parent bounds and forces immediate layout after being shown.
- Published OpenXR OpenGL eye viewports into shared `VRState.LeftEyeViewport` / `RightEyeViewport`, so directional shadow preparation, cascade source selection, and residency logic see the actual eye views instead of only the desktop/editor viewport.
- Changed directional shadow source selection to prefer VR eye viewports while VR is active, and treated external swapchain eye viewports as cascade-capable even though their cameras are standalone `XRCamera`s without `CameraComponent`.
- Fixed OpenXR head-to-eye local pose composition for `VREyeTransform` by deriving eye-local relative to HMD as `eyeLocal * inverseHead`.
- Anchored the ImGui hierarchy popup to the current ImGui viewport when opening the node context menu under multi-viewport/docking.
- Added explicit native UI context-menu Z ordering so the popup background, item backgrounds, separators, and text sort consistently.
- Moved the deferred directional shadow atlas sampler from texture unit `30` to the renderer's reserved directional-atlas unit `9`, and aligned `DeferredLightingDir.fs`'s layout binding with that unit.
- Allowed bloom during external OpenXR swapchain eye renders while keeping the light-probe and scene-capture bloom guards intact.
- Inferred the OpenXR locomotion root from app-provided `VREyeTransform` cameras when `HMDNode` is not available yet. Late OpenXR eye poses now compose against the HMD's parent render space for app rig eyes, so the rendered eye view no longer borrows the editor camera fallback root.
- Allowed ambient occlusion mode evaluation for external OpenXR swapchain eye renders in both default render pipeline variants.
- Allowed motion blur to run in VR/external swapchain eye renders when its post-process stage is enabled, while leaving the shared temporal-history guard in place for temporal accumulation uniforms.
- Added explicit DoF copy-FBO creation to the older `DefaultRenderPipeline` conditional DoF pass chain.
- Changed `VRHeadsetComponent` to create/reuse real `Left Eye` and `Right Eye` child `SceneNode`s under the headset node, with `VREyeTransform`s parented to `VRHeadsetTransform` through `SetParent(...)`. The published VRState eye cameras now point at those scene-node transforms.
- Renamed the editor stereo preview overlay children to `Left Eye Preview` and `Right Eye Preview` so hierarchy views no longer make the UI preview nodes look like render eye cameras.
- Added a runtime VR rendering service repair path so OpenXR can ask the engine to republish the active `VRHeadsetComponent` eye cameras before deciding the rig is unavailable.
- Removed the OpenXR renderer-owned eye-camera fallback path entirely. OpenXR now requires `VRState.ViewInformation` to contain an HMD node, render world, and both eye cameras, with both eye camera transforms being `VREyeTransform`s parented directly to the HMD transform. If that strict scene rig is missing, OpenXR skips the eye frame and logs the reason instead of deriving anything from the desktop/editor view.
- Changed the engine VR rendering service repair path to only republish the active `VRHeadsetComponent`'s existing eye cameras. It no longer creates substitute eye scene nodes when the component did not publish a valid rig.
- Removed the private OpenXR tracking-root fallback and stopped storing the desktop/editor camera as the persistent OpenXR frame base camera. The desktop viewport can still contribute pipeline/post-process settings, but not transform roots or eye cameras.
- Fixed the no-render regression from strict OpenXR rig validation by mirroring authoritative `Engine.VRState` data into the renderer-facing `RuntimeEngine.VRState`. `ViewInformation`, `IsInVR`, `IsOpenXRActive`, and the active `OpenXRApi` pointer now sync when the view info changes and when OpenXR/OpenVR runtime activation changes.
- Added an OpenXR-owned shared `RenderCommandCollection` and assigned it as `MeshRenderCommandsOverride` for both eye viewports. The eye render passes keep separate viewport/pipeline-instance resources, but consume one shared visibility buffer.
- Changed OpenXR `CollectVisible` to combine left/right projection matrices with `ProjectionMatrixCombiner`, using each eye's inverse local matrix and the HMD render matrix to build one world-space stereo culling frustum. OpenXR now performs a single scene collect into the shared command collection, then swaps that collection once for both eye renders.
- Removed the active per-eye parallel collect branch from OpenXR frame collection. Vulkan setup logging now reflects that OpenXR uses combined stereo visibility collection and serial swapchain submission.
- Made `XRCameraParameters.GetUntransformedFrustum()` ensure and read the cached frustum under the same projection lock, eliminating the nullable race seen during directional cascade shadow preparation.
- Added `XRRenderPipelineInstance.ActiveMeshRenderCommands`, which resolves the render-state command collection override before falling back to the pipeline instance's private collection.
- Updated the main mesh pass, meshlet pass, mesh-pass execution predicates, forward depth-normal prepass, motion-vector pass, full-overdraw pass, voxelization pass, and filtered/debug mesh commands to use `ActiveMeshRenderCommands`. OpenXR can now render from the one shared stereo visibility collection while normal viewports keep using their private collections.
- Assigned the shared OpenXR command collection an owner pipeline for GPU pass/debug context after the eye viewports have their pipeline instance.

## Validation

- Built the editor with:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Build log:
  `Build/_AgentValidation/20260625-104002-openxr-opengl-diagnostics/logs/build-editor-final.log`

- Result: build succeeded with `0 Warning(s), 0 Error(s)`.
- Follow-up build log:
  `Build/_AgentValidation/20260625-104002-openxr-opengl-diagnostics/logs/build-editor-followup-openxr-opengl-final.log`

- Follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Second follow-up build log:
  `Build/_AgentValidation/20260625-104002-openxr-opengl-diagnostics/logs/build-editor-followup-openxr-opengl-final2.log`

- Second follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Third follow-up build log:
  `Build/_AgentValidation/20260625-120613-openxr-opengl-eye-lighting/logs/build-editor.log`

- Third follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Fourth follow-up build log:
  `Build/_AgentValidation/20260625-123730-openxr-gl-deferred-area/logs/build-editor.log`

- Fourth follow-up result: build succeeded.

- Fifth follow-up build logs:
  `Build/_AgentValidation/20260625-133325-openxr-gl-deferred-followup/logs/build-editor-after-light-cache.log`
  `Build/_AgentValidation/20260625-133325-openxr-gl-deferred-followup/logs/build-editor-after-ui-menu.log`

- Fifth follow-up result: both builds succeeded with `0 Warning(s), 0 Error(s)`.

- Sixth follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Sixth follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Seventh follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Seventh follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.
- `git diff --check` passed; output only reported existing LF/CRLF conversion warnings.

- Eighth follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Eighth follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.
- `git diff --check` passed; output only reported existing LF/CRLF conversion warnings.

- Ninth follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Ninth follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Tenth follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Tenth follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Eleventh follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Eleventh follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Twelfth follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Twelfth follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

- Thirteenth follow-up build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- Thirteenth follow-up result: build succeeded with `0 Warning(s), 0 Error(s)`.

## Remaining Check

A live Monado/OpenXR OpenGL run should verify that:

- The repeated OpenGL invalid-texture and incomplete-FBO messages are gone.
- Monado eye output matches the editor's deferred-lit image.
- Shadow visibility is restored in the eye output.
- The stereo preview appears portrait/aspect-correct instead of squished.
- Eye motion is rooted under the VR playspace/headset rig rather than following the desktop editor camera transform.
- Bloom is visible in each Monado eye render when enabled in the scene/post-process settings.
- Ambient occlusion, motion blur, and depth of field are visible in each Monado eye render when enabled in the scene/post-process settings.
- The hierarchy shows render eye scene nodes under `VRHeadsetNode`, while editor overlay preview nodes are clearly named as previews.
- The OpenGL log no longer repeats light-combine inline renderer generation every eye frame.
- The editor view no longer shows transform-only fallback eye children under the editor scene node.
- Native hierarchy right-click context menus open at the correct size and location on the first click.
- OpenXR eye mesh visibility is stable at stereo frustum edges, with no left/right eye object popping from independent culling.
- `log_general.log` no longer reports the repeated nullable `GetUntransformedFrustum()` exceptions or the collect-visible `AggregateException` wrapper.
