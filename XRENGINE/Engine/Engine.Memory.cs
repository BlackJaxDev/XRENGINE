namespace XREngine;

public static partial class Engine
{
    public static EngineMemoryPolicySnapshot MemoryPolicy => RuntimeMemoryPolicy.Current;

    public static EngineMemoryPolicySnapshot ConfigureMemoryPolicy(EngineMemoryProfile profile)
        => RuntimeMemoryPolicy.Configure(profile, static message => Debug.Out(message));

    internal static EngineMemoryPolicySnapshot EnsureMemoryPolicyConfigured(
        GameStartupSettings startupSettings,
        EngineMemoryProfile fallbackProfile = EngineMemoryProfile.DesktopRuntime)
    {
        EngineMemoryProfile profile = startupSettings is IVRGameStartupSettings
            ? EngineMemoryProfile.VRLowLatency
            : fallbackProfile;

        return RuntimeMemoryPolicy.EnsureConfigured(profile, static message => Debug.Out(message));
    }

    public static bool TryStartBenchmarkNoGcRegion(long? byteBudget = null)
        => RuntimeMemoryPolicy.TryStartBenchmarkNoGcRegion(
            byteBudget,
            static message => Debug.Out(message),
            static message => Debug.LogWarning(message));

    public static void EndBenchmarkNoGcRegion()
        => RuntimeMemoryPolicy.EndBenchmarkNoGcRegion(
            static message => Debug.Out(message),
            static message => Debug.LogWarning(message));

    public static EngineMaintenanceGcResult RequestMaintenanceGarbageCollection(EngineMaintenanceGcRequest request)
    {
        using (Profiler.Start($"Engine.MaintenanceGC.{request.Reason}"))
            return RuntimeMemoryPolicy.RequestMaintenanceGarbageCollection(
                request,
                IsDispatchingRenderFrame,
                static message => Debug.Out(message),
                static message => Debug.LogWarning(message));
    }
}
