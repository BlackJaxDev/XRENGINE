using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System.Diagnostics;
using EngineDebug = XREngine.Debug;
using XREngine;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Vulkan;
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
        private const int MaxOcclusionEvidenceSnapshotsPerFrame = CpuOcclusionValidationEvidence.MaximumEntriesPerFrame;
        private const string ExternalValidationAllowlistEnvironmentVariable = "XRE_VULKAN_EXTERNAL_VALIDATION_ALLOWLIST";
        private const string DesktopFinalCaptureStage = "15_FinalOutput";
        private static readonly string[] RequiredPhase524bCaptureStages =
        [
            "07_Velocity",
            "07b_VelocityFBO",
            "08_BloomMip0",
            "09_BloomMip1",
            "09b_BloomMip2",
            "09c_BloomMip3",
            "10_BloomMip4",
            "11_TemporalColorInput",
            "11b_CurrentDepth",
            "11c_HistoryDepth",
            "12_PostProcessOutput",
            "13_FinalPostProcessOutput",
            "13b_PreTsrHistoryColor",
            "13c_MonoTsrReference",
            "14_TsrOutput",
            "14b_TsrHistoryColor",
        ];
        private static readonly string[] TemporalScenarioCaptureStages =
        [
            "07_Velocity",
            "09_BloomMip1",
            "13c_MonoTsrReference",
            "14_TsrOutput",
        ];
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
        private readonly OpenXrSmokeOcclusionEvidenceLedgerEntry[] _occlusionEvidenceLedger;
        private readonly CpuOcclusionValidationEvidenceSnapshot[] _occlusionEvidenceScratch;
        private readonly Engine.Rendering.Stats.FrameOutputEntrySnapshot[] _currentOutputScratch;
        private OpenXrSmokeCaptureLedgerEntry[] _strictSpsBoundaryCaptureLedger = [];
        private readonly object _ledgerLock = new();
        private readonly List<string> _failures = [];
        private readonly List<string> _warnings = [];
        private OpenXRAPI? _subscribedApi;
        private OpenXrSmokeSummary? _preTeardownSmokeSummary;
        private int _frameLedgerCount;
        private int _occlusionViewLedgerCount;
        private int _outputLedgerCount;
        private int _occlusionEvidenceLedgerCount;
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
        private bool _validationLayersObserved;
        private bool _synchronizationValidationObserved;
        private string _vulkanRenderTargetModeEffective = string.Empty;
        private string _vulkanDiagnosticPresetEffective = string.Empty;
        private string _antiAliasingModeEffective = string.Empty;
        private string _occlusionCullingModeEffective = string.Empty;
        private string _mirrorModeEffective = string.Empty;
        private double _tsrResolutionScaleRequested = 1.0;
        private int _exitCode = ExitStartupFailure;
        private DateTimeOffset? _retainedCohortStartedAtUtc;

        private OpenXrSmokeRunController(int targetFrames, int warmupFrames, TimeSpan timeout, string? summaryPath)
        {
            _targetFrames = targetFrames;
            _warmupFrames = warmupFrames;
            _timeout = timeout;
            _summaryPath = summaryPath;
            _frameLedger = new OpenXrSmokeFrameLedgerEntry[targetFrames];
            _occlusionViewLedger = new OpenXrSmokeOcclusionViewLedgerEntry[targetFrames * MaxOcclusionViewSnapshotsPerFrame];
            _outputLedger = new OpenXrSmokeOutputLedgerEntry[targetFrames * MaxOutputSnapshotsPerFrame];
            _occlusionEvidenceLedger = new OpenXrSmokeOcclusionEvidenceLedgerEntry[targetFrames * MaxOcclusionEvidenceSnapshotsPerFrame];
            _occlusionEvidenceScratch = new CpuOcclusionValidationEvidenceSnapshot[MaxOcclusionEvidenceSnapshotsPerFrame];
            _currentOutputScratch = new Engine.Rendering.Stats.FrameOutputEntrySnapshot[MaxOutputSnapshotsPerFrame];
            Phase524bTemporalStateDiagnostics.Reset();
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

            string? requestedScale = Environment.GetEnvironmentVariable(
                XREngineEnvironmentVariables.VulkanPhase524bTsrResolutionScale);
            if (!string.IsNullOrWhiteSpace(requestedScale))
            {
                if (!double.TryParse(
                        requestedScale,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _tsrResolutionScaleRequested) ||
                    !double.IsFinite(_tsrResolutionScaleRequested) ||
                    _tsrResolutionScaleRequested < 0.5 ||
                    _tsrResolutionScaleRequested > 1.0)
                {
                    _failures.Add($"Invalid Phase 5.2.4b TSR resolution scale '{requestedScale}'; expected 0.5..1.0.");
                    _tsrResolutionScaleRequested = 1.0;
                }
            }

            EngineDebug.Out($"[OpenXRSmoke] Enabled warmupFrames={_warmupFrames}, retainedFrames={_targetFrames}, timeout={_timeout.TotalSeconds:F0}s, summary='{_summaryPath ?? "<log directory>"}'.");
        }

        public void Install()
        {
            if (!Enabled || _installed)
                return;

            Engine.Rendering.Settings.TsrRenderScale = (float)_tsrResolutionScaleRequested;
            bool injectDesktopRejection = ReadBooleanEnvironmentVariable(
                XREngineEnvironmentVariables.VulkanPhase524bInjectDesktopRejection);
            VulkanRenderer.ResetPhase524bDesktopRejectionEvidence(injectDesktopRejection);

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
            OpenXRAPI? summaryApi = Engine.VRState.OpenXRApi ?? _subscribedApi;
            OpenXrSmokeSummary summary = summaryApi?.CreateSmokeSummary(logDirectory)
                ?? _preTeardownSmokeSummary
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
                summary.RetainedCohortStartedAtUtc = _retainedCohortStartedAtUtc;
                summary.FrameLedger = new OpenXrSmokeFrameLedgerEntry[_frameLedgerCount];
                Array.Copy(_frameLedger, summary.FrameLedger, _frameLedgerCount);
                summary.OcclusionViewLedger = new OpenXrSmokeOcclusionViewLedgerEntry[_occlusionViewLedgerCount];
                Array.Copy(_occlusionViewLedger, summary.OcclusionViewLedger, _occlusionViewLedgerCount);
                summary.OcclusionViewLedgerOverflow = _occlusionViewLedgerOverflow;
                summary.OcclusionEvidenceLedger = new OpenXrSmokeOcclusionEvidenceLedgerEntry[_occlusionEvidenceLedgerCount];
                Array.Copy(
                    _occlusionEvidenceLedger,
                    summary.OcclusionEvidenceLedger,
                    _occlusionEvidenceLedgerCount);
                summary.OcclusionEvidenceOverflowCount = CpuOcclusionValidationEvidence.OverflowCount;
                summary.OutputLedger = new OpenXrSmokeOutputLedgerEntry[_outputLedgerCount];
                Array.Copy(_outputLedger, summary.OutputLedger, _outputLedgerCount);
                summary.OutputLedgerOverflow = _outputLedgerOverflow;
            }

            PopulateEffectiveConfiguration(summary);
            CaptureDefaultPipelineArtifacts(summary, _strictSpsBoundaryCaptureLedger);
            summary.TemporalStateLedger = Phase524bTemporalStateDiagnostics.Capture();
            summary.TemporalStateLedgerOverflowCount = Phase524bTemporalStateDiagnostics.OverflowCount;

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

            float requestedTsrScale = (float)_tsrResolutionScaleRequested;
            if (Math.Abs(Engine.Rendering.Settings.TsrRenderScale - requestedTsrScale) > float.Epsilon)
                Engine.Rendering.Settings.TsrRenderScale = requestedTsrScale;

            OpenXRAPI? currentApi = Engine.VRState.OpenXRApi;
            if (currentApi is not null)
                EnsureSmokeFrameSubscription(currentApi);

            // OpenXR teardown can clear the globally published API before the
            // update loop observes its terminal diagnostics. Keep the subscribed
            // instance alive until FinishAfterRun captures the completed summary.
            OpenXRAPI? api = currentApi ?? _subscribedApi;
            long totalTargetFrames = (long)_warmupFrames + _targetFrames;
            if (api is not null && (_sessionExitRequested || api.SmokeCompletedFrameCount >= totalTargetFrames))
            {
                _targetReached = true;
                _exitCode = ExitSummaryFailure;
                if (!_sessionExitRequested)
                {
                    _preTeardownSmokeSummary ??= api.CreateSmokeSummary(TryGetLogDirectory());
                    _strictSpsBoundaryCaptureLedger = api.GetStrictSpsBoundaryCaptureLedger();
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

                _retainedCohortStartedAtUtc ??= DateTimeOffset.UtcNow;
                if (_strictSpsBoundaryCaptureLedger.Length == 0)
                    _strictSpsBoundaryCaptureLedger = api.GetStrictSpsBoundaryCaptureLedger();

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
                Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = outputManifest.Outputs;
                ulong renderFrameId = api.SmokeLastRenderedFrameId;
                if (renderFrameId == 0UL)
                {
                    renderFrameId = outputManifest.FrameId != 0UL
                        ? outputManifest.FrameId
                        : Engine.Rendering.State.RenderFrameId;
                }
                int currentOutputRequiredCount = Engine.Rendering.Stats.FrameOutputs.CopyCurrentOutputs(_currentOutputScratch);
                int currentOutputCount = Math.Min(currentOutputRequiredCount, _currentOutputScratch.Length);
                if (currentOutputRequiredCount > _currentOutputScratch.Length)
                    _outputLedgerOverflow = true;
                int currentOpenXrOutputCount = CountCurrentOpenXrOutputs(
                    _currentOutputScratch,
                    currentOutputCount,
                    renderFrameId);
                CountCurrentOpenXrOutputEvents(
                    _currentOutputScratch,
                    currentOutputCount,
                    renderFrameId,
                    out int currentOutputEvents,
                    out int currentCollectEvents,
                    out int currentSwapEvents,
                    out int currentRenderEvents,
                    out int currentSubmitEvents,
                    out int currentOverlayEvents,
                    out int currentPresentEvents);
                bool validationLayersEnabled = Engine.Rendering.Stats.Vulkan.VulkanValidationLayersEnabled;
                bool synchronizationValidationEnabled = Engine.Rendering.Stats.Vulkan.VulkanSynchronizationValidationEnabled;
                int validationErrorCount = Engine.Rendering.Stats.Vulkan.VulkanValidationErrorCount;
                int queueSubmitCount = Engine.Rendering.Stats.Vulkan.VulkanQueueSubmitCount;
                int presentAttemptCount = Engine.Rendering.Stats.Vulkan.VulkanPresentAttemptCount;
                int presentAcceptedCount = Engine.Rendering.Stats.Vulkan.VulkanPresentAcceptedCount;
                int lastPresentResult = Engine.Rendering.Stats.Vulkan.VulkanLastPresentResult;
                bool lifetimeValidationPassed = validationLayersEnabled &&
                    validationErrorCount == 0 &&
                    outputWork.SubmissionRejectionCount == 0;
                bool desktopFinalWriteObserved =
                    (Engine.Rendering.Stats.Vulkan.VulkanSceneSwapchainWriters > 0 ||
                     Engine.Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount > 0) &&
                    Engine.Rendering.Stats.Vulkan.VulkanMissingSceneSwapchainWriteFrames == 0;
                bool desktopPresentPhaseObserved = HasDesktopPresentPhase(outputs);
                bool desktopPresentAccepted = presentAttemptCount > 0 && presentAcceptedCount == presentAttemptCount;
                int plannerStateCount = CountDistinctPlannerStates(
                    outputs,
                    _currentOutputScratch,
                    currentOutputCount,
                    renderFrameId);
                int commandVariantCount = CountDistinctCommandVariants(
                    outputs,
                    _currentOutputScratch,
                    currentOutputCount,
                    renderFrameId);
                ulong resourcePlanGeneration = ResolveMaximumResourcePlanGeneration(
                    outputs,
                    _currentOutputScratch,
                    currentOutputCount,
                    renderFrameId);
                ulong commandGeneration = ResolveMaximumCommandGeneration(
                    outputs,
                    _currentOutputScratch,
                    currentOutputCount,
                    renderFrameId);

                _validationLayersObserved |= validationLayersEnabled;
                _synchronizationValidationObserved |= synchronizationValidationEnabled;
                _vulkanRenderTargetModeEffective = AbstractRenderer.Current is VulkanRenderer vulkanRenderer
                    ? vulkanRenderer.EffectiveRenderTargetMode.ToString()
                    : Engine.EffectiveSettings.VulkanRenderTargetMode.ToString();
                _vulkanDiagnosticPresetEffective = Engine.EffectiveSettings.VulkanDiagnosticPreset.ToString();
                _occlusionCullingModeEffective = Engine.EffectiveSettings.GpuOcclusionCullingMode.ToString();
                _mirrorModeEffective = outputManifest.MirrorMode.ToString();
                _antiAliasingModeEffective = Engine.EffectiveSettings.AntiAliasingMode.ToString();

                _frameLedger[_frameLedgerCount] = new OpenXrSmokeFrameLedgerEntry
                {
                    RetainedIndex = _frameLedgerCount,
                    CompletedFrameCount = completedFrames,
                    SubmittedFrameCount = submittedFrames,
                    NoLayerFrameCount = noLayerFrames,
                    ProjectionLayerSubmitted = projectionLayerSubmitted,
                    EndFrameResult = api.SmokeLastEndFrameResult,
                    EndFrameLayerCount = api.SmokeLastEndFrameLayerCount,
                    RenderFrameId = renderFrameId,
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
                    ValidationErrorCount = validationErrorCount,
                    ValidationLayersEnabled = validationLayersEnabled,
                    SynchronizationValidationEnabled = synchronizationValidationEnabled,
                    LifetimeValidationEnabled = validationLayersEnabled,
                    LifetimeValidationPassed = lifetimeValidationPassed,
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
                    LifetimePendingRetirementCount = Engine.Rendering.Stats.Vulkan.VulkanLifetimePendingRetirementCount,
                    LifetimeOldestPendingRetirementAgeMilliseconds = Engine.Rendering.Stats.Vulkan.VulkanLifetimeOldestPendingRetirementAgeMilliseconds,
                    MeshFrameDataArenaChunkCount = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkCount,
                    MeshFrameDataMappedBytes = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytes,
                    MeshFrameDataReservedBytes = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytes,
                    MeshFrameDataReservationCount = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationCount,
                    MeshFrameDataGeneration = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataGeneration,
                    MeshFrameDataRecordingLeases = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataRecordingLeases,
                    MeshFrameDataCachedLeases = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataCachedLeases,
                    MeshFrameDataSubmittedLeases = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataSubmittedLeases,
                    MeshFrameDataActiveGenerationCount = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataActiveGenerationCount,
                    MeshFrameDataLeaseRetainedGenerationCount = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseRetainedGenerationCount,
                    MeshDescriptorAllocationVariants = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariants,
                    MeshDescriptorPools = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPools,
                    MeshDescriptorAllocatedSets = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocatedSets,
                    MeshDescriptorReservedSets = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorReservedSets,
                    MeshFrameDataArenaChunkHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkHighWater,
                    MeshFrameDataMappedBytesHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytesHighWater,
                    MeshFrameDataReservedBytesHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytesHighWater,
                    MeshFrameDataReservationHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationHighWater,
                    MeshFrameDataLeaseHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseHighWater,
                    MeshDescriptorAllocationVariantHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariantHighWater,
                    MeshDescriptorPoolHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPoolHighWater,
                    MeshDescriptorSetHighWater = Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorSetHighWater,
                    QueueSubmitCount = queueSubmitCount,
                    SubmitCompleted = projectionLayerSubmitted &&
                        queueSubmitCount > 0 &&
                        HasSubmittedVrEyes(_currentOutputScratch, currentOutputCount, renderFrameId),
                    SubmissionRejectionCount = outputWork.SubmissionRejectionCount,
                    GlobalInFlightWaitCount = outputWork.GlobalInFlightWaitCount,
                    ForceFlushCount = outputWork.ForceFlushCount,
                    UnapprovedPolicyEventCount = outputWork.UnapprovedPolicyEventCount,
                    PlannerPruneCount = outputWork.PlannerPruneCount,
                    OutputManifestFrameId = outputManifest.FrameId,
                    OutputWorkloadIdentityHash = outputManifest.WorkloadIdentityHash,
                    OutputRequestCount = outputWork.OutputRequestCount + CountNewCurrentOpenXrViewFamilies(
                        outputs,
                        _currentOutputScratch,
                        currentOutputCount,
                        renderFrameId),
                    OutputEventCount = outputWork.OutputEventCount + currentOutputEvents,
                    CollectEventCount = outputWork.CollectEventCount + currentCollectEvents,
                    SwapEventCount = outputWork.SwapEventCount + currentSwapEvents,
                    RenderEventCount = outputWork.RenderEventCount + currentRenderEvents,
                    SubmitEventCount = outputWork.SubmitEventCount + currentSubmitEvents,
                    OverlayEventCount = outputWork.OverlayEventCount + currentOverlayEvents,
                    PresentEventCount = outputWork.PresentEventCount + currentPresentEvents,
                    PlannerStateCount = plannerStateCount,
                    CommandVariantCount = commandVariantCount,
                    ResourcePlanGeneration = resourcePlanGeneration,
                    CommandGeneration = commandGeneration,
                    MirrorMode = outputManifest.MirrorMode.ToString(),
                    VrActive = outputManifest.VrActive,
                    SceneSwapchainWriterCount = Engine.Rendering.Stats.Vulkan.VulkanSceneSwapchainWriters,
                    SwapchainWriteCount = Engine.Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount,
                    MissingSceneSwapchainWriteCount = Engine.Rendering.Stats.Vulkan.VulkanMissingSceneSwapchainWriteFrames,
                    DesktopFinalWriteObserved = desktopFinalWriteObserved,
                    DesktopPresentObserved = desktopPresentPhaseObserved && presentAttemptCount > 0,
                    DesktopPresentAttemptCount = presentAttemptCount,
                    DesktopPresentAccepted = desktopPresentAccepted,
                    DesktopPresentResult = lastPresentResult.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

                if (outputs.Length + currentOpenXrOutputCount > MaxOutputSnapshotsPerFrame)
                    _outputLedgerOverflow = true;
                int outputsWrittenThisFrame = 0;
                int outputCount = Math.Min(outputs.Length, MaxOutputSnapshotsPerFrame);
                for (int outputIndex = 0; outputIndex < outputCount; outputIndex++)
                {
                    AppendOutputLedgerEntry(
                        in outputs[outputIndex],
                        outputManifest.FrameId,
                        lifetimeValidationPassed,
                        presentAttemptCount,
                        desktopPresentAccepted,
                        lastPresentResult,
                        ref outputsWrittenThisFrame);
                }

                for (int outputIndex = 0;
                     outputIndex < currentOutputCount && outputsWrittenThisFrame < MaxOutputSnapshotsPerFrame;
                     outputIndex++)
                {
                    ref readonly Engine.Rendering.Stats.FrameOutputEntrySnapshot output =
                        ref _currentOutputScratch[outputIndex];
                    if (!IsCurrentOpenXrOutput(in output, renderFrameId))
                        continue;

                    AppendOutputLedgerEntry(
                        in output,
                        outputManifest.FrameId,
                        lifetimeValidationPassed,
                        presentAttemptCount,
                        desktopPresentAccepted,
                        lastPresentResult,
                        ref outputsWrittenThisFrame);
                }

                Span<CpuOcclusionViewTelemetrySnapshot> viewSnapshots =
                    stackalloc CpuOcclusionViewTelemetrySnapshot[MaxOcclusionViewSnapshotsPerFrame];
                int requiredViewSnapshotCount = OcclusionTelemetry.CpuActiveViewSnapshotCount;
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
                        OutputId = key.OutputId,
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
                        RecoveryStarts = snapshot.RecoveryStarts,
                        RecoveryCompletions = snapshot.RecoveryCompletions,
                        CurrentRecoveryAgeFrames = snapshot.CurrentRecoveryAgeFrames,
                        MaxRecoveryAgeFrames = snapshot.MaxRecoveryAgeFrames,
                        CurrentResultAgeFrames = snapshot.CurrentResultAgeFrames,
                        MaxResultAgeFrames = snapshot.MaxResultAgeFrames,
                        RecoveryLatencyFrames = snapshot.RecoveryLatencyFrames,
                    };
                }

                Span<CpuOcclusionValidationEvidenceSnapshot> evidenceSnapshots = _occlusionEvidenceScratch;
                Span<ulong> evidenceFrameIds = stackalloc ulong[MaxOutputSnapshotsPerFrame + 2];
                int evidenceFrameIdCount = 0;

                AddUniqueEvidenceFrameId(evidenceFrameIds, ref evidenceFrameIdCount, outputManifest.FrameId);
                AddUniqueEvidenceFrameId(evidenceFrameIds, ref evidenceFrameIdCount, renderFrameId);
                for (int outputIndex = 0; outputIndex < outputs.Length; outputIndex++)
                    AddUniqueEvidenceFrameId(evidenceFrameIds, ref evidenceFrameIdCount, outputs[outputIndex].FrameId);
                for (int outputIndex = 0; outputIndex < currentOutputCount; outputIndex++)
                    AddUniqueEvidenceFrameId(evidenceFrameIds, ref evidenceFrameIdCount, _currentOutputScratch[outputIndex].FrameId);

                for (int frameIndex = 0; frameIndex < evidenceFrameIdCount; frameIndex++)
                {
                    int evidenceSnapshotCount = CpuOcclusionValidationEvidence.CopyFrame(
                        evidenceFrameIds[frameIndex],
                        evidenceSnapshots);
                    for (int evidenceIndex = 0; evidenceIndex < evidenceSnapshotCount; evidenceIndex++)
                    {
                        if (_occlusionEvidenceLedgerCount >= _occlusionEvidenceLedger.Length)
                            break;

                        CpuOcclusionValidationEvidenceSnapshot evidence = evidenceSnapshots[evidenceIndex];
                        OcclusionViewKey key = evidence.ViewKey;
                        _occlusionEvidenceLedger[_occlusionEvidenceLedgerCount++] =
                            new OpenXrSmokeOcclusionEvidenceLedgerEntry
                            {
                                RetainedIndex = _frameLedgerCount,
                                RenderFrameId = evidence.FrameId,
                                RenderPass = key.RenderPass,
                                Scope = key.Scope.ToString(),
                                ViewId = key.ViewId,
                                PipelineInstanceId = key.PipelineInstanceId,
                                OutputId = key.OutputId,
                                PovId = key.PovId,
                                CoverageMask = key.CoverageMask,
                                RequiredCoverageMask = key.RequiredCoverageMask,
                                DeclaredViewCount = key.DeclaredViewCount,
                                ResourceGeneration = key.ResourceGeneration,
                                StableQueryKey = evidence.StableQueryKey,
                                Role = evidence.Role.ToString(),
                                Mode = evidence.Mode.ToString(),
                                CandidateObserved = evidence.CandidateObserved,
                                Rendered = evidence.Rendered,
                                Culled = evidence.Culled,
                                OcclusionProofCoverageMask = evidence.OcclusionProofCoverageMask,
                                HasDecision = evidence.HasDecision,
                                Decision = evidence.Decision.ToString(),
                            };
                    }
                }
                _frameLedgerCount++;
            }
        }

        private static void AddUniqueEvidenceFrameId(
            Span<ulong> frameIds,
            ref int frameIdCount,
            ulong frameId)
        {
            if (frameId == 0UL)
                return;
            for (int i = 0; i < frameIdCount; i++)
            {
                if (frameIds[i] == frameId)
                    return;
            }
            if (frameIdCount < frameIds.Length)
                frameIds[frameIdCount++] = frameId;
        }

        private void AppendOutputLedgerEntry(
            in Engine.Rendering.Stats.FrameOutputEntrySnapshot output,
            ulong manifestFrameId,
            bool lifetimeValidationPassed,
            int presentAttemptCount,
            bool desktopPresentAccepted,
            int lastPresentResult,
            ref int outputsWrittenThisFrame)
        {
            if (_outputLedgerCount >= _outputLedger.Length ||
                outputsWrittenThisFrame >= MaxOutputSnapshotsPerFrame)
            {
                _outputLedgerOverflow = true;
                return;
            }

            RenderOutputTargetDescriptor target = output.Request.Target;
            _outputLedger[_outputLedgerCount++] = new OpenXrSmokeOutputLedgerEntry
            {
                RetainedIndex = _frameLedgerCount,
                ManifestFrameId = manifestFrameId,
                RenderFrameId = output.FrameId,
                OutputId = output.Request.OutputId,
                ViewFamilyId = output.Request.ViewFamilyId,
                OutputKind = output.OutputKind.ToString(),
                ViewKind = output.ViewKind.ToString(),
                OutputClass = output.Request.OutputClass.ToString(),
                Name = output.Name,
                PipelineName = output.PipelineName,
                PipelineInstanceId = output.PipelineInstanceId,
                ResourcePlanGeneration = unchecked((ulong)Math.Max(0, output.ResourcePlanGeneration)),
                CommandGeneration = output.CommandGeneration,
                AntiAliasingMode = output.AntiAliasingMode,
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
                LifetimeValidationPassed = lifetimeValidationPassed,
                SubmitObserved = output.SubmitObserved,
                FinalWriteObserved =
                    target.TargetClass.ToString() == "DesktopSwapchain" && output.RenderPhaseSceneRendered,
                PresentObserved = output.PresentObserved && presentAttemptCount > 0,
                PresentAccepted = output.PresentObserved && desktopPresentAccepted,
                PresentResult = output.PresentObserved
                    ? lastPresentResult.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null,
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
            outputsWrittenThisFrame++;
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

        private static bool IsCurrentOpenXrOutput(
            in Engine.Rendering.Stats.FrameOutputEntrySnapshot output,
            ulong renderFrameId)
            => output.FrameId == renderFrameId &&
                (output.OutputKind == EFrameOutputKind.OpenXREyeSubmit ||
                 output.Request.Target.TargetClass == ERenderOutputTargetClass.RuntimeExternalImage);

        private static int CountCurrentOpenXrOutputs(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs,
            int outputCount,
            ulong renderFrameId)
        {
            int count = 0;
            for (int i = 0; i < outputCount; i++)
            {
                if (IsCurrentOpenXrOutput(in outputs[i], renderFrameId))
                    count++;
            }
            return count;
        }

        private static int CountNewCurrentOpenXrViewFamilies(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] snapshottedOutputs,
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] currentOutputs,
            int currentOutputCount,
            ulong renderFrameId)
        {
            int count = 0;
            for (int i = 0; i < currentOutputCount; i++)
            {
                ref readonly Engine.Rendering.Stats.FrameOutputEntrySnapshot candidate = ref currentOutputs[i];
                if (!IsCurrentOpenXrOutput(in candidate, renderFrameId))
                    continue;

                ulong familyId = candidate.Request.ViewFamilyId != 0
                    ? candidate.Request.ViewFamilyId
                    : candidate.Request.OutputId;
                bool alreadyCounted = false;
                for (int previous = 0; previous < i; previous++)
                {
                    if (IsCurrentOpenXrOutput(in currentOutputs[previous], renderFrameId) &&
                        (currentOutputs[previous].Request.ViewFamilyId != 0
                            ? currentOutputs[previous].Request.ViewFamilyId
                            : currentOutputs[previous].Request.OutputId) == familyId)
                    {
                        alreadyCounted = true;
                        break;
                    }
                }
                if (alreadyCounted)
                    continue;

                for (int previous = 0; previous < snapshottedOutputs.Length; previous++)
                {
                    if (snapshottedOutputs[previous].FrameId == renderFrameId &&
                        (snapshottedOutputs[previous].Request.ViewFamilyId != 0
                            ? snapshottedOutputs[previous].Request.ViewFamilyId
                            : snapshottedOutputs[previous].Request.OutputId) == familyId)
                    {
                        alreadyCounted = true;
                        break;
                    }
                }
                if (!alreadyCounted)
                    count++;
            }
            return count;
        }

        private static void CountCurrentOpenXrOutputEvents(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs,
            int outputCount,
            ulong renderFrameId,
            out int outputEvents,
            out int collectEvents,
            out int swapEvents,
            out int renderEvents,
            out int submitEvents,
            out int overlayEvents,
            out int presentEvents)
        {
            outputEvents = 0;
            collectEvents = 0;
            swapEvents = 0;
            renderEvents = 0;
            submitEvents = 0;
            overlayEvents = 0;
            presentEvents = 0;
            for (int i = 0; i < outputCount; i++)
            {
                ref readonly Engine.Rendering.Stats.FrameOutputEntrySnapshot output = ref outputs[i];
                if (!IsCurrentOpenXrOutput(in output, renderFrameId))
                    continue;
                outputEvents += output.OutputEventCount;
                collectEvents += output.CollectEventCount;
                swapEvents += output.SwapEventCount;
                renderEvents += output.RenderEventCount;
                submitEvents += output.SubmitEventCount;
                overlayEvents += output.OverlayEventCount;
                presentEvents += output.PresentEventCount;
            }
        }

        private static bool HasSubmittedVrEyes(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs,
            int outputCount,
            ulong renderFrameId)
        {
            bool left = false;
            bool right = false;
            for (int i = 0; i < outputCount; i++)
            {
                ref readonly Engine.Rendering.Stats.FrameOutputEntrySnapshot output = ref outputs[i];
                if (!IsCurrentOpenXrOutput(in output, renderFrameId))
                    continue;
                if (!output.SubmitObserved || output.OutputKind != EFrameOutputKind.OpenXREyeSubmit)
                    continue;

                left |= output.ViewKind == EVrOutputViewKind.LeftEye;
                right |= output.ViewKind == EVrOutputViewKind.RightEye;
            }
            return left && right;
        }

        private static bool HasDesktopPresentPhase(Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs)
        {
            for (int i = 0; i < outputs.Length; i++)
            {
                Engine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
                if (output.PresentObserved && output.OutputKind == EFrameOutputKind.Present)
                    return true;
            }
            return false;
        }

        private static int CountDistinctPlannerStates(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] finalizedOutputs,
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] currentOutputs,
            int currentOutputCount,
            ulong renderFrameId)
        {
            Span<PlannerStateIdentity> identities =
                stackalloc PlannerStateIdentity[MaxOutputSnapshotsPerFrame * 2];
            int count = 0;
            for (int i = 0; i < finalizedOutputs.Length; i++)
            {
                if (!TryAddPlannerState(in finalizedOutputs[i], identities, ref count))
                    return identities.Length + 1;
            }
            for (int i = 0; i < currentOutputCount; i++)
            {
                if (!IsCurrentOpenXrOutput(in currentOutputs[i], renderFrameId))
                    continue;
                if (!TryAddPlannerState(in currentOutputs[i], identities, ref count))
                    return identities.Length + 1;
            }
            return count;
        }

        private static bool TryAddPlannerState(
            in Engine.Rendering.Stats.FrameOutputEntrySnapshot output,
            Span<PlannerStateIdentity> identities,
            ref int count)
        {
            if (output.PipelineInstanceId <= 0)
                return true;

            var identity = new PlannerStateIdentity(
                output.PipelineInstanceId,
                output.ResourcePlanGeneration);
            for (int i = 0; i < count; i++)
            {
                if (identities[i] == identity)
                    return true;
            }
            if (count >= identities.Length)
                return false;

            identities[count++] = identity;
            return true;
        }

        private static int CountDistinctCommandVariants(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] finalizedOutputs,
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] currentOutputs,
            int currentOutputCount,
            ulong renderFrameId)
        {
            Span<CommandVariantIdentity> identities =
                stackalloc CommandVariantIdentity[MaxOutputSnapshotsPerFrame * 2];
            int count = 0;
            for (int i = 0; i < finalizedOutputs.Length; i++)
            {
                if (!TryAddCommandVariant(in finalizedOutputs[i], identities, ref count))
                    return identities.Length + 1;
            }
            for (int i = 0; i < currentOutputCount; i++)
            {
                if (!IsCurrentOpenXrOutput(in currentOutputs[i], renderFrameId))
                    continue;
                if (!TryAddCommandVariant(in currentOutputs[i], identities, ref count))
                    return identities.Length + 1;
            }
            return count;
        }

        private static bool TryAddCommandVariant(
            in Engine.Rendering.Stats.FrameOutputEntrySnapshot output,
            Span<CommandVariantIdentity> identities,
            ref int count)
        {
            if (output.PipelineInstanceId <= 0 || output.CommandGeneration == 0UL)
                return true;

            var identity = new CommandVariantIdentity(
                output.PipelineInstanceId,
                output.CommandGeneration,
                output.Request.Target.StableTargetId,
                output.Request.Target.ExternalImageSlot);
            for (int i = 0; i < count; i++)
            {
                if (identities[i] == identity)
                    return true;
            }
            if (count >= identities.Length)
                return false;

            identities[count++] = identity;
            return true;
        }

        private static ulong ResolveMaximumResourcePlanGeneration(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] finalizedOutputs,
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] currentOutputs,
            int currentOutputCount,
            ulong renderFrameId)
        {
            ulong generation = 0UL;
            for (int i = 0; i < finalizedOutputs.Length; i++)
            {
                int candidate = finalizedOutputs[i].ResourcePlanGeneration;
                if (candidate > 0)
                    generation = Math.Max(generation, unchecked((ulong)candidate));
            }
            for (int i = 0; i < currentOutputCount; i++)
            {
                if (!IsCurrentOpenXrOutput(in currentOutputs[i], renderFrameId))
                    continue;
                int candidate = currentOutputs[i].ResourcePlanGeneration;
                if (candidate > 0)
                    generation = Math.Max(generation, unchecked((ulong)candidate));
            }
            return generation;
        }

        private static ulong ResolveMaximumCommandGeneration(
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] finalizedOutputs,
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] currentOutputs,
            int currentOutputCount,
            ulong renderFrameId)
        {
            ulong generation = 0UL;
            for (int i = 0; i < finalizedOutputs.Length; i++)
                generation = Math.Max(generation, finalizedOutputs[i].CommandGeneration);
            for (int i = 0; i < currentOutputCount; i++)
            {
                if (!IsCurrentOpenXrOutput(in currentOutputs[i], renderFrameId))
                    continue;
                generation = Math.Max(generation, currentOutputs[i].CommandGeneration);
            }
            return generation;
        }

        private readonly record struct PlannerStateIdentity(
            int PipelineInstanceId,
            int ResourcePlanGeneration);

        private readonly record struct CommandVariantIdentity(
            int PipelineInstanceId,
            ulong CommandGeneration,
            ulong StableTargetId,
            int ExternalImageSlot);

        private void PopulateEffectiveConfiguration(OpenXrSmokeSummary summary)
        {
            summary.VulkanRenderTargetModeRequested =
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VkRenderTargetMode) ?? string.Empty;
            summary.VulkanRenderTargetModeEffective = _vulkanRenderTargetModeEffective;
            summary.VulkanDiagnosticPresetRequested =
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDiagnosticPreset) ?? string.Empty;
            summary.VulkanDiagnosticPresetEffective = _vulkanDiagnosticPresetEffective;
            summary.VulkanValidationLayersEffective = _validationLayersObserved;
            summary.VulkanSynchronizationValidationEffective = _synchronizationValidationObserved;
            summary.VulkanValidationLayers = _validationLayersObserved
                ? ["VK_LAYER_KHRONOS_validation"]
                : [];
            summary.VulkanValidationFeatures = _synchronizationValidationObserved
                ? ["SynchronizationValidation"]
                : [];
            summary.ExternallyOwnedValidationAllowlist = ResolveExternalValidationAllowlist();
            summary.AntiAliasingModeEffective = string.IsNullOrWhiteSpace(_antiAliasingModeEffective)
                ? Engine.EffectiveSettings.AntiAliasingMode.ToString()
                : _antiAliasingModeEffective;
            summary.TsrResolutionScaleRequested = _tsrResolutionScaleRequested;
            summary.TsrResolutionScaleEffective = _subscribedApi?.SmokeEffectiveTsrRenderScale
                ?? Engine.Rendering.Settings.TsrRenderScale;
            summary.DesktopRejectionEvidence = VulkanRenderer.CapturePhase524bDesktopRejectionEvidence();
            summary.OcclusionCullingModeRequested =
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OcclusionCullingMode) ?? string.Empty;
            summary.OcclusionCullingModeEffective = _occlusionCullingModeEffective;
            summary.MirrorModeEffective = _mirrorModeEffective;
        }

        private static string[] ResolveExternalValidationAllowlist()
        {
            string? raw = Environment.GetEnvironmentVariable(ExternalValidationAllowlistEnvironmentVariable);
            return string.IsNullOrWhiteSpace(raw)
                ? []
                : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static void CaptureDefaultPipelineArtifacts(
            OpenXrSmokeSummary summary,
            OpenXrSmokeCaptureLedgerEntry[] strictSpsBoundaryCaptures)
        {
            summary.DefaultPipelineCaptureEnabled = ReadBooleanEnvironmentVariable(
                XREngineEnvironmentVariables.CaptureDefaultPipelineFbo);
            summary.DefaultPipelineCaptureSkipFrames = int.TryParse(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CaptureDefaultPipelineSkipFrames),
                out int skipFrames)
                    ? Math.Max(0, skipFrames)
                    : 0;
            summary.DefaultPipelineCaptureOutputDirectory = ResolveCaptureOutputDirectory();
            summary.RequiredCaptureStages = [.. RequiredPhase524bCaptureStages];
            summary.DesktopFinalCaptureStage = DesktopFinalCaptureStage;
            summary.TemporalScenarioCaptureStages = [.. TemporalScenarioCaptureStages];
            summary.TemporalScenarioMatrix = CreateTemporalScenarioMatrix();

            if (!summary.DefaultPipelineCaptureEnabled ||
                string.IsNullOrWhiteSpace(summary.DefaultPipelineCaptureOutputDirectory))
            {
                summary.CaptureLedger = [];
                summary.TemporalScenarioCaptureLedger = [];
                return;
            }

            var captures = new List<OpenXrSmokeCaptureLedgerEntry>(
                (RequiredPhase524bCaptureStages.Length * 2) + 3 + strictSpsBoundaryCaptures.Length);
            for (int stageIndex = 0; stageIndex < RequiredPhase524bCaptureStages.Length; stageIndex++)
            {
                string stage = RequiredPhase524bCaptureStages[stageIndex];
                for (int layerIndex = 0; layerIndex < 2; layerIndex++)
                {
                    string path = Path.Combine(
                        summary.DefaultPipelineCaptureOutputDirectory,
                        $"DefaultPipelineSps_{stage}_layer{layerIndex}.png");
                    if (!File.Exists(path))
                        continue;

                    FileInfo info = new(path);
                    var entry = new OpenXrSmokeCaptureLedgerEntry
                    {
                        PipelineName = "DefaultPipelineSps",
                        OutputRole = "StrictSinglePassStereo",
                        Stage = stage,
                        LayerIndex = layerIndex,
                        ExpectedLayerCount = 2,
                        ViewMask = 0x3u,
                        AntiAliasingMode = "Tsr",
                        Path = info.FullName,
                        LengthBytes = info.Length,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                    };
                    PopulateCaptureMetrics(entry);
                    captures.Add(entry);
                }
            }

            string desktopPath = Path.Combine(
                summary.DefaultPipelineCaptureOutputDirectory,
                $"DefaultPipelineDesktop_{DesktopFinalCaptureStage}_layer0.png");
            if (File.Exists(desktopPath))
            {
                FileInfo info = new(desktopPath);
                var entry = new OpenXrSmokeCaptureLedgerEntry
                {
                    PipelineName = "DefaultPipelineDesktop",
                    OutputRole = "DesktopFullIndependent",
                    Stage = DesktopFinalCaptureStage,
                    LayerIndex = 0,
                    ExpectedLayerCount = 1,
                    ViewMask = 0u,
                    AntiAliasingMode = "Tsr",
                    ViewKind = "Motion0",
                    Path = info.FullName,
                    LengthBytes = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                };
                PopulateCaptureMetrics(entry);
                captures.Add(entry);
            }

            for (int motionIndex = 1; motionIndex < 3; motionIndex++)
            {
                string motionPath = Path.Combine(
                    summary.DefaultPipelineCaptureOutputDirectory,
                    $"DefaultPipelineDesktop_{DesktopFinalCaptureStage}_motion{motionIndex}_layer0.png");
                if (!File.Exists(motionPath))
                    continue;

                FileInfo info = new(motionPath);
                var entry = new OpenXrSmokeCaptureLedgerEntry
                {
                    PipelineName = "DefaultPipelineDesktop",
                    OutputRole = "DesktopMotionSequence",
                    Stage = DesktopFinalCaptureStage,
                    LayerIndex = 0,
                    ExpectedLayerCount = 1,
                    ViewMask = 0u,
                    AntiAliasingMode = "Tsr",
                    ViewKind = $"Motion{motionIndex}",
                    Path = info.FullName,
                    LengthBytes = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                };
                PopulateCaptureMetrics(entry);
                captures.Add(entry);
            }

            captures.AddRange(strictSpsBoundaryCaptures);
            summary.CaptureLedger = [.. captures];
            summary.TemporalScenarioCaptureLedger = CaptureTemporalScenarioArtifacts(
                summary.DefaultPipelineCaptureOutputDirectory,
                summary.TemporalScenarioMatrix);
        }

        private static OpenXrSmokeTemporalScenarioDefinition[] CreateTemporalScenarioMatrix()
        {
            ReadOnlySpan<Phase524bTemporalSampleDefinition> definitions =
                Phase524bTemporalScenarioDiagnostics.Definitions;
            var result = new OpenXrSmokeTemporalScenarioDefinition[definitions.Length];
            for (int i = 0; i < definitions.Length; i++)
            {
                ref readonly Phase524bTemporalSampleDefinition definition = ref definitions[i];
                result[i] = new OpenXrSmokeTemporalScenarioDefinition
                {
                    Scenario = definition.Scenario.ToString(),
                    Sample = definition.Sample.ToString(),
                    VelocityOracle = definition.VelocityOracle.ToString(),
                    CaptureStartFrame = definition.CaptureStartFrame,
                    CaptureEndFrame = definition.CaptureEndFrame,
                    RequiresTemporalConvergence = definition.RequiresTemporalConvergence,
                    IsDisocclusionBaseline = definition.IsDisocclusionBaseline,
                    IsDisocclusionResult = definition.IsDisocclusionResult,
                };
            }
            return result;
        }

        private static OpenXrSmokeCaptureLedgerEntry[] CaptureTemporalScenarioArtifacts(
            string captureDirectory,
            OpenXrSmokeTemporalScenarioDefinition[] scenarioMatrix)
        {
            var captures = new List<OpenXrSmokeCaptureLedgerEntry>(
                scenarioMatrix.Length * TemporalScenarioCaptureStages.Length * 2);
            for (int sampleIndex = 0; sampleIndex < scenarioMatrix.Length; sampleIndex++)
            {
                OpenXrSmokeTemporalScenarioDefinition definition = scenarioMatrix[sampleIndex];
                for (int stageIndex = 0; stageIndex < TemporalScenarioCaptureStages.Length; stageIndex++)
                {
                    string stage = TemporalScenarioCaptureStages[stageIndex];
                    for (int layerIndex = 0; layerIndex < 2; layerIndex++)
                    {
                        string path = Path.Combine(
                            captureDirectory,
                            $"DefaultPipelineSps_Temporal_{definition.Sample}_{stage}_layer{layerIndex}.png");
                        if (!File.Exists(path))
                            continue;

                        FileInfo info = new(path);
                        var entry = new OpenXrSmokeCaptureLedgerEntry
                        {
                            PipelineName = "DefaultPipelineSps",
                            OutputRole = "TemporalScenarioRenderedOutput",
                            Stage = stage,
                            LayerIndex = layerIndex,
                            ExpectedLayerCount = 2,
                            ViewMask = 0x3u,
                            AntiAliasingMode = "Tsr",
                            ViewKind = definition.Sample,
                            TemporalScenario = definition.Scenario,
                            TemporalSample = definition.Sample,
                            VelocityOracle = definition.VelocityOracle,
                            Path = info.FullName,
                            LengthBytes = info.Length,
                            LastWriteTimeUtc = info.LastWriteTimeUtc,
                        };
                        PopulateCaptureMetrics(entry);
                        captures.Add(entry);
                    }
                }
            }
            return [.. captures];
        }

        private static void PopulateCaptureMetrics(OpenXrSmokeCaptureLedgerEntry entry)
        {
            string metricsPath = entry.Path + ".metrics.json";
            if (!File.Exists(metricsPath))
                return;

            RenderedOutputCaptureMetrics? metrics = JsonConvert.DeserializeObject<RenderedOutputCaptureMetrics>(
                File.ReadAllText(metricsPath));
            if (metrics is null)
                return;

            entry.Width = metrics.Width;
            entry.Height = metrics.Height;
            entry.NonBlackPixelCount = metrics.NonBlackPixelCount;
            entry.NonBlackPixelRatio = metrics.NonBlackPixelRatio;
            entry.MaximumLuminance = metrics.MaximumLuminance;
            entry.LuminanceEnergy = metrics.LuminanceEnergy;
            entry.BloomCentroidX = metrics.BloomCentroidX;
            entry.BloomCentroidY = metrics.BloomCentroidY;
            entry.VelocityMeanX = metrics.VelocityMeanX;
            entry.VelocityMeanY = metrics.VelocityMeanY;
            entry.VelocityMeanMagnitude = metrics.VelocityMeanMagnitude;
            entry.VelocityMaxMagnitude = metrics.VelocityMaxMagnitude;
            entry.VelocityNonZeroSampleCount = metrics.VelocityNonZeroSampleCount;
            entry.EdgeMeanGradient = metrics.EdgeMeanGradient;
            entry.EdgeMaxGradient = metrics.EdgeMaxGradient;
            entry.TopBandRows = metrics.TopBandRows;
            entry.TopBandNonBlackPixelCount = metrics.TopBandNonBlackPixelCount;
            entry.TopBandNonBlackPixelRatio = metrics.TopBandNonBlackPixelRatio;
            entry.TopBandMaximumLuminance = metrics.TopBandMaximumLuminance;
            entry.TopBandMagentaPixelCount = metrics.TopBandMagentaPixelCount;
            entry.LuminanceFingerprintWidth = metrics.LuminanceFingerprintWidth;
            entry.LuminanceFingerprintHeight = metrics.LuminanceFingerprintHeight;
            entry.LuminanceFingerprint = metrics.LuminanceFingerprint;
            entry.VelocityMagnitudeFingerprintWidth = metrics.VelocityMagnitudeFingerprintWidth;
            entry.VelocityMagnitudeFingerprintHeight = metrics.VelocityMagnitudeFingerprintHeight;
            entry.VelocityMagnitudeFingerprint = metrics.VelocityMagnitudeFingerprint;
            entry.TemporalScenario = metrics.TemporalScenario;
            entry.TemporalSample = metrics.TemporalSample;
            entry.VelocityOracle = metrics.VelocityOracle;
            entry.TemporalSequenceFrame = metrics.TemporalSequenceFrame;
            entry.RenderFrameId = metrics.RenderFrameId;
            entry.Sha256 = metrics.CaptureSha256;
            entry.MetricsCapturePath = metrics.CapturePath;
            entry.MetricsCapturedAtUtc = metrics.CapturedAtUtc;
        }

        private static string ResolveCaptureOutputDirectory()
        {
            string? configured = Environment.GetEnvironmentVariable(
                XREngineEnvironmentVariables.CaptureDefaultPipelineOutputDirectory);
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);

            string? runRoot = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AgentValidationRunRoot);
            string root = string.IsNullOrWhiteSpace(runRoot)
                ? Path.Combine(Environment.CurrentDirectory, "Build", "_AgentValidation", "manual-default-pipeline-capture")
                : Path.GetFullPath(runRoot);
            return Path.Combine(root, "mcp-captures");
        }

        private static bool ReadBooleanEnvironmentVariable(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
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

            bool phase524bValidation = ReadBooleanEnvironmentVariable(
                XREngineEnvironmentVariables.VulkanPhase524bValidation);
            if (phase524bValidation)
            {
                failures.AddRange(OpenXrSmokePhase524bEvidenceValidator.ValidateDesktopRejection(
                    summary.DesktopRejectionEvidence,
                    required: ReadBooleanEnvironmentVariable(
                        XREngineEnvironmentVariables.VulkanPhase524bInjectDesktopRejection)));
                failures.AddRange(OpenXrSmokePhase524bEvidenceValidator.ValidateTsrResolutionCohort(
                    summary.TsrResolutionScaleRequested,
                    summary.TsrResolutionScaleEffective,
                    summary.OutputLedger,
                    expectSubNative: summary.TsrResolutionScaleRequested < 0.9999));
                failures.AddRange(OpenXrSmokePhase524bEvidenceValidator.ValidateTemporalScenarioMatrix(
                    summary.TemporalScenarioMatrix,
                    summary.TemporalScenarioCaptureStages,
                    summary.TemporalScenarioCaptureLedger));
            }

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
