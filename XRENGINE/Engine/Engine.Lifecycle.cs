using System.Threading;
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
        /// Calls <see cref="Initialize"/>, <see cref="RunGameLoop"/>, then blocks through either
        /// <see cref="BlockForRendering"/> or <see cref="BlockWithoutRendering"/> before cleanup.
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
            => Run(startupSettings, state, beginPlayingAllWorlds: true);

        /// <summary>
        /// Runs the engine and selects whether initialized worlds start in standalone play or
        /// in the editor lifecycle.
        /// </summary>
        /// <param name="startupSettings">Configuration for the game including windows, worlds, and networking.</param>
        /// <param name="state">The initial game state object.</param>
        /// <param name="beginPlayingAllWorlds">
        /// If <c>true</c>, all worlds begin standalone play during initialization. If <c>false</c>,
        /// all worlds enter the non-simulating editor lifecycle before the game loop starts.
        /// </param>
        public static void Run(
            GameStartupSettings startupSettings,
            GameState state,
            bool beginPlayingAllWorlds)
        {
            if (!RuntimeRenderingHostServices.HasConcreteHost)
            {
                throw new InvalidOperationException(
                    "No concrete rendering host is installed. Call the application composition root's " +
                    "rendering bootstrap before Engine.Run.");
            }

            bool initialized = Initialize(startupSettings, state, beginPlayingAllWorlds);
            if (initialized)
            {
                if (!beginPlayingAllWorlds)
                    BeginEditAllWorlds();

                RunGameLoop();
                if (startupSettings.RunWithoutWindows)
                    BlockWithoutRendering();
                else
                    BlockForRendering();
            }
            else
                Environment.ExitCode = 1;
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
        ///   <item><description>Create requested windows and initialize graphics contexts, when any were requested</description></item>
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
                ShuttingDown = false;
                Interlocked.Exchange(ref _abandonProcessExitCleanup, 0);
                Interlocked.Exchange(ref _headlessShutdownRequested, 0);
                int startupThreadId = Environment.CurrentManagedThreadId;
                RuntimeEngine.AssignWindowThread(startupThreadId);
                RuntimeEngine.AssignRenderThread(startupThreadId);
                Debug.Rendering(
                    "[WindowOwnership] Startup thread owns collapsed window/render mode. WindowThreadId={0} RenderThreadId={1}.",
                    RuntimeEngine.WindowThreadId,
                    RuntimeEngine.RenderThreadId);

                using (SuppressSettingsCascades())
                {
                    GameSettings = startupSettings;
                    StartupOpenXrRuntimeRequested = startupSettings is IVRGameStartupSettings { VRRuntime: EVRRuntime.OpenXR };
                    UserSettings = GameSettings.DefaultUserSettings?.DeepClone() ?? new UserSettings();

                    if (CurrentProject is null)
                        LoadSandboxSettings();
                }

#if !XRE_PUBLISHED
                ProfileCapture.ApplyPerformanceProfileContract();
#endif
                EnsureMemoryPolicyConfigured(startupSettings);
                ValidateGpuRenderingStartupConfiguration();
                ResolveExecutionTopology();
                ConfigureJobManager(GameSettings);
                ValidateInstalledWorkScheduler();

                BeforeCreateWindows?.Invoke(startupSettings, state);

                // Creating windows first is critical—they initialize the render context and graphics API
                CreateWindows(startupSettings.StartupWindows);
                AfterCreateWindows?.Invoke(startupSettings, state);
                EngineRenderingSettingsApplication.LogVulkanFeatureProfileFingerprint(force: true);
#if !XRE_PUBLISHED
                ProfileCapture.LogActivePerformanceProfile();
#endif
                RuntimeEngine.Rendering.SecondaryContext.InitializeIfSupported(RuntimeEngine.Windows.FirstOrDefault());
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

                // Wire up the external profiler UDP sender (delegates bridge XREngine.Data → Engine)
#if !XRE_PUBLISHED
                WireProfilerSenderCollectors();
                UdpProfilerSender.TryStartFromEnvironment();
#endif

                success = true;
            }
            catch (Exception e)
            {
                string diagnostic = $"Error during engine initialization: {e}";
                Console.Error.WriteLine(diagnostic);
                Debug.WriteAuxiliaryLog("startup-failure.log", diagnostic);
                Debug.LogWarning(diagnostic);
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
        /// After calling this method, block through <see cref="BlockForRendering"/> for a windowed
        /// runtime or <see cref="BlockWithoutRendering"/> for a presentationless runtime.
        /// The game loop runs until all windows close or a presentationless runtime is shut down.
        /// </remarks>
        public static void RunGameLoop()
            => Time.Timer.RunGameLoop();

        /// <summary>
        /// Blocks the current thread to submit render commands to the graphics API.
        /// </summary>
        /// <remarks>
        /// This method will not return until the engine shuts down (all windows closed).
        /// It must be called from the current render host thread. In the collapsed
        /// GLFW/Silk.NET mode this is also the native window/event thread.
        /// </remarks>
        public static void BlockForRendering()
            => RenderThreadHost.BlockForRendering(IsEngineStillActive);

        /// <summary>
        /// Blocks a presentationless runtime while update, physics, and networking execute on their
        /// normal engine threads. This avoids waiting on render fences that do not exist when no
        /// local window was requested.
        /// </summary>
        public static void BlockWithoutRendering()
        {
            Debug.Out("Blocking without local rendering.");
            while (IsEngineStillActive())
                Thread.Sleep(10);
            Debug.Out("No longer blocking presentationless main thread.");
        }

        /// <summary>
        /// Initiates engine shutdown by closing all windows.
        /// </summary>
        /// <remarks>
        /// This will trigger the cleanup process once all windows have closed.
        /// </remarks>
        public static void ShutDown()
        {
            if (GameSettings?.RunWithoutWindows == true)
                Interlocked.Exchange(ref _headlessShutdownRequested, 1);

            var windows = RuntimeEngine.Windows.ToArray();
            foreach (var window in windows)
                window.RequestClose();
        }

        /// <summary>
        /// Stops the engine and disposes of all allocated resources.
        /// </summary>
        /// <remarks>
        /// Called internally once no windows remain active, or manually if needed.
        /// </remarks>
        internal static void Cleanup()
        {
            if (ShuttingDown)
                return;

            ShuttingDown = true;

            // Stop producers before disposing anything they can still reference. A timeout means
            // the closing window has selected process-exit abandonment instead of unsafe teardown.
            bool coreLoopsStopped = Time.Timer.StopAndWait(TimeSpan.FromSeconds(2));
            if (!coreLoopsStopped)
                Interlocked.Exchange(ref _abandonProcessExitCleanup, 1);

            if (Volatile.Read(ref _abandonProcessExitCleanup) != 0)
            {
                Debug.RenderingWarning(
                    "[Shutdown] Skipping process-exit resource cleanup because a bounded quiesce/GPU wait failed. " +
                    "The operating system will reclaim resources after foreground engine hosts exit.");
                WindowPumpHost.Stop();
                return;
            }

            bool schedulerStopped = WorkScheduler?.Shutdown(waitForWorkers: true)
                ?? (_jobs?.Shutdown(waitForWorkers: true) ?? true);
            if (!schedulerStopped)
            {
                Interlocked.Exchange(ref _abandonProcessExitCleanup, 1);
                Debug.RenderingWarning(
                    "[Shutdown] Skipping process-exit resource cleanup because the bounded work-scheduler " +
                    "quiesce failed. Executor and backend ownership remains retained until process exit.");
                WindowPumpHost.Stop();
                return;
            }

            // Finalize profiler output before tearing down subsystems it reads from.
#if !XRE_PUBLISHED
            ProfileCapture.Shutdown();
            UdpProfilerSender.Stop();
#endif

            ShutdownNetworking();

            // TODO: Implement clean shutdown where each window disposes of its own allocated assets
            RuntimeEngine.Rendering.SecondaryContext.Dispose();
            WindowPumpHost.Stop();
            Assets.Dispose();
        }

        /// <summary>
        /// Checks whether the engine should continue running.
        /// </summary>
        /// <returns><c>true</c> while a window is active, or while a presentationless runtime has not requested shutdown.</returns>
        private static bool IsEngineStillActive()
            => RuntimeEngine.Windows.Count > 0 ||
                (GameSettings?.RunWithoutWindows == true && Volatile.Read(ref _headlessShutdownRequested) == 0);

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
            // Standalone startup must enter the engine play state before each world reaches
            // EPlayState.Playing. XRWorldInstance uses that state to keep physics enabled.
            => PlayMode.BeginStandalonePlay();

        /// <summary>
        /// Starts all world instances in the active, non-simulating editor lifecycle.
        /// </summary>
        public static void BeginEditAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.BeginEditMode().GetAwaiter().GetResult();
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
