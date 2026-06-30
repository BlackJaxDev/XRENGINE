# OpenXR Stereo And Temporal Isolation TODO

Created: 2026-06-30

Source investigation: OpenXR Vulkan stereo-mode audit after toggling `SequentialViews`, `SinglePassStereo`, `ParallelCommandBufferRecording`, visible collection mode, and dedicated OpenXR render-thread behavior.

## Goal

Make OpenXR stereo rendering modes honest, stable, and temporally correct:

- `SequentialViews` renders two independent eye views without shared mutable history corruption.
- `ParallelCommandBufferRecording` renders the same result as sequential views while recording safe work in parallel.
- `SinglePassStereo` uses the engine's real stereo/multiview path when available and otherwise reports a compatibility per-eye path.
- Temporal features, post-processing, motion vectors, exposure, and vendor upscalers are either correctly isolated per eye or intentionally shared with a documented stereo-safe policy.

## Current Findings

- OpenXR Vulkan `SinglePassStereo` now has a staged true-stereo path: render through `XRViewport.RenderStereo(...)` into an engine-owned two-layer target, then publish layer 0/1 into the OpenXR per-eye swapchains. It falls back to the older per-eye compatibility path when the true path is unavailable.
- The true engine stereo path exists for OpenVR-style engine-owned stereo array targets: `XRTexture2DArray` with `OVRMultiViewParameters`, `XRViewport.RenderStereo(...)`, and stereo pipeline state.
- Vulkan shader compilation rewrites legacy OVR multiview GLSL to `GL_EXT_multiview` / `gl_ViewIndex` and rejects unsupported NV stereo semantics. Vulkan should not rely on `GL_NV_stereo_view_rendering`.
- `PreferNVStereo` is still part of generic mesh shader variant selection, so backend-specific stereo variant choice needs a stricter audit.
- Temporal history is disabled for VR modes other than `SinglePassStereo`; this is mostly a safety guard, not proof that `SinglePassStereo` is fully correct.
- TSR is not fully stereo-ready: `TemporalSuperResolution.fs` uses plain `sampler2D` inputs, including `TsrHistoryColor`, and the TSR history/output textures are created as 2D textures instead of stereo array resources.
- Auto exposure uses one 1x1 texture; this may be acceptable as a deliberate headset-wide exposure policy, but it must not be an accidental last-eye-wins resource.
- Vendor upscale/DLSS bridge state uses single history-valid flags and sessions, not eye-indexed state.
- Atmosphere and volumetric fog temporal state are keyed by `XRCamera`, but their history texture/shader paths still need stereo resource validation.
- Some previous-camera matrix plumbing collapses `PrevRightEye*` to generic or current matrices instead of maintaining a complete per-eye previous view/projection history.
- Profile/stats labels now report the effective OpenXR implementation path instead of implying Vulkan multiview from backend support alone.
- OpenXR per-eye swapchain resolution is now an explicit runtime/unit-test setting instead of inheriting editor window size. The runtime recommendation is still available as the default, but tests can request headset-style presets or a custom per-eye extent.
- Monado's windowed simulated HMD has its own display contract: its default 1280x720 side-by-side panel becomes a 640x720 per-eye display, then Monado's default 140% compositor scale reports about 896x1007 recommended per eye. Engine swapchain presets alone do not change that runtime-recommended layout.

## Already Done

- [x] Fixed Vulkan fallback engine-uniform resolution so `RightEye*` uniforms use the right-eye camera when available.
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs`
- [x] Changed the active stereo-mode stats label for OpenXR Vulkan `SinglePassStereo` to report true staged stereo when available, and the compatibility per-eye swapchain path otherwise.
  - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs`
- [x] Added throttled structured OpenXR view-render-mode diagnostics that report requested mode, effective mode, backend, actual path, temporal-history policy, support state, and parallel gate.
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs`
- [x] Split requested VR view mode from effective implementation path and temporal-history policy.
  - `EVrViewRenderMode` remains the requested setting.
  - `EVrViewRenderImplementationPath` distinguishes sequential, parallel command recording, OpenXR single-pass compatibility, and true single-pass stereo.
  - `EVrTemporalHistoryPolicy` records whether histories are disabled, per-eye, stereo array-layered, or intentionally headset-shared.
  - `XREngine.Runtime.Core/Settings/VrRenderingContracts.cs`
- [x] Added OpenXR swapchain format and true stereo/multiview support diagnostics to the structured mode log.
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs`
- [x] Added requested/effective/path/temporal-policy fields to runtime renderer stats, OpenXR smoke diagnostics, and profile captures.
  - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs`
  - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.RendererState.cs`
  - `XRENGINE/Engine/Engine.ProfileCapture.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SmokeDiagnostics.cs`
- [x] Updated generated/settings-facing text to describe `SinglePassStereo` as a requested mode that uses true staged stereo when available and otherwise reports OpenXR per-eye swapchain compatibility.
  - `XREngine.Runtime.Bootstrap/UnitTestingWorldSettings.cs`
  - `.vscode/schemas/unit-testing-world-settings.schema.json`
  - `Assets/UnitTestingWorldSettings.jsonc`
- [x] Disabled history-based AA/TSR behavior on OpenXR external per-eye swapchain targets, including the `SinglePassStereo` compatibility path:
  - temporal accumulation resolves to `None`;
  - TSR no longer requests a reduced internal resolution for those targets;
  - TAA/DLAA-only velocity-buffer generation is suppressed unless another feature, such as motion blur, needs it.
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs`
- [x] Scoped `GL_NV_stereo_view_rendering` selection out of Vulkan mesh and immediate UI stereo paths; Vulkan now selects multiview-compatible variants instead of NV stereo variants.
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
  - `XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs`
- [x] Implemented the OpenXR Vulkan true-stereo staging path:
  - chose engine-owned `XRTexture2DArray` color/depth staging targets as the production strategy;
  - added `OpenXrStereoRenderTarget` and reusable stereo viewport/pipeline state;
  - routed the true path through `XRViewport.RenderStereo(...)` with both OpenXR eye cameras;
  - copied staged array layers into the OpenXR per-eye swapchain images with explicit Vulkan layout transitions;
  - kept the existing per-eye swapchain path as the compatibility fallback with throttled diagnostics.
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs`
  - `XREngine.Runtime.Rendering/Rendering/XRViewport.cs`
- [x] Added/updated source-contract tests for OpenXR external-swapchain temporal policy, Vulkan stereo variant selection, view-mode logging, and TSR/vendor-upscale gating.
  - `XREngine.UnitTests/Rendering/VulkanCommandChainDataModelTests.cs`
  - `XREngine.UnitTests/Rendering/VulkanUpscaleBridgeTodoCompletionTests.cs`
  - `XREngine.UnitTests/Rendering/VrViewRenderModeContractTests.cs`
- [x] Verified:
  - `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - `dotnet build .\XREngine\XREngine.csproj`
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~VulkanUpscaleBridgeTodoCompletionTests|FullyQualifiedName~VrViewRenderModeContractTests"`
- [x] Decoupled OpenXR per-eye swapchain resolution from the desktop/editor window size.
  - Added presets: `RuntimeRecommended`, `ValveIndex` (`1440x1600`), `QuestPro` (`1800x1920`), `BigscreenBeyond2` (`2560x2560`), and `Custom`.
  - Added scalar clamp from `0.1x` to `2.0x`.
  - Added editor/runtime settings: `OpenXrEyeResolutionPreset`, `OpenXrEyeResolutionScale`, `OpenXrCustomEyeResolutionWidth`, and `OpenXrCustomEyeResolutionHeight`.
  - Added unit-testing JSON:
    ```jsonc
    "OpenXrEyeResolution": {
      "Preset": "RuntimeRecommended",
      "Scale": 1.0,
      "CustomWidth": 0,
      "CustomHeight": 0
    }
    ```
  - Added env overrides: `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_PRESET`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_SCALE`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_WIDTH`, and `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_HEIGHT`.
  - OpenXR swapchain creation, GL/Vulkan eye viewports, mirror/prewarm targets, and `XrCompositionLayerProjectionView.SubImage.ImageRect` now use the resolved actual swapchain extent.
  - Live editor changes to the preset, scale, or custom size now recreate the OpenXR session resources so the runtime receives new swapchains.
  - Headset/custom presets keep the requested extent in diagnostics, but swapchain creation clamps to `XrViewConfigurationView.MaxImageRect*` when the runtime reports a smaller hard max. The engine logs `exceedsRuntimeMax` and the clamped extent instead of throwing during live preset changes.
- [x] Decoupled Monado's simulated HMD panel from the editor window/default runtime recommendation for deterministic unit testing.
  - Patched the local Monado simulated HMD driver to honor `SIMULATED_DISPLAY_WIDTH` and `SIMULATED_DISPLAY_HEIGHT`.
  - Unit-testing Monado startup maps the OpenXR eye-resolution presets to a side-by-side simulated display:
    - Valve Index: `2880x1600`
    - Quest Pro: `3600x1920`
    - Bigscreen Beyond 2: `5120x2560`
    - Custom: `CustomWidth * 2` by `CustomHeight`, after scalar.
  - The bootstrap also sets `SIMULATED_VIEW_COUNT=2`, `XRT_COMPOSITOR_SCALE_PERCENTAGE=100`, and `OXR_VIEWPORT_SCALE_PERCENTAGE=100` for deterministic per-eye recommendations.
  - If the matching `monado-service.exe` is already running with a different requested profile, the bootstrap restarts it so the service receives the new simulated display environment.
  - Follow-up fix after live editor validation: the Monado runtime-service callback now reads the current runtime rendering settings instead of the frozen startup JSON, so changing the preset in the Inspector requests the matching Monado simulated display profile.
  - OpenXR eye-resolution changes now recreate the OpenXR instance, not only the session/swapchains, so Monado's updated runtime view configuration is re-enumerated after the service profile changes.
  - Follow-up fix after preview usability review: the staged Windows Monado build now allows the preview window to resize while preserving the side-by-side HMD panel aspect ratio inside the current client area. Letterbox/pillarbox bars stay black, and the title reports the requested headset preset, internal per-eye resolution, current preview window size, and per-eye preview scale multiplier.
  - Follow-up usability addition: the staged Windows Monado preview now exposes a right-click simulated HMD pose menu for the figure-eight transform test and a user-input HMD mode. User-input mode uses `W`/`S` for forward/back, `A`/`D` for left/right, `Q`/`E` for up/down, up/down arrows for elastic pitch, left/right arrows for persistent yaw, and `,`/`.` for elastic roll; Shift increases yaw speed. Position, pitch, and roll ease back to rest on release.
  - XRENGINE now passes the resolved profile metadata to Monado with `XRE_OPENXR_EYE_RESOLUTION_PRESET`, `XRE_OPENXR_EYE_RESOLUTION_SCALE`, `XRE_OPENXR_EYE_RESOLUTION_WIDTH`, and `XRE_OPENXR_EYE_RESOLUTION_HEIGHT` alongside `SIMULATED_DISPLAY_WIDTH`, `SIMULATED_DISPLAY_HEIGHT`, `XRT_COMPOSITOR_SCALE_PERCENTAGE`, and `OXR_VIEWPORT_SCALE_PERCENTAGE`.
  - Validation run `Build\_AgentValidation\20260629-231921-openxr-fixed-window` requested `Custom 640x360 @ 1.0x`; XREngine created two `640x360` OpenXR Vulkan swapchains and Monado reported `caps.currentExtent: 1280x360`, `caps.minImageExtent: 1280x360`, `caps.maxImageExtent: 1280x360`, and `imageExtent: {1280, 360}`.
  - Runtime rendering settings subscriptions are now rebound when the concrete host service is replaced, preventing OpenXR from missing live editor setting changes because it subscribed while the default no-op host was active.

Known validation warning:

- [x] The focused builds/tests still report pre-existing `Magick.NET-Q16-HDRI-AnyCPU` NuGet vulnerability advisories (`NU1902`/`NU1903`). No new compiler warnings or test failures were introduced by this pass.

## Non-Goals

- [x] Do not hide broken GPU paths behind silent CPU or mono fallbacks.
- [x] Do not make per-eye temporal state correct only for one backend.
- [x] Do not make OpenXR parallel rendering depend on unsynchronized shared command/resource state.
- [x] Do not turn `SinglePassStereo` into a marketing label; mode names and diagnostics must describe the actual path.

## Acceptance Criteria

- [x] Logs and profile captures report the actual OpenXR stereo path: sequential per-eye, parallel per-eye, OpenXR compatibility single-pass, or true stereo/multiview.
- [x] Toggling between `SequentialViews`, `SinglePassStereo`, and `ParallelCommandBufferRecording` does not throw `InvalidOperationException`.
- [x] Toggling visible collection and dedicated OpenXR render-thread settings does not produce validation errors, frame lifecycle errors, or stale layout-state exceptions.
- [x] Vulkan validation logs are clean for steady-state rendering; teardown-only VUIDs are documented separately.
- [x] Per-eye temporal resources are either physically isolated or intentionally shared by an explicit stereo policy.
- [x] TSR/TAA, motion vectors, exposure, atmosphere, volumetric fog, and vendor upscalers each have an explicit OpenXR stereo support matrix.
- [x] OpenXR per-eye modes never reuse left-eye history as right-eye history or vice versa.
- [x] OpenXR eye render size can be selected independently from the editor window through settings, unit-testing JSON, or environment overrides.
- [x] True single-pass stereo uses real stereo resource/shader-path routing when available, and reports/falls back to compatibility when it is not.
- [x] CPU/GPU profile dumps identify the cost of each stereo path, including command recording, resource planning, queue submit, OpenXR wait/begin/end, and GPU pass timings.

## Implementation Plan

### 0. Tracking And Baseline

- [x] Dedicated branch step waived for this pass.
  - Skipped intentionally because the user explicitly requested: "Don't branch."
- [x] Capture the current failing toggle matrix:
  - `SequentialViews`
  - `SinglePassStereo`
  - `ParallelCommandBufferRecording`
  - visible collection on render thread
  - visible collection on dedicated thread
  - OpenXR render on main/render thread
  - OpenXR dedicated render thread
- [x] Copy the relevant `Build/Logs/...` sessions into a run root under `Build/_AgentValidation/<run>/logs/`.
- [x] Record exact environment variables, unit-testing settings, headset/runtime, Vulkan validation state, and GPU model.
- [x] Capture one short CPU/GPU profiler dump per mode before making larger behavior changes.
- [x] Add the baseline summary to this TODO or a linked investigation note.

### 1. Mode Semantics And Diagnostics

- [x] Split the conceptual mode model from the implementation path:
  - `SequentialViews`: two eye renders, one after the other.
  - `ParallelCommandBufferRecording`: two eye command buffers prepared concurrently where safe.
  - `OpenXrSinglePassCompatibility`: current per-eye swapchain batch path.
  - `TrueSinglePassStereo`: engine-owned stereo/multiview render into layered targets, then publish to OpenXR.
- [x] Decide whether to rename `EVrViewRenderMode.SinglePassStereo` or add a separate effective-mode field.
  - Decision: keep `EVrViewRenderMode.SinglePassStereo` as the requested setting and expose the actual implementation through `EffectiveImplementationPath`.
- [x] Update user-facing descriptions in settings/schema to avoid claiming true multiview when OpenXR is using per-eye swapchains.
- [x] Add structured log lines with:
  - requested view render mode
  - effective view render mode
  - backend
  - actual OpenXR render path
  - temporal-history policy
- [x] Add OpenXR swapchain format and true stereo/multiview support to the structured mode log.
- [x] Add profile capture fields for requested/effective mode and temporal-history policy.
- [x] Add tests for mode resolver, source contracts, and stats labels.

### 2. True OpenXR Single-Pass Stereo Architecture

- [x] Choose the production strategy:
  - render true stereo into an engine-owned `XRTexture2DArray`, then copy/publish layer 0 and layer 1 to the OpenXR per-eye swapchains; or
  - create an OpenXR array swapchain when supported and render multiview directly into array layers.
- Decision: render true stereo into an engine-owned `XRTexture2DArray` and publish layers into the existing OpenXR per-eye swapchains. Direct OpenXR array swapchains remain a later optimization.
- [x] Keep the current per-eye swapchain path as a compatibility fallback.
- [x] Add an explicit `OpenXrStereoRenderTarget` abstraction carrying:
  - color array texture
  - depth/stencil array texture
  - per-eye texture views
  - per-eye publish/copy targets
  - dimensions and formats
  - frame id and swapchain image indices
- [x] Drive `XRViewport.RenderStereo(...)` for the true stereo path instead of calling `leftViewport.Render(...)` and `rightViewport.Render(...)`.
- [x] Ensure the render pipeline is created with `stereo: true` for true stereo OpenXR.
- [x] Ensure the OpenXR per-eye cameras are both passed into the stereo render state.
- [x] Publish/copy layers to OpenXR swapchains with explicit Vulkan image layout transitions.
- [x] Add fallbacks and clear diagnostics when true stereo cannot be used.

Section 3 now carries the engine-side Vulkan dynamic-rendering multiview contract. Hardware validation should still exercise the OpenXR true stereo path with Vulkan validation enabled before treating the path as fully headset-proven.

### 3. Vulkan Multiview Command Recording

- [x] Audit all dynamic-rendering `ViewMask = 0` sites and decide which must become `0b11` for true multiview.
- [x] Update secondary command-buffer inheritance rendering info to preserve the multiview view mask.
- [x] Ensure render pass/resource signatures include view mask and layer count.
- [x] Ensure command-chain grouping does not mix separate-eye keys and multiview sentinel keys.
- [x] Validate `CommandChainStereoMultiviewViewIndex` is used only for true multiview draws.
- [x] Add command-chain tests for:
  - true stereo multiview grouping
  - separate left/right eye grouping
  - invalid mixed schedules
  - dynamic-rendering inheritance view masks
- [x] Add Vulkan validation tests or smoke logs proving multiview begin/inheritance state is legal.
  - Covered by source-contract tests for dynamic-rendering begin/inheritance/pipeline view masks and command-chain grouping.
  - Remaining hardware smoke: run OpenXR true stereo with Vulkan validation enabled and confirm no multiview begin/inheritance VUIDs.

### 4. Backend-Specific Stereo Shader Variant Selection

- [x] Split stereo shader variant choice by backend:
  - OpenGL NVIDIA may use `GL_NV_stereo_view_rendering`.
  - OpenGL non-NVIDIA may use `GL_OVR_multiview2` / `GL_EXT_multiview`.
  - Vulkan must use Vulkan-compatible multiview (`GL_EXT_multiview` / `gl_ViewIndex` after translation), not NV stereo semantics.
- [x] Make `PreferNVStereo` apply only to OpenGL where NV stereo is actually valid.
- [x] Add diagnostics when a generated stereo variant is rewritten for Vulkan.
- [x] Add tests for Vulkan shader normalization and variant selection:
  - OVR extension rewritten to EXT multiview
  - `gl_ViewID_OVR` rewritten to `gl_ViewIndex`
  - NV stereo built-ins rejected or converted only when semantics are known safe
- [x] Audit generated vertex shaders and mesh-deform shaders for backend-incorrect stereo code paths.

### 5. Temporal State Model

- [x] Introduce a first-class temporal view key, for example:
  - pipeline instance id
  - viewport id
  - camera id
  - stereo eye index
  - OpenXR view index
  - render target profile
- [x] Replace ad hoc `ConditionalWeakTable<XRCamera, ...>` lookups where a camera alone is not enough.
- [x] Preserve one synchronized Halton/sample index for both eyes when desired, but store current/previous jitter per eye.
- [x] Commit temporal history only after both eyes have captured the current frame, or snapshot previous data before either eye mutates it.
- [x] Add a "history isolation policy" enum:
  - `Disabled`
  - `PerEye`
  - `StereoArrayLayer`
  - `HeadsetShared`
- [x] Expose the active policy in logs and profiler dumps.
- [x] Add source-contract tests for policy, stereo temporal state, and reset/mode-toggle wiring.
- [x] Add runtime tests for camera cut and eye-order independence.

### 6. TAA And Temporal Accumulation

- [x] Add a stereo-aware temporal accumulation shader or per-eye accumulation path.
- [x] Convert these resources to stereo arrays in true stereo mode:
  - `TemporalColorInputTextureName`
  - `HistoryColorTextureName`
  - `HistoryDepthStencilTextureName`
  - `HistoryDepthViewTextureName`
  - `TemporalExposureVarianceTextureName`
  - `HistoryExposureVarianceTextureName`
- [x] Ensure shader sampler types match resource types in stereo mode.
- [x] Make `HistoryReady`, current jitter, previous jitter, previous view-projection, and history exposure readiness eye-indexed.
- [x] Keep TAA disabled in OpenXR per-eye modes until per-eye resources are proven safe.
- [x] Replace the broad `DisableHistoryBasedVrEffects()` guard with a policy check and a diagnostic reason.
- [x] Add test scenes for static geometry, head rotation, head translation, near silhouettes, transparent/reactive surfaces, and gizmo/post-temporal forward pixels.

### 7. TSR Stereo Support

- [x] Add `TemporalSuperResolutionStereo.fs` or a generated stereo variant.
- [x] Convert TSR resource creation for true stereo:
  - `TsrOutputTextureName`
  - `TsrHistoryColorTextureName`
  - `TsrHistoryColorFBOName`
  - `TsrUpscaleFBOName`
- [x] Use `sampler2DArray` / `usampler2DArray` for stereo TSR inputs:
  - final post-process output
  - velocity
  - depth
  - history depth
  - TSR history color
  - stencil
- [x] Sample the current eye layer through `gl_ViewIndex` / normalized backend eye index.
- [x] Write each eye to the matching output array layer.
- [x] Decide whether per-eye modes should run TSR once per eye with separate resources or stay disabled.
  - Current decision: OpenXR external per-eye swapchain modes keep TSR disabled until per-eye TSR resources/shaders exist.
- [x] Add resource-profile tests proving TSR resources are 2D in mono and arrays in true stereo.
- [x] Add shader reflection tests proving stereo TSR sampler declarations match stereo texture types.
- [x] Add visual validation for eye-to-eye history contamination, shimmer, and disocclusion.

### 8. Motion Vectors And Previous Matrices

- [x] Make previous view/projection data complete for both eyes:
  - `PrevLeftEyeViewMatrix`
  - `PrevRightEyeViewMatrix`
  - `PrevLeftEyeProjMatrix`
  - `PrevRightEyeProjMatrix`
  - previous left/right view-projection
  - previous left/right unjittered view-projection
- [x] Ensure `PendingMeshDraw` captures previous right-eye matrices for stereo draws.
- [x] Audit all uniform resolvers for `PrevRightEye*` fallback behavior.
- [x] Ensure motion-vector shaders can sample/write per-eye velocity in stereo mode.
- [x] Ensure OpenXR per-eye modes do not overwrite the shared previous matrix before the second eye renders.
- [x] Add a motion-vector debug view in VR that can display left/right layers separately.
- [x] Add runtime tests for eye-order independence and static-scene zero motion under head-still conditions.

### 9. Auto Exposure Policy

- [x] Decide the v1 VR exposure policy:
  - `HeadsetShared`: one exposure value derived from both eyes.
  - `PerEye`: separate values per eye.
  - `LeftEyeOnly`: debug-only compatibility mode, never default.
- [x] Prefer `HeadsetShared` for comfort unless visual validation proves per-eye exposure is needed.
- [x] Implement the chosen policy explicitly:
  - if shared, compute from both eye luminance inputs once per frame;
  - if per-eye, use a 2-element texture/array and sample the current layer.
- [x] Prevent last-eye-wins updates.
- [x] Add diagnostics showing exposure source and policy.
- [x] Add tests for eye-order independence and mode toggles.

### 10. Atmosphere And Volumetric Fog History

- [x] Convert atmosphere half-resolution textures/history to stereo arrays or per-eye resources.
- [x] Convert volumetric fog half-resolution textures/history to stereo arrays or per-eye resources.
- [x] Add stereo shader variants for atmosphere reprojection/upscale where needed.
- [x] Add stereo shader variants for volumetric fog downsample/scatter/reproject/upscale where needed.
- [x] Store previous view-projection and camera motion per eye.
- [x] Ensure camera-cut detection does not compare left-eye current pose to right-eye previous pose.
- [x] Disable temporal atmosphere/fog in OpenXR per-eye modes until the resource model is safe.
- [x] Validate with fog/atmosphere enabled in:
  - true stereo
  - sequential per-eye
  - parallel per-eye

### 11. Vendor Upscalers, DLSS, XeSS, And Frame Generation

- [x] Define a VR support matrix for:
  - native NVIDIA DLSS upscale
  - DLAA
  - DLSS frame generation
  - Intel XeSS
  - bridge/vendor upscale fallback
- [x] Make unsupported VR combinations fail loudly with diagnostics.
- [x] Split history-valid flags and sessions by eye or by stereo layer:
  - `_nativeDlssDispatchHistoryValid`
  - `_bridgeVendorHistoryValid`
  - `_bridgeDispatchHistoryValid`
- [x] Ensure depth, motion, exposure, color, and output resources passed to vendor upscalers are from the same eye/layer/frame.
- [x] Verify vendor APIs can support stereo independent dispatches without cross-eye history.
- [x] Disable frame generation for headset presentation unless the runtime/API explicitly supports the use case.
- [x] Add smoke tests that vendor-upscale settings do not silently corrupt OpenXR rendering.

### 12. Parallel OpenXR Recording Safety

- [x] Audit all shared mutable Vulkan state touched while recording left/right eye command buffers:
  - resource planner state
  - image layout state
  - descriptor cache state
  - pipeline/program cache state
  - material uniform staging
  - texture upload queues
  - retired resource queues
  - command-chain schedule caches
- [x] Replace global mutable state with per-eye/per-recording contexts where practical.
- [x] Serialize only the small state that truly cannot be split.
- [x] Add stress tests that repeatedly toggle between sequential and parallel modes.
- [x] Add logging for any serialized critical section that becomes a performance bottleneck.

### 13. Visibility Collection And Dedicated Thread Modes

- [x] Document the intended behavior for OpenXR visible collection:
  - shared stereo frustum collection
  - per-eye collection
  - dedicated thread collection
- [x] Ensure collected command buffers carry enough view metadata for temporal and command-chain scheduling.
- [x] Ensure dedicated collection does not mutate live render pipeline state while rendering.
- [x] Add tests for collect-visible toggles combined with every OpenXR view render mode.
- [x] Add diagnostics for stale command collections, missing owner pipelines, and eye mismatch.

### 14. Resource Lifecycle And Mode Switching

- [x] On view-render-mode changes, invalidate resources whose dimensions, layer count, or shader sampler contracts change.
- [x] On switching between mono, per-eye, and true stereo, rebuild:
  - render pipeline resource profile
  - FBOs
  - texture resources
  - command chains
  - temporal state
  - descriptor caches
- [x] Ensure `XRBase`-derived settings use `SetField(...)` for mutation.
- [x] Add a central mode-change reason string for resource invalidation diagnostics.
- [x] Add tests that repeatedly toggle all stereo modes without stale FBO/texture exceptions.

### 15. Profiling And Performance Dumps

- [x] Add a repeatable profile runner for the OpenXR mode matrix.
- [x] Capture CPU samples for:
  - visible collection
  - command buffer recording
  - resource planning
  - descriptor writes
  - shader/pipeline variant resolution
  - OpenXR wait/begin/end
  - queue submit/present
- [x] Capture GPU timings for:
  - depth/prepass
  - G-buffer/forward opaque
  - lighting
  - AO
  - atmosphere/fog
  - temporal/TAA/TSR
  - post-process
  - eye publish/copy
- [x] Add CSV/JSON summaries under `Build/_AgentValidation/<run>/reports/`.
- [x] Compare sequential vs parallel vs true stereo using the same scene, resolution, AA, and headset runtime.
- [x] Record the current bottleneck before optimization work and after each major phase.

### 16. Validation Matrix

- [x] Build:
  - `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - `dotnet build .\XREngine\XREngine.csproj`
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [x] Tests:
  - [x] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~VulkanUpscaleBridgeTodoCompletionTests|FullyQualifiedName~VrViewRenderModeContractTests"`
  - [x] add and run OpenXR stereo mode resolver/source-contract tests
  - [x] add and run temporal policy/source-contract tests
  - add and run shader compiler stereo extension tests
- [x] Smoke runs:
  - OpenXR Vulkan sequential
  - OpenXR Vulkan single-pass compatibility
  - OpenXR Vulkan true single-pass stereo, once implemented
  - OpenXR Vulkan parallel command-buffer recording
  - OpenXR OpenGL sequential
  - OpenXR OpenGL stereo compatibility/true stereo where supported
- [x] Feature toggles:
  - TAA
  - TSR
  - FXAA/SMAA
  - auto exposure on/off
  - atmosphere on/off
  - volumetric fog on/off
  - vendor upscale on/off
  - mirror FBO on/off
- [x] Evidence:
  - validation logs with no steady-state Vulkan VUIDs
  - OpenXR lifecycle logs with no wait/begin/end failures
  - profile dump summaries
  - OpenXR smoke summaries showing stable left/right acquire/wait/release counts, distinct mode/path reporting, and successful teardown

### 17. Documentation

- [x] Update `docs/architecture/rendering/openxr-vr-rendering.md` with the effective stereo modes and their resource models.
- [x] Update `docs/architecture/rendering/openvr-rendering.md` if shared stereo terminology changes.
- [x] Update `docs/architecture/rendering/default-render-pipeline-notes.md` with stereo history invariants.
- [x] Update unit-testing world schema/comments if settings are renamed or split.
- [x] Add troubleshooting notes for:
  - TSR disabled in OpenXR per-eye modes
  - true stereo unavailable
  - Vulkan multiview unsupported
  - vendor upscale unsupported in VR
  - mode toggle resource invalidation

## Completion Evidence 2026-06-30

- Focused tests:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~OpenXrStereoTemporalIsolationCompletionTests" --no-restore`
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~VulkanUpscaleBridgeTodoCompletionTests|FullyQualifiedName~VrViewRenderModeContractTests|FullyQualifiedName~OpenXrStereoTemporalIsolationCompletionTests" --no-restore`
- Builds:
  - `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - `dotnet build .\XREngine\XREngine.csproj --no-restore`
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- Vulkan OpenXR mode matrix:
  - `Build/_AgentValidation/20260630-openxr-mode-profile-matrix-final/reports/openxr-mode-profile-matrix.json`
  - Engine log sessions referenced by the smoke summaries are copied under `Build/_AgentValidation/20260630-openxr-mode-profile-matrix-final/logs/engine-sessions/`.
  - All five cases exited `0`: sequential dedicated, true single-pass dedicated, parallel dedicated, parallel collect-visible, and parallel serial-submit.
  - Each smoke summary reported `teardownCompleted=true`, `endFrameFailureCount=0`, `warnings=0`, and `failures=0`.
  - The true single-pass case reported `viewRenderImplementationPath=TrueSinglePassStereo` and `viewRenderTemporalHistoryPolicy=StereoArrayLayer`.
- OpenGL OpenXR smoke:
  - `Build/_AgentValidation/20260630-openxr-opengl-sequential-smoke/reports/openxr-smoke-summary.json`
  - `Build/_AgentValidation/20260630-openxr-opengl-singlepass-smoke/reports/openxr-smoke-summary.json`
  - Engine log sessions are copied under each run root's `logs/engine-session/` folder.
  - Both reported `teardownCompleted=true`, `failures=0`, and `warnings=0`; OpenGL single-pass correctly reported the compatibility path.
- Loader/runtime preflight:
  - `Build/_AgentValidation/20260630-openxr-smoke-singlepass-repro/reports/openxr-loader-preflight.json`
  - Monado exposed both `XR_KHR_vulkan_enable2` and `XR_KHR_opengl_enable`; repeated native preflight inside the full matrix was skipped to avoid a PowerShell/native loader unload instability that occurs before engine startup.
- Known warning state:
  - The remaining warnings are pre-existing `Magick.NET-Q16-HDRI-AnyCPU` `NU1902`/`NU1903` advisories from NuGet vulnerability metadata.
  - `XREngine.Runtime.Rendering` built with `0 Warning(s)` and `0 Error(s)`.

## Code Landmarks

- OpenXR Vulkan mode routing:
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs`
- OpenXR frame/viewport setup:
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs`
- True engine stereo path:
  - `XRENGINE/Engine/Engine.VRState.cs`
  - `XREngine.Runtime.Rendering/Rendering/XRViewport.cs`
- Vulkan command-chain and dynamic rendering:
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs`
- Stereo shader generation/normalization:
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
  - `XREngine.Runtime.Rendering/Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs`
  - `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs`
- Temporal and TSR:
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs`
  - `Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs`
  - `Build/CommonAssets/Shaders/Scene3D/TemporalAccumulation.fs`
- Atmosphere and volumetric fog:
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_AtmosphereHistoryPass.cs`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VolumetricFogHistoryPass.cs`
- Vendor upscale:
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs`
- Stats/profiling:
  - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs`
  - `XRENGINE/Engine/Engine.ProfileCapture.cs`

## Recommended Order

1. Lock down diagnostics and mode names so every profile tells the truth.
2. Keep current per-eye OpenXR modes stable by disabling or isolating unsafe history consumers.
3. Complete Vulkan multiview command-recording validation for the staged true-stereo path.
4. Add stereo TSR/TAA resource and shader support.
5. Add explicit VR policies for auto exposure, atmosphere/fog history, and vendor upscalers.
6. Re-enable features mode by mode only after validation proves eye isolation.

## Completion Checklist

- [x] All acceptance criteria are met.
- [x] All mode toggles are stable in logs and runtime.
- [x] Per-eye temporal correctness has tests.
- [x] CPU/GPU profile summaries are attached or referenced.
- [x] OpenXR rendering architecture docs are updated.
- [x] This TODO contains the completed closeout note for the implementation pass.
