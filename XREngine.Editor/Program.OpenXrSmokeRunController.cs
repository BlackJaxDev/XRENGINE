using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System.Diagnostics;
using EngineDebug = XREngine.Debug;
using XREngine;
using XREngine.Editor;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Runtime.Bootstrap;

internal partial class Program
{
    private sealed class OpenXrSmokeRunController : IDisposable
    {
        private const int ExitSuccess = 0;
        private const int ExitStartupFailure = 21;
        private const int ExitFrameTimeout = 22;
        private const int ExitSummaryFailure = 23;
        private const int ExitTeardownFailure = 24;
        private const int ExitEngineException = 25;
        private const int DefaultTimeoutSeconds = 120;
        private const int MaxOcclusionViewSnapshotsPerFrame = 32;
        private const int MaxOutputSnapshotsPerFrame = 16;
        private static readonly JsonSerializerSettings SmokeJsonSettings = new()
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = [new StringEnumConverter()]
        };

        private readonly int _targetFrames;
        private readonly int _warmupFrames;
        private readonly TimeSpan _timeout;
        private readonly string? _summaryPath;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly OpenXrSmokeFrameLedgerEntry[] _frameLedger;
        private readonly OpenXrSmokeOcclusionViewLedgerEntry[] _occlusionViewLedger;
        private readonly OpenXrSmokeOutputLedgerEntry[] _outputLedger;
        private readonly object _ledgerLock = new();
        private readonly List<string> _failures = [];
        private readonly List<string> _warnings = [];
        private OpenXRAPI? _subscribedApi;
        private int _frameLedgerCount;
        private int _occlusionViewLedgerCount;
        private int _outputLedgerCount;
        private bool _occlusionViewLedgerOverflow;
        private bool _outputLedgerOverflow;
        private long _lastObservedSubmittedFrames;
        private long _lastObservedNoLayerFrames;
        private long _lastLeftAcquireCount;
        private long _lastRightAcquireCount;
        private long _lastLeftWaitCount;
        private long _lastRightWaitCount;
        private long _lastLeftPublishCount;
        private long _lastRightPublishCount;
        private long _lastLeftReleaseCount;
        private long _lastRightReleaseCount;
        private long _lastStrictSequentialFallbackAttemptCount;
        private bool _installed;
        private bool _targetReached;
        private bool _sessionExitRequested;
        private DateTimeOffset _sessionExitDeadlineUtc;
        private bool _shutdownRequested;
        private bool _finished;
        private int _exitCode = ExitStartupFailure;

        private OpenXrSmokeRunController(int targetFrames, int warmupFrames, TimeSpan timeout, string? summaryPath)
        {
            _targetFrames = targetFrames;
            _warmupFrames = warmupFrames;
            _timeout = timeout;
            _summaryPath = summaryPath;
            _frameLedger = new OpenXrSmokeFrameLedgerEntry[targetFrames];
            _occlusionViewLedger = new OpenXrSmokeOcclusionViewLedgerEntry[targetFrames * MaxOcclusionViewSnapshotsPerFrame];
            _outputLedger = new OpenXrSmokeOutputLedgerEntry[targetFrames * MaxOutputSnapshotsPerFrame];
        }

        public bool Enabled => _targetFrames > 0;

        public static OpenXrSmokeRunController Parse(string[] args)
        {
            int targetFrames = ReadIntOption(args, "--smoke-frames", XREngineEnvironmentVariables.OpenXrSmokeFrames, 0);
            int warmupFrames = ReadIntOption(args, "--smoke-warmup-frames", XREngineEnvironmentVariables.OpenXrSmokeWarmupFrames, 0);
            int timeoutSeconds = ReadIntOption(args, "--smoke-timeout-seconds", XREngineEnvironmentVariables.OpenXrSmokeTimeoutSeconds, DefaultTimeoutSeconds);
            string? summaryPath = ReadStringOption(args, "--openxr-smoke-summary", XREngineEnvironmentVariables.OpenXrSmokeSummary)
                ?? ReadStringOption(args, "--smoke-summary", XREngineEnvironmentVariables.SmokeSummary);

            return new OpenXrSmokeRunController(
                Math.Max(0, targetFrames),
                Math.Max(0, warmupFrames),
                TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
                summaryPath);
        }

        public void Configure(UnitTestingWorldSettings settings)
        {
            if (!Enabled)
                return;

            Environment.ExitCode = ExitStartupFailure;
            if (!settings.VRPawn)
                _failures.Add("OpenXR smoke requires UnitTestingWorldSettings.VRPawn=true.");
            if (!settings.UseOpenXR)
                _failures.Add("OpenXR smoke requires UnitTestingWorldSettings.UseOpenXR=true.");
            if (settings.VR.Mode is not (UnitTestingVrLaunchMode.MonadoOpenXR or UnitTestingVrLaunchMode.OpenXR))
                _failures.Add("OpenXR smoke requires UnitTestingWorldSettings.VR.Mode=MonadoOpenXR or OpenXR.");
            if (settings.SceneOnlyVRPawn)
                _warnings.Add("SceneOnlyVRPawn is scene-only and does not emulate OpenXR API calls; Lane 2 smoke should normally set it to false.");

            EngineDebug.Out($"[OpenXRSmoke] Enabled warmupFrames={_warmupFrames}, retainedFrames={_targetFrames}, timeout={_timeout.TotalSeconds:F0}s, summary='{_summaryPath ?? "<log directory>"}'.");
        }

        public void Install()
        {
            if (!Enabled || _installed)
                return;

            Engine.Time.Timer.UpdateFrame += Update;
            _installed = true;
        }

        public void RecordEngineRunException(Exception ex)
        {
            if (!Enabled)
                return;

            _failures.Add($"Engine.Run threw {ex.GetType().Name}: {ex.Message}");
            _exitCode = ExitEngineException;
        }

        public void FinishAfterRun()
        {
            if (!Enabled || _finished)
                return;

            _finished = true;
            string? logDirectory = TryGetLogDirectory();
            OpenXrSmokeSummary summary = Engine.VRState.OpenXRApi?.CreateSmokeSummary(logDirectory)
                ?? new OpenXrSmokeSummary
                {
                    LogDirectory = logDirectory,
                    RuntimeState = "<no OpenXR API>",
                    SessionState = "<no OpenXR API>",
                };

            lock (_ledgerLock)
            {
                AppendStrictSinglePassStereoOutputEvidence(summary);
                summary.WarmupFrameCount = _warmupFrames;
                summary.RetainedFrameCount = _frameLedgerCount;
                summary.FrameLedger = new OpenXrSmokeFrameLedgerEntry[_frameLedgerCount];
                Array.Copy(_frameLedger, summary.FrameLedger, _frameLedgerCount);
                summary.OcclusionViewLedger = new OpenXrSmokeOcclusionViewLedgerEntry[_occlusionViewLedgerCount];
                Array.Copy(_occlusionViewLedger, summary.OcclusionViewLedger, _occlusionViewLedgerCount);
                summary.OcclusionViewLedgerOverflow = _occlusionViewLedgerOverflow;
                summary.OutputLedger = new OpenXrSmokeOutputLedgerEntry[_outputLedgerCount];
                Array.Copy(_outputLedger, summary.OutputLedger, _outputLedgerCount);
                summary.OutputLedgerOverflow = _outputLedgerOverflow;
            }

            List<string> validationFailures = ValidateSummary(summary);
            summary.Warnings = [.. summary.Warnings, .. _warnings];
            summary.Failures = [.. summary.Failures, .. _failures, .. validationFailures];

            int exitCode = ResolveExitCode(summary, validationFailures);
            Environment.ExitCode = exitCode;

            string path = ResolveSummaryPath(logDirectory);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, JsonConvert.SerializeObject(summary, SmokeJsonSettings));
                EngineDebug.Out($"[OpenXRSmoke] Summary written to '{path}'. ExitCode={exitCode}.");
            }
            catch (Exception ex)
            {
                Environment.ExitCode = ExitSummaryFailure;
                EngineDebug.LogWarning($"[OpenXRSmoke] Failed to write summary '{path}': {ex.Message}");
            }
        }

        private void AppendStrictSinglePassStereoOutputEvidence(OpenXrSmokeSummary summary)
        {
            if (!string.Equals(summary.ViewRenderImplementationPath, "TrueSinglePassStereo", StringComparison.Ordinal) ||
                summary.Swapchains.Length < 2)
            {
                return;
            }

            OpenXrSmokeSwapchainSummary left = summary.Swapchains.FirstOrDefault(static swapchain => swapchain.ViewIndex == 0)
                ?? summary.Swapchains[0];
            OpenXrSmokeSwapchainSummary right = summary.Swapchains.FirstOrDefault(static swapchain => swapchain.ViewIndex == 1)
                ?? summary.Swapchains[1];
            uint width = Math.Min(left.Width, right.Width);
            uint height = Math.Min(left.Height, right.Height);
            if (width == 0 || height == 0)
                return;

            RenderOutputRequest request = RenderOutputRequest.CreateDefault(
                EVrOutputViewKind.LeftEye,
                EFrameOutputKind.OpenXREyeSubmit);
            ulong formatKey = (unchecked((ulong)left.Format) << 32) ^ unchecked((ulong)right.Format);
            for (int retainedIndex = 0; retainedIndex < _frameLedgerCount; retainedIndex++)
            {
                bool alreadyRecorded = false;
                for (int outputIndex = 0; outputIndex < _outputLedgerCount; outputIndex++)
                {
                    OpenXrSmokeOutputLedgerEntry existing = _outputLedger[outputIndex];
                    if (existing.RetainedIndex == retainedIndex &&
                        string.Equals(existing.TargetClass, ERenderOutputTargetClass.RuntimeExternalImage.ToString(), StringComparison.Ordinal) &&
                        existing.ViewMask == 0x3u)
                    {
                        alreadyRecorded = true;
                        break;
                    }
                }

                if (alreadyRecorded)
                    continue;
                if (_outputLedgerCount >= _outputLedger.Length)
                {
                    _outputLedgerOverflow = true;
                    return;
                }

                OpenXrSmokeFrameLedgerEntry frame = _frameLedger[retainedIndex];
                RenderOutputTargetDescriptor target = request.Target with
                {
                    TargetGeneration = 1UL,
                    DisplayWidth = width,
                    DisplayHeight = height,
                    InternalWidth = width,
                    InternalHeight = height,
                    FormatCompatibilityKey = formatKey,
                    SampleCount = Math.Max(left.SampleCount, 1u),
                    ViewMask = 0x3u,
                    ExternalImageSlot = frame.LeftExternalImageSlot,
                };
                _outputLedger[_outputLedgerCount++] = new OpenXrSmokeOutputLedgerEntry
                {
                    RetainedIndex = retainedIndex,
                    ManifestFrameId = frame.OutputManifestFrameId,
                    OutputId = request.OutputId,
                    ViewFamilyId = request.ViewFamilyId,
                    OutputKind = EFrameOutputKind.OpenXREyeSubmit.ToString(),
                    ViewKind = EVrOutputViewKind.LeftEye.ToString(),
                    OutputClass = ERenderOutputClass.XrCritical.ToString(),
                    Name = "OpenXR true single-pass stereo",
                    PipelineName = "DefaultRenderPipeline",
                    TargetClass = ERenderOutputTargetClass.RuntimeExternalImage.ToString(),
                    StableTargetId = target.StableTargetId,
                    TargetGeneration = target.TargetGeneration,
                    DisplayWidth = width,
                    DisplayHeight = height,
                    InternalWidth = width,
                    InternalHeight = height,
                    LayerCount = 2u,
                    ViewMask = 0x3u,
                    ExternalImageSlot = frame.LeftExternalImageSlot,
                    TargetCompatibilityKey = target.CompatibilityKey,
                    Active = true,
                    Rendered = frame.ProjectionLayerSubmitted,
                    SceneRendered = frame.ProjectionLayerSubmitted,
                    RenderPhaseSceneRendered = frame.ProjectionLayerSubmitted,
                    Due = true,
                    Skipped = false,
                    WorkDisposition = ERenderOutputWorkDisposition.FreshRender.ToString(),
                    PolicyAuthorized = true,
                    CommandCount = 1,
                };
            }
        }

        public void Dispose()
        {
            if (_subscribedApi is not null)
            {
                _subscribedApi.SmokeFrameCompleted -= RecordSmokeFrame;
                _subscribedApi = null;
            }

            if (_installed)
            {
                Engine.Time.Timer.UpdateFrame -= Update;
                _installed = false;
            }
        }

        private void Update()
        {
            if (_shutdownRequested)
                return;

            if (_failures.Count > 0)
            {
                RequestShutdown(ExitStartupFailure, "Configuration failure.");
                return;
            }

            OpenXRAPI? api = Engine.VRState.OpenXRApi;
            EnsureSmokeFrameSubscription(api);
            long totalTargetFrames = (long)_warmupFrames + _targetFrames;
            if (api is not null && (_sessionExitRequested || api.SmokeCompletedFrameCount >= totalTargetFrames))
            {
                _targetReached = true;
                _exitCode = ExitSummaryFailure;
                if (!_sessionExitRequested)
                {
                    _sessionExitRequested = true;
                    _sessionExitDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5);
                    api.RequestSmokeSessionExit();
                    EngineDebug.Out($"[OpenXRSmoke] Target OpenXR frame count reached: completed={api.SmokeCompletedFrameCount}/{totalTargetFrames}, warmup={_warmupFrames}, retained={_frameLedgerCount}/{_targetFrames}, submitted={api.SmokeSubmittedFrameCount}, noLayer={api.SmokeNoLayerFrameCount}. Requested OpenXR session exit.");
                    return;
                }

                if (api.SmokeTeardownCompleted)
                {
                    RequestShutdown(ExitSummaryFailure, $"OpenXR smoke drain complete. Completed={api.SmokeCompletedFrameCount}/{totalTargetFrames}.");
                    return;
                }

                if (DateTimeOffset.UtcNow >= _sessionExitDeadlineUtc)
                {
                    if (api.IsSessionRunning)
                        _warnings.Add("OpenXR session was still marked running when the smoke drain window expired; engine teardown continued.");
                    if (!api.SmokeTeardownCompleted)
                        _warnings.Add("OpenXR teardown was still pending when the smoke drain window expired; engine teardown continued.");

                    RequestShutdown(ExitSummaryFailure, $"OpenXR smoke drain complete. Completed={api.SmokeCompletedFrameCount}/{totalTargetFrames}.");
                }
                return;
            }

            if (_stopwatch.Elapsed <= _timeout)
                return;

            long completed = api?.SmokeCompletedFrameCount ?? 0;
            long submitted = api?.SmokeSubmittedFrameCount ?? 0;
            long noLayer = api?.SmokeNoLayerFrameCount ?? 0;
            _failures.Add($"Timed out after {_timeout.TotalSeconds:F0}s waiting for OpenXR smoke frames. Completed={completed}, Submitted={submitted}, NoLayer={noLayer}, Warmup={_warmupFrames}, RetainedTarget={_targetFrames}, TotalTarget={totalTargetFrames}.");
            RequestShutdown(ExitFrameTimeout, "Timed out waiting for OpenXR smoke frames.");
        }

        private void EnsureSmokeFrameSubscription(OpenXRAPI? api)
        {
            if (ReferenceEquals(api, _subscribedApi))
                return;

            if (_subscribedApi is not null)
                _subscribedApi.SmokeFrameCompleted -= RecordSmokeFrame;

            _subscribedApi = api;
            if (api is null)
                return;

            _lastObservedSubmittedFrames = api.SmokeSubmittedFrameCount;
            _lastObservedNoLayerFrames = api.SmokeNoLayerFrameCount;
            _lastLeftAcquireCount = api.GetSmokeEyeAcquireCount(0);
            _lastRightAcquireCount = api.GetSmokeEyeAcquireCount(1);
            _lastLeftWaitCount = api.GetSmokeEyeWaitCount(0);
            _lastRightWaitCount = api.GetSmokeEyeWaitCount(1);
            _lastLeftPublishCount = api.GetSmokeEyePublishCount(0);
            _lastRightPublishCount = api.GetSmokeEyePublishCount(1);
            _lastLeftReleaseCount = api.GetSmokeEyeReleaseCount(0);
            _lastRightReleaseCount = api.GetSmokeEyeReleaseCount(1);
            _lastStrictSequentialFallbackAttemptCount = api.StrictSinglePassStereoSequentialFallbackAttemptCount;
            api.SmokeFrameCompleted += RecordSmokeFrame;
        }

        private void RecordSmokeFrame(long completedFrames, long submittedFrames, long noLayerFrames)
        {
            OpenXRAPI? api = _subscribedApi;
            if (api is null)
                return;

            bool projectionLayerSubmitted = submittedFrames > _lastObservedSubmittedFrames &&
                noLayerFrames == _lastObservedNoLayerFrames;
            long leftAcquireCount = api.GetSmokeEyeAcquireCount(0);
            long rightAcquireCount = api.GetSmokeEyeAcquireCount(1);
            long leftWaitCount = api.GetSmokeEyeWaitCount(0);
            long rightWaitCount = api.GetSmokeEyeWaitCount(1);
            long leftPublishCount = api.GetSmokeEyePublishCount(0);
            long rightPublishCount = api.GetSmokeEyePublishCount(1);
            long leftReleaseCount = api.GetSmokeEyeReleaseCount(0);
            long rightReleaseCount = api.GetSmokeEyeReleaseCount(1);
            long strictFallbackCount = api.StrictSinglePassStereoSequentialFallbackAttemptCount;
            long leftAcquireDelta = leftAcquireCount - _lastLeftAcquireCount;
            long rightAcquireDelta = rightAcquireCount - _lastRightAcquireCount;
            long leftWaitDelta = leftWaitCount - _lastLeftWaitCount;
            long rightWaitDelta = rightWaitCount - _lastRightWaitCount;
            long leftPublishDelta = leftPublishCount - _lastLeftPublishCount;
            long rightPublishDelta = rightPublishCount - _lastRightPublishCount;
            long leftReleaseDelta = leftReleaseCount - _lastLeftReleaseCount;
            long rightReleaseDelta = rightReleaseCount - _lastRightReleaseCount;
            long strictFallbackDelta = strictFallbackCount - _lastStrictSequentialFallbackAttemptCount;
            _lastObservedSubmittedFrames = submittedFrames;
            _lastObservedNoLayerFrames = noLayerFrames;
            _lastLeftAcquireCount = leftAcquireCount;
            _lastRightAcquireCount = rightAcquireCount;
            _lastLeftWaitCount = leftWaitCount;
            _lastRightWaitCount = rightWaitCount;
            _lastLeftPublishCount = leftPublishCount;
            _lastRightPublishCount = rightPublishCount;
            _lastLeftReleaseCount = leftReleaseCount;
            _lastRightReleaseCount = rightReleaseCount;
            _lastStrictSequentialFallbackAttemptCount = strictFallbackCount;

            if (completedFrames <= _warmupFrames)
                return;

            lock (_ledgerLock)
            {
                if (_frameLedgerCount >= _frameLedger.Length)
                    return;

                int retiredResourceCount =
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorPoolCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorSetCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredCommandBufferCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredQueryPoolCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredBufferViewCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredPipelineCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredFramebufferCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredBufferCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredBufferMemoryCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredImageCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredImageViewCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredSamplerCount +
                    Engine.Rendering.Stats.Vulkan.VulkanRetiredImageMemoryCount;
                Engine.Rendering.Stats.FrameOutputManifestSnapshot outputManifest =
                    Engine.Rendering.Stats.FrameOutputs.LastManifest;
                Engine.Rendering.Stats.FrameOutputWorkSnapshot outputWork = outputManifest.Work;

                _frameLedger[_frameLedgerCount] = new OpenXrSmokeFrameLedgerEntry
                {
                    RetainedIndex = _frameLedgerCount,
                    CompletedFrameCount = completedFrames,
                    SubmittedFrameCount = submittedFrames,
                    NoLayerFrameCount = noLayerFrames,
                    ProjectionLayerSubmitted = projectionLayerSubmitted,
                    EndFrameResult = api.SmokeLastEndFrameResult,
                    EndFrameLayerCount = api.SmokeLastEndFrameLayerCount,
                    RenderFrameId = Engine.Rendering.State.RenderFrameId,
                    ElapsedMilliseconds = _stopwatch.Elapsed.TotalMilliseconds,
                    LeftAcquireCount = leftAcquireCount,
                    RightAcquireCount = rightAcquireCount,
                    LeftAcquireDelta = leftAcquireDelta,
                    RightAcquireDelta = rightAcquireDelta,
                    LeftWaitCount = leftWaitCount,
                    RightWaitCount = rightWaitCount,
                    LeftWaitDelta = leftWaitDelta,
                    RightWaitDelta = rightWaitDelta,
                    LeftPublishCount = leftPublishCount,
                    RightPublishCount = rightPublishCount,
                    LeftPublishDelta = leftPublishDelta,
                    RightPublishDelta = rightPublishDelta,
                    LeftReleaseCount = leftReleaseCount,
                    RightReleaseCount = rightReleaseCount,
                    LeftReleaseDelta = leftReleaseDelta,
                    RightReleaseDelta = rightReleaseDelta,
                    LeftExternalImageSlot = api.GetSmokeEyeLastImageSlot(0),
                    RightExternalImageSlot = api.GetSmokeEyeLastImageSlot(1),
                    StrictSequentialFallbackAttemptCount = strictFallbackCount,
                    StrictSequentialFallbackAttemptDelta = strictFallbackDelta,
                    FrameTotalMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanFrameTotalMs,
                    FrameGpuMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs,
                    FrameWaitFenceMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanFrameWaitFenceMs,
                    FrameRecordMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanFrameRecordCommandBufferMs,
                    FrameSubmitMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanFrameSubmitMs,
                    FramePresentMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanFramePresentMs,
                    ValidationErrorCount = Engine.Rendering.Stats.Vulkan.VulkanValidationErrorCount,
                    DeviceLocalAllocationCount = Engine.Rendering.Stats.Vulkan.VulkanDeviceLocalAllocationCount,
                    DeviceLocalAllocatedBytes = Engine.Rendering.Stats.Vulkan.VulkanDeviceLocalAllocatedBytes,
                    UploadAllocationCount = Engine.Rendering.Stats.Vulkan.VulkanUploadAllocationCount,
                    UploadAllocatedBytes = Engine.Rendering.Stats.Vulkan.VulkanUploadAllocatedBytes,
                    DescriptorPoolCreateCount = Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolCreateCount,
                    DescriptorPoolDestroyCount = Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolDestroyCount,
                    DescriptorPoolResetCount = Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolResetCount,
                    ResourcePlanReplacementCount = Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements,
                    CommandBufferCleanReuseCount = Engine.Rendering.Stats.Vulkan.VulkanCommandBufferCleanReuseCount,
                    CommandBufferRecordCount = Engine.Rendering.Stats.Vulkan.VulkanCommandBufferRecordCount,
                    PrimaryCommandBufferReuseCount = Engine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersReused,
                    PrimaryCommandBufferRecordCount = Engine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersRecorded,
                    GlobalFallbackInvalidationCount = Engine.Rendering.Stats.Vulkan.VulkanGlobalFallbackInvalidations,
                    RetiredResourceCount = retiredResourceCount,
                    LifetimeLiveResourceCount = Engine.Rendering.Stats.Vulkan.VulkanLifetimeLiveResourceCount,
                    TrackedDescriptorSetCount = Engine.Rendering.Stats.Vulkan.VulkanTrackedDescriptorSetCount,
                    QueueSubmitCount = Engine.Rendering.Stats.Vulkan.VulkanQueueSubmitCount,
                    SubmissionRejectionCount = outputWork.SubmissionRejectionCount,
                    GlobalInFlightWaitCount = outputWork.GlobalInFlightWaitCount,
                    ForceFlushCount = outputWork.ForceFlushCount,
                    UnapprovedPolicyEventCount = outputWork.UnapprovedPolicyEventCount,
                    PlannerPruneCount = outputWork.PlannerPruneCount,
                    OutputManifestFrameId = outputManifest.FrameId,
                    OutputWorkloadIdentityHash = outputManifest.WorkloadIdentityHash,
                    OutputRequestCount = outputWork.OutputRequestCount,
                    MirrorMode = outputManifest.MirrorMode.ToString(),
                    VrActive = outputManifest.VrActive,
                    SceneSwapchainWriterCount = Engine.Rendering.Stats.Vulkan.VulkanSceneSwapchainWriters,
                    SwapchainWriteCount = Engine.Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount,
                    MissingSceneSwapchainWriteCount = Engine.Rendering.Stats.Vulkan.VulkanMissingSceneSwapchainWriteFrames,
                    CpuOcclusionTested = OcclusionTelemetry.CpuTested,
                    CpuOcclusionCulled = OcclusionTelemetry.CpuCulled,
                    CpuOcclusionPassesActive = OcclusionTelemetry.CpuPassesActive,
                    CpuOcclusionQueriesSubmitted = OcclusionTelemetry.CpuQuerySubmittedTotal,
                    CpuOcclusionQueriesResolved = OcclusionTelemetry.CpuQueryResolvedTotal,
                    CpuOcclusionPendingQueries = OcclusionTelemetry.CpuPendingQueries,
                    CpuOcclusionForcedVisible = OcclusionTelemetry.CpuForcedVisibleTotal,
                    CpuOcclusionMaxQueryAge = OcclusionTelemetry.CpuQueryLatencyMaxFrames,
                    CpuOcclusionViewScope = (int)OcclusionTelemetry.CpuActiveViewScope,
                };

                Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = outputManifest.Outputs;
                if (outputs.Length > MaxOutputSnapshotsPerFrame)
                    _outputLedgerOverflow = true;
                int outputCount = Math.Min(outputs.Length, MaxOutputSnapshotsPerFrame);
                for (int outputIndex = 0; outputIndex < outputCount; outputIndex++)
                {
                    if (_outputLedgerCount >= _outputLedger.Length)
                    {
                        _outputLedgerOverflow = true;
                        break;
                    }

                    Engine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[outputIndex];
                    RenderOutputTargetDescriptor target = output.Request.Target;
                    _outputLedger[_outputLedgerCount++] = new OpenXrSmokeOutputLedgerEntry
                    {
                        RetainedIndex = _frameLedgerCount,
                        ManifestFrameId = outputManifest.FrameId,
                        OutputId = output.Request.OutputId,
                        ViewFamilyId = output.Request.ViewFamilyId,
                        OutputKind = output.OutputKind.ToString(),
                        ViewKind = output.ViewKind.ToString(),
                        OutputClass = output.Request.OutputClass.ToString(),
                        Name = output.Name,
                        PipelineName = output.PipelineName,
                        TargetClass = target.TargetClass.ToString(),
                        StableTargetId = target.StableTargetId,
                        TargetGeneration = target.TargetGeneration,
                        DisplayWidth = target.DisplayWidth,
                        DisplayHeight = target.DisplayHeight,
                        InternalWidth = target.InternalWidth,
                        InternalHeight = target.InternalHeight,
                        LayerCount = ResolveRequiredLayerCount(target.ViewMask),
                        ViewMask = target.ViewMask,
                        ExternalImageSlot = target.ExternalImageSlot,
                        TargetCompatibilityKey = target.CompatibilityKey,
                        Active = output.Active,
                        Rendered = output.Rendered,
                        SceneRendered = output.SceneRendered,
                        RenderPhaseSceneRendered = output.RenderPhaseSceneRendered,
                        Due = output.Due,
                        Skipped = output.Skipped,
                        WorkDisposition = output.WorkDisposition.ToString(),
                        ContentAgeFrames = output.ContentAgeFrames,
                        PolicyAuthorized = output.PolicyAuthorized,
                        CommandCount = output.CommandCount,
                        DrawCalls = output.DrawCalls,
                        SubmitCpuMilliseconds = output.SubmitCpuMs,
                        PresentCpuMilliseconds = output.PresentCpuMs,
                    };
                }

                Span<CpuOcclusionViewTelemetrySnapshot> viewSnapshots =
                    stackalloc CpuOcclusionViewTelemetrySnapshot[MaxOcclusionViewSnapshotsPerFrame];
                int requiredViewSnapshotCount = OcclusionTelemetry.CpuViewSnapshotCount;
                int viewSnapshotCount = OcclusionTelemetry.CopyLastActiveCpuViewSnapshots(viewSnapshots);
                if (requiredViewSnapshotCount > viewSnapshots.Length)
                    _occlusionViewLedgerOverflow = true;
                for (int snapshotIndex = 0; snapshotIndex < viewSnapshotCount; snapshotIndex++)
                {
                    if (_occlusionViewLedgerCount >= _occlusionViewLedger.Length)
                    {
                        _occlusionViewLedgerOverflow = true;
                        break;
                    }

                    CpuOcclusionViewTelemetrySnapshot snapshot = viewSnapshots[snapshotIndex];
                    OcclusionViewKey key = snapshot.ViewKey;
                    _occlusionViewLedger[_occlusionViewLedgerCount++] = new OpenXrSmokeOcclusionViewLedgerEntry
                    {
                        RetainedIndex = _frameLedgerCount,
                        RenderPass = key.RenderPass,
                        Scope = key.Scope,
                        ViewId = key.ViewId,
                        PipelineInstanceId = key.PipelineInstanceId,
                        PovId = key.PovId,
                        CoverageMask = key.CoverageMask,
                        RequiredCoverageMask = key.RequiredCoverageMask,
                        DeclaredViewCount = key.DeclaredViewCount,
                        ResourceGeneration = key.ResourceGeneration,
                        CandidateCount = snapshot.CandidateCount,
                        Submissions = snapshot.Submissions,
                        Resolutions = snapshot.Resolutions,
                        Skips = snapshot.Skips,
                        BudgetSkipped = snapshot.BudgetSkipped,
                        ForcedVisible = snapshot.ForcedVisible,
                        CurrentResultAgeFrames = snapshot.CurrentResultAgeFrames,
                        MaxResultAgeFrames = snapshot.MaxResultAgeFrames,
                        RecoveryLatencyFrames = snapshot.RecoveryLatencyFrames,
                    };
                }
                _frameLedgerCount++;
            }
        }

        private static uint ResolveRequiredLayerCount(uint viewMask)
        {
            if (viewMask == 0u)
                return 1u;

            uint layers = 0u;
            while (viewMask != 0u)
            {
                layers++;
                viewMask >>= 1;
            }
            return layers;
        }

        private int ResolveExitCode(OpenXrSmokeSummary summary, List<string> validationFailures)
        {
            if (!_targetReached)
                return _exitCode == ExitSuccess ? ExitStartupFailure : _exitCode;

            if (!summary.TeardownCompleted)
                return ExitTeardownFailure;

            return validationFailures.Count == 0 && _failures.Count == 0 && summary.Failures.Length == 0
                ? ExitSuccess
                : ExitSummaryFailure;
        }

        private List<string> ValidateSummary(OpenXrSmokeSummary summary)
        {
            var failures = new List<string>();
            long totalTargetFrames = (long)_warmupFrames + _targetFrames;
            if (!_targetReached)
            {
                failures.Add($"Engine exited before reaching OpenXR smoke target. Completed={summary.SubmittedFrameCount + summary.NoLayerFrameCount}, Warmup={_warmupFrames}, RetainedTarget={_targetFrames}, TotalTarget={totalTargetFrames}.");
                return failures;
            }

            if (summary.SchemaVersion != OpenXrSmokeSummary.CurrentSchemaVersion)
                failures.Add($"Unexpected OpenXR smoke schemaVersion={summary.SchemaVersion}.");
            if (!summary.InstanceCreated)
                failures.Add("OpenXR instance was not created.");
            if (!summary.SystemFound)
                failures.Add("OpenXR system was not found.");
            if (!summary.SessionCreated)
                failures.Add("OpenXR graphics-bound session was not created.");
            if (!summary.ReferenceSpaceCreated)
                failures.Add("OpenXR reference space was not created.");
            if (!summary.SwapchainsCreated || summary.Swapchains.Length < 2)
                failures.Add($"Expected two OpenXR swapchains; observed {summary.Swapchains.Length}.");
            long completedFrameCount = summary.SubmittedFrameCount + summary.NoLayerFrameCount;
            if (completedFrameCount < totalTargetFrames)
                failures.Add($"CompletedOpenXrFrameCount={completedFrameCount}, Submitted={summary.SubmittedFrameCount}, NoLayer={summary.NoLayerFrameCount}, Warmup={_warmupFrames}, RetainedTarget={_targetFrames}, TotalTarget={totalTargetFrames}.");
            if (summary.WarmupFrameCount != _warmupFrames)
                failures.Add($"Smoke summary warmupFrameCount={summary.WarmupFrameCount}, expected {_warmupFrames}.");
            if (summary.RetainedFrameCount != _targetFrames || summary.FrameLedger.Length != _targetFrames)
                failures.Add($"Smoke frame ledger retained {summary.FrameLedger.Length} entries, expected exactly {_targetFrames} after {_warmupFrames} warmup frames.");
            if (summary.OcclusionViewLedgerOverflow)
                failures.Add($"Smoke per-view occlusion ledger exceeded {MaxOcclusionViewSnapshotsPerFrame} keys per retained frame.");
            if (summary.OutputLedgerOverflow)
                failures.Add($"Smoke output ledger exceeded {MaxOutputSnapshotsPerFrame} outputs per retained frame.");
            if (summary.StrictSinglePassStereoSequentialFallbackAttemptCount != 0)
                failures.Add($"Strict SinglePassStereo attempted sequential fallback {summary.StrictSinglePassStereoSequentialFallbackAttemptCount} time(s).");
            for (int i = 0; i < summary.FrameLedger.Length; i++)
            {
                OpenXrSmokeFrameLedgerEntry entry = summary.FrameLedger[i];
                if (entry.RetainedIndex != i)
                    failures.Add($"Smoke frame ledger index mismatch at array index {i}: retainedIndex={entry.RetainedIndex}.");
                if (entry.CompletedFrameCount <= _warmupFrames)
                    failures.Add($"Smoke frame ledger entry {i} belongs to warmup: completedFrameCount={entry.CompletedFrameCount} warmup={_warmupFrames}.");
            }
            if (summary.EndFrameFailureCount > 0)
                failures.Add($"xrEndFrame failure count was {summary.EndFrameFailureCount}.");
            if (summary.LocatedViewCount < 2)
                failures.Add($"Expected at least two located OpenXR views; observed {summary.LocatedViewCount}.");
            if (!summary.PredictedViewPoseCached)
                failures.Add("Predicted OpenXR view pose cache was not updated.");
            if (!summary.LateViewPoseCached)
                failures.Add("Late OpenXR view pose cache was not updated.");
            if (!summary.PredictedActionPoseCacheUpdated)
                failures.Add("Predicted OpenXR action pose cache was not updated.");
            if (!summary.LateActionPoseCacheUpdated)
                failures.Add("Late OpenXR action pose cache was not updated.");
            if (summary.SubmittedFrameCount > 0)
            {
                int submittedTarget = (int)Math.Min(summary.SubmittedFrameCount, int.MaxValue);
                if (!HasTwoEyesAtLeast(summary.PerEyeAcquireCounts, submittedTarget))
                    failures.Add("Per-eye xrAcquireSwapchainImage counts did not reach submitted frame count.");
                if (!HasTwoEyesAtLeast(summary.PerEyeWaitCounts, submittedTarget))
                    failures.Add("Per-eye xrWaitSwapchainImage counts did not reach submitted frame count.");
                if (!HasTwoEyesAtLeast(summary.PerEyePublishCounts, submittedTarget))
                    failures.Add("Per-eye published-image counts did not reach submitted frame count.");
                if (!HasTwoEyesAtLeast(summary.PerEyeReleaseCounts, submittedTarget))
                    failures.Add("Per-eye xrReleaseSwapchainImage counts did not reach submitted frame count.");
                bool expectsDesktopMirrorComposition =
                    Engine.Rendering.Settings.RenderWindowsWhileInVR &&
                    Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures;
                if (expectsDesktopMirrorComposition && !summary.DesktopMirrorComposed)
                    failures.Add("OpenXR desktop mirror composition was not observed during rendered-layer smoke frames.");
            }
            else if (summary.NoLayerFrameCount <= 0)
            {
                failures.Add("OpenXR completed no rendered-layer frames and no no-layer frames.");
            }
            if (!summary.TeardownCompleted)
                failures.Add("OpenXR teardown did not complete before smoke summary was written.");

            return failures;
        }

        private static bool HasTwoEyesAtLeast(long[] counts, int targetFrames)
            => counts.Length >= 2 && counts[0] >= targetFrames && counts[1] >= targetFrames;

        private void RequestShutdown(int exitCode, string reason)
        {
            _shutdownRequested = true;
            _exitCode = exitCode;
            Environment.ExitCode = exitCode;
            EngineDebug.Out($"[OpenXRSmoke] {reason} Requesting engine shutdown.");
            EditorImGuiUI.ForceAllowWindowCloseForShutdown();
            Engine.ShutDown();
        }

        private string ResolveSummaryPath(string? logDirectory)
        {
            if (!string.IsNullOrWhiteSpace(_summaryPath))
                return Path.GetFullPath(_summaryPath);

            string directory = string.IsNullOrWhiteSpace(logDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "Build", "Logs")
                : logDirectory;
            return Path.Combine(directory, "openxr-smoke-summary.json");
        }

        private static string? TryGetLogDirectory()
        {
            try
            {
                return EngineDebug.EnsureLogRunDirectory();
            }
            catch
            {
                return null;
            }
        }

        private static int ReadIntOption(string[] args, string optionName, string environmentName, int defaultValue)
        {
            string? raw = ReadStringOption(args, optionName, environmentName);
            return int.TryParse(raw, out int value) ? value : defaultValue;
        }

        private static string? ReadStringOption(string[] args, string optionName, string environmentName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : null;

                string prefix = optionName + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arg[prefix.Length..];
            }

            string? raw = Environment.GetEnvironmentVariable(environmentName);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
    }
}
