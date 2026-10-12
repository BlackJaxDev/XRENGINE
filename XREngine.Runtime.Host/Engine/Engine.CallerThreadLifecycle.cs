using XREngine.Execution;
using XREngine.Rendering;

namespace XREngine;

public static partial class Engine
{
    private static bool _callerThreadSession;

    /// <summary>
    /// Initializes the shared engine for a host that owns its event loop and advances frames
    /// on the calling thread. The host installs its platform and world services first.
    /// </summary>
    public static void InitializeForCallerThread(GameStartupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = RuntimeEngineStartupPolicyServices.Require();
        if (!RuntimeApplicationCapabilityServices.Current.IsConfigured)
            throw new InvalidOperationException("A caller-thread engine host requires an application capability profile.");
        if (_callerThreadSession)
            throw new InvalidOperationException("A caller-thread engine session is already active.");
        if (settings.StartupWindows.Count != 0 || !settings.RunWithoutWindows)
            throw new InvalidOperationException("Caller-thread startup requires no desktop windows and RunWithoutWindows=true.");

        RuntimeLifecycleState.Current.BeginStartup();
        try
        {
            int ownerThread = Environment.CurrentManagedThreadId;
            RuntimeEngine.AssignWindowThread(ownerThread);
            RuntimeEngine.AssignRenderThread(ownerThread);
            using (SuppressSettingsCascades())
            {
                GameSettings = settings;
                UserSettings = settings.DefaultUserSettings?.DeepClone() ?? new UserSettings();
                RuntimeEngineStartupPolicyServices.Require().PrepareSettings(settings);
                if (EffectiveSettings.RecalcChildMatricesLoopType != ELoopType.Sequential ||
                    EffectiveSettings.TickGroupedItemsInParallel)
                {
                    throw new NotSupportedException(
                        "Caller-thread startup requires sequential transform and tick-group execution.");
                }
                RuntimeWorkScheduler.ConfigureCallerThread(
                    EffectiveSettings.JobQueueLimit,
                    EffectiveSettings.JobQueueWarningThreshold,
                    ConfigureJobManagerHooks);
            }

            InstallRuntimeTimingServices();
            InstallRuntimePhysicsServices();
            Time.Initialize(GameSettings, EffectiveUserSettings);
            IsEditor = false;
            _callerThreadSession = true;
        }
        catch
        {
            UninstallRuntimeTimingServices();
            UninstallRuntimePhysicsServices();
            RuntimeWorkScheduler.Shutdown(waitForWorkers: false);
            RuntimeLifecycleState.Current.RequestShutdown();
            throw;
        }
        finally
        {
            RuntimeLifecycleState.Current.CompleteStartup();
        }
    }

    /// <summary>Stops a caller-owned session after its world host has ended play and quiesced.</summary>
    public static void StopCallerThreadSession()
    {
        if (!_callerThreadSession)
            return;

        RuntimeLifecycleState.Current.RequestShutdown();
        Time.Timer.Stop();
        if (!RuntimeWorkScheduler.Shutdown(waitForWorkers: false))
            throw new InvalidOperationException("Caller-thread jobs remain pending during engine shutdown.");

        UninstallRuntimeTimingServices();
        UninstallRuntimePhysicsServices();
        if (_assets.IsValueCreated)
            _assets.Value.Dispose();
        _sessionSettings.ClearAll();
        RuntimeLifecycleState.Current.TryBeginShutdown();
        _callerThreadSession = false;
    }
}
