# OpenVR (SteamVR) Rendering

OpenVR remains the tested day-to-day VR path. Its native `OpenVR.NET` and `Valve.VR` types, action manifest conversion, device enumeration, prediction queries, compositor submission, and render-model loading live in `XREngine.Runtime.XR.OpenVR`. `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs` owns application startup, timer callbacks, eye targets, scene coupling, and runtime selection through neutral interfaces. Rendering and Input expose device, pose, action, model, and compositor values without vendor types. Bootstrap registers `OpenVrRuntimeBackend.Register()` before VR startup.

The OpenVR source move keeps the existing tracked desktop native binary supply. The distinct VRClient `openvr_api.dll` is still carried by VRClient; changing or consolidating that binary requires a separate supply decision. Source organization alone does not replace live SteamVR validation.

## Startup and frame callbacks

The runtime selection chooses local or client mode according to startup settings. Local startup creates the OpenVR scene application and action manifests in the OpenVR leaf, starts the runtime, requires a compositor, installs the application manifest, and creates actions. A forced OpenVR failure is reported rather than silently using another runtime. Bootstrap attaches visibility collection, buffer swapping, and render callbacks to the engine timer and selects two-eye or stereo viewport mode. Only one VR runtime is active at a time; OpenXR uses the same host callback slots through `IOpenXrRuntime` while bypassing OpenVR eye targets and compositor submission.

For the local OpenVR path, input update computes prediction lead from time since VSync, display frequency, and seconds from VSync to photons. The OpenVR leaf calls `UpdateInput` with that lead, updates devices, and samples poses. Render update calls `UpdateDraw` for the selected neutral tracking origin; the scene's HMD, controllers, trackers, and eye cameras read copied neutral pose/device descriptors. Device transforms retain parent and local-offset multiplication. A render-model request returns owned mesh/texture data to the host after native buffers have been copied and released.

## Eye rendering and compositor submission

The engine owns OpenVR eye textures and viewports. Sequential rendering uses one target per eye; the stereo path uses the engine's array/multiview resources when its rendering backend supports them. The compositor receives actual OpenGL texture names with explicit color space, full-eye UV bounds, and submit flags through `IRuntimeOpenVrCompositor`. A Vulkan image handle is not presented as an OpenGL name; an unsupported texture backend reports a failure. The OpenVR leaf submits left and right eyes, records both exact compositor results, and calls `PostPresentHandoff` once for that pair.

The application can mirror the rendered eye output to the desktop without changing the texture submitted to SteamVR. Renderer resources remain owned by their graphics backend; OpenVR holds only the native compositor payload during the synchronous submission call.

## Input, models, and statistics

`RuntimeVrInputServices` provides action registration, state, poses, haptics, and skeleton summaries to gameplay code. The OpenVR leaf adapts the neutral action manifest to `OpenVR.NET` action sets and action handles. `RuntimeOpenVrStateServices` supplies immutable device IDs and descriptors plus copied local poses; scene components do not retain `VrDevice` instances. Controller and tracker model loading copies native buffers before returning data, preserving winding and texture orientation in the host's model construction.

Frame diagnostics use compositor timing samples for GPU, CPU, total time, and frame rate. The OpenVR backend owns the native timing records and returns `RuntimeVrFrameStats` to the host. The prediction and submission path should be checked in the live editor before treating this extraction as runtime-qualified.

## Source map

| Responsibility | Source |
|---|---|
| Native runtime, prediction, actions, manifests | `XREngine.Runtime.XR.OpenVR/OpenVrRuntimeBackend.cs`, `OpenVrActionBackend.cs`, `OpenVrActionManifestAdapter.cs` |
| Native compositor and device/model adapters | `XREngine.Runtime.XR.OpenVR/OpenVrCompositorBackend.cs`, `OpenVrDeviceBackend.cs`, `OpenVrModelLoader.cs` |
| Application eye targets and callbacks | `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs` |
| Neutral compositor and state contracts | `XREngine.Runtime.Rendering/Runtime/IRuntimeOpenVrCompositor.cs`, `IRuntimeOpenVrStateProvider.cs` |
| Neutral input and device values | `XREngine.Input/RuntimeVrInputServices.cs`, `XREngine.Data/Input/RuntimeVrDeviceInfo.cs` |

See [OpenXR rendering](openxr-vr-rendering.md) for runtime-owned swapchains and the separate OpenGL/Vulkan XR bindings.
