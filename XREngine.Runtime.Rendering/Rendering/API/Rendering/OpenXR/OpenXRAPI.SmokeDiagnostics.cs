using Silk.NET.OpenXR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrSmokeSwapchainSummary
{
    public int ViewIndex { get; set; }
    public string Backend { get; set; } = string.Empty;
    public uint Width { get; set; }
    public uint Height { get; set; }
    public long Format { get; set; }
    public uint SampleCount { get; set; }
    public uint ImageCount { get; set; }
}

public sealed class OpenXrSmokeSummary
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LogDirectory { get; set; }
    public string? RuntimeManifestPath { get; set; }
    public string? RuntimeName { get; set; }
    public string? RuntimeVersion { get; set; }
    public string RendererBackend { get; set; } = string.Empty;
    public string RuntimeState { get; set; } = string.Empty;
    public string SessionState { get; set; } = string.Empty;
    public string ReferenceSpaceType { get; set; } = string.Empty;
    public string[] EnabledExtensions { get; set; } = [];
    public bool InstanceCreated { get; set; }
    public bool SystemFound { get; set; }
    public bool SessionCreated { get; set; }
    public bool ReferenceSpaceCreated { get; set; }
    public bool SwapchainsCreated { get; set; }
    public bool SessionRunning { get; set; }
    public bool TeardownCompleted { get; set; }
    public long SubmittedFrameCount { get; set; }
    public long NoLayerFrameCount { get; set; }
    public long EndFrameFailureCount { get; set; }
    public uint LocatedViewCount { get; set; }
    public bool PredictedViewPoseCached { get; set; }
    public bool LateViewPoseCached { get; set; }
    public bool PredictedActionPoseCacheUpdated { get; set; }
    public bool LateActionPoseCacheUpdated { get; set; }
    public bool DesktopMirrorComposed { get; set; }
    public long[] PerEyeAcquireCounts { get; set; } = [];
    public long[] PerEyeWaitCounts { get; set; } = [];
    public long[] PerEyeReleaseCounts { get; set; } = [];
    public long PerFrameAllocationsBytes { get; set; }
    public OpenXrSmokeSwapchainSummary[] Swapchains { get; set; } = [];
    public string[] RuntimeStateTransitions { get; set; } = [];
    public string[] SessionStateTransitions { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public string[] Failures { get; set; } = [];
}

public unsafe partial class OpenXRAPI
{
    private readonly object _smokeDiagnosticsLock = new();
    private readonly List<string> _smokeRuntimeStateTransitions = [];
    private readonly List<string> _smokeSessionStateTransitions = [];
    private readonly List<string> _smokeWarnings = [];
    private readonly List<string> _smokeFailures = [];
    private readonly List<OpenXrSmokeSwapchainSummary> _smokeSwapchains = [];
    private readonly long[] _smokePerEyeAcquireCounts = new long[2];
    private readonly long[] _smokePerEyeWaitCounts = new long[2];
    private readonly long[] _smokePerEyeReleaseCounts = new long[2];
    private string[] _smokeEnabledExtensions = [];
    private string _smokeRendererBackend = string.Empty;
    private string _smokeReferenceSpaceType = string.Empty;
    private int _smokeInstanceCreated;
    private int _smokeSystemFound;
    private int _smokeSessionCreated;
    private int _smokeReferenceSpaceCreated;
    private int _smokeSwapchainsCreated;
    private int _smokeSessionRunning;
    private int _smokeTeardownCompleted;
    private int _smokePredictedViewPoseCached;
    private int _smokeLateViewPoseCached;
    private int _smokePredictedActionPoseCacheUpdated;
    private int _smokeLateActionPoseCacheUpdated;
    private int _smokeDesktopMirrorComposed;
    private long _smokeSubmittedFrameCount;
    private long _smokeNoLayerFrameCount;
    private long _smokeEndFrameFailureCount;
    private uint _smokeLocatedViewCount;

    public long SmokeSubmittedFrameCount => Volatile.Read(ref _smokeSubmittedFrameCount);
    public long SmokeNoLayerFrameCount => Volatile.Read(ref _smokeNoLayerFrameCount);
    public long SmokeCompletedFrameCount => SmokeSubmittedFrameCount + SmokeNoLayerFrameCount;
    public bool SmokeTeardownCompleted => Volatile.Read(ref _smokeTeardownCompleted) != 0;

    public OpenXrSmokeSummary CreateSmokeSummary(string? logDirectory = null)
    {
        string? runtimeManifestPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (string.IsNullOrWhiteSpace(runtimeManifestPath))
            runtimeManifestPath = TryGetOpenXRActiveRuntime();

        var (runtimeName, runtimeVersion) = TryReadRuntimeManifestMetadata(runtimeManifestPath);

        lock (_smokeDiagnosticsLock)
        {
            return new OpenXrSmokeSummary
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                LogDirectory = logDirectory,
                RuntimeManifestPath = runtimeManifestPath,
                RuntimeName = runtimeName,
                RuntimeVersion = runtimeVersion,
                RendererBackend = _smokeRendererBackend,
                RuntimeState = _runtimeState.ToString(),
                SessionState = _sessionState.ToString(),
                ReferenceSpaceType = _smokeReferenceSpaceType,
                EnabledExtensions = [.. _smokeEnabledExtensions],
                InstanceCreated = Volatile.Read(ref _smokeInstanceCreated) != 0,
                SystemFound = Volatile.Read(ref _smokeSystemFound) != 0,
                SessionCreated = Volatile.Read(ref _smokeSessionCreated) != 0,
                ReferenceSpaceCreated = Volatile.Read(ref _smokeReferenceSpaceCreated) != 0,
                SwapchainsCreated = Volatile.Read(ref _smokeSwapchainsCreated) != 0,
                SessionRunning = Volatile.Read(ref _smokeSessionRunning) != 0,
                TeardownCompleted = Volatile.Read(ref _smokeTeardownCompleted) != 0,
                SubmittedFrameCount = Volatile.Read(ref _smokeSubmittedFrameCount),
                NoLayerFrameCount = Volatile.Read(ref _smokeNoLayerFrameCount),
                EndFrameFailureCount = Volatile.Read(ref _smokeEndFrameFailureCount),
                LocatedViewCount = Volatile.Read(ref _smokeLocatedViewCount),
                PredictedViewPoseCached = Volatile.Read(ref _smokePredictedViewPoseCached) != 0,
                LateViewPoseCached = Volatile.Read(ref _smokeLateViewPoseCached) != 0,
                PredictedActionPoseCacheUpdated = Volatile.Read(ref _smokePredictedActionPoseCacheUpdated) != 0,
                LateActionPoseCacheUpdated = Volatile.Read(ref _smokeLateActionPoseCacheUpdated) != 0,
                DesktopMirrorComposed = Volatile.Read(ref _smokeDesktopMirrorComposed) != 0,
                PerEyeAcquireCounts = CopyCounterArray(_smokePerEyeAcquireCounts),
                PerEyeWaitCounts = CopyCounterArray(_smokePerEyeWaitCounts),
                PerEyeReleaseCounts = CopyCounterArray(_smokePerEyeReleaseCounts),
                PerFrameAllocationsBytes = 0,
                Swapchains = [.. _smokeSwapchains],
                RuntimeStateTransitions = [.. _smokeRuntimeStateTransitions],
                SessionStateTransitions = [.. _smokeSessionStateTransitions],
                Warnings = [.. _smokeWarnings],
                Failures = [.. _smokeFailures],
            };
        }
    }

    public void RequestSmokeSessionExit()
    {
        if (_session.Handle == 0)
            return;

        try
        {
            Result result = CheckResult(Api.RequestExitSession(_session), "xrRequestExitSession");
            if (result != Result.Success)
                RecordSmokeFailure($"xrRequestExitSession returned {result}.");
        }
        catch (Exception ex)
        {
            RecordSmokeFailure($"xrRequestExitSession threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ResetSmokeDiagnostics()
    {
        lock (_smokeDiagnosticsLock)
        {
            _smokeRuntimeStateTransitions.Clear();
            _smokeSessionStateTransitions.Clear();
            _smokeWarnings.Clear();
            _smokeFailures.Clear();
            _smokeSwapchains.Clear();
            _smokeEnabledExtensions = [];
            _smokeRendererBackend = string.Empty;
            _smokeReferenceSpaceType = string.Empty;
        }

        Array.Clear(_smokePerEyeAcquireCounts);
        Array.Clear(_smokePerEyeWaitCounts);
        Array.Clear(_smokePerEyeReleaseCounts);
        Volatile.Write(ref _smokeInstanceCreated, 0);
        Volatile.Write(ref _smokeSystemFound, 0);
        Volatile.Write(ref _smokeSessionCreated, 0);
        Volatile.Write(ref _smokeReferenceSpaceCreated, 0);
        Volatile.Write(ref _smokeSwapchainsCreated, 0);
        Volatile.Write(ref _smokeSessionRunning, 0);
        Volatile.Write(ref _smokeTeardownCompleted, 0);
        Volatile.Write(ref _smokePredictedViewPoseCached, 0);
        Volatile.Write(ref _smokeLateViewPoseCached, 0);
        Volatile.Write(ref _smokePredictedActionPoseCacheUpdated, 0);
        Volatile.Write(ref _smokeLateActionPoseCacheUpdated, 0);
        Volatile.Write(ref _smokeDesktopMirrorComposed, 0);
        Volatile.Write(ref _smokeSubmittedFrameCount, 0);
        Volatile.Write(ref _smokeNoLayerFrameCount, 0);
        Volatile.Write(ref _smokeEndFrameFailureCount, 0);
        Volatile.Write(ref _smokeLocatedViewCount, 0);
    }

    private void RecordSmokeInstanceCreated(string rendererBackend, string[] enabledExtensions)
    {
        lock (_smokeDiagnosticsLock)
        {
            _smokeRendererBackend = rendererBackend;
            _smokeEnabledExtensions = enabledExtensions;
        }

        Volatile.Write(ref _smokeInstanceCreated, 1);
    }

    private void RecordSmokeSystemFound()
        => Volatile.Write(ref _smokeSystemFound, 1);

    private void RecordSmokeSessionCreated(string rendererBackend)
    {
        lock (_smokeDiagnosticsLock)
            _smokeRendererBackend = rendererBackend;

        Volatile.Write(ref _smokeSessionCreated, 1);
    }

    private void RecordSmokeReferenceSpaceCreated(ReferenceSpaceType referenceSpaceType)
    {
        lock (_smokeDiagnosticsLock)
            _smokeReferenceSpaceType = referenceSpaceType.ToString();

        Volatile.Write(ref _smokeReferenceSpaceCreated, 1);
    }

    private void RecordSmokeSwapchain(
        string backend,
        int viewIndex,
        uint width,
        uint height,
        long format,
        uint sampleCount,
        uint imageCount)
    {
        lock (_smokeDiagnosticsLock)
        {
            _smokeSwapchains.RemoveAll(s => s.ViewIndex == viewIndex && string.Equals(s.Backend, backend, StringComparison.Ordinal));
            _smokeSwapchains.Add(new OpenXrSmokeSwapchainSummary
            {
                Backend = backend,
                ViewIndex = viewIndex,
                Width = width,
                Height = height,
                Format = format,
                SampleCount = sampleCount,
                ImageCount = imageCount,
            });
        }
    }

    private void RecordSmokeSwapchainsCreated()
        => Volatile.Write(ref _smokeSwapchainsCreated, 1);

    private void RecordSmokeRuntimeState(OpenXrRuntimeState state)
    {
        Volatile.Write(ref _smokeSessionRunning, state == OpenXrRuntimeState.SessionRunning ? 1 : 0);
        lock (_smokeDiagnosticsLock)
            _smokeRuntimeStateTransitions.Add($"{DateTimeOffset.UtcNow:O} {state}");
    }

    private void RecordSmokeSessionState(SessionState state)
    {
        lock (_smokeDiagnosticsLock)
            _smokeSessionStateTransitions.Add($"{DateTimeOffset.UtcNow:O} {state}");
    }

    private void RecordSmokeLocatedViews(uint viewCountOutput)
        => Volatile.Write(ref _smokeLocatedViewCount, viewCountOutput);

    private void RecordSmokeViewPoseCache(OpenXrPoseTiming timing)
    {
        if (timing == OpenXrPoseTiming.Late)
            Volatile.Write(ref _smokeLateViewPoseCached, 1);
        else
            Volatile.Write(ref _smokePredictedViewPoseCached, 1);
    }

    private void RecordSmokeActionPoseCache(OpenXrPoseTiming timing)
    {
        if (timing == OpenXrPoseTiming.Late)
            Volatile.Write(ref _smokeLateActionPoseCacheUpdated, 1);
        else
            Volatile.Write(ref _smokePredictedActionPoseCacheUpdated, 1);
    }

    private void RecordSmokeEyeAcquire(uint viewIndex)
        => IncrementEyeCounter(_smokePerEyeAcquireCounts, viewIndex);

    private void RecordSmokeEyeWait(uint viewIndex)
        => IncrementEyeCounter(_smokePerEyeWaitCounts, viewIndex);

    private void RecordSmokeEyeRelease(uint viewIndex)
        => IncrementEyeCounter(_smokePerEyeReleaseCounts, viewIndex);

    private void RecordSmokeEndFrame(Result result, uint layerCount)
    {
        if (result == Result.Success)
        {
            if (layerCount > 0)
                Interlocked.Increment(ref _smokeSubmittedFrameCount);
            else
                Interlocked.Increment(ref _smokeNoLayerFrameCount);
        }
        else
        {
            Interlocked.Increment(ref _smokeEndFrameFailureCount);
        }
    }

    private void RecordSmokeDesktopMirrorComposed()
        => Volatile.Write(ref _smokeDesktopMirrorComposed, 1);

    private void RecordSmokeTeardownCompleted()
        => Volatile.Write(ref _smokeTeardownCompleted, 1);

    private void RecordSmokeWarning(string warning)
    {
        lock (_smokeDiagnosticsLock)
            _smokeWarnings.Add(warning);
    }

    private void RecordSmokeFailure(string failure)
    {
        lock (_smokeDiagnosticsLock)
            _smokeFailures.Add(failure);
    }

    private static void IncrementEyeCounter(long[] counters, uint viewIndex)
    {
        if (viewIndex >= counters.Length)
            return;

        Interlocked.Increment(ref counters[viewIndex]);
    }

    private static long[] CopyCounterArray(long[] source)
    {
        var copy = new long[source.Length];
        for (int i = 0; i < source.Length; i++)
            copy[i] = Volatile.Read(ref source[i]);
        return copy;
    }

    private static (string? RuntimeName, string? RuntimeVersion) TryReadRuntimeManifestMetadata(string? runtimeManifestPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeManifestPath) || !File.Exists(runtimeManifestPath))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
            if (!document.RootElement.TryGetProperty("runtime", out var runtime))
                return (null, null);

            string? name = runtime.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            string? version = runtime.TryGetProperty("api_version", out var versionElement)
                ? versionElement.GetString()
                : null;
            return (name, version);
        }
        catch
        {
            return (null, null);
        }
    }
}
