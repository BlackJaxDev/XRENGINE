using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Execution;

namespace XREngine.Rendering.GI.DDGI;

internal sealed partial class DDGIFrameContext
{
    private BakedLoadRequest? _pendingBakedLoad;
    private ulong _attemptedBakedPathRevision;
    private ulong _bakedLoadEpoch;

    private bool PrepareBakedVolumeCallerThread(DDGIVolumeComponent volume)
    {
        if (!IsCurrentVolume(volume))
        {
            CancelPendingBakedLoad();
            _configuredAsset = null;
            _attemptedAssetLoad = false;
        }

        string? path = volume.BakedAssetPath;
        DDGIBakedAsset? asset = volume.BakedAsset;
        ulong pathRevision = volume.PendingBakedAssetPathRevision;
        if (_pendingBakedLoad is { } pending &&
            (!ReferenceEquals(pending.VolumeTarget, volume) ||
             !string.Equals(path, pending.Path, StringComparison.Ordinal) ||
             pathRevision != pending.PathRevision ||
             volume.RuntimeState.InvalidationRevision != pending.Revision ||
             !ReferenceEquals(asset, pending.PreviousAsset)))
            CancelPendingBakedLoad();

        bool needsPathRead = !string.IsNullOrWhiteSpace(path) && pathRevision != 0;
        if (needsPathRead && _pendingBakedLoad is null &&
            (!_attemptedAssetLoad || !string.Equals(_attemptedAssetPath, path, StringComparison.Ordinal) ||
             _attemptedAssetRevision != volume.RuntimeState.InvalidationRevision ||
             _attemptedBakedPathRevision != pathRevision))
            StartBakedLoad(volume, path!, pathRevision, asset);

        if (volume.BakedAsset is not { } current)
        {
            Synchronize(volume);
            return false;
        }

        try
        {
            if (!ReferenceEquals(_configuredAsset, current))
            {
                current.ApplyTo(volume);
                _configuredAsset = current;
            }
            else
                current.ApplyLayoutTo(volume);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
        {
            Debug.LightingWarning("Failed to configure baked DDGI asset: {0}", error.Message);
            Synchronize(volume);
            return false;
        }
        Synchronize(volume);
        return true;
    }

    private void StartBakedLoad(DDGIVolumeComponent volume, string path, ulong pathRevision, DDGIBakedAsset? previousAsset)
    {
        _attemptedAssetLoad = true;
        _attemptedAssetPath = path;
        _attemptedAssetRevision = volume.RuntimeState.InvalidationRevision;
        _attemptedBakedPathRevision = pathRevision;
        try
        {
            JobManager jobs = RuntimeWorkScheduler.CaptureCallerThreadJobs();
            CancellationTokenSource cancellation = new();
            RuntimeAssetReadLease read;
            try { read = RuntimeAssetReadServices.Capture(cancellation.Token); }
            catch { cancellation.Dispose(); throw; }
            BakedLoadRequest request = new(this, volume, jobs, read, cancellation,
                path, pathRevision, _attemptedAssetRevision, _bakedLoadEpoch, previousAsset);
            _pendingBakedLoad = request;
            volume.BakedLoadOwnerEnded += request.Cancel;
            _ = request.RunAsync();
        }
        catch (Exception error)
        {
            Debug.LightingWarning("Failed to start baked DDGI asset load '{0}': {1}", path, error.Message);
        }
    }

    private void CancelPendingBakedLoad()
    {
        BakedLoadRequest? request = _pendingBakedLoad;
        _pendingBakedLoad = null;
        if (request is not null)
            _attemptedAssetLoad = false;
        request?.Cancel();
    }

    private void AdoptBakedLoad(BakedLoadRequest request)
    {
        if (!ReferenceEquals(_pendingBakedLoad, request) || request.IsRetired ||
            _bakedLoadEpoch != request.Epoch ||
            !request.TryGetVolume(out DDGIVolumeComponent? volume) || volume is null || volume.IsDestroyed ||
            !volume.IsActiveInHierarchy || !IsCurrentVolume(volume) ||
            !string.Equals(volume.BakedAssetPath, request.Path, StringComparison.Ordinal) ||
            volume.PendingBakedAssetPathRevision != request.PathRevision ||
            volume.RuntimeState.InvalidationRevision != request.Revision ||
            !ReferenceEquals(volume.BakedAsset, request.PreviousAsset))
            return;

        try
        {
            using IDisposable publication = request.Read.BeginPublication();
            if (request.Error is { } error)
            {
                Debug.LightingWarning("Failed to load baked DDGI asset '{0}': {1}", request.Path, error.Message);
                return;
            }

            DDGIBakedAsset candidate = request.Asset!;
            if (volume.TryAdoptBakedAsset(candidate, request.Path, request.PathRevision, request.PreviousAsset,
                    expectedRevision => IsCurrentBakedLoad(request, volume, expectedRevision)) &&
                ReferenceEquals(_pendingBakedLoad, request))
            {
                _configuredAsset = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Debug.LightingWarning("Failed to adopt baked DDGI asset '{0}': {1}", request.Path, error.Message);
        }
        finally
        {
            if (ReferenceEquals(_pendingBakedLoad, request))
                _pendingBakedLoad = null;
        }
    }

    private bool IsCurrentBakedLoad(BakedLoadRequest request, DDGIVolumeComponent volume, ulong expectedRevision)
        => ReferenceEquals(_pendingBakedLoad, request) && _bakedLoadEpoch == request.Epoch &&
            !request.IsRetired && !request.Read.CancellationToken.IsCancellationRequested &&
            !volume.IsDestroyed && volume.IsActiveInHierarchy && IsCurrentVolume(volume) &&
            string.Equals(volume.BakedAssetPath, request.Path, StringComparison.Ordinal) &&
            volume.PendingBakedAssetPathRevision == request.PathRevision &&
            volume.RuntimeState.InvalidationRevision == expectedRevision;

    private sealed class BakedLoadRequest(
        DDGIFrameContext owner,
        DDGIVolumeComponent volume,
        JobManager jobs,
        RuntimeAssetReadLease read,
        CancellationTokenSource cancellation,
        string path,
        ulong pathRevision,
        ulong revision,
        ulong epoch,
        DDGIBakedAsset? previousAsset)
    {
        private readonly WeakReference<DDGIFrameContext> _owner = new(owner);
        private readonly WeakReference<DDGIVolumeComponent> _volume = new(volume);
        private readonly JobManager _jobs = jobs;
        private readonly CancellationTokenSource _cancellation = cancellation;
        private RuntimeAssetReadLease? _read = read;
        private JobHandle _dispatch;
        // A single CAS chooses between a queued callback and cancellation cleanup.
        // Running callbacks retain the lease until their job handle completes.
        private int _handoff;
        internal bool IsRetired => Volatile.Read(ref _handoff) == 2;
        internal DDGIVolumeComponent? VolumeTarget => _volume.TryGetTarget(out DDGIVolumeComponent? volume) ? volume : null;
        internal bool TryGetVolume(out DDGIVolumeComponent? volume) => _volume.TryGetTarget(out volume);
        internal RuntimeAssetReadLease Read => _read ?? throw new ObjectDisposedException(nameof(BakedLoadRequest));
        internal string Path { get; } = path;
        internal ulong PathRevision { get; } = pathRevision;
        internal ulong Revision { get; } = revision;
        internal ulong Epoch { get; } = epoch;
        private readonly WeakReference<DDGIBakedAsset>? _previousAsset = previousAsset is null ? null : new(previousAsset);
        internal DDGIBakedAsset? PreviousAsset => _previousAsset is not null && _previousAsset.TryGetTarget(out DDGIBakedAsset? asset) ? asset : null;
        internal DDGIBakedAsset? Asset { get; private set; }
        internal Exception? Error { get; private set; }

        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException error)
            {
                System.Diagnostics.Trace.TraceError("Baked DDGI read cancellation callback failed: {0}", error);
            }
            try { _dispatch.Cancel(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Baked DDGI adoption job cancellation failed: {0}", error);
            }
        }

        internal async Task RunAsync()
        {
            try
            {
                try { Asset = await DDGIBakedAsset.LoadAsync(Read, Path).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception error) { Error = error; }

                if (Read.CancellationToken.IsCancellationRequested)
                    return;
                _dispatch = _jobs.Schedule(new LabeledActionJob(() =>
                {
                    if (Interlocked.CompareExchange(ref _handoff, 1, 0) != 0)
                        return;
                    try
                    {
                        if (_owner.TryGetTarget(out DDGIFrameContext? owner))
                            owner.AdoptBakedLoad(this);
                    }
                    finally { Interlocked.Exchange(ref _handoff, 2); }
                }, "DDGIFrameContext.AdoptBakedAsset"), JobPriority.Normal, JobAffinity.RenderThread,
                    Read.CancellationToken);
                try { await _dispatch.WaitAsync(Read.CancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { _ = ObserveDispatchAsync(_dispatch.WaitAsync()); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Baked DDGI dispatch failed: {0}", error);
            }
            finally
            {
                bool canceled = _read?.CancellationToken.IsCancellationRequested ?? false;
                if (Interlocked.CompareExchange(ref _handoff, 2, 0) == 1)
                {
                    try { await _dispatch.WaitAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.TraceError("Baked DDGI adoption completion failed: {0}", error);
                    }
                }
                if (TryGetVolume(out DDGIVolumeComponent? volume) && volume is not null)
                    volume.BakedLoadOwnerEnded -= Cancel;
                _read?.Dispose();
                _read = null;
                _cancellation.Dispose();
                Asset = null;
                Error = null;
                _dispatch = default;
                if (_owner.TryGetTarget(out DDGIFrameContext? owner))
                {
                    if (Interlocked.CompareExchange(ref owner._pendingBakedLoad, null, this) == this && canceled)
                        owner._attemptedAssetLoad = false;
                }
            }
        }

        private static async Task ObserveDispatchAsync(Task completion)
        {
            try { await completion.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Baked DDGI adoption job failed: {0}", error);
            }
        }
    }
}
