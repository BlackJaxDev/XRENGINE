using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System.Diagnostics;
using EngineDebug = XREngine.Debug;
using XREngine;
using XREngine.Editor;
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
        private static readonly JsonSerializerSettings SmokeJsonSettings = new()
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = [new StringEnumConverter()]
        };

        private readonly int _targetFrames;
        private readonly TimeSpan _timeout;
        private readonly string? _summaryPath;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly List<string> _failures = [];
        private readonly List<string> _warnings = [];
        private bool _installed;
        private bool _targetReached;
        private bool _sessionExitRequested;
        private DateTimeOffset _sessionExitDeadlineUtc;
        private bool _shutdownRequested;
        private bool _finished;
        private int _exitCode = ExitStartupFailure;

        private OpenXrSmokeRunController(int targetFrames, TimeSpan timeout, string? summaryPath)
        {
            _targetFrames = targetFrames;
            _timeout = timeout;
            _summaryPath = summaryPath;
        }

        public bool Enabled => _targetFrames > 0;

        public static OpenXrSmokeRunController Parse(string[] args)
        {
            int targetFrames = ReadIntOption(args, "--smoke-frames", XREngineEnvironmentVariables.OpenXrSmokeFrames, 0);
            int timeoutSeconds = ReadIntOption(args, "--smoke-timeout-seconds", XREngineEnvironmentVariables.OpenXrSmokeTimeoutSeconds, DefaultTimeoutSeconds);
            string? summaryPath = ReadStringOption(args, "--openxr-smoke-summary", XREngineEnvironmentVariables.OpenXrSmokeSummary)
                ?? ReadStringOption(args, "--smoke-summary", XREngineEnvironmentVariables.SmokeSummary);

            return new OpenXrSmokeRunController(
                Math.Max(0, targetFrames),
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

            EngineDebug.Out($"[OpenXRSmoke] Enabled targetFrames={_targetFrames}, timeout={_timeout.TotalSeconds:F0}s, summary='{_summaryPath ?? "<log directory>"}'.");
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

        public void Dispose()
        {
            if (!_installed)
                return;

            Engine.Time.Timer.UpdateFrame -= Update;
            _installed = false;
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
            if (api is not null && (_sessionExitRequested || api.SmokeCompletedFrameCount >= _targetFrames))
            {
                _targetReached = true;
                _exitCode = ExitSummaryFailure;
                if (!_sessionExitRequested)
                {
                    _sessionExitRequested = true;
                    _sessionExitDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5);
                    api.RequestSmokeSessionExit();
                    EngineDebug.Out($"[OpenXRSmoke] Target OpenXR frame count reached: completed={api.SmokeCompletedFrameCount}/{_targetFrames}, submitted={api.SmokeSubmittedFrameCount}, noLayer={api.SmokeNoLayerFrameCount}. Requested OpenXR session exit.");
                    return;
                }

                if (api.SmokeTeardownCompleted)
                {
                    RequestShutdown(ExitSummaryFailure, $"OpenXR smoke drain complete. Completed={api.SmokeCompletedFrameCount}/{_targetFrames}.");
                    return;
                }

                if (DateTimeOffset.UtcNow >= _sessionExitDeadlineUtc)
                {
                    if (api.IsSessionRunning)
                        _warnings.Add("OpenXR session was still marked running when the smoke drain window expired; engine teardown continued.");
                    if (!api.SmokeTeardownCompleted)
                        _warnings.Add("OpenXR teardown was still pending when the smoke drain window expired; engine teardown continued.");

                    RequestShutdown(ExitSummaryFailure, $"OpenXR smoke drain complete. Completed={api.SmokeCompletedFrameCount}/{_targetFrames}.");
                }
                return;
            }

            if (_stopwatch.Elapsed <= _timeout)
                return;

            long completed = api?.SmokeCompletedFrameCount ?? 0;
            long submitted = api?.SmokeSubmittedFrameCount ?? 0;
            long noLayer = api?.SmokeNoLayerFrameCount ?? 0;
            _failures.Add($"Timed out after {_timeout.TotalSeconds:F0}s waiting for OpenXR smoke frames. Completed={completed}, Submitted={submitted}, NoLayer={noLayer}, Target={_targetFrames}.");
            RequestShutdown(ExitFrameTimeout, "Timed out waiting for OpenXR smoke frames.");
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
            if (!_targetReached)
            {
                failures.Add($"Engine exited before reaching OpenXR smoke target. Submitted={summary.SubmittedFrameCount}, Target={_targetFrames}.");
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
            if (completedFrameCount < _targetFrames)
                failures.Add($"CompletedOpenXrFrameCount={completedFrameCount}, Submitted={summary.SubmittedFrameCount}, NoLayer={summary.NoLayerFrameCount}, Target={_targetFrames}.");
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
