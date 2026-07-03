using System.Runtime;

namespace XREngine;

public enum EngineMemoryProfile
{
    EditorInteractive,
    DesktopRuntime,
    VRLowLatency,
    HeadlessServer,
    Benchmark,
    PublishedDefault,
}

public sealed record EngineMemoryPolicySnapshot(
    EngineMemoryProfile Profile,
    GCLatencyMode RequestedLatencyMode,
    GCLatencyMode EffectiveLatencyMode,
    bool IsServerGc,
    bool BackgroundGcConfigured,
    bool DiagnosticsEnabled,
    bool MaintenanceGcAllowed,
    bool BenchmarkNoGcRegionAllowed,
    long BenchmarkNoGcRegionBytes,
    IReadOnlyDictionary<string, object> GcConfigurationVariables);

public static class EngineMemoryPolicy
{
    private static readonly object Sync = new();

    public static EngineMemoryPolicySnapshot Current { get; private set; } = CreateSnapshot(
        EngineMemoryProfile.PublishedDefault,
        GCSettings.LatencyMode,
        diagnosticsEnabled: false,
        maintenanceGcAllowed: true,
        benchmarkNoGcRegionAllowed: false,
        benchmarkNoGcRegionBytes: 0L);

    public static EngineMemoryPolicySnapshot Apply(
        EngineMemoryProfile defaultProfile,
        Action<string>? log = null)
    {
        EngineMemoryProfile profile = ResolveProfile(defaultProfile);
        GCLatencyMode latencyMode = ResolveLatencyMode(profile);
        bool diagnosticsEnabled = ReadFlag(XREngineEnvironmentVariables.MemoryDiagnostics, defaultValue: true);
        bool maintenanceGcAllowed = !ReadFlag(XREngineEnvironmentVariables.DisableMaintenanceGc, defaultValue: false);
        long noGcBytes = ReadPositiveLong(XREngineEnvironmentVariables.BenchmarkNoGcBytes);
        bool benchmarkNoGcAllowed =
            profile == EngineMemoryProfile.Benchmark &&
            noGcBytes > 0L &&
            ReadFlag(XREngineEnvironmentVariables.BenchmarkNoGcRegion, defaultValue: false);

        lock (Sync)
        {
            try
            {
                GCSettings.LatencyMode = latencyMode;
            }
            catch (InvalidOperationException)
            {
                log?.Invoke("[MemoryPolicy] Could not change GC latency mode because a no-GC region is active.");
            }

            Current = CreateSnapshot(
                profile,
                latencyMode,
                diagnosticsEnabled,
                maintenanceGcAllowed,
                benchmarkNoGcAllowed,
                noGcBytes);
        }

        if (diagnosticsEnabled)
            LogDiagnostics(Current, log);

        return Current;
    }

    public static bool TryParseProfile(string? value, out EngineMemoryProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse(value.Trim(), ignoreCase: true, out profile))
        {
            return true;
        }

        profile = default;
        return false;
    }

    private static EngineMemoryProfile ResolveProfile(EngineMemoryProfile defaultProfile)
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MemoryProfile);
        return TryParseProfile(raw, out EngineMemoryProfile parsed)
            ? parsed
            : defaultProfile;
    }

    private static GCLatencyMode ResolveLatencyMode(EngineMemoryProfile profile)
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GcLatencyMode);
        if (!string.IsNullOrWhiteSpace(raw) &&
            Enum.TryParse(raw.Trim(), ignoreCase: true, out GCLatencyMode parsed))
        {
            return parsed;
        }

        return profile switch
        {
            EngineMemoryProfile.EditorInteractive => GCLatencyMode.Interactive,
            EngineMemoryProfile.DesktopRuntime => GCLatencyMode.Interactive,
            EngineMemoryProfile.VRLowLatency => GCLatencyMode.SustainedLowLatency,
            EngineMemoryProfile.HeadlessServer => GCLatencyMode.Interactive,
            EngineMemoryProfile.Benchmark => GCLatencyMode.SustainedLowLatency,
            EngineMemoryProfile.PublishedDefault => GCLatencyMode.Interactive,
            _ => GCLatencyMode.Interactive,
        };
    }

    private static EngineMemoryPolicySnapshot CreateSnapshot(
        EngineMemoryProfile profile,
        GCLatencyMode requestedLatencyMode,
        bool diagnosticsEnabled,
        bool maintenanceGcAllowed,
        bool benchmarkNoGcRegionAllowed,
        long benchmarkNoGcRegionBytes)
    {
        IReadOnlyDictionary<string, object> variables = GC.GetConfigurationVariables();
        bool backgroundGcConfigured = TryReadBooleanConfiguration(
            variables,
            "System.GC.Concurrent",
            XREngineEnvironmentVariables.DotNetGcConcurrent,
            defaultValue: true);

        return new EngineMemoryPolicySnapshot(
            profile,
            requestedLatencyMode,
            GCSettings.LatencyMode,
            GCSettings.IsServerGC,
            backgroundGcConfigured,
            diagnosticsEnabled,
            maintenanceGcAllowed,
            benchmarkNoGcRegionAllowed,
            benchmarkNoGcRegionBytes,
            variables);
    }

    private static bool TryReadBooleanConfiguration(
        IReadOnlyDictionary<string, object> variables,
        string configName,
        string environmentName,
        bool defaultValue)
    {
        if (variables.TryGetValue(configName, out object? value))
        {
            if (value is bool b)
                return b;

            if (value is int i)
                return i != 0;

            if (value is string s && TryParseBoolean(s, out bool parsed))
                return parsed;
        }

        string? env = Environment.GetEnvironmentVariable(environmentName);
        return TryParseBoolean(env, out bool envParsed) ? envParsed : defaultValue;
    }

    private static bool ReadFlag(string name, bool defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return TryParseBoolean(value, out bool parsed) ? parsed : defaultValue;
    }

    private static long ReadPositiveLong(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, out long parsed) && parsed > 0L ? parsed : 0L;
    }

    private static bool TryParseBoolean(string? value, out bool parsed)
    {
        parsed = false;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
            case "enabled":
                parsed = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
            case "disabled":
                parsed = false;
                return true;
            default:
                return false;
        }
    }

    private static void LogDiagnostics(EngineMemoryPolicySnapshot snapshot, Action<string>? log)
    {
        if (log is null)
            return;

        GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
        log(
            "[MemoryPolicy] profile=" + snapshot.Profile +
            "; latency=" + snapshot.EffectiveLatencyMode +
            "; serverGC=" + snapshot.IsServerGc +
            "; backgroundGC=" + snapshot.BackgroundGcConfigured +
            "; heapSize=" + memoryInfo.HeapSizeBytes +
            "; highMemoryThreshold=" + memoryInfo.HighMemoryLoadThresholdBytes +
            "; totalAvailable=" + memoryInfo.TotalAvailableMemoryBytes);

        LogConfigValue(snapshot, log, "System.GC.Server");
        LogConfigValue(snapshot, log, "System.GC.Concurrent");
        LogConfigValue(snapshot, log, "System.GC.HeapCount");
        LogConfigValue(snapshot, log, "System.GC.NoAffinitize");
        LogConfigValue(snapshot, log, "System.GC.HeapAffinitizeMask");
        LogConfigValue(snapshot, log, "System.GC.HeapAffinitizeRanges");
        LogConfigValue(snapshot, log, "System.GC.DynamicAdaptationMode");
        LogConfigValue(snapshot, log, "System.GC.ConserveMemory");
    }

    private static void LogConfigValue(
        EngineMemoryPolicySnapshot snapshot,
        Action<string> log,
        string key)
    {
        if (snapshot.GcConfigurationVariables.TryGetValue(key, out object? value))
            log("[MemoryPolicy] " + key + "=" + value);
    }
}
