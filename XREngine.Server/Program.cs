using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Fbx;
using XREngine.Native;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.GameStartupSettings;
namespace XREngine.Networking
{
    /// <summary>
    /// Dedicated realtime server entry point. Instance discovery, allocation, and asset delivery live outside
    /// this engine process; this executable only accepts direct UDP joins against its loaded world.
    /// </summary>
    public class Program
    {
        private static readonly Guid _serverSessionId = ResolveConfiguredSessionId();
        private static readonly string? _requiredSessionToken = GetOptionalEnvironmentValue("XRE_SESSION_TOKEN");
        private static readonly string _udpMulticastGroup = GetOptionalEnvironmentValue("XRE_UDP_MULTICAST_GROUP") ?? "239.0.0.222";
        private static readonly int _udpMulticastPort = GetOptionalIntEnvironmentValue("XRE_UDP_MULTICAST_PORT")
            ?? 5000;
        private static readonly int _udpBindPort = GetOptionalIntEnvironmentValue("XRE_UDP_BIND_PORT")
            ?? GetOptionalIntEnvironmentValue("XRE_UDP_SERVER_BIND_PORT")
            ?? 5000;
        private static readonly int _udpAdvertisedPort = GetOptionalIntEnvironmentValue("XRE_UDP_ADVERTISED_PORT")
            ?? GetOptionalIntEnvironmentValue("XRE_UDP_SERVER_SEND_PORT")
            ?? _udpBindPort;

        private static void Main(string[] args)
        {
            // Apply engine settings
            Engine.Rendering.Settings.OutputVerbosity = EOutputVerbosity.Verbose;
            Engine.EditorPreferences.Debug.UseDebugOpaquePipeline = false;

            Engine.ServerSessionResolver = ResolveServerSession;
            Engine.ServerJoinAdmissionResolver = ResolveServerJoin;

            //JsonConvert.DefaultSettings = DefaultJsonSettings;

            // Determine world mode from command line or environment variable
            //EWorldMode worldMode = ResolveWorldMode(args);

            // Note: engine startup settings (render API, update rates, etc.) are sourced from UnitTestingWorld.Toggles
            // via GetEngineSettings(). Load the JSON settings for both Default and UnitTesting modes so defaults don't
            // accidentally pick unsupported/undesired values and render a black screen.
            var settings = UnitTestingWorldSettingsStore.Load(false);
            UnitTestingWorldSettingsStore.ApplyWorldKindOverride(settings);
            UnitTestingWorldSettingsStore.ApplyAudioOverrides(settings);
            ConfigureFbxTraceLogging(settings);
            XRWorld targetWorld = BootstrapWorldFactory.CreateServerDefaultWorld();

            Engine.Run(GetEngineSettings(targetWorld), Engine.LoadOrGenerateGameState());
        }

        private static void ConfigureFbxTraceLogging(UnitTestingWorldSettings settings)
        {
            FbxTrace.LogSink = static message => Debug.Assets(message);
            FbxTrace.ProfilerScopeFactory = static scopeName => Engine.Profiler.Start(scopeName);

            if (settings.FbxLogVerbosity == UnitTestFbxLogVerbosity.UseEnvironment)
                FbxTrace.RefreshFromEnvironment();
            else
            {
                FbxTrace.Verbosity = settings.FbxLogVerbosity switch
                {
                    UnitTestFbxLogVerbosity.Off => FbxLogVerbosity.Off,
                    UnitTestFbxLogVerbosity.Errors => FbxLogVerbosity.Errors,
                    UnitTestFbxLogVerbosity.Warnings => FbxLogVerbosity.Warnings,
                    UnitTestFbxLogVerbosity.Info => FbxLogVerbosity.Info,
                    UnitTestFbxLogVerbosity.Verbose => FbxLogVerbosity.Verbose,
                    _ => FbxLogVerbosity.Off,
                };
            }

            Debug.Assets($"FBX trace logging configured: setting={settings.FbxLogVerbosity}, effective={FbxTrace.Verbosity}, category={ELogCategory.Assets}.");
        }

        private static ServerJoinAdmissionResult? ResolveServerJoin(PlayerJoinRequest request)
        {
            AdmissionFailureReason sessionFailure = RealtimeAdmissionValidator.ValidateSession(
                request,
                _serverSessionId,
                _requiredSessionToken,
                out string sessionFailureMessage);
            if (sessionFailure != AdmissionFailureReason.None)
                return new ServerJoinAdmissionResult(null, sessionFailure, sessionFailureMessage);

            ServerSessionContext? session = ResolveServerSession(request);
            return session is null
                ? new ServerJoinAdmissionResult(null, AdmissionFailureReason.SessionNotFound, "No local world instance is ready for realtime joins.")
                : new ServerJoinAdmissionResult(session);
        }

        private static ServerSessionContext? ResolveServerSession(PlayerJoinRequest request)
        {
            XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance();
            if (worldInstance?.TargetWorld is null)
                return null;

            WorldAssetIdentity worldAsset = WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, CurrentProtocolVersion);

            return new ServerSessionContext(_serverSessionId, worldInstance, worldAsset);
        }

        private static XRWorldInstance? ResolvePrimaryWorldInstance()
        {
            foreach (var window in Engine.Windows)
            {
                if (window?.TargetWorldInstance is XRWorldInstance worldInstance)
                    return worldInstance;
            }

            return XRWorldInstance.WorldInstances.Values.FirstOrDefault();
        }

        private static Guid ResolveConfiguredSessionId()
        {
            string? configured = GetOptionalEnvironmentValue("XRE_SESSION_ID");
            return Guid.TryParse(configured, out Guid sessionId)
                ? sessionId
                : Guid.NewGuid();
        }

        private static string? GetOptionalEnvironmentValue(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static int? GetOptionalIntEnvironmentValue(string name)
        {
            string? value = GetOptionalEnvironmentValue(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is > 0 and <= 65535
                ? parsed
                : null;
        }

        private static string CurrentProtocolVersion { get; } = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "dev";

        static XRWorld CreateServerDebugWorld()
        {
            var scene = new XRScene("Server Console Scene");
            var rootNode = new SceneNode("Root Node");
            scene.RootNodes.Add(rootNode);

            // Add simple 3D debug geometry (no assets) so we can visually confirm the
            // camera + viewport + render pipeline are working even if UI is disabled.
            AddDefaultGridFloor(rootNode);

            SceneNode cameraNode = CreateCamera(rootNode, out var camComp);
            var pawn = CreateDesktopViewerPawn(cameraNode, camComp);

            // Create UI canvas as a root scene node (like Editor does) for proper screen-space rendering
            var uiRootNode = new SceneNode("Server UI Root");
            scene.RootNodes.Add(uiRootNode);
            CreateConsoleUI(uiRootNode, camComp!, pawn);

            return new XRWorld("Server World", scene);
        }

        private static void AddDefaultGridFloor(SceneNode rootNode)
        {
            var gridNode = rootNode.NewChild("GridFloor");
            var debug = gridNode.AddComponent<DebugDrawComponent>()!;

            const float extent = 50.0f;
            const float step = 1.0f;
            const int majorEvery = 10;
            const float y = 0.0f;

            for (float x = -extent; x <= extent; x += step)
            {
                int xi = (int)MathF.Round(x);
                bool isAxis = xi == 0;
                bool isMajor = (xi % majorEvery) == 0;
                var color = isAxis ? ColorF4.White : isMajor ? ColorF4.Gray : ColorF4.DarkGray;
                debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
            }

            for (float z = -extent; z <= extent; z += step)
            {
                int zi = (int)MathF.Round(z);
                bool isAxis = zi == 0;
                bool isMajor = (zi % majorEvery) == 0;
                var color = isAxis ? ColorF4.White : isMajor ? ColorF4.Gray : ColorF4.DarkGray;
                debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
            }
        }

        private static SceneNode CreateCamera(SceneNode parentNode, out CameraComponent? camComp, bool smoothed = true)
        {
            var cameraNode = new SceneNode(parentNode, "TestCameraNode");

            if (smoothed)
            {
                var laggedTransform = cameraNode.GetTransformAs<SmoothedTransform>(true)!;
                laggedTransform.RotationSmoothingSpeed = 30.0f;
                laggedTransform.TranslationSmoothingSpeed = 30.0f;
                laggedTransform.ScaleSmoothingSpeed = 30.0f;
            }

            if (cameraNode.TryAddComponent(out camComp, "TestCamera"))
                camComp!.SetPerspective(60.0f, 0.1f, 100000.0f, null);
            else
                camComp = null;

            return cameraNode;
        }

        private static FlyingCameraPawnComponent CreateDesktopViewerPawn(SceneNode cameraNode, CameraComponent? camera)
        {
            var pawnComp = cameraNode.AddComponent<FlyingCameraPawnComponent>();
            var listener = cameraNode.AddComponent<AudioListenerComponent>()!;
            listener.Gain = 1.0f;
            listener.DistanceModel = EDistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;

            pawnComp!.Name = "TestPawn";
            // Ensure GetCamera() resolves to the intended camera component before possession.
            if (camera is not null)
                pawnComp.CameraComponent = camera;

            // Use direct possession (not queued) so the viewport camera wiring is deterministic.
            pawnComp.PossessByLocalPlayer(ELocalPlayerIndex.One);
            return pawnComp;
        }

        private static void CreateConsoleUI(SceneNode uiRootNode, out UICanvasComponent uiCanvas, out UICanvasInputComponent input)
        {
            // Add the canvas component directly to the root node (matching Editor pattern)
            uiCanvas = uiRootNode.AddComponent<UICanvasComponent>("Console Canvas")!;
            uiCanvas.IsActive = true;
            var canvasTfm = uiCanvas.CanvasTransform;
            canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
            canvasTfm.Width = 1920.0f;
            canvasTfm.Height = 1080.0f;
            canvasTfm.CameraDrawSpaceDistance = 10.0f;
            canvasTfm.Padding = new Vector4(0.0f);

            // Large obvious test label in center to verify text rendering works
            //AddTestLabel(uiRootNode);

            AddFPSText(null, uiRootNode);

            //Add input handler
            input = uiRootNode.AddComponent<UICanvasInputComponent>()!;
            input.IsActive = true;
            //var outputLogNode = uiRootNode.NewChild(out UIMaterialComponent outputLogBackground);
            //outputLogBackground.Material = BackgroundMaterial;
            //outputLogBackground.IsActive = true;

            var logTextNode = uiRootNode.NewChild(out VirtualizedConsoleUIComponent outputLogComp);
            outputLogComp.HorizontalAlignment = EHorizontalAlignment.Left;
            outputLogComp.VerticalAlignment = EVerticalAlignment.Top;
            outputLogComp.WrapMode = FontGlyphSet.EWrapMode.None;
            outputLogComp.Color = ColorF4.White;
            outputLogComp.FontSize = 16;
            outputLogComp.SetAsConsoleOut();
            outputLogComp.IsActive = true;
            var logTfm = outputLogComp.BoundableTransform;
            logTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            logTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            logTfm.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
        }

        /// <summary>
        /// Creates a console UI for the server and sets the UI and input components to the main player's camera and pawn.
        /// </summary>
        /// <param name="rootNode"></param>
        private static void CreateConsoleUI(SceneNode rootNode)
        {
            CreateConsoleUI(rootNode, out UICanvasComponent uiCanvas, out UICanvasInputComponent input);
            var pawnForInput = Engine.State.MainPlayer?.ControlledPawnComponent as PawnComponent;
            var pawnCam = pawnForInput?.GetSiblingComponent<CameraComponent>(false);
            pawnCam?.UserInterface = uiCanvas;
            input.OwningPawn = pawnForInput;
        }

        /// <summary>
        /// Creates a console UI for the server and sets the UI and input components to the given camera and pawn.
        /// </summary>
        /// <param name="rootNode"></param>
        /// <param name="camComp"></param>
        /// <param name="pawnForInput"></param>
        private static void CreateConsoleUI(SceneNode rootNode, CameraComponent camComp, PawnComponent? pawnForInput = null)
        {
            CreateConsoleUI(rootNode, out UICanvasComponent uiCanvas, out UICanvasInputComponent input);
            camComp?.UserInterface = uiCanvas;
            input.OwningPawn = pawnForInput;
        }

        //Simple FPS counter in the bottom right for debugging.
        private static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
        {
            SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
            UITextComponent text = textNode.AddComponent<UITextComponent>()!;
            text.IsActive = true;
            text.Font = font;
            text.FontSize = 22;
            text.Color = ColorF4.White;
            text.WrapMode = FontGlyphSet.EWrapMode.None;
            text.RegisterAnimationTick<UITextComponent>(TickFPS);
            var textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
            textTransform.MinAnchor = new Vector2(1.0f, 0.0f);
            textTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
            textTransform.NormalizedPivot = new Vector2(1.0f, 0.0f);
            textTransform.Width = null;
            textTransform.Height = null;
            textTransform.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
            textTransform.Scale = new Vector3(1.0f);
            return text;
        }

        private static readonly Queue<float> _fpsAvg = new();
        private static void TickFPS(UITextComponent t)
        {
            _fpsAvg.Enqueue(1.0f / Engine.Time.Timer.Update.SmoothedDelta);
            if (_fpsAvg.Count > 60)
                _fpsAvg.Dequeue();
            t.Text = $"Update: {MathF.Round(_fpsAvg.Sum() / _fpsAvg.Count)}hz";
        }

        /// <summary>
        /// Adds a large, obvious static test label in the center of the screen
        /// to verify that text rendering works at all on the server.
        /// </summary>
        private static UITextComponent AddTestLabel(SceneNode parentNode)
        {
            SceneNode labelNode = new(parentNode) { Name = "ServerTestLabel" };
            UITextComponent label = labelNode.AddComponent<UITextComponent>()!;
            label.IsActive = true;
            label.FontSize = 48;
            label.Color = ColorF4.Yellow;
            label.Text = "=== XRE SERVER RUNNING ===";
            label.WrapMode = FontGlyphSet.EWrapMode.None;
            label.HorizontalAlignment = EHorizontalAlignment.Center;
            label.VerticalAlignment = EVerticalAlignment.Center;

            var tfm = labelNode.GetTransformAs<UIBoundableTransform>(true)!;
            // Center anchors
            tfm.MinAnchor = new Vector2(0.5f, 0.5f);
            tfm.MaxAnchor = new Vector2(0.5f, 0.5f);
            tfm.NormalizedPivot = new Vector2(0.5f, 0.5f);
            tfm.Width = null;
            tfm.Height = null;
            return label;
        }

        private static XRMaterial? _backgroundMaterial;
        public static XRMaterial BackgroundMaterial
        {
            get => _backgroundMaterial ??= MakeBackgroundMaterial();
            set => _backgroundMaterial = value;
        }

        private static XRMaterial MakeBackgroundMaterial()
        {
            var floorShader = ShaderHelper.LoadEngineShader("UI\\GrabpassGaussian.frag");
            ShaderVar[] floorUniforms =
            [
                new ShaderVector4(new ColorF4(0.0f, 1.0f), "MatColor"),
                new ShaderFloat(10.0f, "BlurStrength"),
                new ShaderInt(30, "SampleCount"),
            ];
            XRTexture2D grabTex = XRTexture2D.CreateGrabPassTextureResized(1.0f, EReadBufferMode.Front, true, false, false, false);
            var floorMat = new XRMaterial(floorUniforms, [grabTex], floorShader);
            floorMat.RenderOptions.CullMode = ECullMode.None;
            floorMat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            floorMat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
            return floorMat;
        }

        static GameStartupSettings GetEngineSettings(XRWorld? targetWorld = null, bool enableDevRendering = true)
        {
            int w = 1920;
            int h = 1080;
            float updateHz = 60.0f;
            float renderHz = 30.0f;
            float fixedHz = 30.0f;

            int primaryX = NativeMethods.GetSystemMetrics(0);
            int primaryY = NativeMethods.GetSystemMetrics(1);

            return new GameStartupSettings()
            {
                // Always create a visible window so it's obvious the server launched.
                // Dev rendering may add extra UI, but a window should exist in all modes.
                StartupWindows =
                [
                    new()
                    {
                        WindowTitle = "XRE Server",
                        TargetWorld = targetWorld ?? new XRWorld(),
                        // Ensure at least one viewport exists so the window actually renders.
                        // Rendering is gated by: Viewports.Count > 0 && TargetWorldInstance != null.
                        LocalPlayers = ELocalPlayerIndexMask.One,
                        WindowState = EWindowState.Windowed,
                        X = primaryX / 2 - w / 2,
                        Y = primaryY / 2 - h / 2,
                        Width = w,
                        Height = h,
                    }
                ],
                OutputVerbosityOverride = new XREngine.Data.Core.OverrideableSetting<EOutputVerbosity>(EOutputVerbosity.Verbose, true),
                UdpClientRecievePort = 5001,
                UdpServerBindPort = _udpBindPort,
                UdpServerSendPort = _udpAdvertisedPort,
                UdpMulticastGroupIP = _udpMulticastGroup,
                UdpMulticastPort = _udpMulticastPort,
                MultiplayerSessionId = _serverSessionId,
                NetworkingType = ENetworkingType.Server,
                GPURenderDispatch = RuntimeBootstrapState.Settings.GPURenderDispatch,
                DefaultUserSettings = new UserSettings()
                {
                    VSync = EVSyncMode.Off,
                    RenderLibrary = RuntimeBootstrapState.Settings.RenderAPI,
                    PhysicsLibrary = RuntimeBootstrapState.Settings.PhysicsAPI,
                },
                TargetUpdatesPerSecond = updateHz,
                TargetFramesPerSecond = renderHz,
                FixedFramesPerSecond = fixedHz,
            };
        }
    }
}
