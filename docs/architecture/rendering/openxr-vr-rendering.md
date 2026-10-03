# OpenXR VR Rendering

OpenXR rendering has three owners. `XREngine.Runtime.XR.OpenXR` owns the loader, instance, session, reference spaces, actions, runtime swapchains, frame calls, and native projection structures. `XREngine.Runtime.Rendering` owns `IOpenXrRuntime`, `IOpenXrGraphicsHost`, `IOpenXrGraphicsCalls`, `IXrGraphicsBinding`, and the engine's view and diagnostic values. The OpenGL and Vulkan renderer projects own their own OpenXR graphics bindings, GPU resources, and submission/retirement behavior. Bootstrap installs `OpenXrRuntimeBackend.Register()` before renderer initialization and orchestrates the runtime through `IOpenXrRuntime`.

The source split keeps the `OpenXRAPI` namespace and public type identity in the OpenXR leaf. It does not establish SteamVR headset, Monado, or OpenGL/Vulkan runtime qualification; use the [runtime guide](../../developer-guides/vr/openxr-runtime.md) for those lanes.

## Startup and graphics binding

The application selects OpenXR through the VR startup settings. A forced OpenXR choice reports a named failure when the module, loader, runtime, or required graphics extension is unavailable. The runtime creates an instance and system, selects the renderer binding through its registered backend, and creates the session only after the renderer can supply a valid graphics binding. OpenGL session creation uses the current thread's borrowed `XRWindow.DesktopGlContext` HDC/HGLRC. Vulkan can borrow a renderer-owned OpenXR bootstrap lease for `XR_KHR_vulkan_enable2`; that lease retains the loader and instance while sessions and retired children still depend on them. An unknown renderer-owned lease is rejected diagnostically.

The graphics binding sees a native-free host and exact signed OpenXR result codes. It passes typed `ulong` native handles and uses a short-lived, pinned dispatch borrow only while obtaining a native graphics function pointer or creating a session. The OpenXR leaf owns the authoritative instance, session, swapchain, and acquired-image state. The bindings do not cast the runtime interface back to `OpenXRAPI`.

## Frame lifecycle and pose timing

The engine separates frame preparation, visibility collection, and rendering. The runtime polls events and session state, waits for the runtime's predicted display time, locates predicted views for visible collection, then locates late views near rendering. Eye cameras and controller/tracker transforms select explicit predicted or late pose caches; a runtime-specific pose-time offset affects locate calls without changing the display time submitted to the runtime.

For each eye the binding acquires an image, waits for that image, renders to the external swapchain viewport, flushes as required, and releases it. A wait failure does not authorize a release. A failed release keeps the runtime's acquired ownership in the OpenXR host ledger; the frame ends without a projection layer and swapchain destruction is deferred. The OpenXR leaf stages native projection views in cached storage and calls `xrEndFrame` synchronously with the pointer graph still valid. No per-frame neutral projection array is handed across the renderer boundary.

`SequentialViews` renders eyes separately. OpenXR Vulkan's `SinglePassStereo` request is strict: it uses the layered multiview path only when the required capabilities and resource generation are available. If it cannot honor that path, it reports the rejection and submits no projection layer; choose `SequentialViews` explicitly to request per-eye rendering. Desktop mirror composition remains renderer-owned.

## Swapchain and device lifetime

The OpenXR host owns native swapchain handles and an authoritative acquired-image ledger. A successful `xrAcquireSwapchainImage` adds ownership; only a successful `xrReleaseSwapchainImage` removes it, except explicit terminal abandonment after device loss. Neither normal cleanup nor deferred retirement may destroy a swapchain with an acquired image.

Vulkan retirement reserves host custody before mutating child resources. Under queue admission and graphics-transition synchronization, it recaptures completion and resource receipts, rechecks acquired images, commits active handles to a retired generation, and publishes the renderer payload together. The host pins the parent instance and loader until both native handles and renderer resources are disposed. A failure after child mutation keeps recovery custody instead of retrying an unsafe detach. Device loss abandons active and retired resources without GPU waits or pretending that an image was released.

OpenGL keeps its WGL context on its owner thread and destroys eye FBO resources before releasing valid runtime swapchains. Vulkan serializes OpenXR begin, acquire, wait, release, and end operations with its command admission; normal retirement waits for the relevant GPU receipts before native destruction.

## Input and scene resources

`RuntimeVrInputServices` exposes action values, grip/aim poses, haptics, and hand data without native OpenXR types. The OpenXR leaf owns action creation, profile suggestions, per-frame synchronization, `XR_HTCX_vive_tracker_interaction` paths, and optional hand tracking. The runtime model provider supplies `XR_MSFT_controller_model` GLB data when available; scene components consume neutral model descriptors. SteamVR OpenVR model lookup can supply controller or tracker visuals when the OpenXR extension does not provide them. Pose authority remains with the active OpenXR runtime.

## Source map

| Responsibility | Source |
|---|---|
| Native runtime and frame lifecycle | `XREngine.Runtime.XR.OpenXR/OpenXRAPI.FrameLifecycle.cs`, `OpenXRAPI.XrCalls.cs`, `OpenXRAPI.RuntimeStateMachine.cs` |
| Native session, graphics gateway, and custody | `XREngine.Runtime.XR.OpenXR/Instance.cs`, `OpenXRAPI.GraphicsCalls.cs`, `OpenXRAPI.RetirementCustody.cs` |
| Engine-facing contracts | `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/IOpenXrRuntime.cs`, `IOpenXrGraphicsHost.cs`, `IXrGraphicsBinding.cs` |
| Renderer bindings | `XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenXR/OpenGlXrGraphicsBinding*.cs`; `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding*.cs` |
| Application orchestration | `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs` |

See [OpenVR rendering](openvr-rendering.md) for the engine-owned eye texture and compositor path, and [project organization](../runtime/project-organization.md) for dependency direction.
