using System.Threading.Tasks;
using XREngine.Data.Profiling;
using XREngine.Rendering;

namespace XREngine
{
    /// <summary>
    /// Engine lifecycle management - initialization, game loop, and shutdown.
    /// </summary>
    public static partial class Engine
    {
        #region Engine Lifecycle - Main Entry Points

        /// <summary>
        /// The primary method to run the engine.
        /// Calls <see cref="Initialize"/>, <see cref="RunGameLoop"/>, <see cref="BlockForRendering"/>, 
        /// and <see cref="Cleanup"/> in sequence.
        /// </summary>
        /// <param name="startupSettings">Configuration for the game including windows, worlds, and networking.</param>
        /// <param name="state">The initial game state object.</param>
        /// <example>
        /// <code>
        /// var settings = new GameStartupSettings
        /// {
        ///     StartupWindows = { new GameWindowStartupSettings { Width = 1920, Height = 1080 } }
        /// };
        /// Engine.Run(settings, new MyGameState());
        /// </code>
        /// </example>
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
        /// Initializes the engine with the specified settings.
        /// </summary>
        /// <param name="startupSettings">Configuration for the game.</param>
        /// <param name="state">The initial game state.</param>
        /// <param name="beginPlayingAllWorlds">
        /// If <c>true</c>, all worlds will begin playing immediately after initialization.
        /// Set to <c>false</c> if you need to perform additional setup before play begins.
        /// </param>
        /// <returns><c>true</c> if initialization succeeded; otherwise, <c>false</c>.</returns>
        /// <remarks>
        /// <para>Initialization performs the following steps in order:</para>
        /// <list type="number">
        ///   <item><description>Apply game and user settings</description></item>
        ///   <item><description>Configure the job manager for parallel processing</description></item>
        ///   <item><description>Create windows and initialize graphics contexts</description></item>
        ///   <item><description>Initialize VR if configured</description></item>
        ///   <item><description>Start the timing system</description></item>
        ///   <item><description>Initialize networking if configured</description></item>
        ///   <item><description>Begin play for all worlds (if enabled)</description></item>
        /// </list>
        /// </remarks>
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

                if (CurrentProject is null)
                    LoadSandboxSettings();

                ValidateGpuRenderingStartupConfiguration();
                ConfigureJobManager(GameSettings);

                BeforeCreateWindows?.Invoke(startupSettings, state);

                // Creating windows first is critical—they initialize the render context and graphics API
                CreateWindows(startupSettings.StartupWindows);
                AfterCreateWindows?.Invoke(startupSettings, state);
                Rendering.LogVulkanFeatureProfileFingerprint(force: true);
                Rendering.SecondaryContext.InitializeIfSupported(Windows.FirstOrDefault());
                XRWindow.AnyWindowFocusChanged += WindowFocusChanged;

                // VR initialization can run asynchronously in the background
                // Windows must be created first if initializing VR in place
                if (startupSettings is IVRGameStartupSettings vrSettings)
                    Task.Run(async () => await InitializeVR(vrSettings, startupSettings.RunVRInPlace));

                // Start the engine timer for update/render ticks
                Time.Initialize(GameSettings, UserSettings);

                // Initialize networking based on configuration
                InitializeNetworking(startupSettings);

                // Wire up event callbacks for task processing
                Time.Timer.SwapBuffers += SwapBuffers;
                Time.Timer.RenderFrame += DequeueMainThreadTasks;

                // Wire up the external profiler UDP sender (delegates bridge XREngine.Data → Engine)
                WireProfilerSenderCollectors();
                UdpProfilerSender.TryStartFromEnvironment();

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

        /// <summary>
        /// Starts the game loop, initializing parallel threads for update and physics.
        /// </summary>
        /// <remarks>
        /// After calling this method, call <see cref="BlockForRendering"/> to begin rendering.
        /// The game loop runs until all windows are closed.
        /// </remarks>
        public static void RunGameLoop()
            => Time.Timer.RunGameLoop();

        /// <summary>
        /// Blocks the current thread to submit render commands to the graphics API.
        /// </summary>
        /// <remarks>
        /// This method will not return until the engine shuts down (all windows closed).
        /// It must be called from the main/render thread.
        /// </remarks>
        public static void BlockForRendering()
            => Time.Timer.BlockForRendering(IsEngineStillActive);

        /// <summary>
        /// Initiates engine shutdown by closing all windows.
        /// </summary>
        /// <remarks>
        /// This will trigger the cleanup process once all windows have closed.
        /// </remarks>
        public static void ShutDown()
        {
            var windows = _windows.ToArray();
            foreach (var window in windows)
                window.Window.Close();
        }

        /// <summary>
        /// Stops the engine and disposes of all allocated resources.
        /// </summary>
        /// <remarks>
        /// Called internally once no windows remain active, or manually if needed.
        /// </remarks>
        internal static void Cleanup()
        {
            // Stop profiler sender before tearing down subsystems it reads from
            UdpProfilerSender.Stop();

            // TODO: Implement clean shutdown where each window disposes of its own allocated assets
            Rendering.SecondaryContext.Dispose();
            Time.Timer.Stop();
            Jobs.Shutdown();
            Assets.Dispose();
        }

        /// <summary>
        /// Checks whether the engine should continue running.
        /// </summary>
        /// <returns><c>true</c> if at least one window is still active.</returns>
        private static bool IsEngineStillActive()
            => Windows.Count > 0;

        private static void ValidateGpuRenderingStartupConfiguration()
        {
            bool forcePassthrough = EditorPreferences?.Debug?.ForceGpuPassthroughCulling ?? false;
            bool allowCpuFallback = EditorPreferences?.Debug?.AllowGpuCpuFallback
                ?? EffectiveSettings.EnableGpuIndirectCpuFallback;

            if (!forcePassthrough && !allowCpuFallback)
                return;

            EBuildConfiguration configuration = GameSettings?.BuildSettings?.Configuration ?? EBuildConfiguration.Development;
            string profile = configuration == EBuildConfiguration.Debug ? "debug" : "non-debug";

            string issue = forcePassthrough && allowCpuFallback
                ? "passthrough culling is forced and CPU fallback is enabled"
                : forcePassthrough
                    ? "passthrough culling is forced"
                    : "CPU fallback is enabled";

            Debug.RenderingWarning(
                "[GPU Render Startup Validation] Unsafe GPU rendering defaults detected ({0}, {1} build): {2}. " +
                "For production baselines set EditorPreferences.Debug.ForceGpuPassthroughCulling=false and " +
                "EditorPreferences.Debug.AllowGpuCpuFallback=false.",
                configuration,
                profile,
                issue);
        }

        #endregion

        #region World Management

        /// <summary>
        /// Starts play for all world instances.
        /// </summary>
        /// <remarks>
        /// This activates all scene nodes and their components, registers ticking events,
        /// and begins simulation for all worlds.
        /// </remarks>
        public static void BeginPlayAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.BeginPlay().Wait();
        }

        /// <summary>
        /// Stops play for all world instances.
        /// </summary>
        /// <remarks>
        /// This deactivates all scene nodes and their components, unregisters ticking events,
        /// and stops simulation for all worlds.
        /// </remarks>
        public static void EndPlayAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.EndPlay();
        }

        #endregion
    }
}
