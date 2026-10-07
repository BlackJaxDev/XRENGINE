# OpenXR VR Rendering

OpenXR rendering has three owners. `XREngine.Runtime.XR.OpenXR` owns the loader, instance, session, reference spaces, actions, runtime swapchains, frame calls, and native projection structures. `XREngine.Runtime.Rendering` owns `IOpenXrRuntime`, `IOpenXrGraphicsHost`, `IOpenXrGraphicsCalls`, `IXrGraphicsBinding`, smoke DTOs, and engine view and diagnostic contracts. The OpenGL and Vulkan renderer projects own their OpenXR graphics bindings, GPU resources, submission, and retirement behavior. Bootstrap installs `OpenXrRuntimeBackend.Register()` before renderer initialization and orchestrates the runtime through `IOpenXrRuntime`.

The source split keeps the `OpenXRAPI` namespace and public type identity in the OpenXR leaf. It does not qualify SteamVR headset, Monado, or OpenGL/Vulkan runtime behavior by itself. Use the [runtime guide](../../developer-guides/vr/openxr-runtime.md) for those lanes.

## Startup and graphics binding

The application selects OpenXR through VR startup settings. A forced OpenXR choice reports a named failure when the module, loader, runtime, or required graphics extension is unavailable. The runtime creates an instance and system, selects the renderer binding through its registered backend, and creates the session only after the renderer can supply a valid graphics binding. OpenGL session creation uses the current thread borrowed `XRWindow.DesktopGlContext` HDC/HGLRC. Vulkan can borrow a renderer-owned OpenXR bootstrap lease for `XR_KHR_vulkan_enable2`; that lease retains the loader and instance while sessions and retired children still depend on them. An unknown renderer-owned lease is rejected with a diagnostic.

The graphics binding sees a native-free host and exact signed OpenXR result codes. It passes typed `ulong` native handles and uses a short-lived pinned dispatch borrow only while it obtains a native graphics function pointer or creates a session. The OpenXR leaf owns the authoritative instance, session, swapchain, and acquired-image state. The bindings do not cast the runtime interface back to `OpenXRAPI`.

## Frame lifecycle and pose timing

The engine separates frame preparation, visibility collection, and rendering. The runtime polls events and session state, waits for the runtime predicted display time, locates predicted views for visible collection, then locates late views near rendering. Eye cameras and controller or tracker transforms select explicit predicted or late pose caches. A runtime-specific pose-time offset affects locate calls without changing the display time submitted to the runtime.

For each eye, the binding acquires an image, waits for that image, renders to the external swapchain viewport, flushes as required, and releases it. A wait failure does not authorize a release. A failed release keeps the runtime acquired ownership in the OpenXR host ledger. The frame ends without a projection layer, and swapchain destruction is deferred. The OpenXR leaf stages native projection views in cached storage and calls `xrEndFrame` synchronously with the pointer graph still valid. No per-frame neutral projection array is handed across the renderer boundary.

`SequentialViews` renders eyes separately. OpenXR Vulkan `SinglePassStereo` is strict. It uses the layered multiview path only when required capabilities and resource generation are available. If it cannot honor that path, it reports the rejection and submits no projection layer. Choose `SequentialViews` explicitly to request per-eye rendering. Desktop mirror composition remains renderer-owned.

## View render modes and view-scoped state

`EVrViewRenderMode` is the requested mode. `EVrViewRenderImplementationPath` is the path that the renderer actually uses: `SequentialViews`, `ParallelCommandBufferRecording`, `TrueSinglePassStereo`, or `Unsupported`. `EVrTemporalHistoryPolicy` records how temporal history is kept for that path. Logs, renderer stats, profile captures, and OpenXR smoke summaries report all three values. The temporal rules are in [Default Render Pipeline Notes](default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation).

| Mode | Backends | Behavior |
|---|---|---|
| `SequentialViews` | OpenGL, Vulkan | Each eye is one view context. This is the reference path. |
| `SinglePassStereo` | OpenGL, Vulkan | One stereo context renders both eyes into an engine-owned layered target (`OpenXrStereoRenderTarget`, `XRViewport.RenderStereo(...)`), then publishes each layer to its eye swapchain. Vulkan uses `GL_EXT_multiview` or `gl_ViewIndex`, never NV stereo semantics. |
| `ParallelCommandBufferRecording` | Vulkan only | The default mode in settings. It has the same output contract as `SequentialViews`. `OpenXrEyeRecordWorkerScheduler` records left and right primary command buffers on bounded workers after all eye inputs are prepared. OpenGL rejects this mode with a diagnostic, so OpenGL VR must select `SequentialViews`. |

Mutable render state belongs to a view, not to the renderer. On Vulkan, each eye recording receives an immutable `OpenXrEyeRenderTargetContext` with eye index, acquired image index, image and view handles, format, extent, depth target, external region, command-chain key, and planner key. `VulkanOpenXrViewResourcePlannerContextKey` scopes resource-planner state, allocator-backed image views, framebuffers, and descriptor image info per view. Primary command-buffer cache keys include the eye, swapchain image, depth generation, planner signature, and secondary-buffer generations. Eyes and images cannot alias. Recorded texture uploads publish only after the submit that used them completes. Uploads that belong to a failed recording are cancelled. If one eye fails to record, the renderer submits neither eye and releases the acquired images in OpenXR order.

`ViewRenderGroupContext` holds every output view in a frame and its visibility groups:

- `VR.AllowDesktopEditing=true`: the desktop editor view has its own camera, visible set, and history. VR eyes use a separate combined visibility pass.
- `VR.AllowDesktopEditing=false`: one collect-visible pass covers the left eye, the right eye, and the smoothed cyclopean desktop view. `ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(...)` builds the conservative frustum. Each view keeps its own matrices, targets, and command state.

`ViewFoveationContext` carries requested and effective `EVrFoveationMode`, quality preset, foveal center, and fallback reason for each view. Foveation never shrinks the visibility frustum. An explicit request that the backend cannot honor reports a diagnostic.

`OpenXrRenderPacingMode` selects the thread that owns `xrWaitFrame`, `xrBeginFrame`, and `xrLocateViews`: `InRenderCallback`, `PostRenderCallback`, `DedicatedThread` (default), or `CollectVisibleThread`. `xrEndFrame` and layer submission stay on the OpenXR render path in all modes.

Per-eye swapchain size does not follow the desktop window. `OpenXrEyeResolutionPreset`, `OpenXrEyeResolutionScale`, and `OpenXrCustomEyeResolutionWidth`/`Height` select it. Swapchain creation clamps to the runtime maximum image rectangle and logs the clamp. A live change recreates the OpenXR instance and session resources. Unit-testing worlds can set the same values through `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_PRESET`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_SCALE`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_WIDTH`, and `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_HEIGHT`. The Monado launch path passes the resolved profile to the simulated HMD through `XRE_OPENXR_EYE_RESOLUTION_*`.

Desktop output while VR is active follows [VR output pacing and mirror policy](vr-output-pacing-and-mirror-policy.md).

## Mirror and desktop output

OpenXR eye submission is separate from desktop output. A mirror mode change never changes XR eye submission. `BlitSubmittedEye` is the low-cost profiling mode. `FullIndependentRender` renders a separate desktop scene and uses independent desktop visibility. `CyclopeanReconstruct` is the intended low-cost spectator mode that composes a middle view from left and right eye color plus resolved linear depth.

The reconstruction path must publish per-eye color, per-eye linear depth, frame identity, matrices, depth convention metadata, and output dimensions before composition. It rejects mixed-frame inputs and must not mutate swapchain images. Vulkan is the primary implementation path because it already has per-eye mirror color publication infrastructure. OpenGL needs per-eye sampleable color and depth targets before it can use the same composition contract.

## Swapchain and device lifetime

The OpenXR host owns native swapchain handles and an authoritative acquired-image ledger. A successful `xrAcquireSwapchainImage` adds ownership. Only a successful `xrReleaseSwapchainImage` removes it, except explicit terminal abandonment after device loss. Normal teardown waits for acquired images to release before destroying swapchains. Runtime loss, graphics loss, and renderer recreation record a loss reason and use custody objects so native parents outlive children.

Vulkan retirement reserves host custody before mutating child resources. Under queue admission and graphics-transition synchronization, it recaptures completion and resource receipts, rechecks acquired images, commits active handles to a retired generation, and publishes the renderer payload together. The host pins the parent instance and loader until both native handles and renderer resources are disposed. A failure after child mutation keeps recovery custody instead of retrying an unsafe detach. Device loss abandons active and retired resources without GPU waits and without pretending that an image was released.

OpenGL keeps its WGL context on its owner thread and destroys eye FBO resources before releasing valid runtime swapchains. Vulkan serializes OpenXR begin, acquire, wait, release, and end operations with command admission. Normal retirement waits for the relevant GPU receipts before native destruction.

Vulkan eye submissions go through `OpenXrVulkanSubmissionTracker`. The tracker reserves admission before ordinary, parallel, or mirror submission and honors a rejected admission. Each accepted receipt carries the exact timeline semaphore and value and the frozen XR frame and display identity. The receipt keeps command, arena, upload, prepared-input, frame-slot, and native-resource ownership until real completion, then settles each owner once. A cancelled submission releases its owners without a native receipt. Storage is fixed: three in-flight submissions, three tracked command buffers and frame slots, 64 tracked uploads, and 64 tracked swapchain images. A payload above these limits is rejected.

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
