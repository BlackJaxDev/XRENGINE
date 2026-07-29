using Assimp;
using XREngine.Extensions;
using OpenVR.NET;
using OpenVR.NET.Devices;
using OpenVR.NET.Manifest;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valve.VR;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Runtime.Memory;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using ETextureType = Valve.VR.ETextureType;

namespace XREngine
{
    /// <summary>
    /// Application-owned VR lifecycle, transport, and render-callback orchestration.
    /// Process-wide VR state is owned by <see cref="RuntimeVrState"/>.
    /// </summary>
    internal static class EngineVrLifecycle
    {
            public enum VRRuntime
            {
                None,
                OpenVR,
                OpenXR
            }

            private static VRRuntime _activeRuntime
            {
                get => (VRRuntime)RuntimeEngine.VRState.ActiveRuntime;
                set => RuntimeEngine.VRState.ActiveRuntime = (RuntimeVrState.VRRuntime)value;
            }
            public static VRRuntime ActiveRuntime => _activeRuntime;

            public static bool IsOpenVRActive => _activeRuntime == VRRuntime.OpenVR;
            public static bool IsOpenXRActive => _activeRuntime == VRRuntime.OpenXR;

            private static OpenXRAPI? _openXRApi
            {
                get => RuntimeEngine.VRState.OpenXRApi;
                set => RuntimeEngine.VRState.OpenXRApi = value;
            }
            public static OpenXRAPI? OpenXRApi => RuntimeEngine.VRState.OpenXRApi;
            public static event Action<bool>? OpenXRSessionRunningChanged;

            private static void SyncRuntimeVrState()
            {
                RuntimeEngine.VRState.IsInVR = IsInVR;
                RuntimeEngine.VRState.ActiveRuntime = (RuntimeVrState.VRRuntime)_activeRuntime;
                RuntimeEngine.VRState.LeftEyeViewport = LeftEyeViewport;
                RuntimeEngine.VRState.RightEyeViewport = RightEyeViewport;
                // Keep the API alive while the runtime monitor waits for a session.
                // ActiveRuntime/IsInVR distinguish an active OpenXR session; clearing
                // OpenXRApi here prevents UpdateOpenXRRuntime from ever observing one.
                RuntimeEngine.VRState.ViewInformation = (_viewInformation.left, _viewInformation.right, _viewInformation.world, _viewInformation.HMDNode);
            }

            private static VR? _openVRApi
            {
                get => RuntimeEngine.VRState.OpenVRApiIfCreated;
                set
                {
                    if (value is not null)
                        RuntimeEngine.VRState.OpenVRApi = value;
                }
            }
            public static VR OpenVRApi => RuntimeEngine.VRState.OpenVRApi;

            private static VR? OpenVRApiIfActive => IsOpenVRActive ? _openVRApi : null;

            public enum VRMode
            {
                /// <summary>
                /// This mode indicates the VR system is awaiting inputs from a client and will send rendered frames to the client.
                /// </summary>
                Server,
                /// <summary>
                /// This mode indicates the VR system is sending inputs to a server and will receive rendered fr.
                /// </summary>
                Client,
                Local,
            }

            public static ETrackingUniverseOrigin Origin { get; set; } = ETrackingUniverseOrigin.TrackingUniverseStanding;

            public static VRIKCalibrationSettings CalibrationSettings
            {
                get
                {
                    if (RuntimeEngine.VRState.CalibrationSettings is VRIKCalibrationSettings settings)
                        return settings;

                    settings = new VRIKCalibrationSettings();
                    RuntimeEngine.VRState.CalibrationSettings = settings;
                    return settings;
                }
                set => RuntimeEngine.VRState.CalibrationSettings = value;
            }

            private static Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> _actions
                => RuntimeEngine.VRState.Actions;
            public static Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> Actions => _actions;

            private static bool _vrCallbacksInstalled;
            private static bool _vrCallbacksStereo;
            private static bool _openXrRuntimeMonitoring;
            private static bool _openXrUpdateHooked;
            private static bool _openXrSessionRunning;

            private static void InitRenderCallbacks(XRWindow window)
            {
                AttachRenderCallback(window);
                Renderer = window.Renderer;

                bool wantStereo = Stereo;
                if (!_vrCallbacksInstalled)
                {
                    if (wantStereo)
                    {
                        Engine.Time.Timer.CollectVisible += CollectVisibleStereo;
                        Engine.Time.Timer.SwapBuffers += SwapBuffersStereo;
                    }
                    else
                    {
                        Engine.Time.Timer.CollectVisible += CollectVisibleTwoPass;
                        Engine.Time.Timer.SwapBuffers += SwapBuffersTwoPass;
                    }

                    Debug.Out($"VRState callbacks: CollectVisible={(wantStereo ? nameof(CollectVisibleStereo) : nameof(CollectVisibleTwoPass))}, " +
                              $"SwapBuffers={(wantStereo ? nameof(SwapBuffersStereo) : nameof(SwapBuffersTwoPass))}, " +
                              $"Stereo={wantStereo}, Runtime={_activeRuntime}");

                    _vrCallbacksInstalled = true;
                    _vrCallbacksStereo = wantStereo;
                    return;
                }

                if (_vrCallbacksStereo == wantStereo)
                    return;

                // Switch variants: remove only the previously-installed handlers, then add the new ones.
                if (_vrCallbacksStereo)
                {
                    Engine.Time.Timer.CollectVisible -= CollectVisibleStereo;
                    Engine.Time.Timer.SwapBuffers -= SwapBuffersStereo;
                }
                else
                {
                    Engine.Time.Timer.CollectVisible -= CollectVisibleTwoPass;
                    Engine.Time.Timer.SwapBuffers -= SwapBuffersTwoPass;
                }

                if (wantStereo)
                {
                    Engine.Time.Timer.CollectVisible += CollectVisibleStereo;
                    Engine.Time.Timer.SwapBuffers += SwapBuffersStereo;
                }
                else
                {
                    Engine.Time.Timer.CollectVisible += CollectVisibleTwoPass;
                    Engine.Time.Timer.SwapBuffers += SwapBuffersTwoPass;
                }

                Debug.Out($"VRState callbacks: CollectVisible={(wantStereo ? nameof(CollectVisibleStereo) : nameof(CollectVisibleTwoPass))}, " +
                          $"SwapBuffers={(wantStereo ? nameof(SwapBuffersStereo) : nameof(SwapBuffersTwoPass))}, " +
                          $"Stereo={wantStereo}, Runtime={_activeRuntime}");

                _vrCallbacksStereo = wantStereo;
            }
            public static event Action<Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>>? ActionsChanged;

            private static Frustum? _stereoCullingFrustum
            {
                get => RuntimeEngine.VRState.StereoCullingFrustum;
                set => RuntimeEngine.VRState.StereoCullingFrustum = value;
            }
            public static Frustum? StereoCullingFrustum => RuntimeEngine.VRState.StereoCullingFrustum;

            private static XRRenderPipelineInstance? _twoPassLeftPipeline
            {
                get => RuntimeEngine.VRState.TwoPassLeftPipeline;
                set => RuntimeEngine.VRState.TwoPassLeftPipeline = value;
            }
            private static XRRenderPipelineInstance? _twoPassRightPipeline
            {
                get => RuntimeEngine.VRState.TwoPassRightPipeline;
                set => RuntimeEngine.VRState.TwoPassRightPipeline = value;
            }
            private static RenderCommandCollection? _sharedMeshRenderCommands
            {
                get => RuntimeEngine.VRState.SharedMeshRenderCommands;
                set => RuntimeEngine.VRState.SharedMeshRenderCommands = value;
            }

            /// <summary>
            /// The distance between the eyes in meters.
            /// </summary>
            public static float RealWorldIPD
            {
                get
                {
                    switch (_activeRuntime)
                    {
                        case VRRuntime.OpenVR:
                            var vr = OpenVRApiIfActive;
                            if (vr?.Headset is not null)
                            {
                                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                                return (float)vr.CVR.GetFloatTrackedDeviceProperty(vr.Headset!.DeviceIndex, ETrackedDeviceProperty.Prop_UserIpdMeters_Float, ref error);
                            }
                            return 0f;
                        case VRRuntime.OpenXR:
                            // OpenXR does not expose a single "user IPD" property in core.
                            // Derive it from the per-eye poses returned by xrLocateViews.
                            // This is available before engine-eye transforms are applied.
                            var oxr = OpenXRApi;
                            if (oxr is null)
                                return 0f;

                            return oxr.TryGetLatestIPD(out float ipd) ? ipd : 0f;
                        default:
                            return 0f;
                    }
                }
            }

            public static event Action<float>? IPDScalarChanged
            {
                add => RuntimeEngine.VRState.IPDScalarChanged += value;
                remove => RuntimeEngine.VRState.IPDScalarChanged -= value;
            }

            public static float IPDScalar
            {
                get => RuntimeEngine.VRState.IPDScalar;
                set => RuntimeEngine.VRState.IPDScalar = value;
            }

            /// <summary>
            /// Calculates the interpupillary distance (IPD) in world space,
            /// scaling the real-world IPD to match the avatar�s in-world height.
            /// </summary>
            public static float ScaledIPD
                => RealWorldIPD * ModelToRealWorldHeightRatio * IPDScalar;

            /// <summary>
            /// The ratio of the desired avatar height to the real-world height (desired divided by real).
            /// Multiply by IPD to get the scaled IPD.
            /// </summary>
            public static float RealToDesiredAvatarHeightRatio => DesiredAvatarHeight / RealWorldHeight;

            ///// <summary>
            ///// The ratio of the desired avatar height to the model height (desired divided by model).
            ///// Use as model scaling factor.
            ///// </summary>
            //public static float ModelToDesiredAvatarHeightRatio => DesiredAvatarHeight / ModelHeight;

            /// <summary>
            /// The ratio of the real-world height to the model height (real divided by model).
            /// Use as model scaling factor.
            /// </summary>
            public static float ModelToRealWorldHeightRatio => RealWorldHeight / ModelHeight;

            /// <summary>
            /// The ratio of the desired avatar height to the real-world height (desired divided by real).
            /// Use as model scaling factor after scaling to real-world height.
            /// </summary>
            public static float RealWorldToDesiredAvatarHeightRatio => DesiredAvatarHeight / RealWorldHeight;

            public static float RealWorldHeight
            {
                get => RuntimeEngine.VRState.RealWorldHeight;
                set => RuntimeEngine.VRState.RealWorldHeight = value;
            }

            public static float DesiredAvatarHeight
            {
                get => RuntimeEngine.VRState.DesiredAvatarHeight;
                set => RuntimeEngine.VRState.DesiredAvatarHeight = value;
            }

            public static float ModelHeight
            {
                get => RuntimeEngine.VRState.ModelHeight;
                set => RuntimeEngine.VRState.ModelHeight = value;
            }

            public static event Action<float>? RealWorldHeightChanged
            {
                add => RuntimeEngine.VRState.RealWorldHeightChanged += value;
                remove => RuntimeEngine.VRState.RealWorldHeightChanged -= value;
            }
            public static event Action<float>? DesiredAvatarHeightChanged
            {
                add => RuntimeEngine.VRState.DesiredAvatarHeightChanged += value;
                remove => RuntimeEngine.VRState.DesiredAvatarHeightChanged -= value;
            }
            public static event Action<float>? ModelHeightChanged
            {
                add => RuntimeEngine.VRState.ModelHeightChanged += value;
                remove => RuntimeEngine.VRState.ModelHeightChanged -= value;
            }

            public static OpenVR.NET.Input.Action? GetAction<TCategory, TName>(TCategory category, TName name)
                where TCategory : struct, Enum
                where TName : struct, Enum
            {
                if (_actions.TryGetValue(category.ToString(), out var nameDic))
                    if (nameDic.TryGetValue(name.ToString(), out var action))
                        return action;
                return null;
            }

            public static bool TryGetAction<TCategory, TName>(TCategory category, TName name, [NotNullWhen(true)] out OpenVR.NET.Input.Action? action)
                where TCategory : struct, Enum
                where TName : struct, Enum
            {
                action = GetAction(category, name);
                return action is not null;
            }

            public static bool InitializeOpenXR(XRWindow? window)
            {
                if (window is null)
                {
                    Debug.LogWarning("Cannot initialize OpenXR without an attached window.");
                    return false;
                }

                try
                {
                    // OpenXR should reuse the same engine callback hooks (Render + Timer.CollectVisible/SwapBuffers)
                    // as the OpenVR path. Disable OpenVR submission/state, but keep callback wiring unified.
                    DisableOpenVRRuntimeState();

                    _openXRApi ??= new OpenXRAPI();
                    _openXRApi.Window = window;
                    _openXRApi.EnableRuntimeMonitoring();
                    _openXrRuntimeMonitoring = true;
                    _openXrSessionRunning = false;
                    DeactivateOpenXRRuntime();

                    if (!_openXrUpdateHooked)
                    {
                        Engine.Time.Timer.PreUpdateFrame += UpdateOpenXRRuntime;
                        _openXrUpdateHooked = true;
                    }

                    // Render callbacks will be installed once the OpenXR session is actually running.
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to initialize OpenXR: {ex.Message}");
                    return false;
                }
            }

            private static void UpdateOpenXRRuntime()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.OpenXR.RuntimeUpdate", AllocationScopeCategory.VrInput)
                    : default;

                if (!_openXrRuntimeMonitoring || _openXRApi is null)
                    return;

                _openXRApi.UpdateRuntimeState();
                bool running = _openXRApi.IsSessionRunning;
                if (running == _openXrSessionRunning)
                    return;

                _openXrSessionRunning = running;
                if (running)
                    ActivateOpenXRRuntime();
                else
                    DeactivateOpenXRRuntime();

                OpenXRSessionRunningChanged?.Invoke(running);
                RuntimeEngine.VRState.NotifyOpenXRSessionRunningChanged(running);
            }

            private static void ActivateOpenXRRuntime()
            {
                if (_openXRApi?.Window is null)
                    return;

                _activeRuntime = VRRuntime.OpenXR;
                IsInVR = true;
                SyncRuntimeVrState();
                InitRenderCallbacks(_openXRApi.Window);
            }

            private static void DeactivateOpenXRRuntime()
            {
                if (_activeRuntime == VRRuntime.OpenXR)
                    _activeRuntime = VRRuntime.None;

                IsInVR = false;
                SyncRuntimeVrState();
            }

            private static void DisableOpenVRRuntimeState()
            {
                _openVrRuntimeActiveForRender = false;
                _emulatedRenderActive = false;
            }

            // Expose the same "RecalcMatrixOnDraw" hook so OpenXR can keep locomotion/VR rigs updated
            // at the same point in the frame as the OpenVR path.
            internal static void InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
                => RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(timing);

            private static void CreateActions(IActionManifest actionManifest, VR vr)
            {
                _actions.Clear();
                foreach (var actionSet in actionManifest.ActionSets)
                {
                    var actions = actionManifest.ActionsForSet(actionSet);
                    foreach (var action in actions)
                    {
                        var a = action.CreateAction(vr, null);
                        if (a is null)
                            continue;

                        string categoryName = actionSet.Name.ToString();
                        if (!_actions.TryGetValue(categoryName, out var nameDic))
                            _actions.Add(categoryName, nameDic = []);

                        nameDic.Add(action.Name.ToString(), a);
                    }
                }
                ActionsChanged?.Invoke(_actions);
                RuntimeEngine.VRState.NotifyActionsChanged();
            }

            //public static XRTexture2DArray? VRStereoViewTextureArray { get; private set; } = null;
            public static XRFrameBuffer? VRStereoRenderTarget
            {
                get => RuntimeEngine.VRState.VRStereoRenderTarget;
                private set => RuntimeEngine.VRState.VRStereoRenderTarget = value;
            }
            public static XRTexture2DArrayView? StereoLeftViewTexture
            {
                get => RuntimeEngine.VRState.StereoLeftViewTexture;
                private set => RuntimeEngine.VRState.StereoLeftViewTexture = value;
            }
            public static XRTexture2DArrayView? StereoRightViewTexture
            {
                get => RuntimeEngine.VRState.StereoRightViewTexture;
                private set => RuntimeEngine.VRState.StereoRightViewTexture = value;
            }
            private static XRViewport? StereoViewport
            {
                get => RuntimeEngine.VRState.StereoViewport;
                set => RuntimeEngine.VRState.StereoViewport = value;
            }

            public static XRTexture2D? VRLeftEyeViewTexture
            {
                get => RuntimeEngine.VRState.VRLeftEyeViewTexture;
                private set => RuntimeEngine.VRState.VRLeftEyeViewTexture = value;
            }
            public static XRMaterialFrameBuffer? VRLeftEyeRenderTarget
            {
                get => RuntimeEngine.VRState.VRLeftEyeRenderTarget;
                private set => RuntimeEngine.VRState.VRLeftEyeRenderTarget = value;
            }

            public static XRMaterialFrameBuffer? VRRightEyeRenderTarget
            {
                get => RuntimeEngine.VRState.VRRightEyeRenderTarget;
                private set => RuntimeEngine.VRState.VRRightEyeRenderTarget = value;
            }
            public static XRTexture2D? VRRightEyeViewTexture
            {
                get => RuntimeEngine.VRState.VRRightEyeViewTexture;
                private set => RuntimeEngine.VRState.VRRightEyeViewTexture = value;
            }

            public static AbstractRenderer? Renderer
            {
                get => RuntimeEngine.VRState.Renderer;
                set => RuntimeEngine.VRState.Renderer = value;
            }

            private static async Task<bool> InitSteamVR(IActionManifest actionManifest, VrManifest vrManifest)
                => await Task.Run(() =>
                {
                    var vr = OpenVRApi;
                    vr.DeviceDetected += OnDeviceDetected;
                    if (!vr.TryStart(EVRApplicationType.VRApplication_Scene))
                    {
                        Debug.LogWarning("Failed to initialize SteamVR.");
                        vr.DeviceDetected -= OnDeviceDetected;
                        return false;
                    }
                    else
                    {
                        _openVRApi = vr;
                        _activeRuntime = VRRuntime.OpenVR;

                        InstallApp(vrManifest);
                        vr.SetActionManifest(actionManifest);
                        CreateActions(actionManifest, vr);
                        Engine.Time.Timer.PreUpdateFrame += Update;
                        IsInVR = true;
                        SyncRuntimeVrState();
                        return true;
                    }
                });

            /// <summary>
            /// This method initializes the VR system in local mode.
            /// All VR input and rendering will be handled by this process.
            /// </summary>
            /// <param name="actionManifest"></param>
            /// <param name="vrManifest"></param>
            /// <param name="getEyeTextureHandleFunc"></param>
            /// <returns></returns>
            public static async Task<bool> InitializeLocal(
                IActionManifest actionManifest,
                VrManifest vrManifest,
                XRWindow window)
            {
                bool init = await InitSteamVR(actionManifest, vrManifest);
                if (!init)
                    return false;
                InitRender(window);
                return true;
            }

            private static bool Stereo => RuntimeEngine.Rendering.Settings.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo;
            //private static bool StereoUseTextureViews => RuntimeEngine.Rendering.Settings.SubmitOpenVRTextureArrayAsTwoViews;

            private static uint _lastRenderWidth
            {
                get => RuntimeEngine.VRState.LastRenderWidth;
                set => RuntimeEngine.VRState.LastRenderWidth = value;
            }
            private static uint _lastRenderHeight
            {
                get => RuntimeEngine.VRState.LastRenderHeight;
                set => RuntimeEngine.VRState.LastRenderHeight = value;
            }

            private static bool _openVrRuntimeActiveForRender
            {
                get => RuntimeEngine.VRState.OpenVrRuntimeActiveForRender;
                set => RuntimeEngine.VRState.OpenVrRuntimeActiveForRender = value;
            }
            private static bool _emulatedRenderActive
            {
                get => RuntimeEngine.VRState.EmulatedRenderActive;
                set => RuntimeEngine.VRState.EmulatedRenderActive = value;
            }
            private static XRWindow? _renderWindow
            {
                get => RuntimeEngine.VRState.RenderWindow;
                set => RuntimeEngine.VRState.RenderWindow = value;
            }

            private static void AttachRenderCallback(XRWindow window)
            {
                if (_renderWindow == window)
                    return;

                _renderWindow?.RenderViewportsCallback -= Render;
                _renderWindow?.PostRenderViewportsCallback -= PostRender;

                _renderWindow = window;
                window.RenderViewportsCallback += Render;
                window.PostRenderViewportsCallback += PostRender;
            }

            public static void InitRenderEmulated(XRWindow window)
            {
                if (IsOpenXRActive)
                    return;

                _openVrRuntimeActiveForRender = false;
                _emulatedRenderActive = true;

                InitRenderCallbacks(window);

                if (!TryGetRenderTargetSize(out uint rW, out uint rH))
                    return;

                _lastRenderWidth = rW;
                _lastRenderHeight = rH;

                var left = MakeFBOTexture(rW, rH);
                var right = MakeFBOTexture(rW, rH);

                if (Stereo)
                {
                    if (StereoViewport is not null)
                        return;
                    InitSinglePass(window, rW, rH, left, right);
                }
                else
                {
                    if (LeftEyeViewport is not null && RightEyeViewport is not null)
                        return;
                    InitTwoPass(window, rW, rH, left, right);
                }
            }

            private static bool TryGetRenderTargetSize(out uint rW, out uint rH)
            {
                rW = 0u;
                rH = 0u;

                if (_openVrRuntimeActiveForRender && IsOpenVRActive)
                {
                    try
                    {
                        OpenVRApi.CVR.GetRecommendedRenderTargetSize(ref rW, ref rH);
                    }
                    catch
                    {
                        rW = 0u;
                        rH = 0u;
                    }
                }

                if (rW == 0u || rH == 0u)
                {
                    var window = Renderer?.XRWindow;
                    var fb = window?.EffectiveFramebufferSize;
                    if (fb.HasValue && fb.Value.X > 0 && fb.Value.Y > 0)
                    {
                        rW = (uint)fb.Value.X;
                        rH = (uint)fb.Value.Y;
                        return true;
                    }

                    var size = window?.WindowSizeSnapshot;
                    if (size.HasValue && size.Value.X > 0 && size.Value.Y > 0)
                    {
                        rW = (uint)size.Value.X;
                        rH = (uint)size.Value.Y;
                        return true;
                    }
                }

                return rW > 0u && rH > 0u;
            }

            private static void InitRender(XRWindow window)
            {
                if (!IsOpenVRActive)
                    return;

                _openVrRuntimeActiveForRender = true;
                _emulatedRenderActive = false;

                InitRenderCallbacks(window);

                uint rW = 0u, rH = 0u;
                OpenVRApi.CVR.GetRecommendedRenderTargetSize(ref rW, ref rH);
                _lastRenderWidth = rW;
                _lastRenderHeight = rH;

                SetNormalUpdate();

                var left = MakeFBOTexture(rW, rH);
                var right = MakeFBOTexture(rW, rH);

                if (Stereo)
                    InitSinglePass(window, rW, rH, left, right);
                else
                    InitTwoPass(window, rW, rH, left, right);
            }

            private static void InitTwoPass(XRWindow window, uint rW, uint rH, XRTexture2D left, XRTexture2D right)
            {
                RemakeTwoPass(window, rW, rH, left, right);

                if (ViewInformation.LeftEyeCamera is not null)
                    LeftEyeViewport!.Camera = ViewInformation.LeftEyeCamera;

                if (ViewInformation.RightEyeCamera is not null)
                    RightEyeViewport!.Camera = ViewInformation.RightEyeCamera;

                if (ViewInformation.World is not null)
                {
                    LeftEyeViewport!.WorldInstanceOverride = ViewInformation.World;
                    RightEyeViewport!.WorldInstanceOverride = ViewInformation.World;
                }

                var pipeline = (RenderPipeline)RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
                _twoPassLeftPipeline = new XRRenderPipelineInstance(pipeline);
                _twoPassRightPipeline = new XRRenderPipelineInstance(RuntimeEngine.Rendering.NewRenderPipeline(stereo: false));
                _sharedMeshRenderCommands = new RenderCommandCollection();
                _sharedMeshRenderCommands.SetRenderPasses(pipeline.PassIndicesAndSorters, pipeline.PassMetadata);

                ConfigureDesktopViewportForVrWindow(window);

                RecalculateStereoCullingFrustum();
            }

            private static void ConfigureDesktopViewportForVrWindow(XRWindow window)
            {
                var desktopViewport = window.Viewports.FirstOrDefault();
                if (desktopViewport is null)
                    return;

                bool shareStereoCommands = RuntimeRenderingHostServices.Presentation.VrMirrorComposeFromEyeTextures;
                if (_sharedMeshRenderCommands is not null)
                {
                    // The VR stereo collection is collected and swapped on its own timer path.
                    // Let it publish command snapshots instead of depending on a desktop view
                    // that may cull/swap at a different point in the frame.
                    _sharedMeshRenderCommands.IsRenderCommandSnapshotAuthority = true;
                }

                if (shareStereoCommands)
                {
                    // Eye-texture mirror mode does not run an independent desktop scene view.
                    desktopViewport.AutomaticallyCollectVisible = false;
                    desktopViewport.AutomaticallySwapBuffers = false;
                    desktopViewport.MeshRenderCommandsOverride = _sharedMeshRenderCommands;
                    return;
                }

                // Runtime desktop/cyclopean camera mode renders a real third view, so it must not
                // consume the stereo eye command buffer. Sharing that buffer can make deferred
                // meshes appear/disappear as the eye-visible set is swapped for a different camera.
                desktopViewport.MeshRenderCommandsOverride = null;
                desktopViewport.AutomaticallyCollectVisible = true;
                desktopViewport.AutomaticallySwapBuffers = true;
            }

            private static void RemakeTwoPass(XRWindow window, uint rW, uint rH, XRTexture2D left, XRTexture2D right)
            {
                left.FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0;
                right.FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0;

                VRLeftEyeRenderTarget?.Destroy();
                VRRightEyeRenderTarget?.Destroy();
                VRLeftEyeViewTexture?.Destroy();
                VRRightEyeViewTexture?.Destroy();

                VRLeftEyeRenderTarget = MakeTwoPassFBO(rW, rH, VRLeftEyeViewTexture = left, LeftEyeViewport = new XRViewport(window)
                {
                    Index = 0,
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false
                });
                VRRightEyeRenderTarget = MakeTwoPassFBO(rW, rH, VRRightEyeViewTexture = right, RightEyeViewport = new XRViewport(window)
                {
                    Index = 1,
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false
                });
                SyncRuntimeVrState();
            }

            private static void InitSinglePass(XRWindow window, uint rW, uint rH, XRTexture2D left, XRTexture2D right)
            {
                SetViewportParameters(rW, rH, StereoViewport = new XRViewport(window));
                StereoViewport.RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(stereo: true);
                StereoViewport.AutomaticallyCollectVisible = false;
                StereoViewport.AutomaticallySwapBuffers = false;

                RecalculateStereoCullingFrustum();

                var outputTextures = new XRTexture2DArray(left, right)
                {
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.Rgb8,
                    OVRMultiViewParameters = new(0, 2u),
                };
                VRStereoRenderTarget = new XRFrameBuffer((outputTextures, EFrameBufferAttachment.ColorAttachment0, 0, -1));
                StereoLeftViewTexture = new XRTexture2DArrayView(outputTextures, 0u, 1u, 0u, 1u, ESizedInternalFormat.Rgb8, false, false);
                StereoRightViewTexture = new XRTexture2DArrayView(outputTextures, 0u, 1u, 1u, 1u, ESizedInternalFormat.Rgb8, false, false);

                ConfigureDesktopViewportForVrWindow(window);
            }

            private static Matrix4x4 _combinedProjectionMatrix
            {
                get => RuntimeEngine.VRState.CombinedProjectionMatrix;
                set => RuntimeEngine.VRState.CombinedProjectionMatrix = value;
            }
            public static Matrix4x4 CombinedProjectionMatrix => RuntimeEngine.VRState.CombinedProjectionMatrix;

            public static void RecalculateStereoCullingFrustum()
            {
                //var cvr = Api.CVR;
                //var leftEyeView = cvr.GetEyeToHeadTransform(EVREye.Eye_Left).ToNumerics().Transposed().Inverted();
                //var leftProj = cvr.GetProjectionMatrix(EVREye.Eye_Left, 0.1f, 100000.0f).ToNumerics().Transposed();
                //var rightEyeView = cvr.GetEyeToHeadTransform(EVREye.Eye_Right).ToNumerics().Transposed().Inverted();
                //var rightProj = cvr.GetProjectionMatrix(EVREye.Eye_Right, 0.1f, 100000.0f).ToNumerics().Transposed();

                var leftCam = ViewInformation.LeftEyeCamera;
                var rightCam = ViewInformation.RightEyeCamera;
                if (leftCam is null || rightCam is null)
                    return;

                try
                {
                    var leftEyeView = leftCam.Transform.InverseLocalMatrix;
                    var leftProj = leftCam.ProjectionMatrix;
                    var rightEyeView = rightCam.Transform.InverseLocalMatrix;
                    var rightProj = rightCam.ProjectionMatrix;

                    _stereoCullingFrustum = new Frustum((_combinedProjectionMatrix = ProjectionMatrixCombiner.CombineProjectionMatrices(leftProj, rightProj, leftEyeView, rightEyeView)).Inverted());
                }
                catch (Exception ex)
                {
                    _stereoCullingFrustum = null;
                    _combinedProjectionMatrix = Matrix4x4.Identity;
                    Debug.LogException(ex, "Failed to recalculate stereo culling frustum.");
                }
            }

            private static void CollectVisibleTwoPass()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.Visibility.TwoPass", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    OpenXRApi?.EngineCollectVisibleTick();
                    return;
                }

                if (_sharedMeshRenderCommands is null)
                    return;

                //GetStereoCullingFrustum();

                var scene = ViewInformation.World?.VisualScene;
                var node = ViewInformation.HMDNode;
                var frustum = _stereoCullingFrustum;
                if (scene is null || node is null || frustum is null)
                    return;

                ViewInformation.World?.VisualScene?.CollectRenderedItems(
                    _sharedMeshRenderCommands,
                    ViewInformation.LeftEyeCamera,
                    true,
                    null,
                    frustum.Value.TransformedBy(node.Transform.RenderMatrix),
                    true);

                //LeftEyeViewport?.CollectVisible();
                //RightEyeViewport?.CollectVisible();
            }
            private static void CollectVisibleStereo()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.Visibility.Stereo", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    OpenXRApi?.EngineCollectVisibleTick();
                    return;
                }

                var scene = ViewInformation.World?.VisualScene;
                var node = ViewInformation.HMDNode;
                var frustum = _stereoCullingFrustum;
                if (scene is null || node is null || frustum is null)
                    return;

                scene.CollectRenderedItems(
                    StereoViewport!.RenderPipelineInstance.MeshRenderCommands,
                    frustum.Value.TransformedBy(node.Transform.RenderMatrix),
                    ViewInformation.LeftEyeCamera,
                    true);
            }

            private static void SwapBuffersTwoPass()
            {
                using var sample = Engine.Profiler.Start("VRState.SwapBuffersTwoPass");
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.SwapBuffers.TwoPass", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    OpenXRApi?.EngineSwapBuffersTick();
                    return;
                }

                _sharedMeshRenderCommands?.SwapBuffers();
                //LeftEyeViewport?.SwapBuffers();
                //RightEyeViewport?.SwapBuffers();
            }
            private static void SwapBuffersStereo()
            {
                using var sample = Engine.Profiler.Start("VRState.SwapBuffersStereo");
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.SwapBuffers.Stereo", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    OpenXRApi?.EngineSwapBuffersTick();
                    return;
                }

                StereoViewport?.SwapBuffers();
            }

            private static void Render()
            {
                using var sample = Engine.Profiler.Start("VRState.Render");
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.Render", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    var beforeVrRender = RuntimeEngine.Rendering.Stats.Frame.CurrentCounters;
                    long vrRenderStartTicks = Stopwatch.GetTimestamp();
                    OpenXRApi?.EngineRenderTick();
                    RecordVrRenderPass(beforeVrRender, vrRenderStartTicks);
                    return;
                }

                if (!_openVrRuntimeActiveForRender && !_emulatedRenderActive)
                    return;

                if (!TryGetRenderTargetSize(out uint rW, out uint rH))
                    return;

                if (rW != _lastRenderWidth || rH != _lastRenderHeight)
                {
                    _lastRenderWidth = rW;
                    _lastRenderHeight = rH;
                    if (Stereo)
                    {
                        StereoViewport?.Resize(rW, rH);
                        VRStereoRenderTarget?.Resize(rW, rH);
                        //StereoLeftViewTexture?.Resize(rW, rH);
                        //StereoRightViewTexture?.Resize(rW, rH);
                    }
                    else
                    {
                        var left = MakeFBOTexture(rW, rH);
                        var right = MakeFBOTexture(rW, rH);
                        RemakeTwoPass(Renderer!.XRWindow, rW, rH, left, right);
                        _twoPassLeftPipeline?.DestroyCache();
                        _twoPassRightPipeline?.DestroyCache();
                    }
                }

                //Begin drawing to the headset (OpenVR runtime only)
                if (_openVrRuntimeActiveForRender && IsOpenVRActive)
                    _ = OpenVRApi.UpdateDraw(Origin);

                //Update VR-related transforms
                RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Recalc);

                if (_openVrRuntimeActiveForRender && IsOpenVRActive)
                    IsPowerSaving = OpenVRApi.CVR.ShouldApplicationReduceRenderingWork();

                var beforeVrPass = RuntimeEngine.Rendering.Stats.Frame.CurrentCounters;
                long vrPassStartTicks = Stopwatch.GetTimestamp();
                if (Stereo)
                    RenderSinglePass();
                else
                    RenderTwoPass();
                RecordVrRenderPass(beforeVrPass, vrPassStartTicks);

                if (_openVrRuntimeActiveForRender && IsOpenVRActive && RuntimeEngine.Rendering.Settings.LogVRFrameTimes)
                    ReadStats();
            }

            private static void RecordVrRenderPass(RuntimeEngine.Rendering.Stats.RenderPassCounters before, long startTicks)
            {
                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                RuntimeEngine.Rendering.Stats.Vr.RecordVrRenderPass(
                    before,
                    RuntimeEngine.Rendering.Stats.Frame.CurrentCounters,
                    TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency));
            }

            private static void PostRender()
            {
                if (IsOpenXRActive)
                    OpenXRApi?.EnginePostRenderTick();
            }

            private static void RenderTwoPass()
            {
                if (_twoPassLeftPipeline is null || _twoPassRightPipeline is null || _sharedMeshRenderCommands is null)
                    return;

                var lcam = ViewInformation.LeftEyeCamera;
                var rcam = ViewInformation.RightEyeCamera;
                if (lcam is null || rcam is null)
                    return;

                var scene = ViewInformation.World?.VisualScene;
                if (scene is null)
                    return;

                //Render the scene to left and right eyes separately, each with its own FBOs but sharing the same culled mesh commands
                _twoPassLeftPipeline.Render(scene, lcam, null, LeftEyeViewport, VRLeftEyeRenderTarget, meshRenderCommandsOverride: _sharedMeshRenderCommands);
                _twoPassRightPipeline.Render(scene, rcam, null, RightEyeViewport, VRRightEyeRenderTarget, meshRenderCommandsOverride: _sharedMeshRenderCommands);

                //LeftEyeViewport?.Render(VRLeftEyeRenderTarget);
                //RightEyeViewport?.Render(VRRightEyeRenderTarget);

                if (_openVrRuntimeActiveForRender)
                {
                    //Submit the rendered frames to the headset
                    nint? leftHandle = VRLeftEyeViewTexture?.APIWrappers?.FirstOrDefault()?.GetHandle();
                    nint? rightHandle = VRRightEyeViewTexture?.APIWrappers?.FirstOrDefault()?.GetHandle();
                    if (leftHandle is not null && rightHandle is not null)
                        SubmitRenders(leftHandle.Value, rightHandle.Value);
                }
            }

            private static void RenderSinglePass()
            {
                var world = ViewInformation.World;
                var left = ViewInformation.LeftEyeCamera;
                var right = ViewInformation.RightEyeCamera;
                if (world is null || left is null || right is null)
                    return;

                //Render the scene to left and right eyes stereoscopically
                StereoViewport?.RenderStereo(VRStereoRenderTarget, left, right, world);

                if (_openVrRuntimeActiveForRender)
                {
                    //Submit the rendered frames to the headset
                    //if (StereoUseTextureViews)
                    //{
                    nint? leftHandle = StereoLeftViewTexture?.APIWrappers?.FirstOrDefault()?.GetHandle();
                    nint? rightHandle = StereoRightViewTexture?.APIWrappers?.FirstOrDefault()?.GetHandle();
                    if (leftHandle is not null && rightHandle is not null)
                        SubmitRenders(leftHandle.Value, rightHandle.Value);
                }

                //else
                //{
                //    nint? arrayHandle = VRStereoViewTextureArray?.APIWrappers?.FirstOrDefault()?.GetHandle();
                //    if (arrayHandle is not null)
                //        SubmitRender(arrayHandle.Value);
                //}
            }

            public static bool IsPowerSaving
            {
                get => RuntimeEngine.VRState.IsPowerSaving;
                set
                {
                    if (RuntimeEngine.VRState.IsPowerSaving == value)
                        return;
                    RuntimeEngine.VRState.IsPowerSaving = value;
                    if (!_openVrRuntimeActiveForRender || !IsOpenVRActive)
                        return;

                    if (value)
                        SetPowerSavingUpdate();
                    else
                        SetNormalUpdate();
                }
            }

            private static void SetNormalUpdate()
            {
                if (!_openVrRuntimeActiveForRender)
                    return;

                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                float hz = OpenVRApi.CVR.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
                if (error != ETrackedPropertyError.TrackedProp_Success || hz <= 0.0f)
                    return;
                
                //Time.Timer.TargetRenderFrequency = hz;
            }
            private static void SetPowerSavingUpdate()
            {
                if (!_openVrRuntimeActiveForRender)
                    return;

                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                float hz = OpenVRApi.CVR.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
                if (error != ETrackedPropertyError.TrackedProp_Success || hz <= 0.0f)
                    return;
                
                //Time.Timer.TargetRenderFrequency = hz / 2;
            }

            private static XRMaterialFrameBuffer MakeTwoPassFBO(uint rW, uint rH, XRTexture2D tex, XRViewport vp)
            {
                var rt = new XRMaterialFrameBuffer(new XRMaterial([tex], ShaderHelper.UnlitTextureFragForward()!));
                tex.Resizable = false;
                tex.SizedInternalFormat = ESizedInternalFormat.Rgb8;
                SetViewportParameters(rW, rH, vp);
                return rt;
            }

            private static void SetViewportParameters(uint rW, uint rH, XRViewport vp)
            {
                vp.AllowUIRender = false;
                vp.SetFullScreen();
                vp.SetInternalResolution((int)rW, (int)rH, false);
                vp.Resize(rW, rH, false);
            }

            private static XRTexture2D MakeFBOTexture(uint rW, uint rH)
                => XRTexture2D.CreateFrameBufferTexture(
                    rW, rH,
                    EPixelInternalFormat.Rgb,
                    EPixelFormat.Bgr,
                    EPixelType.UnsignedByte);

            /// <summary>
            /// This method initializes the VR system in client mode.
            /// All VR input will be send to and handled by the server process and rendered frames will be sent to this process.
            /// </summary>
            /// <returns></returns>
            public static async Task<bool> IninitializeClient(
                IActionManifest actionManifest,
                VrManifest vrManifest)
                => await InitSteamVR(actionManifest, vrManifest);

            /// <summary>
            /// This method initializes the VR system in server mode.
            /// VR input is sent to this process and rendered frames are sent to the client process to submit to OpenVR.
            /// </summary>
            /// <returns></returns>
            public static bool InitializeServer()
            {
                return false;
            }

            private static void InstallApp(VrManifest vrManifest)
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), ".vrmanifest");
                string manifestJson = JsonSerializer.Serialize(
                    new VrManifestInstallDocument
                    {
                        Applications = [vrManifest]
                    },
                    XREnginePrettyJsonContext.Default.VrManifestInstallDocument);
                File.WriteAllText(path, manifestJson);

                //Valve.VR.OpenVR.Applications.RemoveApplicationManifest( path );
                var error = Valve.VR.OpenVR.Applications?.AddApplicationManifest(path, false);
                if (error != EVRApplicationError.None)
                    Debug.LogWarning($"Error installing app manifest: {error}");
            }

            //public static float PosePredictionSec { get; set; } = 0f / 1000.0f;

            private static void Update()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.OpenVR.InputUpdate", AllocationScopeCategory.VrInput)
                    : default;

                if (OpenVRApi.Headset is null)
                    OpenVRApi.UpdateInput(0);
                else
                {
                    uint deviceIndex = OpenVRApi.Headset!.DeviceIndex;
                    ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;

                    float secondsSinceLastVsync = 0.0f;
                    ulong frameCount = 0uL;
                    OpenVRApi.CVR.GetTimeSinceLastVsync(ref secondsSinceLastVsync, ref frameCount);

                    float displayFrequency = OpenVRApi.CVR.GetFloatTrackedDeviceProperty(
                        deviceIndex,
                        ETrackedDeviceProperty.Prop_DisplayFrequency_Float,
                        ref error);

                    float motionToPhoton = OpenVRApi.CVR.GetFloatTrackedDeviceProperty(
                        deviceIndex,
                        ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float,
                        ref error);

                    float frameDuration = 1.0f / displayFrequency;
                    float fSecondsFromNow = frameDuration - secondsSinceLastVsync + motionToPhoton;

                    OpenVRApi.UpdateInput(fSecondsFromNow);
                }
                OpenVRApi.Update();
            }

            /// <summary>
            /// VR-related transforms must subscribe to this event to recalculate their matrices directly before drawing.
            /// </summary>
            public static event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw
            {
                add => RuntimeEngine.VRState.RecalcMatrixOnDraw += value;
                remove => RuntimeEngine.VRState.RecalcMatrixOnDraw -= value;
            }

            public static uint LastFrameSampleIndex
            {
                get => RuntimeEngine.VRState.LastFrameSampleIndex;
                private set => RuntimeEngine.VRState.LastFrameSampleIndex = value;
            }

            public static XRViewport? LeftEyeViewport
            {
                get => RuntimeEngine.VRState.LeftEyeViewport;
                private set => RuntimeEngine.VRState.LeftEyeViewport = value;
            }
            public static XRViewport? RightEyeViewport
            {
                get => RuntimeEngine.VRState.RightEyeViewport;
                private set => RuntimeEngine.VRState.RightEyeViewport = value;
            }

            private static void OnDeviceDetected(VrDevice device)
            {
                Debug.Out($"Device detected: {device}");
            }

            //private static VRTextureBounds_t _leftEyeTexBounds = new()
            //{
            //    uMin = 0.0f,
            //    uMax = 0.5f,
            //    vMin = 0.0f,
            //    vMax = 1.0f,
            //};

            //private static VRTextureBounds_t _rightEyeTexBounds = new()
            //{
            //    uMin = 0.5f,
            //    uMax = 1.0f,
            //    vMin = 0.0f,
            //    vMax = 1.0f,
            //};

            public static void SubmitRenders(
                IntPtr leftEyeHandle,
                IntPtr rightEyeHandle,
                ETextureType apiType = ETextureType.OpenGL,
                EColorSpace colorSpace = EColorSpace.Auto,
                EVRSubmitFlags flags = EVRSubmitFlags.Submit_Default)
            {
                if (!IsOpenVRActive)
                    return;

                Texture_t eyeTexture = RuntimeEngine.VRState.EyeTexture;
                VRTextureBounds_t textureBounds = RuntimeEngine.VRState.SingleTextureBounds;
                eyeTexture.eColorSpace = colorSpace;
                eyeTexture.eType = apiType;

                var comp = Valve.VR.OpenVR.Compositor;

                eyeTexture.handle = leftEyeHandle;
                bool leftSubmitFailed = CheckError(comp.Submit(EVREye.Eye_Left, ref eyeTexture, ref textureBounds, flags));

                eyeTexture.handle = rightEyeHandle;
                bool rightSubmitFailed = CheckError(comp.Submit(EVREye.Eye_Right, ref eyeTexture, ref textureBounds, flags));
                RuntimeEngine.VRState.EyeTexture = eyeTexture;
                RuntimeEngine.VRState.SingleTextureBounds = textureBounds;

                comp.PostPresentHandoff();
                if (!leftSubmitFailed && !rightSubmitFailed)
                    RuntimeEngine.Rendering.Stats.Vr.RecordVrRenderFramePresented();
            }

            //public static void SubmitRender(
            //    IntPtr eyesHandle,
            //    ETextureType apiType = ETextureType.OpenGL,
            //    EColorSpace colorSpace = EColorSpace.Auto,
            //    EVRSubmitFlags flags = EVRSubmitFlags.Submit_GlArrayTexture)
            //{
            //    _eyeTex.eColorSpace = colorSpace;
            //    _eyeTex.handle = eyesHandle;
            //    _eyeTex.eType = apiType;

            //    var comp = Valve.VR.OpenVR.Compositor;
            //    CheckError(comp.Submit(EVREye.Eye_Left, ref _eyeTex, ref _singleTexBounds, flags));
            //    CheckError(comp.Submit(EVREye.Eye_Right, ref _eyeTex, ref _singleTexBounds, flags));

            //    comp.PostPresentHandoff();
            //}

            //enum EVRSubmitFlags
            //{
            //    // Simple render path. App submits rendered left and right eye images with no lens distortion correction applied.
            //    Submit_Default = 0x00,

            //    // App submits final left and right eye images with lens distortion already applied (lens distortion makes the images appear
            //    // barrel distorted with chromatic aberration correction applied). The app would have used the data returned by
            //    // vr::IVRSystem::ComputeDistortion() to apply the correct distortion to the rendered images before calling Submit().
            //    Submit_LensDistortionAlreadyApplied = 0x01,

            //    // If the texture pointer passed in is actually a renderbuffer (e.g. for MSAA in OpenGL) then set this flag.
            //    Submit_GlRenderBuffer = 0x02,

            //    // Do not use
            //    Submit_Reserved = 0x04,

            //    // Set to indicate that pTexture is a pointer to a VRTextureWithPose_t.
            //    // This flag can be combined with Submit_TextureWithDepth to pass a VRTextureWithPoseAndDepth_t.
            //    Submit_TextureWithPose = 0x08,

            //    // Set to indicate that pTexture is a pointer to a VRTextureWithDepth_t.
            //    // This flag can be combined with Submit_TextureWithPose to pass a VRTextureWithPoseAndDepth_t.
            //    Submit_TextureWithDepth = 0x10,

            //    // Set to indicate a discontinuity between this and the last frame.
            //    // This will prevent motion smoothing from attempting to extrapolate using the pair.
            //    Submit_FrameDiscontinuty = 0x20,

            //    // Set to indicate that pTexture->handle is a contains VRVulkanTextureArrayData_t
            //    Submit_VulkanTextureWithArrayData = 0x40,

            //    // If the texture pointer passed in is an OpenGL Array texture, set this flag
            //    Submit_GlArrayTexture = 0x80,

            //    // If the texture is an EGL texture and not an glX/wGL texture (Linux only, currently)
            //    Submit_IsEgl = 0x100,

            //    // Do not use
            //    Submit_Reserved2 = 0x08000,
            //    Submit_Reserved3 = 0x10000,
            //};

            public static bool CheckError(EVRCompositorError error)
            {
                bool hasError = error != EVRCompositorError.None;
                if (hasError)
                    Debug.LogWarning($"OpenVR compositor error: {error}");
                return hasError;
            }

            public static NamedPipeServerStream? PipeServer { get; private set; }
            public static NamedPipeClientStream? PipeClient { get; private set; }

            private static (XRCamera? left, XRCamera? right, XRWorldInstance? world, SceneNode? HMDNode) _viewInformation
            {
                get
                {
                    var value = RuntimeEngine.VRState.ViewInformation;
                    return (value.LeftEyeCamera, value.RightEyeCamera, value.World as XRWorldInstance, value.HMDNode);
                }
                set => RuntimeEngine.VRState.ViewInformation = value;
            }
            /// <summary>
            /// The world instance to render in the VR headset, and the cameras for the left and right eyes.
            /// </summary>
            public static (XRCamera? LeftEyeCamera, XRCamera? RightEyeCamera, XRWorldInstance? World, SceneNode? HMDNode) ViewInformation
            {
                get => _viewInformation;
                set
                {
                    _viewInformation.left?.Transform.LocalMatrixChanged -= EyeLocalMatrixChanged;
                    _viewInformation.right?.Transform.LocalMatrixChanged -= EyeLocalMatrixChanged;
                    
                    _viewInformation = value;

                    var leftEye = LeftEyeViewport;
                    if (leftEye is not null)
                    {
                        leftEye.Camera = _viewInformation.left;
                        leftEye.WorldInstanceOverride = _viewInformation.world;
                        _viewInformation.left?.Transform.LocalMatrixChanged += EyeLocalMatrixChanged;
                    }

                    var rightEye = RightEyeViewport;
                    if (rightEye is not null)
                    {
                        rightEye.Camera = _viewInformation.right;
                        rightEye.WorldInstanceOverride = _viewInformation.world;
                        _viewInformation.right?.Transform.LocalMatrixChanged += EyeLocalMatrixChanged;
                    }

                    // ViewInformation can be set before VR rendering has been initialized (e.g., during component activation).
                    // Only compute the stereo culling frustum once the VR viewports exist; otherwise we can end up querying
                    // VR projection parameters too early.
                    if (LeftEyeViewport is not null || RightEyeViewport is not null || StereoViewport is not null)
                        RecalculateStereoCullingFrustum();
                    else
                    {
                        _stereoCullingFrustum = null;
                        _combinedProjectionMatrix = Matrix4x4.Identity;
                    }

                    SyncRuntimeVrState();
                }
            }

            private static void EyeLocalMatrixChanged(TransformBase @base, Matrix4x4 localMatrix)
            {
                if (LeftEyeViewport is not null || RightEyeViewport is not null || StereoViewport is not null)
                    RecalculateStereoCullingFrustum();
            }

            private static void ReadStats()
            {
                if (!IsOpenVRActive)
                    return;

                uint size = (uint)Marshal.SizeOf<Compositor_FrameTiming>();
                Compositor_FrameTiming currentFrame = new();
                Compositor_FrameTiming previousFrame = new();
                currentFrame.m_nSize = size;
                previousFrame.m_nSize = size;
                Valve.VR.OpenVR.Compositor.GetFrameTiming(ref currentFrame, 0);
                Valve.VR.OpenVR.Compositor.GetFrameTiming(ref previousFrame, 1);

                uint currentFrameIndex = currentFrame.m_nFrameIndex;
                uint amountOfFramesSinceLast = currentFrameIndex - LastFrameSampleIndex;

                double gpuFrametimeMs = 0;
                double cpuFrametimeMs = 0;
                double totalFrametimeMs = 0;

                for (uint i = 0; i < amountOfFramesSinceLast; i++)
                {
                    Valve.VR.OpenVR.Compositor.GetFrameTiming(ref currentFrame, i);
                    Valve.VR.OpenVR.Compositor.GetFrameTiming(ref previousFrame, i + 1);

                    gpuFrametimeMs += currentFrame.m_flTotalRenderGpuMs;
                    cpuFrametimeMs += currentFrame.m_flNewFrameReadyMs - currentFrame.m_flNewPosesReadyMs + currentFrame.m_flCompositorRenderCpuMs;
                    totalFrametimeMs += (currentFrame.m_flSystemTimeInSeconds - previousFrame.m_flSystemTimeInSeconds) * 1000f;
                }

                gpuFrametimeMs /= amountOfFramesSinceLast;
                cpuFrametimeMs /= amountOfFramesSinceLast;
                totalFrametimeMs /= amountOfFramesSinceLast;

                LastFrameSampleIndex = currentFrameIndex;

                GpuFrametime = (float)gpuFrametimeMs;
                CpuFrametime = (float)cpuFrametimeMs;
                TotalFrametime = (float)totalFrametimeMs;
                Framerate = (int)(1.0f / totalFrametimeMs * 1000.0f);

                Debug.Out($"VR: {Framerate}fps / GPU: {MathF.Round(GpuFrametime, 2, MidpointRounding.AwayFromZero)}ms / CPU: {MathF.Round(CpuFrametime, 2, MidpointRounding.AwayFromZero)}ms");
            }

            public static float GpuFrametime
            {
                get => RuntimeEngine.VRState.GpuFrametime;
                private set => RuntimeEngine.VRState.GpuFrametime = value;
            }
            public static float CpuFrametime
            {
                get => RuntimeEngine.VRState.CpuFrametime;
                private set => RuntimeEngine.VRState.CpuFrametime = value;
            }
            public static float TotalFrametime
            {
                get => RuntimeEngine.VRState.TotalFrametime;
                private set => RuntimeEngine.VRState.TotalFrametime = value;
            }
            public static float Framerate
            {
                get => RuntimeEngine.VRState.Framerate;
                private set => RuntimeEngine.VRState.Framerate = value;
            }
            public static float MaxFrametime
            {
                get => RuntimeEngine.VRState.MaxFrametime;
                private set => RuntimeEngine.VRState.MaxFrametime = value;
            }
            public static bool IsInVR
            {
                get => RuntimeEngine.VRState.IsInVR;
                private set => RuntimeEngine.VRState.IsInVR = value;
            }

            #region Separated Client

            public static void StartInputClient()
            {
                PipeClient = new(".", "VRInputPipe", PipeDirection.Out, PipeOptions.Asynchronous);
                PipeClient.Connect();
            }
            private static void ProcessInputData(RuntimeVrState.VRInputData? inputData)
            {
                if (inputData is null)
                    return;

                // Update the latest input data
                _latestInputData = inputData;
            }
            public static void StopInputServer()
            {
                if (PipeServer is null)
                    return;

                if (PipeServer.IsConnected)
                    PipeServer.Disconnect();
                
                PipeServer.Close();
                PipeServer.Dispose();
            }
            
            public static async Task SendInputs()
            {
                if (PipeClient is null)
                    return;

                try
                {
                    CaptureVRInputData();
                    string json = JsonSerializer.Serialize(_data, XREngineRuntimeJsonContext.Default.RuntimeVrInputData);
                    await PipeClient.WriteAsync(Encoding.UTF8.GetBytes(json));
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Error sending input data: {ex.Message}");
                }
            }

            private static RuntimeVrState.VRInputData _data = new();

            private static void CaptureVRInputData()
            {

            }

            private static StreamReader? _reader = null;

            private static async Task InputListenerAsync()
            {
                Debug.Out("Waiting for VR input connection...");
                try
                {
                    PipeServer = new("VRInputPipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await PipeServer!.WaitForConnectionAsync();
                    Debug.Out("VR input connection established.");
                    _reader = new(PipeServer);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Error accepting VR input connection: {ex.Message}");
                }
            }

            private static DateTime _lastInputRead = DateTime.MinValue;

            private static async Task ReadVRInput()
            {
                if (_reader is null)
                    return;

                // Read input data from the pipe asynchronously
                string? jsonData = await _reader.ReadLineAsync();
                if (jsonData is null)
                {
                    if ((DateTime.Now - _lastInputRead).Seconds > 1)
                    {
                        Debug.Out("VR input client disconnected.");
                        _reader.Dispose();
                        _reader = null;
                    }
                    return;
                }
                _lastInputRead = DateTime.Now;
                ProcessInputData(JsonSerializer.Deserialize(jsonData, XREngineRuntimeJsonContext.Default.RuntimeVrInputData));
            }

            private static RuntimeVrState.VRInputData? _latestInputData = null;

            #endregion
    }
}
