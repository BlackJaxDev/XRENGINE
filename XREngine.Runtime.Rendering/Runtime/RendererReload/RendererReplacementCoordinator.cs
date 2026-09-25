using System.Diagnostics;

namespace XREngine.Rendering;

/// <summary>
/// Coordinates all windows that use one backend through a legal renderer teardown,
/// registration swap, resource rehydration, first-frame acceptance, and rollback.
/// </summary>
public sealed class RendererReplacementCoordinator
{
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly object _statusSync = new();
    private readonly Dictionary<string, TimeSpan> _phaseDurations = new(StringComparer.Ordinal);
    private RendererReloadSnapshot _snapshot = RendererReloadSnapshot.Idle;
    private IDisposable? _activeDynamicRegistrationLease;
    private long _successfulReloads;
    private long _failedReloads;
    private long _lastGoodRollbacks;
    private long _unloadLeaks;

    public static RendererReplacementCoordinator Current { get; } = new();

    public event Action<RendererReloadSnapshot>? StatusChanged;

    public RendererReloadSnapshot Snapshot
    {
        get
        {
            lock (_statusSync)
                return _snapshot;
        }
    }

    public bool IsReloadInProgress
        => Snapshot.State is not RendererReloadState.Idle and
           not RendererReloadState.Failed and
           not RendererReloadState.FailedStopped;

    public Task<RendererReplacementResult> RestartCurrentGenerationAsync(
        RendererBackendId backendId,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken = default)
        => ReplaceAsync(backendId, candidate: null, firstFrameTimeout, cancellationToken);

    /// <summary>
    /// Restarts the current backend after changing configuration that cannot be changed
    /// while a renderer device exists. The supplied transaction owns its previous-value snapshot.
    /// </summary>
    public async Task<RendererReplacementResult> RestartCurrentGenerationWithConfigurationAsync(
        RendererBackendId backendId,
        IRendererReplacementConfiguration configuration,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RendererBackendRegistration registration =
                RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(backendId);
            XRWindow[] windows = [.. RuntimeEngine.Windows.Where(
                window => window.Renderer.BackendId == backendId)];
            if (windows.Length != 0 && windows.Length != RuntimeEngine.Windows.Count)
                return Fail(registration, RendererReloadFailureKind.ReloadBoundary,
                    "The shared Advanced preparation owner cannot be retired while another backend window remains active.");
            bool[] detached = new bool[windows.Length];
            bool[] candidateAttached = new bool[windows.Length];
            bool[] candidateCleanupFailed = new bool[windows.Length];
            bool configurationAttempted = false;
            bool teardownCompleted = false;
            RendererReloadFailureKind failureKind = RendererReloadFailureKind.Teardown;

            lock (_statusSync)
                _phaseDurations.Clear();
            Publish(registration, RendererReloadState.ReplacementRequested, "Renderer configuration restart requested.");
            if (RuntimeEngine.VRState.IsInVR)
                return Fail(registration, RendererReloadFailureKind.ReloadBoundary,
                    "Stop XR presentation before changing renderer configuration.");
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i].Renderer.IsDeviceLost ||
                    windows[i].Renderer.ShouldSkipNativeWindowDisposeForShutdown)
                    return Fail(registration, RendererReloadFailureKind.ReloadBoundary,
                        $"Window {i} has a lost device or abandoned native teardown; renderer configuration cannot be changed safely.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Publish(registration, RendererReloadState.Quiescing, "Quiescing backend work publication.");
                Publish(registration, RendererReloadState.DrainingGpu, "Waiting for the retiring backend GPU boundary.");
                RendererReloadFailureInjection.ThrowIfEnabled(
                    RendererReloadInjectedFailure.DeviceLoss, "configuration restart request");
                string? detachFailure = await MeasurePhaseAsync(
                    "teardown",
                    () => InvokeOnRenderThreadAsync(
                        () => DetachWindowsTracked(windows, detached, "renderer configuration restart"),
                        cancellationToken)).ConfigureAwait(false);
                if (detachFailure is not null)
                    return await RollBackConfigurationAsync(
                        registration, windows, detached, candidateAttached, candidateCleanupFailed, configuration,
                        configurationAttempted, RendererReloadFailureKind.Teardown, detachFailure,
                        initialDetachUncertain: true)
                        .ConfigureAwait(false);
                teardownCompleted = true;

                Publish(registration, RendererReloadState.DestroyingWrappers, "Retiring generation wrappers destroyed.");
                configurationAttempted = true;
                await InvokeOnRenderThreadAsync(
                    () => { configuration.ApplyAfterDetach(); return true; },
                    CancellationToken.None).ConfigureAwait(false);

                failureKind = RendererReloadFailureKind.CandidateInitialization;
                Publish(registration, RendererReloadState.InitializingCandidate, "Creating replacement renderers with the requested configuration.");
                string? attachFailure = await MeasurePhaseAsync(
                    "candidate-initialization",
                    () => InvokeOnRenderThreadAsync(
                        () => AttachWindowsTracked(windows, candidateAttached, candidateCleanupFailed, "renderer configuration restart"),
                        CancellationToken.None)).ConfigureAwait(false);
                if (attachFailure is not null)
                    return await RollBackConfigurationAsync(
                        registration, windows, detached, candidateAttached, candidateCleanupFailed, configuration,
                        configurationAttempted, failureKind, attachFailure).ConfigureAwait(false);

                Publish(registration, RendererReloadState.RehydratingResources, "Logical resources rebound; awaiting a valid frame.");
                if (windows.Length > 0)
                {
                    failureKind = RendererReloadFailureKind.FirstFrame;
                    Publish(registration, RendererReloadState.AwaitingFirstValidFrame, "Awaiting first valid frame.");
                    bool firstFrame = await WaitForFirstFramesAsync(
                        windows, registration.Metadata.Generation, firstFrameTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    if (RendererReloadFailureInjection.IsEnabled(RendererReloadInjectedFailure.FirstFrame))
                        firstFrame = false;
                    if (!firstFrame)
                        return await RollBackConfigurationAsync(
                            registration, windows, detached, candidateAttached, candidateCleanupFailed, configuration,
                            configurationAttempted, failureKind,
                            $"No valid frame was presented within {firstFrameTimeout.TotalSeconds:F1} seconds.")
                            .ConfigureAwait(false);
                }

                Publish(registration, RendererReloadState.Resuming, "Configured renderer accepted; rendering resumed.");
                Interlocked.Increment(ref _successfulReloads);
                Publish(registration, RendererReloadState.Idle, "Renderer configuration restart completed.");
                return new(true, registration);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await RollBackConfigurationAsync(
                    registration, windows, detached, candidateAttached, candidateCleanupFailed, configuration,
                    configurationAttempted, RendererReloadFailureKind.Cancelled,
                    "Renderer configuration restart was cancelled.",
                    initialDetachUncertain: !teardownCompleted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await RollBackConfigurationAsync(
                    registration, windows, detached, candidateAttached, candidateCleanupFailed, configuration,
                    configurationAttempted, failureKind, ex.ToString(),
                    initialDetachUncertain: !teardownCompleted).ConfigureAwait(false);
            }
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public Task<RendererReplacementResult> ReplaceAsync(
        RendererBackendId backendId,
        RendererBackendRegistration? candidate,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken = default)
        => ReplaceCoreAsync(
            backendId,
            candidate,
            firstFrameTimeout,
            restartOpenXrSession: false,
            cancellationToken);

    public Task<RendererReplacementResult> ReplaceWithOpenXrSessionRestartAsync(
        RendererBackendId backendId,
        RendererBackendRegistration? candidate,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken = default)
        => ReplaceCoreAsync(
            backendId,
            candidate,
            firstFrameTimeout,
            restartOpenXrSession: true,
            cancellationToken);

    private async Task<RendererReplacementResult> ReplaceCoreAsync(
        RendererBackendId backendId,
        RendererBackendRegistration? candidate,
        TimeSpan firstFrameTimeout,
        bool restartOpenXrSession,
        CancellationToken cancellationToken)
    {
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool restoreVrPresentation = false;
        try
        {
            IRendererBackendCatalog catalog = RuntimeRenderingHostServices.Factories.RendererBackends;
            RendererBackendRegistration previous = catalog.GetRequired(backendId);
            RendererBackendRegistration target = candidate ?? previous;
            XRWindow[] windows = [.. RuntimeEngine.Windows.Where(
                window => window.Renderer.BackendId == backendId)];
            if (windows.Length != 0 && windows.Length != RuntimeEngine.Windows.Count)
                return Fail(previous, RendererReloadFailureKind.ReloadBoundary,
                    "The shared Advanced preparation owner cannot be retired while another backend window remains active.");
            bool[] detached = new bool[windows.Length];

            lock (_statusSync)
                _phaseDurations.Clear();
            Publish(target, RendererReloadState.ReplacementRequested, "Renderer replacement requested.");

            cancellationToken.ThrowIfCancellationRequested();
            if (RuntimeEngine.VRState.IsInVR)
            {
                if (!restartOpenXrSession || !RuntimeEngine.VRState.IsOpenXRActive)
                {
                    return Fail(
                        previous,
                        RendererReloadFailureKind.ReloadBoundary,
                        "An XR session is active. Use the explicit OpenXR session restart action, or stop OpenXR/OpenVR presentation before reloading.");
                }

                Publish(
                    target,
                    RendererReloadState.Quiescing,
                    "Stopping OpenXR presentation while preserving the editor process and world.");
                bool xrTeardownCompleted = await InvokeOnRenderThreadAsync(
                    () =>
                    {
                        for (int i = 0; i < windows.Length; i++)
                        {
                            if (RuntimeEngine.VRState.OpenXRApi?.PrepareRendererDeviceTeardown(
                                    windows[i].Renderer,
                                    "renderer backend hot reload") == false)
                                return false;
                        }

                        RuntimeEngine.VRState.IsInVR = false;
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!xrTeardownCompleted)
                {
                    for (int i = 0; i < windows.Length; i++)
                        windows[i].Renderer.AbandonShutdownTeardown();
                    return Fail(previous, RendererReloadFailureKind.Teardown,
                        "OpenXR child teardown did not complete; renderer replacement was aborted.");
                }
                restoreVrPresentation = true;
            }

            RendererReloadFailureInjection.ThrowIfEnabled(
                RendererReloadInjectedFailure.DeviceLoss,
                "replacement request");
            Publish(target, RendererReloadState.Quiescing, "Quiescing backend work publication.");
            Publish(target, RendererReloadState.DrainingGpu, "Waiting for the retiring backend GPU boundary.");
            string? detachFailure = await MeasurePhaseAsync(
                "teardown",
                () => InvokeOnRenderThreadAsync(
                    () =>
                    {
                        RendererReloadFailureInjection.ThrowIfEnabled(
                            RendererReloadInjectedFailure.GpuDrain,
                            "GPU drain");
                        RendererReloadFailureInjection.ThrowIfEnabled(
                            RendererReloadInjectedFailure.WorkerShutdown,
                            "worker shutdown");
                        RendererReloadFailureInjection.ThrowIfEnabled(
                            RendererReloadInjectedFailure.CallbackStillRegistered,
                            "callback unregistration");
                        RendererReloadFailureInjection.ThrowIfEnabled(
                            RendererReloadInjectedFailure.ResourceLeak,
                            "resource teardown");
                        return DetachWindows(
                            windows,
                            detached,
                            $"renderer reload generation {target.Metadata.Generation}");
                    },
                    cancellationToken)).ConfigureAwait(false);
            if (detachFailure is not null)
            {
                string? restoreFailure = await InvokeOnRenderThreadAsync(
                    () => AttachDetachedWindows(windows, detached, "restore after teardown failure"),
                    CancellationToken.None).ConfigureAwait(false);
                if (restoreFailure is not null)
                {
                    Interlocked.Increment(ref _failedReloads);
                    Publish(previous, RendererReloadState.FailedStopped,
                        "Renderer teardown and restoration failed; rendering is stopped.",
                        RendererReloadFailureKind.Rollback,
                        $"{detachFailure}{Environment.NewLine}Rollback: {restoreFailure}");
                    return new(false, previous, RendererReloadFailureKind.Rollback,
                        restoreFailure, RolledBack: false);
                }
                return Fail(previous, RendererReloadFailureKind.Teardown, detachFailure);
            }

            Publish(target, RendererReloadState.DestroyingWrappers, "Retiring generation wrappers destroyed.");
            Publish(target, RendererReloadState.CleaningBackend, "Backend workers and callbacks stopped.");
            IDisposable? candidateLease = null;
            try
            {
                if (candidate is not null)
                {
                    Publish(target, RendererReloadState.UnloadingModule, "Preparing the retiring module for unload.");
                    if (previous.Lifecycle is IRendererBackendModule previousModule)
                    {
                        using CancellationTokenSource unloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        unloadTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await MeasureValueTaskPhaseAsync(
                            "module-unload-prepare",
                            async () =>
                            {
                                await previousModule.PrepareForUnloadAsync(
                                    new(
                                        backendId,
                                        previous.Metadata.Generation,
                                        "backend hot reload",
                                        TimeSpan.FromSeconds(10)),
                                    unloadTimeout.Token).ConfigureAwait(false);
                                return true;
                            }).ConfigureAwait(false);
                    }

                    Publish(target, RendererReloadState.LoadingCandidate, "Activating candidate backend registration.");
                    candidateLease = catalog.Register(
                        candidate,
                        RendererBackendRegistrationBehavior.ReplaceExisting);
                }

                Publish(target, RendererReloadState.InitializingCandidate, "Creating replacement renderers.");
                string? attachFailure = await MeasurePhaseAsync(
                    "candidate-initialization",
                    () => InvokeOnRenderThreadAsync(
                        () =>
                        {
                            RendererReloadFailureInjection.ThrowIfEnabled(
                                RendererReloadInjectedFailure.CandidateInitialization,
                                "candidate initialization");
                            return AttachWindows(
                                windows,
                                $"renderer generation {target.Metadata.Generation}");
                        },
                        CancellationToken.None)).ConfigureAwait(false);
                if (attachFailure is not null)
                {
                    return await RollBackAsync(
                        catalog,
                        previous,
                        target,
                        windows,
                        candidateLease,
                        RendererReloadFailureKind.CandidateInitialization,
                        attachFailure).ConfigureAwait(false);
                }

                Publish(target, RendererReloadState.RehydratingResources, "Logical resources rebound; awaiting a valid frame.");
                if (windows.Length > 0)
                {
                    Publish(target, RendererReloadState.AwaitingFirstValidFrame, "Awaiting first valid frame.");
                    bool firstFrame = await WaitForFirstFramesAsync(
                        windows,
                        target.Metadata.Generation,
                        firstFrameTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (RendererReloadFailureInjection.IsEnabled(
                            RendererReloadInjectedFailure.FirstFrame))
                    {
                        firstFrame = false;
                    }
                    if (!firstFrame)
                    {
                        return await RollBackAsync(
                            catalog,
                            previous,
                            target,
                            windows,
                            candidateLease,
                            RendererReloadFailureKind.FirstFrame,
                            $"No valid frame was presented within {firstFrameTimeout.TotalSeconds:F1} seconds.")
                            .ConfigureAwait(false);
                    }
                }

                Publish(target, RendererReloadState.Resuming, "Candidate accepted; rendering resumed.");
                IDisposable? oldLease = Interlocked.Exchange(
                    ref _activeDynamicRegistrationLease,
                    candidateLease);
                oldLease?.Dispose();
                candidateLease = null;
                Interlocked.Increment(ref _successfulReloads);
                Publish(target, RendererReloadState.Idle, "Renderer reload completed.");
                return new(true, target);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (candidateLease is not null)
                {
                    return await RollBackAsync(
                        catalog,
                        previous,
                        target,
                        windows,
                        candidateLease,
                        RendererReloadFailureKind.Cancelled,
                        "Reload cancellation arrived after teardown began; the last-good backend was restored.")
                        .ConfigureAwait(false);
                }

                await ReattachWindowsAsync(windows, "restore after cancellation", CancellationToken.None)
                    .ConfigureAwait(false);
                return Fail(previous, RendererReloadFailureKind.Cancelled, "Renderer reload was cancelled.");
            }
            catch (Exception ex)
            {
                if (candidateLease is not null)
                {
                    return await RollBackAsync(
                        catalog,
                        previous,
                        target,
                        windows,
                        candidateLease,
                        RendererReloadFailureKind.ModuleValidation,
                        ex.ToString()).ConfigureAwait(false);
                }

                await ReattachWindowsAsync(windows, "restore after reload failure", CancellationToken.None)
                    .ConfigureAwait(false);
                return Fail(previous, RendererReloadFailureKind.Unload, ex.ToString());
            }
            finally
            {
                candidateLease?.Dispose();
            }
        }
        finally
        {
            if (restoreVrPresentation)
                RuntimeEngine.VRState.IsInVR = true;
            _transactionGate.Release();
        }
    }

    public void ReportBuildPending(RendererBackendId backendId, long generation, string status)
    {
        RendererBackendRegistration registration =
            RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(backendId);
        RendererBackendMetadata metadata = registration.Metadata.WithGeneration(generation);
        Publish(
            new RendererBackendRegistration(metadata, registration.Factory, registration.Lifecycle),
            RendererReloadState.BuildPending,
            status);
    }

    public void ReportExternalFailure(
        RendererBackendId backendId,
        long generation,
        RendererReloadFailureKind failureKind,
        string error)
    {
        RendererBackendRegistration current =
            RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(backendId);
        Fail(
            new RendererBackendRegistration(
                current.Metadata.WithGeneration(generation),
                current.Factory,
                current.Lifecycle),
            failureKind,
            error);
    }

    public void ReportUnloadLeak(
        RendererBackendId backendId,
        long generation,
        string error)
    {
        Interlocked.Increment(ref _unloadLeaks);
        ReportExternalFailure(
            backendId,
            generation,
            RendererReloadFailureKind.Unload,
            error);
    }

    private async Task<RendererReplacementResult> RollBackConfigurationAsync(
        RendererBackendRegistration registration,
        XRWindow[] windows,
        bool[] detached,
        bool[] candidateAttached,
        bool[] candidateCleanupFailed,
        IRendererReplacementConfiguration configuration,
        bool configurationAttempted,
        RendererReloadFailureKind originalFailureKind,
        string originalError,
        bool initialDetachUncertain = false)
    {
        bool hadDetachedWindow = false;
        for (int i = 0; i < detached.Length; i++)
            hadDetachedWindow |= detached[i];
        if (!hadDetachedWindow && !configurationAttempted)
            return Fail(registration, originalFailureKind, originalError);

        try
        {
            Publish(registration, RendererReloadState.RollingBack,
                "Configured renderer failed; restoring the previous configuration.",
                originalFailureKind, originalError);
            RendererReloadFailureInjection.ThrowIfEnabled(RendererReloadInjectedFailure.Rollback, "configuration rollback");
            string? candidateDetachFailure = await InvokeOnRenderThreadAsync(
                () => DetachAttachedCandidates(windows, candidateAttached),
                CancellationToken.None).ConfigureAwait(false);
            if (candidateDetachFailure is not null)
                return FailStoppedConfiguration(registration, originalError, candidateDetachFailure);
            for (int i = 0; i < candidateCleanupFailed.Length; i++)
            {
                if (candidateCleanupFailed[i])
                    return FailStoppedConfiguration(registration, originalError,
                        $"Window {i} could not prove candidate renderer cleanup; the previous configuration was not restored.");
            }

            if (windows.Length != 0 && !initialDetachUncertain)
            {
                string? resetFailure = await InvokeOnRenderThreadAsync(
                    () => ResetSharedPreparationForDetachedCohort(windows),
                    CancellationToken.None).ConfigureAwait(false);
                if (resetFailure is not null)
                    return FailStoppedConfiguration(registration, originalError, resetFailure);
            }

            if (configurationAttempted)
            {
                await InvokeOnRenderThreadAsync(
                    () => { configuration.RestoreBeforeRollback(); return true; },
                    CancellationToken.None).ConfigureAwait(false);
            }

            string? attachFailure = await InvokeOnRenderThreadAsync(
                () => AttachDetachedWindows(windows, detached, "previous configuration rollback"),
                CancellationToken.None).ConfigureAwait(false);
            if (attachFailure is not null)
                return FailStoppedConfiguration(registration, originalError, attachFailure);

            if (initialDetachUncertain)
                return Fail(registration, originalFailureKind,
                    $"{originalError} Previous window attachment was restored, but native cleanup and shared preparation recovery are unproven after partial teardown.");

            Interlocked.Increment(ref _failedReloads);
            Interlocked.Increment(ref _lastGoodRollbacks);
            Publish(registration, RendererReloadState.Failed,
                "Configured renderer failed; previous configuration restored.",
                originalFailureKind, originalError);
            return new(false, registration, originalFailureKind, originalError, RolledBack: true);
        }
        catch (Exception ex)
        {
            return FailStoppedConfiguration(registration, originalError, ex.ToString());
        }
    }

    private RendererReplacementResult FailStoppedConfiguration(
        RendererBackendRegistration registration,
        string originalError,
        string rollbackError)
    {
        Interlocked.Increment(ref _failedReloads);
        Publish(registration, RendererReloadState.FailedStopped,
            "Renderer configuration rollback failed; rendering is stopped.",
            RendererReloadFailureKind.Rollback,
            $"{originalError}{Environment.NewLine}Rollback: {rollbackError}");
        return new(false, registration, RendererReloadFailureKind.Rollback, rollbackError, RolledBack: false);
    }

    private static string? DetachWindowsTracked(XRWindow[] windows, bool[] detached, string reason)
    {
        for (int i = 0; i < windows.Length; i++)
        {
            if (!windows[i].TryDetachRendererForReplacement(reason, out string? failure))
                return $"Window {i} could not detach its renderer: {failure}";
            detached[i] = true;
        }

        if (windows.Length != 0)
        {
            string? resetFailure = ResetSharedPreparationForDetachedCohort(windows);
            if (resetFailure is not null)
                return resetFailure;
        }
        return null;
    }

    private static string? ResetSharedPreparationForDetachedCohort(XRWindow[] detachedWindows)
    {
        // The initial window snapshot can become stale while teardown or frame
        // acceptance waits. Check current consumers at the reset boundary.
        foreach (XRWindow liveWindow in RuntimeEngine.Windows)
        {
            bool isDetachedCohortMember = false;
            for (int i = 0; i < detachedWindows.Length; i++)
                if (ReferenceEquals(liveWindow, detachedWindows[i]))
                {
                    isDetachedCohortMember = true;
                    break;
                }
            if (!isDetachedCohortMember)
                return "The shared Advanced preparation owner cannot be retired while a window outside the detached renderer cohort remains active.";
        }

        AdvancedSharedPreparationService.ResetAfterRendererRetirement();
        return null;
    }

    private static string? AttachWindowsTracked(
        XRWindow[] windows,
        bool[] attached,
        bool[] candidateCleanupFailed,
        string reason)
    {
        for (int i = 0; i < windows.Length; i++)
        {
            if (windows[i].TryAttachReplacementRenderer(
                reason, out string? failure, out bool failedCandidateCleanedUp))
            {
                attached[i] = true;
                continue;
            }

            candidateCleanupFailed[i] = !failedCandidateCleanedUp;

            for (int remaining = i + 1; remaining < windows.Length; remaining++)
                windows[remaining].CompleteFailedRendererReplacement();
            return $"Window {i} could not initialize its replacement renderer: {failure}";
        }

        return null;
    }

    private static string? DetachAttachedCandidates(XRWindow[] windows, bool[] candidateAttached)
    {
        string? firstFailure = null;
        for (int i = 0; i < windows.Length; i++)
        {
            if (!candidateAttached[i])
                continue;

            if (windows[i].TryDetachRendererForReplacement("configuration candidate rollback", out string? failure))
                candidateAttached[i] = false;
            else
                firstFailure ??= $"Window {i} could not detach its candidate renderer: {failure}";
        }

        return firstFailure;
    }

    private static string? AttachDetachedWindows(XRWindow[] windows, bool[] detached, string reason)
    {
        string? firstFailure = null;
        for (int i = 0; i < windows.Length; i++)
        {
            if (!detached[i])
                continue;

            if (windows[i].TryAttachReplacementRenderer(reason, out string? failure))
                detached[i] = false;
            else
                firstFailure ??= $"Window {i} could not restore its renderer: {failure}";
        }

        return firstFailure;
    }

    private async Task<RendererReplacementResult> RollBackAsync(
        IRendererBackendCatalog catalog,
        RendererBackendRegistration previous,
        RendererBackendRegistration candidate,
        XRWindow[] windows,
        IDisposable? candidateLease,
        RendererReloadFailureKind originalFailureKind,
        string originalError)
    {
        Publish(candidate, RendererReloadState.RollingBack, "Candidate failed; restoring last-good backend.", originalFailureKind, originalError);
        try
        {
            RendererReloadFailureInjection.ThrowIfEnabled(
                RendererReloadInjectedFailure.Rollback,
                "rollback");
            bool[] candidateDetached = new bool[windows.Length];
            string? candidateDetachFailure = await InvokeOnRenderThreadAsync(
                () => DetachWindows(windows, candidateDetached, "candidate rollback"),
                CancellationToken.None).ConfigureAwait(false);
            if (candidateDetachFailure is not null)
            {
                Interlocked.Increment(ref _failedReloads);
                Publish(
                    previous,
                    RendererReloadState.FailedStopped,
                    "Candidate renderer teardown failed; rendering is stopped.",
                    RendererReloadFailureKind.Rollback,
                    $"{originalError}{Environment.NewLine}Rollback: {candidateDetachFailure}");
                return new(false, previous, RendererReloadFailureKind.Rollback,
                    candidateDetachFailure, RolledBack: false);
            }
            candidateLease?.Dispose();

            IDisposable rollbackLease = catalog.Register(
                previous,
                RendererBackendRegistrationBehavior.ReplaceExisting);
            string? rollbackFailure = await ReattachWindowsAsync(
                windows,
                "last-good rollback",
                CancellationToken.None).ConfigureAwait(false);
            if (rollbackFailure is not null)
            {
                rollbackLease.Dispose();
                Interlocked.Increment(ref _failedReloads);
                Publish(
                    previous,
                    RendererReloadState.FailedStopped,
                    "Candidate and last-good rollback both failed; rendering is stopped.",
                    RendererReloadFailureKind.Rollback,
                    $"{originalError}{Environment.NewLine}Rollback: {rollbackFailure}");
                return new(
                    false,
                    previous,
                    RendererReloadFailureKind.Rollback,
                    rollbackFailure,
                    RolledBack: false);
            }

            IDisposable? oldLease = Interlocked.Exchange(
                ref _activeDynamicRegistrationLease,
                rollbackLease);
            oldLease?.Dispose();
            Interlocked.Increment(ref _failedReloads);
            Interlocked.Increment(ref _lastGoodRollbacks);
            Publish(
                previous,
                RendererReloadState.Failed,
                "Candidate failed; last-good backend restored.",
                originalFailureKind,
                originalError);
            return new(false, previous, originalFailureKind, originalError, RolledBack: true);
        }
        catch (Exception rollbackException)
        {
            Interlocked.Increment(ref _failedReloads);
            Publish(
                previous,
                RendererReloadState.FailedStopped,
                "Candidate and rollback failed; rendering is stopped.",
                RendererReloadFailureKind.Rollback,
                $"{originalError}{Environment.NewLine}Rollback: {rollbackException}");
            return new(
                false,
                previous,
                RendererReloadFailureKind.Rollback,
                rollbackException.ToString(),
                RolledBack: false);
        }
    }

    private RendererReplacementResult Fail(
        RendererBackendRegistration active,
        RendererReloadFailureKind failureKind,
        string error)
    {
        Interlocked.Increment(ref _failedReloads);
        Publish(active, RendererReloadState.Failed, "Renderer reload failed.", failureKind, error);
        return new(false, active, failureKind, error);
    }

    private static string? DetachWindows(XRWindow[] windows, bool[] detached, string reason)
    {
        for (int i = 0; i < windows.Length; i++)
        {
            if (!windows[i].TryDetachRendererForReplacement(reason, out string? failure))
                return $"Window {i} could not detach its renderer: {failure}";
            detached[i] = true;
        }

        if (windows.Length != 0)
        {
            string? resetFailure = ResetSharedPreparationForDetachedCohort(windows);
            if (resetFailure is not null)
                return resetFailure;
        }
        return null;
    }

    private static string? AttachWindows(XRWindow[] windows, string reason)
    {
        for (int i = 0; i < windows.Length; i++)
        {
            if (windows[i].TryAttachReplacementRenderer(reason, out string? failure))
                continue;

            for (int remaining = i + 1; remaining < windows.Length; remaining++)
                windows[remaining].CompleteFailedRendererReplacement();
            return $"Window {i} could not initialize its replacement renderer: {failure}";
        }

        return null;
    }

    private static Task<string?> ReattachWindowsAsync(
        XRWindow[] windows,
        string reason,
        CancellationToken cancellationToken)
        => InvokeOnRenderThreadAsync(() => AttachWindows(windows, reason), cancellationToken);

    private static async Task<bool> WaitForFirstFramesAsync(
        XRWindow[] windows,
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        HashSet<XRWindow> pending = new(windows, ReferenceEqualityComparer.Instance);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        object sync = new();

        void OnFrame(XRWindow window, long frameGeneration)
        {
            if (frameGeneration != generation ||
                window.Renderer.BackendGeneration != generation ||
                !window.Renderer.IsBackendReplacementFrameReady)
            {
                return;
            }

            lock (sync)
            {
                pending.Remove(window);
                if (pending.Count == 0)
                    completion.TrySetResult();
            }
        }

        XRWindow.AnyRendererFrameCompleted += OnFrame;
        try
        {
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            XRWindow.AnyRendererFrameCompleted -= OnFrame;
        }
    }

    private static Task<T> InvokeOnRenderThreadAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        if (RuntimeEngine.IsRenderThread)
            return Task.FromResult(action());

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeEngine.EnqueueRenderThreadTask(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            "RendererReplacementCoordinator");
        return completion.Task;
    }

    private async Task<T> MeasurePhaseAsync<T>(string phase, Func<Task<T>> action)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            lock (_statusSync)
                _phaseDurations[phase] = Stopwatch.GetElapsedTime(start);
        }
    }

    private async Task<T> MeasureValueTaskPhaseAsync<T>(string phase, Func<ValueTask<T>> action)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            lock (_statusSync)
                _phaseDurations[phase] = Stopwatch.GetElapsedTime(start);
        }
    }

    private void Publish(
        RendererBackendRegistration registration,
        RendererReloadState state,
        string status,
        RendererReloadFailureKind failureKind = RendererReloadFailureKind.None,
        string? error = null)
    {
        RendererReloadSnapshot snapshot;
        lock (_statusSync)
        {
            snapshot = new(
                registration.Metadata.Id,
                registration.Metadata.Generation,
                state,
                failureKind,
                status,
                error,
                DateTimeOffset.UtcNow,
                Interlocked.Read(ref _successfulReloads),
                Interlocked.Read(ref _failedReloads),
                Interlocked.Read(ref _lastGoodRollbacks),
                Interlocked.Read(ref _unloadLeaks),
                new Dictionary<string, TimeSpan>(_phaseDurations));
            _snapshot = snapshot;
        }

        StatusChanged?.Invoke(snapshot);
    }
}
