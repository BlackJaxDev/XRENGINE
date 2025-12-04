using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Linq;
using XREngine.Audio;
using XREngine.Data.Core;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine
{
    /// <summary>
    /// The root static class for the engine.
    /// Contains all the necessary functions to run the engine and manage its components.
    /// Organized with several static subclasses for managing different parts of the engine.
    /// You can use these subclasses without typing the whole path every time in your code by adding "using static XREngine.Engine.<path>;" at the top of the file.
    /// </summary>
    public static partial class Engine
    {
        private static readonly EventList<XRWindow> _windows = [];
        private static int _suppressedCleanupRequests;
        private static UserSettings _userSettings = null!;
        private static BuildSettings _buildSettings = null!;

        public static XREvent<UserSettings>? UserSettingsChanged;
        public static event Action<BuildSettings>? BuildSettingsChanged;

        static IDisposable ExternalProfilingHook(string sampleName) => Profiler.Start(sampleName);

        static Engine()
        {
            UserSettings = new UserSettings();
            GameSettings = new GameStartupSettings();
            BuildSettings = new BuildSettings();

            Time.Timer.PostUpdateFrame += Timer_PostUpdateFrame;

            XREvent.ProfilingHook = ExternalProfilingHook;
            IRenderTree.ProfilingHook = ExternalProfilingHook;
        }

        private static void Timer_PostUpdateFrame()
        {
            XRObjectBase.ProcessPendingDestructions();
            TransformBase.ProcessParentReassignments();
        }

        private static readonly ConcurrentQueue<Action> _asyncTaskQueue = new();
        private static readonly ConcurrentQueue<Action> _mainThreadTaskQueue = new();
        private static readonly List<Func<bool>> _mainThreadCoroutines = [];
        private static readonly ConcurrentQueue<Func<bool>> _mainThreadCoroutinesAddQueue = new();

        public static bool IsRenderThread => Environment.CurrentManagedThreadId == RenderThreadId;
        public static int RenderThreadId { get; private set; }

        /// <summary>
        /// These tasks will be executed on a separate dedicated thread.
        /// </summary>
        /// <param name="task"></param>
        public static void EnqueueAsyncTask(Action task)
            => _asyncTaskQueue.Enqueue(task);
        /// <summary>
        /// These tasks will be executed on the main thread, and usually are rendering tasks.
        /// </summary>
        /// <param name="task"></param>
        public static void EnqueueMainThreadTask(Action task)
            => _mainThreadTaskQueue.Enqueue(task);
        public static void AddMainThreadCoroutine(Func<bool> task)
            => _mainThreadCoroutinesAddQueue.Enqueue(task);

        /// <summary>
        /// Invokes the task on the main thread if the current thread is not the render thread.
        /// Returns true if the task was enqueued, false if not.
        /// </summary>
        /// <param name="task"></param>
        /// <returns></returns>
        public static bool InvokeOnMainThread(Action task, bool executeNowIfAlreadyMainThread = false)
        {
            if (IsRenderThread)
            {
                if (executeNowIfAlreadyMainThread)
                    task();
                return false;
            }
            
            EnqueueMainThreadTask(task);
            return true;
        }

        /// <summary>
        /// Indicates the engine is currently starting up and might be still initializing objects.
        /// </summary>
        public static bool StartingUp { get; private set; }
        /// <summary>
        /// Indicates the engine is currently shutting down and might be disposing of objects.
        /// </summary>
        public static bool ShuttingDown { get; private set; }
        /// <summary>
        /// User-defined settings, such as graphical and audio options.
        /// </summary>
        public static UserSettings UserSettings
        {
            get => _userSettings;
            set
            {
                if (ReferenceEquals(_userSettings, value) && value is not null)
                    return;

                if (_userSettings is not null)
                    _userSettings.PropertyChanged -= HandleUserSettingsChanged;
                _userSettings = value ?? new UserSettings();
                _userSettings.PropertyChanged += HandleUserSettingsChanged;
                
                OnUserSettingsChanged();
            }
        }

        /// <summary>
        /// Project-level build settings describing how packaged builds should be produced.
        /// </summary>
        public static BuildSettings BuildSettings
        {
            get => _buildSettings;
            set
            {
                if (ReferenceEquals(_buildSettings, value) && value is not null)
                    return;

                if (_buildSettings is not null)
                    _buildSettings.PropertyChanged -= HandleBuildSettingsChanged;

                _buildSettings = value ?? new BuildSettings();
                _buildSettings.PropertyChanged += HandleBuildSettingsChanged;
                BuildSettingsChanged?.Invoke(_buildSettings);
            }
        }

        private static void OnUserSettingsChanged()
        {
            Profiler.EnableFrameLogging = _userSettings.EnableFrameLogging;
            Profiler.DebugOutputMinElapsedMs = _userSettings.DebugOutputMinElapsedMs;
            Rendering.ApplyRenderPipelinePreference();
            Rendering.ApplyGlobalIlluminationModePreference();
            Rendering.ApplyGpuRenderDispatchPreference();
            UserSettingsChanged?.Invoke(_userSettings);
        }

        private static void HandleUserSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(UserSettings.UseDebugOpaquePipeline):
                    Rendering.ApplyRenderPipelinePreference();
                    break;
                case nameof(UserSettings.GlobalIlluminationMode):
                    Rendering.ApplyGlobalIlluminationModePreference();
                    break;
                case nameof(UserSettings.EnableFrameLogging):
                    Profiler.EnableFrameLogging = UserSettings.EnableFrameLogging;
                    break;
                case nameof(UserSettings.DebugOutputMinElapsedMs):
                    Profiler.DebugOutputMinElapsedMs = UserSettings.DebugOutputMinElapsedMs;
                    break;
                case nameof(UserSettings.GPURenderDispatch):
                    Rendering.ApplyGpuRenderDispatchPreference();
                    break;
                case null:
                case "":
                    Rendering.ApplyRenderPipelinePreference();
                    Rendering.ApplyGlobalIlluminationModePreference();
                    Profiler.EnableFrameLogging = UserSettings.EnableFrameLogging;
                    Profiler.DebugOutputMinElapsedMs = UserSettings.DebugOutputMinElapsedMs;
                    Rendering.ApplyGpuRenderDispatchPreference();
                    break;
            }
        }

        private static void HandleBuildSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            BuildSettingsChanged?.Invoke(_buildSettings);
        }
        /// <summary>
        /// Game-defined settings, such as initial world and libraries.
        /// </summary>
        public static GameStartupSettings GameSettings { get; set; }
        /// <summary>
        /// All networking-related functions.
        /// </summary>
        public static BaseNetworkingManager? Networking { get; private set; }
        /// <summary>
        /// Audio manager for playing and streaming sounds and music.
        /// </summary>
        public static AudioManager Audio { get; } = new();
        /// <summary>
        /// All active world instances.
        /// These are separate from the windows to allow for multiple windows to display the same world.
        /// They are also not the same as XRWorld, which is just the data for a world.
        /// </summary>
        public static IReadOnlyCollection<XRWorldInstance> WorldInstances => XRWorldInstance.WorldInstances.Values;
        /// <summary>
        /// The list of currently active and rendering windows.
        /// </summary>
        public static IEventListReadOnly<XRWindow> Windows => _windows;
        ///// <summary>
        ///// The list of all active render objects being utilized for rendering.
        ///// Each generic render object has a list of API-specific render objects that represent it for each window.
        ///// </summary>
        //public static IEventListReadOnly<GenericRenderObject> RenderObjects => _renderObjects;
        /// <summary>
        /// Manages all assets loaded into the engine.
        /// </summary>
        public static AssetManager Assets { get; } = new();
        /// <summary>
        /// Easily accessible random number generator.
        /// </summary>
        public static Random Random { get; } = new();
        /// <summary>
        /// This class is used to profile the speed of engine and game code to find performance bottlenecks.
        /// </summary>
        public static CodeProfiler Profiler { get; } = new();

        /// <summary>
        /// The sole method needed to run the engine.
        /// Calls Initialize, Run, and ShutDown in order.
        /// </summary>
        /// <param name="startupSettings"></param>
        /// <param name="state"></param>
        public static void Run(GameStartupSettings startupSettings, GameState state)
        {
            if (Initialize(startupSettings, state))
            {
                RunGameLoop();
                BlockForRendering();
            }
            Cleanup();
        }

        /// <summary>
        /// Initializes the engine with settings for the game it will run.
        /// </summary>
        public static bool Initialize(
            GameStartupSettings startupSettings,
            GameState state,
            bool beginPlayingAllWorlds = true)
        {
            bool success = false;
            try
            {
                StartingUp = true;
                RenderThreadId = Environment.CurrentManagedThreadId;
                GameSettings = startupSettings;
                UserSettings = GameSettings.DefaultUserSettings;

                //Creating windows first is most important, because they will initialize the render context and graphics API.
                CreateWindows(startupSettings.StartupWindows);
                Rendering.SecondaryContext.InitializeIfSupported(Windows.FirstOrDefault());
                XRWindow.AnyWindowFocusChanged += WindowFocusChanged;

                //VR is allowed to initialize async in the background.
                //Windows need to be created first if initializing VR in place.
                if (startupSettings is IVRGameStartupSettings vrSettings)
                    Task.Run(async () => await InitializeVR(vrSettings, startupSettings.RunVRInPlace));

                //Run the tick timer for the engine.
                Time.Initialize(GameSettings, UserSettings);

                //Start processing network in/out for what type of client this is
                InitializeNetworking(startupSettings);

                //Attach event callbacks for processing async and main thread tasks
                Time.Timer.SwapBuffers += SwapBuffers;
                Time.Timer.RenderFrame += DequeueMainThreadTasks;
                success = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error during engine initialization: {e.Message}\n{e.StackTrace}");
                success = false;
            }
            finally
            {
                StartingUp = false;
            }

            if (beginPlayingAllWorlds && success)
                BeginPlayAllWorlds();

            return success;
        }

        public static bool LastFocusState { get; private set; } = true;

        private static void WindowFocusChanged(XRWindow window, bool isFocused)
        {
            bool anyWindowFocused = isFocused;
            if (!anyWindowFocused)
            {
                foreach (var w in _windows)
                {
                    if (w == null || w.Window == null)
                        continue; // Skip if the window is null or has been disposed

                    if (w.IsFocused)
                    {
                        anyWindowFocused = true;
                        break;
                    }
                }
            }

            if (LastFocusState == anyWindowFocused)
                return;

            LastFocusState = anyWindowFocused;
            if (anyWindowFocused)
                OnGainedFocus();
            else
                OnLostFocus();
        }

        public static XREvent<bool>? FocusChanged { get; set; }

        private static void OnLostFocus()
        {
            Debug.Out("No windows are focused.");

            //Disable audio if disabled on defocus
            if (UserSettings.DisableAudioOnDefocus)
            {
                if (UserSettings.AudioDisableFadeSeconds > 0.0f)
                    Audio.FadeOut(UserSettings.AudioDisableFadeSeconds);
                else
                    Audio.Enabled = false; // Disable audio immediately
            }

            //Set target FPS to unfocused value
            //If in VR, the headset will handle this instead of window focus
            if (UserSettings.UnfocusedTargetFramesPerSecond is not null && !VRState.IsInVR)
            {
                Time.Timer.TargetRenderFrequency = UserSettings.UnfocusedTargetFramesPerSecond.Value;
                Debug.Out($"Unfocused target FPS set to {UserSettings.UnfocusedTargetFramesPerSecond}.");
            }

            FocusChanged?.Invoke(false);
        }

        private static void OnGainedFocus()
        {
            Debug.Out("At least one window is focused.");

            //Enable audio if it was disabled on defocus
            if (UserSettings.DisableAudioOnDefocus)
            {
                if (UserSettings.AudioDisableFadeSeconds > 0.0f)
                    Audio.FadeIn(UserSettings.AudioDisableFadeSeconds);
                else
                    Audio.Enabled = true; // Enable audio immediately
            }

            //Set target FPS to focused value
            //If in VR, the headset will handle this instead of window focus
            if (UserSettings.UnfocusedTargetFramesPerSecond is not null && !VRState.IsInVR)
            {
                Time.Timer.TargetRenderFrequency = UserSettings.TargetFramesPerSecond ?? 0.0f;
                Debug.Out($"Focused target FPS set to {UserSettings.TargetFramesPerSecond}.");
            }

            FocusChanged?.Invoke(true);
        }

        private static async Task<bool> InitializeVR(IVRGameStartupSettings vrSettings, bool runVRInPlace)
        {
            if (vrSettings is null ||
                vrSettings.VRManifest is null ||
                vrSettings.ActionManifest is null)
            {
                Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                return false;
            }

            bool result;
            if (runVRInPlace)
                result = await VRState.InitializeLocal(vrSettings.ActionManifest, vrSettings.VRManifest, _windows[0]);
            else
                result = await VRState.IninitializeClient(vrSettings.ActionManifest, vrSettings.VRManifest);
            return result;
        }

        private static void InitializeNetworking(GameStartupSettings startupSettings)
        {
            var appType = startupSettings.NetworkingType;
            switch (appType)
            {
                default:
                case GameStartupSettings.ENetworkingType.Local:
                    Networking = null;
                    break;
                case GameStartupSettings.ENetworkingType.Server:
                    var server = new ServerNetworkingManager();
                    Networking = server;
                    server.Start(IPAddress.Parse(startupSettings.UdpMulticastGroupIP), startupSettings.UdpMulticastPort, startupSettings.UdpClientRecievePort);
                    break;
                case GameStartupSettings.ENetworkingType.Client:
                    var client = new ClientNetworkingManager();
                    Networking = client;
                    client.Start(IPAddress.Parse(startupSettings.UdpMulticastGroupIP), startupSettings.UdpMulticastPort, IPAddress.Parse(startupSettings.ServerIP), startupSettings.UdpServerSendPort);
                    break;
                case GameStartupSettings.ENetworkingType.P2PClient:
                    var p2pClient = new PeerToPeerNetworkingManager();
                    Networking = p2pClient;
                    p2pClient.Start(IPAddress.Parse(startupSettings.UdpMulticastGroupIP), startupSettings.UdpMulticastPort, IPAddress.Parse(startupSettings.ServerIP));
                    break;
            }
        }

        /// <summary>
        /// Starts play for all worlds.
        /// This means all scene nodes will become active, and thus their components, and all ticking events will register, etc.
        /// </summary>
        public static void BeginPlayAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.BeginPlay().Wait();
        }

        /// <summary>
        /// Stops play for all worlds.
        /// </summary>
        public static void EndPlayAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.EndPlay();
        }

        private static void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("Engine.SwapBuffers");
            while (_asyncTaskQueue.TryDequeue(out var task))
                task.Invoke();
        }

        private static void DequeueMainThreadTasks()
            => ProcessPendingMainThreadWork();

        internal static void ProcessMainThreadTasks()
            => ProcessPendingMainThreadWork();

        private static void ProcessPendingMainThreadWork()
        {
            while (_mainThreadTaskQueue.TryDequeue(out var task))
                task.Invoke();
            while (_mainThreadCoroutinesAddQueue.TryDequeue(out var task))
                _mainThreadCoroutines.Add(task);
            for (int i = 0; i < _mainThreadCoroutines.Count; i++)
                if (_mainThreadCoroutines[i].Invoke())
                    _mainThreadCoroutines.RemoveAt(i--);
        }

        public static void CreateWindows(List<GameWindowStartupSettings> windows)
        {
            foreach (var windowSettings in windows)
                CreateWindow(windowSettings);
        }

        public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings)
        {
            bool preferHdrOutput = windowSettings.OutputHDR ?? Rendering.Settings.OutputHDR;
            var options = GetWindowOptions(windowSettings, preferHdrOutput);
            XRWindow window = new(options, windowSettings.UseNativeTitleBar);
            window.PreferHDROutput = preferHdrOutput;
            CreateViewports(windowSettings.LocalPlayers, window);
            window.UpdateViewportSizes();
            _windows.Add(window);
            Rendering.ApplyRenderPipelinePreference();

            /*Task.Run(() => */window.SetWorld(windowSettings.TargetWorld);
            return window;
        }

        private static void CreateViewports(ELocalPlayerIndexMask localPlayerMask, XRWindow window)
        {
            if (localPlayerMask == 0)
                return;
            
            for (int i = 0; i < 4; i++)
                if (((int)localPlayerMask & (1 << i)) > 0)
                    window.RegisterLocalPlayer((ELocalPlayerIndex)i, false);
            
            window.ResizeAllViewportsAccordingToPlayers();
        }

        private static WindowOptions GetWindowOptions(GameWindowStartupSettings windowSettings, bool preferHdrOutput)
        {
            WindowState windowState;
            WindowBorder windowBorder;
            Vector2D<int> position = new(windowSettings.X, windowSettings.Y);
            Vector2D<int> size = new(windowSettings.Width, windowSettings.Height);
            switch (windowSettings.WindowState)
            {
                case EWindowState.Fullscreen:
                    windowState = WindowState.Fullscreen;
                    windowBorder = WindowBorder.Hidden;
                    break;
                default:
                case EWindowState.Windowed:
                    windowState = WindowState.Normal;
                    windowBorder = WindowBorder.Resizable;
                    break;
                case EWindowState.Borderless:
                    windowState = WindowState.Normal;
                    windowBorder = WindowBorder.Hidden;
                    position = new Vector2D<int>(0, 0);
                    int primaryX = Native.NativeMethods.GetSystemMetrics(0);
                    int primaryY = Native.NativeMethods.GetSystemMetrics(1);
                    size = new Vector2D<int>(primaryX, primaryY);
                    break;
            }

            if (!windowSettings.UseNativeTitleBar && windowState == WindowState.Normal)
                windowBorder = WindowBorder.Hidden;

            bool requestHdrSurface = preferHdrOutput && UserSettings.RenderLibrary != ERenderLibrary.Vulkan;
            int preferredBitDepth = requestHdrSurface ? 64 : 24;

            return new(
                true,
                position,
                size,
                0.0,
                0.0,
                UserSettings.RenderLibrary == ERenderLibrary.Vulkan
                    ? new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 1))
                    : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6)),
                windowSettings.WindowTitle ?? string.Empty,
                windowState,
                windowBorder,
                windowSettings.VSync,
                true,
                VideoMode.Default,
                preferredBitDepth,
                8,
                null,
                windowSettings.TransparentFramebuffer,
                false,
                false,
                null,
                1);
        }

        /// <summary>
        /// This method will check the condition for the engine to continue running.
        /// </summary>
        /// <returns></returns>
        private static bool IsEngineStillActive()
            => Windows.Count > 0;

        /// <summary>
        /// This will initialize all parallel threads and start the game loop.
        /// The main rendering thread must call BlockForRendering() separately to start rendering.
        /// </summary>
        public static void RunGameLoop()
            => Time.Timer.RunGameLoop();

        /// <summary>
        /// This will block the current thread and submit renders to the graphics API until the engine is shut down.
        /// </summary>
        public static void BlockForRendering()
            => Time.Timer.BlockForRendering(IsEngineStillActive);

        /// <summary>
        /// Closes all windows, resulting in the engine shutting down and the process ending.
        /// </summary>
        public static void ShutDown()
        {
            var windows = _windows.ToArray();
            foreach (var window in windows)
                window.Window.Close();
        }   

        /// <summary>
        /// Stops the engine and disposes of all allocated data.
        /// Called internally once no windows remain active.
        /// </summary>
        internal static void Cleanup()
        {
            //TODO: clean shutdown. Each window should dispose of assets its allocated upon its own closure

            //ShuttingDown = true;
            Rendering.SecondaryContext.Dispose();
            Time.Timer.Stop();
            Assets.Dispose();
            //ShuttingDown = false;
        }

        /// <summary>
        /// Prevents the engine from shutting down the next time the final window is closed.
        /// Useful for temporary utility windows (e.g., file dialogs) that should not tear down the host application.
        /// </summary>
        public static void SuppressNextCleanup()
            => Interlocked.Increment(ref _suppressedCleanupRequests);

        public static void RemoveWindow(XRWindow window)
        {
            _windows.Remove(window);
            if (_windows.Count == 0)
            {
                if (Interlocked.CompareExchange(ref _suppressedCleanupRequests, 0, 0) > 0)
                {
                    Interlocked.Decrement(ref _suppressedCleanupRequests);
                    return;
                }
                Cleanup();
            }
        }

        public delegate int DelBeginOperation(
            string operationMessage,
            string finishedMessage,
            out Progress<float> progress,
            out CancellationTokenSource cancel,
            TimeSpan? maxOperationTime = null);

        public delegate void DelEndOperation(int operationId);
    }
}
