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
                await InvokeOnRenderThreadAsync(
                    () =>
                    {
                        for (int i = 0; i < windows.Length; i++)
                        {
                            RuntimeEngine.VRState.OpenXRApi?.PrepareRendererDeviceTeardown(
                                windows[i].Renderer,
                                "renderer backend hot reload");
                        }

                        RuntimeEngine.VRState.IsInVR = false;
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
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
                            $"renderer reload generation {target.Metadata.Generation}");
                    },
                    cancellationToken)).ConfigureAwait(false);
            if (detachFailure is not null)
            {
                await ReattachWindowsAsync(windows, "restore after teardown failure", CancellationToken.None)
                    .ConfigureAwait(false);
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
            await InvokeOnRenderThreadAsync(
                () => DetachWindows(windows, "candidate rollback"),
                CancellationToken.None).ConfigureAwait(false);
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

    private static string? DetachWindows(XRWindow[] windows, string reason)
    {
        for (int i = 0; i < windows.Length; i++)
        {
            if (windows[i].TryDetachRendererForReplacement(reason, out string? failure))
                continue;

            for (int completed = 0; completed < i; completed++)
                windows[completed].TryAttachReplacementRenderer("restore partial teardown", out _);
            return $"Window {i} could not detach its renderer: {failure}";
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
