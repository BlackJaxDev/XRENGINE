using XREngine.Extensions;
using System.IO.Pipes;
using System.Numerics;
using System.Text;
using System.Text.Json;
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
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Transforms;

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

            private static IOpenXrRuntime? _openXRApi
            {
                get => RuntimeEngine.VRState.OpenXRApi;
                set => RuntimeEngine.VRState.OpenXRApi = value;
            }
            public static IOpenXrRuntime? OpenXRApi => RuntimeEngine.VRState.OpenXRApi;
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

            public static RuntimeVrTrackingOrigin Origin { get; set; } = RuntimeVrTrackingOrigin.Standing;

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
                => RuntimeEngine.VRState.RealWorldIPD;

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

                    _openXRApi ??= OpenXrRuntimeServices.Create();
                    _openXRApi.Window = window;
                    ((IOpenXrApplicationLifecycle)_openXRApi).EnableRuntimeMonitoring();
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

            /// <summary>Stops probing and retires the active OpenXR session on its owning thread.</summary>
            public static bool StopOpenXR()
            {
                if (_openXRApi is not IOpenXrApplicationLifecycle lifecycle)
                    return false;

                lifecycle.DisableRuntimeMonitoring();
                return true;
            }

            private static void UpdateOpenXRRuntime()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.OpenXR.RuntimeUpdate", AllocationScopeCategory.VrInput)
                    : default;

                if (!_openXrRuntimeMonitoring || _openXRApi is null)
                    return;

                ((IOpenXrApplicationLifecycle)_openXRApi).UpdateRuntimeState();
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

            private static async Task<bool> InitSteamVR(IRuntimeOpenVrActionManifest actionManifest, RuntimeOpenVrApplicationManifest vrManifest)
                => await Task.Run(() =>
                {
                    if (!OpenVrRuntimeBackend.TryStartScene(actionManifest, vrManifest, out string? failure))
                    {
                        Debug.LogWarning(failure ?? "Failed to initialize SteamVR.");
                        return false;
                    }
                    _activeRuntime = VRRuntime.OpenVR;
                    Engine.Time.Timer.PreUpdateFrame += Update;
                    IsInVR = true;
                    SyncRuntimeVrState();
                    return true;
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
                IRuntimeOpenVrActionManifest actionManifest,
                RuntimeOpenVrApplicationManifest vrManifest,
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
                        OpenVrRuntimeBackend.TryGetRecommendedRenderTargetSize(out rW, out rH);
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
                OpenVrRuntimeBackend.TryGetRecommendedRenderTargetSize(out rW, out rH);
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

                ConfigureDesktopViewportForVrWindow(window);

                RecalculateStereoCullingFrustum();
            }

            /// <summary>
            /// Creates one two-pass eye viewport that owns its pipeline instance and command
            /// collection. The eyes collect, swap and render through the viewport boundary so
            /// the selected scene pipeline family publishes its canonical frame package for the
            /// eye view exactly as desktop and OpenXR eye viewports do: a canonical package is
            /// only projected for a collection owned by a pipeline instance, so an unowned
            /// shared collection leaves package-driven (Advanced) eyes without scene draws.
            /// Camera synchronization stays off so the eye camera's own pipeline cannot
            /// replace the selected family.
            /// </summary>
            private static XRViewport CreateTwoPassEyeViewport(XRWindow window, int index, RenderPipeline pipeline)
            {
                XRViewport viewport = new(window)
                {
                    Index = index,
                    SetRenderPipelineFromCamera = false,
                    PipelineRequest = RenderPipelineRequest.DesktopScene(stereo: false),
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false,
                };
                viewport.RenderPipeline = pipeline;
                return viewport;
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

                // A resize replaces the eye viewports; tear the previous ones down so their
                // pipeline instances, caches and backend generations do not linger.
                LeftEyeViewport?.Destroy();
                RightEyeViewport?.Destroy();

                // Keep an explicitly selected bootstrap scene family consistent across
                // desktop and two-pass eyes. Automatic selection can otherwise admit
                // Advanced for the eyes while the controlled desktop camera uses Default.
                XRViewport leftViewport = CreateTwoPassEyeViewport(
                    window, 0, BootstrapRenderSettings.CreateSceneRenderPipeline(stereo: false));
                XRViewport rightViewport = CreateTwoPassEyeViewport(
                    window, 1, BootstrapRenderSettings.CreateSceneRenderPipeline(stereo: false));
                VRLeftEyeRenderTarget = MakeTwoPassFBO(rW, rH, VRLeftEyeViewTexture = left, leftViewport);
                VRRightEyeRenderTarget = MakeTwoPassFBO(rW, rH, VRRightEyeViewTexture = right, rightViewport);
                LeftEyeViewport = leftViewport;
                RightEyeViewport = rightViewport;
                _twoPassLeftPipeline = leftViewport.RenderPipelineInstance;
                _twoPassRightPipeline = rightViewport.RenderPipelineInstance;
                // The left eye's collection also feeds the eye-texture desktop mirror.
                _sharedMeshRenderCommands = leftViewport.RenderPipelineInstance.MeshRenderCommands;
                SyncRuntimeVrState();
            }

            private static void InitSinglePass(XRWindow window, uint rW, uint rH, XRTexture2D left, XRTexture2D right)
            {
                SetViewportParameters(rW, rH, StereoViewport = new XRViewport(window)
                {
                    SetRenderPipelineFromCamera = false,
                    PipelineRequest = RenderPipelineRequest.DesktopScene(stereo: true),
                });
                StereoViewport.RenderPipeline = BootstrapRenderSettings.CreateSceneRenderPipeline(stereo: true);
                StereoViewport.AutomaticallyCollectVisible = false;
                StereoViewport.AutomaticallySwapBuffers = false;

                RecalculateStereoCullingFrustum();

                var outputTextures = new XRTexture2DArray(left, right)
                {
                    Name = "VrStereoOutput",
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.Rgb8,
                    // This is rendered output, not an upload from the empty eye
                    // placeholders. Keep its format identical to the two views.
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    AutoGenerateMipmaps = false,
                    SmallestAllowedMipmapLevel = 0,
                    MinFilter = ETexMinFilter.Linear,
                    MagFilter = ETexMagFilter.Linear,
                    OVRMultiViewParameters = new(0, 2u),
                };
                VRStereoRenderTarget = new XRFrameBuffer((outputTextures, EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    ForceOvrMultiview = StereoViewport.RenderPipeline is IAdvancedRenderStageFamilyHost,
                };
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
                    if (OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle)
                        openXrLifecycle.CollectVisible();
                    return;
                }

                XRViewport? leftViewport = LeftEyeViewport;
                XRViewport? rightViewport = RightEyeViewport;
                if (leftViewport is null || rightViewport is null)
                    return;

                IRuntimeRenderWorld? world = ViewInformation.World;
                SceneNode? node = ViewInformation.HMDNode;
                Frustum? frustum = _stereoCullingFrustum;
                if (world?.VisualScene is null || node is null || frustum is null)
                    return;

                // Each eye collects into its own pipeline-owned collection so the pipeline
                // family publishes a canonical frame package per eye. Both use the combined
                // stereo frustum, so the eyes still share one conservative visible set.
                IVolume collectionVolume = frustum.Value.TransformedBy(node.Transform.RenderMatrix);
                leftViewport.CollectVisible(
                    collectMirrors: true,
                    worldOverride: world,
                    cameraOverride: ViewInformation.LeftEyeCamera,
                    allowScreenSpaceUICollectVisible: false,
                    collectionVolumeOverride: collectionVolume);
                rightViewport.CollectVisible(
                    collectMirrors: true,
                    worldOverride: world,
                    cameraOverride: ViewInformation.RightEyeCamera,
                    allowScreenSpaceUICollectVisible: false,
                    collectionVolumeOverride: collectionVolume);
            }
            private static void CollectVisibleStereo()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.Visibility.Stereo", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    if (OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle)
                        openXrLifecycle.CollectVisible();
                    return;
                }

                // The callback stays installed after a dynamic OpenXR stop, but
                // OpenXR does not create the legacy stereo viewport. Its swapchain
                // teardown may take several frames, so tolerate that interval here.
                XRViewport? stereoViewport = StereoViewport;
                if (stereoViewport is null)
                    return;

                var scene = ViewInformation.World?.VisualScene;
                var node = ViewInformation.HMDNode;
                var frustum = _stereoCullingFrustum;
                if (scene is null || node is null || frustum is null)
                    return;

                // Use the viewport collection boundary so this family publishes
                // its canonical scene package as well as visible membership.
                stereoViewport.CollectVisible(
                    worldOverride: ViewInformation.World,
                    cameraOverride: ViewInformation.LeftEyeCamera,
                    allowScreenSpaceUICollectVisible: false,
                    collectionVolumeOverride: frustum.Value.TransformedBy(node.Transform.RenderMatrix));
            }

            private static void SwapBuffersTwoPass()
            {
                using var sample = Engine.Profiler.Start("VRState.SwapBuffersTwoPass");
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.SwapBuffers.TwoPass", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    if (OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle)
                        openXrLifecycle.SwapBuffers();
                    return;
                }

                // Swap through each eye viewport so its prepared canonical frame package is
                // finalized with the command buffers it describes.
                LeftEyeViewport?.SwapBuffers(allowScreenSpaceUISwap: false);
                RightEyeViewport?.SwapBuffers(allowScreenSpaceUISwap: false);
            }
            private static void SwapBuffersStereo()
            {
                using var sample = Engine.Profiler.Start("VRState.SwapBuffersStereo");
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.SwapBuffers.Stereo", AllocationScopeCategory.RenderSubmission)
                    : default;

                if (IsOpenXRActive)
                {
                    if (OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle)
                        openXrLifecycle.SwapBuffers();
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
                    if (OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle)
                        openXrLifecycle.Render();
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
                    }
                }

                //Begin drawing to the headset (OpenVR runtime only)
                if (_openVrRuntimeActiveForRender && IsOpenVRActive)
                    OpenVrRuntimeBackend.UpdateDraw(Origin);

                //Update VR-related transforms
                RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Recalc);

                if (_openVrRuntimeActiveForRender && IsOpenVRActive)
                    IsPowerSaving = OpenVrRuntimeBackend.ShouldReduceRenderingWork();

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
                    if (OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle)
                        openXrLifecycle.PostRender();
            }

            private static void RenderTwoPass()
            {
                XRViewport? leftViewport = LeftEyeViewport;
                XRViewport? rightViewport = RightEyeViewport;
                if (leftViewport is null || rightViewport is null)
                    return;

                var lcam = ViewInformation.LeftEyeCamera;
                var rcam = ViewInformation.RightEyeCamera;
                if (lcam is null || rcam is null)
                    return;

                IRuntimeRenderWorld? world = ViewInformation.World;
                if (world?.VisualScene is null)
                    return;

                // Render each eye through its viewport: its own pipeline instance, collection
                // and eye framebuffer.
                leftViewport.Render(VRLeftEyeRenderTarget, world, lcam);
                rightViewport.Render(VRRightEyeRenderTarget, world, rcam);

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

                if (!OpenVrRuntimeBackend.TryGetDisplayFrequency(out float hz))
                    return;
                
                //Time.Timer.TargetRenderFrequency = hz;
            }
            private static void SetPowerSavingUpdate()
            {
                if (!_openVrRuntimeActiveForRender)
                    return;

                if (!OpenVrRuntimeBackend.TryGetDisplayFrequency(out float hz))
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
            {
                XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
                    rW, rH,
                    EPixelInternalFormat.Rgb8,
                    EPixelFormat.Bgr,
                    EPixelType.UnsignedByte);
                texture.SizedInternalFormat = ESizedInternalFormat.Rgb8;
                texture.SmallestAllowedMipmapLevel = 0;
                return texture;
            }

            /// <summary>
            /// This method initializes the VR system in client mode.
            /// All VR input will be send to and handled by the server process and rendered frames will be sent to this process.
            /// </summary>
            /// <returns></returns>
            public static async Task<bool> IninitializeClient(
                IRuntimeOpenVrActionManifest actionManifest,
                RuntimeOpenVrApplicationManifest vrManifest)
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

            //public static float PosePredictionSec { get; set; } = 0f / 1000.0f;

            private static void Update()
            {
                using var allocationScope = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking
                    ? Engine.Allocations.BeginScope("VR.OpenVR.InputUpdate", AllocationScopeCategory.VrInput)
                    : default;

                OpenVrRuntimeBackend.UpdateInputAndDevices();
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

            public static void SubmitRenders(
                IntPtr leftEyeHandle,
                IntPtr rightEyeHandle,
                RuntimeOpenVrTextureType apiType = RuntimeOpenVrTextureType.OpenGL,
                RuntimeOpenVrColorSpace colorSpace = RuntimeOpenVrColorSpace.Auto,
                RuntimeOpenVrSubmitFlags flags = RuntimeOpenVrSubmitFlags.Default)
            {
                if (!IsOpenVRActive)
                    return;

                if (apiType == RuntimeOpenVrTextureType.OpenGL && Renderer?.BackendId != RendererBackendId.OpenGL)
                {
                    throw new NotSupportedException(
                        "OpenVR OpenGL texture submission requires an active OpenGL renderer; Vulkan image handles cannot be submitted as OpenGL textures.");
                }

                RuntimeOpenVrSubmitResult result = (RuntimeOpenVrCompositorServices.Current
                    ?? throw new InvalidOperationException("The OpenVR compositor service is unavailable."))
                    .SubmitEyes(leftEyeHandle, rightEyeHandle, apiType, colorSpace, flags);
                if (result.LeftError != 0)
                    Debug.LogWarning($"OpenVR left-eye compositor error: {result.LeftError}");
                if (result.RightError != 0)
                    Debug.LogWarning($"OpenVR right-eye compositor error: {result.RightError}");
                if (result.Succeeded)
                    RuntimeEngine.Rendering.Stats.Vr.RecordVrRenderFramePresented();
            }

            public static NamedPipeServerStream? PipeServer { get; private set; }
            public static NamedPipeClientStream? PipeClient { get; private set; }

            private static (XRCamera? left, XRCamera? right, IRuntimeRenderWorld? world, SceneNode? HMDNode) _viewInformation
            {
                get
                {
                    var value = RuntimeEngine.VRState.ViewInformation;
                    return (value.LeftEyeCamera, value.RightEyeCamera, value.World, value.HMDNode);
                }
                set => RuntimeEngine.VRState.ViewInformation = value;
            }
            /// <summary>
            /// The world instance to render in the VR headset, and the cameras for the left and right eyes.
            /// </summary>
            public static (XRCamera? LeftEyeCamera, XRCamera? RightEyeCamera, IRuntimeRenderWorld? World, SceneNode? HMDNode) ViewInformation
            {
                get => _viewInformation;
                set
                {
                    _viewInformation.left?.Transform.LocalMatrixChanged -= EyeLocalMatrixChanged;
                    _viewInformation.right?.Transform.LocalMatrixChanged -= EyeLocalMatrixChanged;
                    
                    _viewInformation = value;

                    // RuntimeVrState applies cameras/world to both viewport
                    // topologies. Track eye changes even when only the shared
                    // stereo viewport exists.
                    _viewInformation.left?.Transform.LocalMatrixChanged += EyeLocalMatrixChanged;
                    _viewInformation.right?.Transform.LocalMatrixChanged += EyeLocalMatrixChanged;

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

                if (!OpenVrRuntimeBackend.TryReadFrameStats(LastFrameSampleIndex, out RuntimeVrFrameStats stats))
                    return;

                LastFrameSampleIndex = stats.LastFrameSampleIndex;
                GpuFrametime = stats.GpuFrameTimeMs;
                CpuFrametime = stats.CpuFrameTimeMs;
                TotalFrametime = stats.TotalFrameTimeMs;
                Framerate = stats.FrameRate;

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
                    string json = JsonSerializer.Serialize(_data, XREngineVrRuntimeJsonContext.Default.RuntimeVrInputData);
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
                ProcessInputData(JsonSerializer.Deserialize(jsonData, XREngineVrRuntimeJsonContext.Default.RuntimeVrInputData));
            }

            private static RuntimeVrState.VRInputData? _latestInputData = null;

            #endregion
    }
}
