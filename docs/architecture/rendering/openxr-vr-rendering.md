# OpenXR VR Rendering

OpenXR rendering has three owners. `XREngine.Runtime.XR.OpenXR` owns the loader, instance, session, reference spaces, actions, runtime swapchains, frame calls, and native projection structures. `XREngine.Runtime.Rendering` owns `IOpenXrRuntime`, `IOpenXrGraphicsHost`, `IOpenXrGraphicsCalls`, `IXrGraphicsBinding`, smoke DTOs, and engine view and diagnostic contracts. The OpenGL and Vulkan renderer projects own their OpenXR graphics bindings, GPU resources, submission, and retirement behavior. Bootstrap installs `OpenXrRuntimeBackend.Register()` before renderer initialization and orchestrates the runtime through `IOpenXrRuntime`.

The source split keeps the `OpenXRAPI` namespace and public type identity in the OpenXR leaf. It does not qualify SteamVR headset, Monado, or OpenGL/Vulkan runtime behavior by itself. Use the [runtime guide](../../developer-guides/vr/openxr-runtime.md) for those lanes.

## Startup and graphics binding

The application selects OpenXR through VR startup settings. A forced OpenXR choice reports a named failure when the module, loader, runtime, or required graphics extension is unavailable. The runtime creates an instance and system, selects the renderer binding through its registered backend, and creates the session only after the renderer can supply a valid graphics binding. OpenGL session creation uses the current thread borrowed `XRWindow.DesktopGlContext` HDC/HGLRC. Vulkan can borrow a renderer-owned OpenXR bootstrap lease for `XR_KHR_vulkan_enable2`; that lease retains the loader and instance while sessions and retired children still depend on them. An unknown renderer-owned lease is rejected with a diagnostic.

The Vulkan renderer creates its instance through the OpenXR bootstrap only when the run requests OpenXR. These signals request it: VR startup settings that select OpenXR, an OpenXR API that is active or already created, `XRE_UNIT_TEST_OPENXR_RUNTIME_JSON` (the editor's live desktop-to-OpenXR switch sets it before it recreates the renderer), `XRE_UNIT_TEST_VR_MODE=MonadoOpenXR` or `OpenXR`, and `XRE_UNIT_TEST_USE_OPENXR`. When OpenXR is requested and the bootstrap fails, renderer startup fails with the OpenXR reason. `XR_RUNTIME_JSON` alone does not request OpenXR: it only selects the runtime that the OpenXR loader uses, so a desktop session with it set creates a plain Vulkan instance.

The graphics binding sees a native-free host and exact signed OpenXR result codes. It passes typed `ulong` native handles and uses a short-lived pinned dispatch borrow only while it obtains a native graphics function pointer or creates a session. The OpenXR leaf owns the authoritative instance, session, swapchain, and acquired-image state. The bindings do not cast the runtime interface back to `OpenXRAPI`.

## Frame lifecycle and pose timing

The engine separates frame preparation, visibility collection, and rendering. The runtime polls events and session state, waits for the runtime predicted display time, locates predicted views for visible collection, then locates late views near rendering. Eye cameras and controller or tracker transforms select explicit predicted or late pose caches. A runtime-specific pose-time offset affects locate calls without changing the display time submitted to the runtime.

For each eye, the binding acquires an image, waits for that image, renders to the external swapchain viewport, flushes as required, and releases it. A wait failure does not authorize a release. A failed release keeps the runtime acquired ownership in the OpenXR host ledger. The frame ends without a projection layer, and swapchain destruction is deferred. When the wait or release fails because the runtime is lost (session lost, instance lost, runtime failure or runtime unavailable, for example when the runtime process stops), `CheckResult` marks the runtime loss, the ownership ledger no longer applies, and the Vulkan eye batch ends the frame without layers instead of throwing to the render circuit breaker (`IOpenXrGraphicsHost.IsRuntimeLossPending`). The OpenXR leaf stages native projection views in cached storage and calls `xrEndFrame` synchronously with the pointer graph still valid. No per-frame neutral projection array is handed across the renderer boundary.

A frame can end without projection layers through several exits, and each exit has its own process counter (`EOpenXrNoLayerFrameReason`, in `get_render_profiler_stats` as `vr.process_openxr_no_layer_frames` and in the profile capture line as `vr_process_openxr_no_layer_frames_<reason>`): `frame_skip` (the runtime returned `shouldRender = false`), `unsupported_view_mode`, `eye_pipeline_pending`, `eye_pipeline_rejected`, `eye_render_failure` (an eye did not render or submit, for example a failed parallel eye batch), `frame_exception` (the cleanup ended a frame that threw), and `prepare_aborted` (the pacing owner began the frame but view location or the pose update failed). A frame that the render thread never takes because its collect did not publish it in time is not a no-layer frame: the pacing owner waits for it before it begins the next one.

`SequentialViews` renders eyes separately. OpenXR Vulkan `SinglePassStereo` is strict. It uses the layered multiview path only when required capabilities and resource generation are available. If it cannot honor that path, it reports the rejection and submits no projection layer. Choose `SequentialViews` explicitly to request per-eye rendering. Desktop mirror composition remains renderer-owned.

On Vulkan, each eye owns two frame-data slots after the desktop slots (`ResolveOpenXrRecordImageIndex`, slot parity 0 and 1). Eye submissions are asynchronous, so a per-eye recording alternates between the two slots of its eye: the next frame of an eye records into one slot while the GPU still reads the other. When the expected slot is still owned by an in-flight submission, the preparation uses the other slot of the eye. When both are busy, it waits for the older slot for at most one predicted display period (one 90 Hz period before the runtime reports a period). If the slot is still busy, the preparation fails and the frame ends without layers (`eye_render_failure`). Single-pass stereo, the mirror and prewarm use only the parity 0 slots. A slot holds only per-frame CPU-written data: mapped uniform arena chunks, frame-data arena chunks (about 82 MiB for each slot at the initial sizes, most of it device-local host-visible memory), descriptors, command buffers and timing queries. Eye render targets, depth targets and temporal history (TSR, auto exposure, GTAO) stay per eye. The process counters `vr.process_openxr_eye_frame_data_slot_alternates`, `..._waits` and `..._busy_failures` (`get_render_profiler_stats`; capture fields `vr_process_openxr_eye_frame_data_slot_*`) count these cases. When a per-eye preparation returns no input, its readiness failure, and so the render readiness failure log, names the reason.

Before a per-eye preparation records, `ShouldDeferOpenXrEyeRenderingWork` can skip the frame (readiness ticket `openxr-eye-resource-work`, the frame ends without layers). It defers only for an unavailable device, texture descriptor publications that are still pending (`VulkanTextureUploadService.TryDescribePendingDescriptorPublications`; a publication changes the descriptors that the eye captures), and high Vulkan allocator pressure (`TryDescribeOpenXrVulkanAllocatorPressure`: 90% of the largest device-local heap or less than 512 MiB left). Texture transfers in flight and active upload preparation do not defer the eye, because the eye keeps binding the resident texture until the publication. The recent allocation-failure cooldown is checked later in the eye preparation, inside the eye planner scope. History: the gate came from a startup memory-pressure and device-loss incident (d7fcbcb20) and first deferred the eye for any texture upload work; at that time it dropped about 200 per-eye frames after each camera placement while textures streamed in, which single-pass stereo does not do.

`XRE_OPENXR_EYE_VIEW_DIAGNOSTICS=N` (a positive XR frame interval) writes one line for each eye every `N` XR frames to the auxiliary log `openxr-eye-view-diagnostics.log`, also in Release sessions. Per-eye rendering writes it right before each eye renders and single-pass stereo writes it for both eyes after the late eye pose. Each line holds the path, the viewport display and internal size, the internal-to-output scale, the anti-aliasing mode and TSR scale of the last frame profile, the camera FOV angles, the camera render-matrix translation (the late eye pose) and the camera position of the same view in the published view set (the collect-time pose), so one run in each mode compares how the two paths set up an eye.

## View render modes and view-scoped state

`EVrViewRenderMode` is the requested mode. `EVrViewRenderImplementationPath` is the path that the renderer actually uses: `SequentialViews`, `ParallelCommandBufferRecording`, `TrueSinglePassStereo`, or `Unsupported`. `EVrTemporalHistoryPolicy` records how temporal history is kept for that path. Logs, renderer stats, profile captures, and OpenXR smoke summaries report all three values. The temporal rules are in [Default Render Pipeline Notes](default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation).

| Mode | Backends | Behavior |
|---|---|---|
| `SequentialViews` | OpenGL, Vulkan | Each eye is one view context. This is the reference path. |
| `SinglePassStereo` | OpenGL, Vulkan | One stereo context renders both eyes into an engine-owned layered target (`OpenXrStereoRenderTarget`, `XRViewport.RenderStereo(...)`), then publishes each layer to its eye swapchain. Vulkan uses `GL_EXT_multiview` or `gl_ViewIndex`, never NV stereo semantics. |
| `ParallelCommandBufferRecording` | Vulkan only | The default mode in settings. It has the same output contract as `SequentialViews`. `OpenXrEyeRecordWorkerScheduler` records left and right primary command buffers on bounded workers after all eye inputs are prepared. The left eye uses render lane 0 (the render thread) and the right eye uses lane 1 (a render worker), so the mode needs at least one render worker; startup adds one when OpenXR starts in this mode (see `XRE_RENDER_WORKER_THREADS` in the [job system guide](../../developer-guides/runtime/job-system.md#environment-variables)). With one lane, both eyes record one after the other on the render thread, the renderer logs one warning, and `vr.process_openxr_parallel_eye_record_serialized_frames` counts the frames. OpenGL rejects this mode with a diagnostic, so OpenGL VR must select `SequentialViews`. |

Measured on Monado with the Sponza avatar benchmark (Vulkan, per-eye; values are for the sun / near camera poses). Two frame-data slots for each eye lowered `eye_render_failure` from about 0.9 to less than 0.01 for each XR frame: the Advanced pipeline went from 27.7 / 28.2 Hz to 38.1 / 38.4 Hz, and RVC with `ParallelCommandBufferRecording` and no render worker went from 29.3 / 31.0 Hz to 36.6 / 40.5 Hz. With one render worker (the startup default for this mode), RVC reached 40.6 / 42.5 Hz: the two eye recordings overlapped for about 86% of their span (span 4.5 / 4.1 ms, overlap 3.5 / 3.1 ms), and the publication age was 8 to 9 ms.

A per-eye eye render is a mono pass, but its frame view is an eye view: `RenderFrameViewSetCapture` gives an OpenXR per-eye render (a viewport that writes an external eye swapchain image, with an eye camera) the `LeftEye` or `RightEye` kind and the eye history key (`LeftEyeHistoryKey` or `RightEyeHistoryKey`), as for a stereo eye. The eye kind sets the stereo eye flag of the Advanced view record, so the shading samples the headset directional shadow cascades (fitted to the eye cameras) and not the desktop cascades, and the eye history keys keep the two eyes' visibility plans and depth-pyramid history apart. A single-view set uses the mono temporal jitter and history, also for a right eye. Desktop renders keep `DesktopEditor` and `MonoHistoryKey`.

Mutable render state belongs to a view, not to the renderer. On Vulkan, each eye recording receives an immutable `OpenXrEyeRenderTargetContext` with eye index, acquired image index, image and view handles, format, extent, depth target, external region, command-chain key, and planner key. `VulkanOpenXrViewResourcePlannerContextKey` scopes resource-planner state, allocator-backed image views, framebuffers, and descriptor image info per view. Primary command-buffer cache keys include the eye, swapchain image, depth generation, planner signature, and secondary-buffer generations. Eyes and images cannot alias. Recorded texture uploads publish only after the submit that used them completes. Uploads that belong to a failed recording are cancelled. If one eye fails to record, the renderer submits neither eye and releases the acquired images in OpenXR order.

`ViewRenderGroupContext` holds every output view in a frame and its visibility groups:

- `VR.AllowDesktopEditing=true`: the desktop editor view has its own camera, visible set, and history. VR eyes use a separate combined visibility pass.
- `VR.AllowDesktopEditing=false`: one collect-visible pass covers the left eye, the right eye, and the smoothed cyclopean desktop view. `ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(...)` builds the conservative frustum. Each view keeps its own matrices, targets, and command state.

`ViewFoveationContext` carries requested and effective `EVrFoveationMode`, quality preset, foveal center, and fallback reason for each view. Foveation never shrinks the visibility frustum. An explicit request that the backend cannot honor reports a diagnostic.

Advanced eye outputs can also shade the outer part of each eye at half rate with the `VrAdvancedEdgeHalfRateShading` engine setting (off by default). It is independent of `EVrFoveationMode` and does not change the view set or the visibility frustum. See [edge half-rate shading](advanced-render-pipeline.md#edge-half-rate-shading-for-eye-outputs).

`OpenXrRenderPacingMode` selects the thread that owns `xrWaitFrame`, `xrBeginFrame`, and `xrLocateViews`: `InRenderCallback`, `PostRenderCallback`, `DedicatedThread` (default), or `CollectVisibleThread`. `xrEndFrame` and layer submission stay on the OpenXR render path in all modes.

With TSR on the eyes, `TsrRenderScale` (default 0.75) renders the eyes below the swapchain size and TSR upscales to it. True single-pass stereo eyes and bound Advanced (or RVC) per-eye eyes on Vulkan have TSR history and use the scale. Default-pipeline per-eye eyes and OpenGL per-eye eyes have no TSR history; they render at the swapchain size without temporal anti-aliasing and log a `[TemporalHistory]` warning. Only true single-pass stereo eyes are validated on a headset with a scale below 1. See [eye render scale](advanced-render-pipeline.md#eye-render-scale).

Per-eye swapchain size does not follow the desktop window. `OpenXrEyeResolutionPreset`, `OpenXrEyeResolutionScale`, and `OpenXrCustomEyeResolutionWidth`/`Height` select it. Swapchain creation clamps to the runtime maximum image rectangle and logs the clamp. A live change recreates the OpenXR instance and session resources. Unit-testing worlds can set the same values through `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_PRESET`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_SCALE`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_WIDTH`, and `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_HEIGHT`. The Monado launch path passes the resolved profile to the simulated HMD through `XRE_OPENXR_EYE_RESOLUTION_*`.

Desktop output while VR is active follows [VR output pacing and mirror policy](vr-output-pacing-and-mirror-policy.md).

## Mirror and desktop output

OpenXR eye submission is separate from desktop output. A mirror mode change never changes XR eye submission. `BlitSubmittedEye` is the low-cost profiling mode. `FullIndependentRender` renders a separate desktop scene and uses independent desktop visibility. `CyclopeanReconstruct` is the intended low-cost spectator mode that composes a middle view from left and right eye color plus resolved linear depth.

The reconstruction path must publish per-eye color, per-eye linear depth, frame identity, matrices, depth convention metadata, and output dimensions before composition. It rejects mixed-frame inputs and must not mutate swapchain images. Vulkan is the primary implementation path because it already has per-eye mirror color publication infrastructure. OpenGL needs per-eye sampleable color and depth targets before it can use the same composition contract.

## VR eye feature profile

The engine setting `VrEyeFeatures` (`VrEyeFeatureProfile`, editor: engine settings, category
**VR**, **VR Eye Features**) selects the optional rendering features of every VR eye frame:
single-pass stereo and per-eye (`SequentialViews`, `ParallelCommandBufferRecording`), the
Default, Advanced and RVC pipelines, and OpenXR and OpenVR. Both eyes use this one profile, and
the desktop and other cameras never read it. The Unit Testing World sets it through
`VR.EyeFeatures`.

| Setting | Default | Controls |
|---|---|---|
| `AmbientOcclusion` | on | Camera AO passes (for example GTAO). Off keeps the neutral AO fill. |
| `FogAndAerialPerspective` | on | Volumetric fog and atmospheric scattering aerial perspective. |
| `OrderIndependentTransparency` | on | Weighted blended OIT and the exact transparency techniques. |
| `DepthOfField` | off | Depth of field. Off for comfort in a headset. |
| `MotionBlur` | off | Motion blur. Off for comfort in a headset. |
| `DeferredMsaa` | on | The deferred MSAA path when the eye camera selects MSAA. |
| `ForwardPlusLightCulling` | on | Forward+ local light culling. Off leaves forward and transparent surfaces without local lights. |

A feature runs in an eye frame only when the profile allows it and the eye camera's own settings
enable it (the shared eye post-process state and render overrides that `VrEyeCameraSettingsLink`
keeps equal for both eyes). The profile cannot add a feature that the active pipeline does not
support for the view; for example, the stereo resource profiles still exclude exact transparency,
fog and aerial perspective.

The profile does not change the eye camera's own stage settings. For an eye output, the MCP tool
`get_camera_post_process` adds `vrEyeFeatureProfile` (the seven values) and, for each stage,
`disabledByVrEyeFeatureProfile` (`VrEyeFeatureProfile.AllowsPostProcessStage`), so an ambient
occlusion stage that reads `Enabled = true` shows that the profile turns it off.

`VrEyeFrameRules` decides whether a render is an eye frame: a stereo pass, or a mono render whose
viewport camera is an eye camera (`XRCamera.StereoEyeLeft`) or whose viewport writes an external
swapchain image. Scene and light probe captures are never eye frames. Resource keys pass their
viewport and the pipeline's stereo flag, so a key built inside and outside the eye render agrees.
Without a viewport (command chain and resource generation), a query is not an eye frame, so the
generated graph keeps every feature and the render-time conditions decide.

Two rules stay tied to the OpenXR external eye target (`VrEyeFrameRules.IsExternalEyeTargetFrame`)
and are not features: the final output of such a target goes straight to the runtime image, so
vendor upscalers do not run there, and an external eye target without an eye exposure pair holds
its auto exposure. Auto exposure of paired eyes follows the headset or independent mode (see the
VR auto exposure notes in [default-render-pipeline-notes.md](default-render-pipeline-notes.md)).

History: until this profile, a blanket predicate (`UseOpenXrVulkanDesktopStartupSafePath`, Vulkan
plus OpenXR plus a mono external swapchain target) removed AO, fog and aerial perspective, motion
blur, depth of field, OIT and exact transparency, deferred MSAA, Forward+ culling and, in the
Default pipeline, bloom, the MSAA targets, the forward pre-pass and the debug visualizations from
every per-eye OpenXR Vulkan eye frame. Single-pass stereo and OpenVR eyes were not affected. The
profile replaces it, so per-eye eyes now get the same features as single-pass stereo eyes unless
the profile turns them off.

## Swapchain and device lifetime

The OpenXR host owns native swapchain handles and an authoritative acquired-image ledger. A successful `xrAcquireSwapchainImage` adds ownership. Only a successful `xrReleaseSwapchainImage` removes it, except explicit terminal abandonment after device loss. Normal teardown waits for acquired images to release before destroying swapchains. Runtime loss, graphics loss, and renderer recreation record a loss reason and use custody objects so native parents outlive children.

Vulkan retirement reserves host custody before mutating child resources. Under queue admission and graphics-transition synchronization, it recaptures completion and resource receipts, rechecks acquired images, commits active handles to a retired generation, and publishes the renderer payload together. The host pins the parent instance and loader until both native handles and renderer resources are disposed. A failure after child mutation keeps recovery custody instead of retrying an unsafe detach. Device loss abandons active and retired resources without GPU waits and without pretending that an image was released.

OpenGL keeps its WGL context on its owner thread and destroys eye FBO resources before releasing valid runtime swapchains. Vulkan serializes OpenXR begin, acquire, wait, release, and end operations with command admission. Normal retirement waits for the relevant GPU receipts before native destruction.

Vulkan eye submissions go through `OpenXrVulkanSubmissionTracker`. The tracker reserves admission before ordinary, parallel, or mirror submission and honors a rejected admission. Each accepted receipt carries the exact timeline semaphore and value and the frozen XR frame and display identity. The receipt keeps command, arena, upload, prepared-input, frame-slot, and native-resource ownership until real completion, then settles each owner once. A cancelled submission releases its owners without a native receipt. Storage is fixed: three in-flight submissions, three tracked command buffers and frame slots, 64 tracked uploads, and 64 tracked swapchain images. A payload above these limits is rejected. When all three in-flight slots are busy, a stereo frame waits for the oldest submission for at most `OpenXrStereoSubmissionWaitMs` (engine setting, default 50 ms, range 1 to 1000 ms). If no slot frees in that time, the frame goes to the runtime without layers, and the headset shows a black frame. A short wait causes black frames under GPU load. A long wait holds the render thread.

## Input and scene resources

`RuntimeVrInputServices` exposes action values, grip and aim poses, haptics, and hand data without native OpenXR types. The OpenXR leaf owns action creation, profile suggestions, per-frame synchronization, `XR_HTCX_vive_tracker_interaction` paths, and optional hand tracking. The runtime model provider supplies `XR_MSFT_controller_model` GLB data when available. Scene components consume neutral model descriptors. SteamVR OpenVR model lookup can supply controller or tracker visuals when the OpenXR extension does not provide them. Pose authority remains with the active OpenXR runtime.

## Avatar pose timing and coordinate ownership

The OpenXR adapter publishes predicted device poses with one snapshot identifier and predicted display time. Calibration and the VR player sample every required device from that publication. A mixed publication is rejected. Real samples expire after 250 ms without a new publication. Runtime poses are metric reference-space transforms. Each raw device composes that pose with its explicitly assigned playspace transform to produce a world pose. Avatar scale does not change this tracking basis.

On the scene owner, the VR player reads a coherent sample, updates room-scale playspace compensation, and queues its locomotion delta. It then publishes slot sources and targets. Stored device-to-target offsets multiply the device world matrix exactly once. The solver owns calibrated child targets under their physical sources and publishes replacements transactionally. Humanoid slots retain the raw source and its captured offset. Animation resets run in the normal animation tick. Target publication runs in the normal scene tick. IK evaluates in the late animation tick. Spectator follow runs in the late scene tick after IK. Character-controller movement consumes its queued delta through its physics owner. Losing a bound hips tracker suspends room-scale compensation instead of treating head lean as locomotion.

The skeleton uses the simulation pose. The render owner can late-locate headset and controller view transforms, but it does not rerun IK or mutate live bones. Thus late-located eye views can be newer than the rendered skeleton. This is an explicit latency tradeoff. Hardware timing validation must measure that difference.

Each body slot keeps its physical identity through tracking loss. It briefly holds its target, then fades to zero weight. The same physical device can fade back without rebinding another device. Estimated body poses are not supplied by the current calibration owner. Unknown reference-space changes invalidate calibration. Teleport, snap turn, avatar replacement, and session generation changes reset spectator history. Session restoration requires matching provider generation and reference-space identity.

## Source map

| Responsibility | Source |
|---|---|
| Native OpenXR runtime | `XREngine.Runtime.XR.OpenXR/` |
| Runtime lifecycle and frame calls | `XREngine.Runtime.XR.OpenXR/OpenXRAPI.FrameLifecycle.cs`, `OpenXRAPI.XrCalls.cs`, `OpenXRAPI.RuntimeStateMachine.cs`, `OpenXRAPI.Pacing.cs` |
| Native session, graphics gateway, and custody | `XREngine.Runtime.XR.OpenXR/Instance.cs`, `OpenXRAPI.GraphicsCalls.cs`, `OpenXRAPI.RetirementCustody.cs`, `OpenXRAPI.SwapchainLifecycle.cs`, `OpenXRAPI.DeviceLoss.cs` |
| Runtime contracts and smoke DTOs | `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/` |
| Renderer bindings | `XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenXR/OpenGlXrGraphicsBinding*.cs`; `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding*.cs` |
| Application orchestration | `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs`, `Engine.RuntimeVrStateServices.cs`, `Engine.RuntimeVrLifecycleServices.cs` |
| Editor smoke host | `XREngine.Editor/Program.OpenXrSmokeRunController.cs` |
| OpenXR tests | `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs`, `OpenXrSubmissionTrackerTests.cs`, `OpenXrStereoTemporalIsolationCompletionTests.cs`, `OpenXrSteamVrParityToolingContractTests.cs`, `OpenXrProbeRetryPolicyTests.cs`, `VrViewRenderModeContractTests.cs` |

See [OpenVR rendering](openvr-rendering.md) for the engine-owned eye texture and compositor path, and [project organization](../runtime/project-organization.md) for dependency direction.
