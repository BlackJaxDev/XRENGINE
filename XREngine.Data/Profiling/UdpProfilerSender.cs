namespace XREngine.Data.Profiling;

/// <summary>Collects profiler packets through delegates and controls the optional host transport.</summary>
public static class UdpProfilerSender
{
    private static IProfilerTransportBackend? _backend;
    public static IProfilerTransportBackend? Backend
    {
        get => Volatile.Read(ref _backend);
        set => Volatile.Write(ref _backend, value);
    }
    public static bool IsRunning => Backend?.IsRunning ?? false;
    public static Func<ProfilerFramePacket?>? CollectProfilerFrame { get; set; }
    public static Func<RenderStatsPacket?>? CollectRenderStats { get; set; }
    public static Func<ThreadAllocationsPacket?>? CollectThreadAllocations { get; set; }
    public static Func<BvhMetricsPacket?>? CollectBvhMetrics { get; set; }
    public static Func<JobSystemStatsPacket?>? CollectJobSystemStats { get; set; }
    public static Func<MainThreadInvokesPacket?>? CollectMainThreadInvokes { get; set; }

    public static void Start(int port = ProfilerProtocol.DefaultPort)
        => (Backend ?? throw new NotSupportedException("The profiler transport is not installed in this host.")).Start(port);

    public static void Stop() => Backend?.Stop();

    public static bool TryStartFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable(ProfilerProtocol.EnabledEnvVar) != "1")
            return false;
        int port = ProfilerProtocol.DefaultPort;
        if (int.TryParse(Environment.GetEnvironmentVariable(ProfilerProtocol.PortEnvVar), out int configuredPort) &&
            configuredPort is > 0 and <= 65535)
            port = configuredPort;
        Start(port);
        return true;
    }
}
