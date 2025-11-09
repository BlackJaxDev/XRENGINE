using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Silk.NET.OpenAL;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor;
using XREngine.Native;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.GameStartupSettings;

namespace XREngine.Networking
{
    /// <summary>
    /// There will be several of these programs running on different machines.
    /// The user is first directed to a load balancer server, which will then redirect them to a game host server.
    /// </summary>
    public class Program
    {
        //private static readonly CommandServer _loadBalancer;

        //static Program()
        //{
        //    _loadBalancer = new CommandServer(
        //        8000,
        //        new RoundRobinLeastLoadBalancer(new[]
        //        {
        //            new Server { IP = "192.168.0.2", Port = 8001 },
        //            new Server { IP = "192.168.0.3", Port = 8002 },
        //            new Server { IP = "192.168.0.4", Port = 8003 },
        //        }),
        //        new Authenticator(""));
        //}

        //public static async Task Main()
        //{
        //    await _loadBalancer.Start();
        //}

        public static Task? WebAppTask { get; private set; }

        private static void Main(string[] args)
        {
            WebAppTask = BuildWebApi();
            var unitTestWorld = UnitTestingWorld.CreateUnitTestWorld(false, true);
            CreateConsoleUI(unitTestWorld.Scenes[0].RootNodes[0]);
            Engine.Run(/*Engine.LoadOrGenerateGameSettings(() => */GetEngineSettings(unitTestWorld)/*, "startup", false)*/, Engine.LoadOrGenerateGameState());
        }

        private static Task BuildWebApi()
        {
            var builder = WebApplication.CreateBuilder();

            builder.Services.AddControllers();
            builder.Services.AddResponseCompression(opts =>
            {
                opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]);
            });
            var app = builder.Build();

            app.UseResponseCompression();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthorization();
            app.MapControllers();

            return app.RunAsync();
        }

        static XRWorld CreateServerDebugWorld()
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var scene = new XRScene("Server Console Scene");
            var rootNode = new SceneNode("Root Node");
            scene.RootNodes.Add(rootNode);

            SceneNode cameraNode = CreateCamera(rootNode, out var camComp);
            var pawn = CreateDesktopViewerPawn(cameraNode);
            CreateConsoleUI(rootNode, camComp!, pawn);

            return new XRWorld("Server World", scene);
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

        private static EditorFlyingCameraPawnComponent CreateDesktopViewerPawn(SceneNode cameraNode)
        {
            var pawnComp = cameraNode.AddComponent<EditorFlyingCameraPawnComponent>();
            var listener = cameraNode.AddComponent<AudioListenerComponent>()!;
            listener.Gain = 1.0f;
            listener.DistanceModel = DistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;

            pawnComp!.Name = "TestPawn";
            pawnComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
            return pawnComp;
        }

        private static void CreateConsoleUI(SceneNode rootNode, out UICanvasComponent uiCanvas, out UICanvasInputComponent input)
        {
            var rootCanvasNode = new SceneNode(rootNode) { Name = "Root Server UI Node" };
            uiCanvas = rootCanvasNode.AddComponent<UICanvasComponent>("Console Canvas")!;
            var canvasTfm = uiCanvas.CanvasTransform;
            canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
            canvasTfm.Width = 1920.0f;
            canvasTfm.Height = 1080.0f;
            canvasTfm.CameraDrawSpaceDistance = 10.0f;
            canvasTfm.Padding = new Vector4(0.0f);

            AddFPSText(null, rootCanvasNode);

            //Add input handler
            input = rootCanvasNode.AddComponent<UICanvasInputComponent>()!;
            var outputLogNode = rootCanvasNode.NewChild(out UIMaterialComponent outputLogBackground);
            outputLogBackground.Material = BackgroundMaterial;

            var logTextNode = outputLogNode.NewChild(out VirtualizedConsoleUIComponent outputLogComp);
            outputLogComp.HorizontalAlignment = EHorizontalAlignment.Left;
            outputLogComp.VerticalAlignment = EVerticalAlignment.Top;
            outputLogComp.WrapMode = FontGlyphSet.EWrapMode.None;
            outputLogComp.Color = new ColorF4(0.0f, 0.0f, 0.0f, 1.0f);
            outputLogComp.FontSize = 16;
            outputLogComp.SetAsConsoleOut();
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

            var pawnForInput = Engine.State.MainPlayer.ControlledPawn;
            var pawnCam = pawnForInput?.GetSiblingComponent<CameraComponent>(false);
            if (pawnCam is not null)
                pawnCam.UserInterface = uiCanvas;
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

            if (camComp is not null)
                camComp.UserInterface = uiCanvas;
            input.OwningPawn = pawnForInput;
        }

        //Simple FPS counter in the bottom right for debugging.
        private static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
        {
            SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
            UITextComponent text = textNode.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 22;
            text.Color = new ColorF4(0.0f, 0.0f, 0.0f, 1.0f);
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

        static GameStartupSettings GetEngineSettings(XRWorld? targetWorld = null)
        {
            int w = 1920;
            int h = 1080;
            float updateHz = 0.0f;
            float renderHz = 30.0f;
            float fixedHz = 30.0f;

            int primaryX = NativeMethods.GetSystemMetrics(0);
            int primaryY = NativeMethods.GetSystemMetrics(1);

            return new GameStartupSettings()
            {
                StartupWindows =
                [
                    new()
                    {
                        WindowTitle = "XRE Server",
                        TargetWorld = targetWorld ?? new XRWorld(),
                        WindowState = EWindowState.Windowed,
                        X = primaryX / 2 - w / 2,
                        Y = primaryY / 2 - h / 2,
                        Width = w,
                        Height = h,
                    }
                ],
                OutputVerbosity = EOutputVerbosity.Verbose,
                NetworkingType = ENetworkingType.Server,
                DefaultUserSettings = new UserSettings()
                {
                    TargetFramesPerSecond = renderHz,
                    VSync = EVSyncMode.Off,
                    UseDebugOpaquePipeline = UnitTestingWorld.Toggles.ForceDebugOpaquePipeline,
                },
                TargetUpdatesPerSecond = updateHz,
                FixedFramesPerSecond = fixedHz,
            };
        }
    }
}