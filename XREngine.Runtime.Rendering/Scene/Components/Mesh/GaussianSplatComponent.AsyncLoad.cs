using XREngine.Data;
using XREngine.Execution;
using XREngine.Rendering.Models.Gaussian;

namespace XREngine.Components.Scene.Mesh;

public partial class GaussianSplatComponent
{
    private CloudLoadRequest? _pendingCloudLoad;
    private GaussianSplatCloud? _ownedCloud;
    private readonly List<GaussianSplatCloud> _retiredOwnedClouds = [];
    private bool _retiringOwnedClouds;
    private string? _loadedCloudPath;
    private bool _cloudLoadTeardown;

    private void StartCloudLoad(string path, long loadIntent)
    {
        CancelPendingCloudLoad();
        if (_cloudLoadTeardown || loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
            return;

        try
        {
            // Capture the actual session before the asynchronous source read. The
            // handle, rather than a separate completion source, owns queued work.
            JobManager jobs = RuntimeWorkScheduler.CaptureCallerThreadJobs();
            CancellationTokenSource cancellation = new();
            RuntimeAssetReadLease read;
            try { read = RuntimeAssetReadServices.Capture(cancellation.Token); }
            catch { cancellation.Dispose(); throw; }
            CloudLoadRequest request = new(this, jobs, read, cancellation, path, _sourcePath);
            _pendingCloudLoad = request;
            _ = request.RunAsync();
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, $"Failed to load gaussian splat data from '{path}'.");
        }
    }

    private void CancelPendingCloudLoad()
    {
        CloudLoadRequest? request = _pendingCloudLoad;
        _pendingCloudLoad = null;
        request?.Cancel();
    }

    private void AdoptCloudLoad(CloudLoadRequest request)
    {
        if (!ReferenceEquals(_pendingCloudLoad, request) || request.IsRetired ||
            _cloudLoadTeardown || IsDestroyed ||
            !string.Equals(_sourcePath, request.SourcePath, StringComparison.Ordinal))
            return;

        try
        {
            using IDisposable publication = request.Read.BeginPublication();
            if (request.Error is { } error)
            {
                Debug.RenderingException(error, $"Failed to load gaussian splat data from '{request.Path}'.");
                return;
            }
            if (request.Cloud is not { } candidate)
            {
                Debug.RenderingWarning($"Gaussian splat data not found at '{request.Path}'.");
                return;
            }

            GaussianSplatCloud? priorCloud = _cloud;
            GaussianSplatCloud? priorOwned = _ownedCloud;
            string? priorPath = _loadedCloudPath;
            long externalCloudSerial = Volatile.Read(ref _externalCloudAssignmentSerial);
            try
            {
                _adoptingCloudRequest = request;
                try { Cloud = candidate; }
                finally { _adoptingCloudRequest = null; }
                if (externalCloudSerial != Volatile.Read(ref _externalCloudAssignmentSerial))
                {
                    if (ReferenceEquals(_cloud, candidate))
                    {
                        request.BorrowedByExternal = true;
                        request.Adopted = true;
                    }
                    return;
                }
                if (!ReferenceEquals(_pendingCloudLoad, request) || !ReferenceEquals(_cloud, candidate) ||
                    request.Read.CancellationToken.IsCancellationRequested)
                {
                    // A notification may have changed SourcePath while publishing. Restore only
                    // state that this request still owns.
                    if (ReferenceEquals(_cloud, candidate))
                        RestoreCloud(priorCloud);
                    return;
                }
                request.Adopted = true;
                _ownedCloud = candidate;
                _loadedCloudPath = request.Path;
                if (priorOwned is not null && !ReferenceEquals(priorOwned, candidate) &&
                    !ReferenceEquals(priorOwned, _cloud))
                    RetireOwnedCloud(priorOwned);
            }
            catch
            {
                if (externalCloudSerial == Volatile.Read(ref _externalCloudAssignmentSerial) &&
                    ReferenceEquals(_cloud, candidate))
                {
                    try
                    {
                        RestoreCloud(priorCloud);
                        if (ReferenceEquals(_cloud, priorCloud))
                        {
                            _ownedCloud = priorOwned;
                            _loadedCloudPath = priorPath;
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        System.Diagnostics.Trace.TraceError("Failed to restore a Gaussian cloud after load adoption error: {0}", rollbackError);
                    }
                }
                throw;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Debug.RenderingException(error, $"Failed to adopt gaussian splat data from '{request.Path}'.");
        }
        finally
        {
            if (!request.BorrowedByExternal && request.Cloud is { } candidate && ReferenceEquals(_cloud, candidate))
            {
                GaussianSplatCloud? previousOwned = _ownedCloud;
                _ownedCloud = candidate;
                request.Adopted = true;
                if (previousOwned is not null && !ReferenceEquals(previousOwned, candidate) &&
                    !ReferenceEquals(previousOwned, _cloud))
                    RetireOwnedCloud(previousOwned);
            }
            if (ReferenceEquals(_pendingCloudLoad, request))
                _pendingCloudLoad = null;
        }
    }

    /// <summary>Puts back the cloud that was current before a rejected load, and its GPU upload.</summary>
    private void RestoreCloud(GaussianSplatCloud? priorCloud)
    {
        if (SetField(ref _cloud, priorCloud, nameof(Cloud)) && ReferenceEquals(_cloud, priorCloud))
            QueueCloudUpload(priorCloud);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (!_cloudLoadTeardown && !string.IsNullOrWhiteSpace(_sourcePath) &&
            !_externalCloudBinding &&
            !string.Equals(_loadedCloudPath, _sourcePath, StringComparison.Ordinal) &&
            _pendingCloudLoad is null)
            LoadFromFile(_sourcePath);
    }

    protected override void OnComponentDeactivated()
    {
        CancelPendingCloudLoad();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        _cloudLoadTeardown = true;
        CancelPendingCloudLoad();
        ReleaseGpuResources();
        if (_ownedCloud is { } owned)
        {
            _ownedCloud = null;
            RetireOwnedCloud(owned);
        }
        RetireOwnedCloud(null);
        base.OnDestroying();
        if (_retiredOwnedClouds.Count != 0)
            throw new InvalidOperationException("Gaussian clouds could not be fully retired; destruction can be retried.");
    }

    private void RetireOwnedCloud(GaussianSplatCloud? cloud)
    {
        if (cloud is not null && !cloud.IsDestroyed && !_retiredOwnedClouds.Contains(cloud))
            _retiredOwnedClouds.Add(cloud);
        if (_retiringOwnedClouds)
            return;
        _retiringOwnedClouds = true;
        try
        {
            for (int index = 0; index < _retiredOwnedClouds.Count;)
            {
                GaussianSplatCloud owned = _retiredOwnedClouds[index];
                if (!_cloudLoadTeardown && ReferenceEquals(owned, _cloud))
                {
                    _retiredOwnedClouds.RemoveAt(index);
                    continue;
                }
                TryDestroy(owned);
                if (owned.IsDestroyed)
                    _retiredOwnedClouds.RemoveAt(index);
                else
                    index++;
            }
        }
        finally { _retiringOwnedClouds = false; }
    }

    private sealed class CloudLoadRequest(
        GaussianSplatComponent owner,
        JobManager jobs,
        RuntimeAssetReadLease read,
        CancellationTokenSource cancellation,
        string path,
        string? sourcePath)
    {
        private readonly WeakReference<GaussianSplatComponent> _owner = new(owner);
        private readonly JobManager _jobs = jobs;
        private readonly CancellationTokenSource _cancellation = cancellation;
        private RuntimeAssetReadLease? _read = read;
        private JobHandle _dispatch;
        // A single CAS chooses between a queued callback and cancellation cleanup.
        // Running callbacks retain the lease until their job handle completes.
        private int _handoff;
        internal bool IsRetired => Volatile.Read(ref _handoff) == 2;
        internal RuntimeAssetReadLease Read => _read ?? throw new ObjectDisposedException(nameof(CloudLoadRequest));
        internal string Path { get; } = path;
        internal string? SourcePath { get; } = sourcePath;
        internal GaussianSplatCloud? Cloud { get; private set; }
        internal Exception? Error { get; private set; }
        internal bool Adopted { get; set; }
        internal bool BorrowedByExternal { get; set; }

        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException error)
            {
                System.Diagnostics.Trace.TraceError("Gaussian read cancellation callback failed: {0}", error);
            }
            try { _dispatch.Cancel(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Gaussian adoption job cancellation failed: {0}", error);
            }
        }

        internal async Task RunAsync()
        {
            try
            {
                try { Cloud = await GaussianSplatCloud.LoadAsync(Read, Path).ConfigureAwait(false); }
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
                        if (_owner.TryGetTarget(out GaussianSplatComponent? owner))
                            owner.AdoptCloudLoad(this);
                    }
                    finally { Interlocked.Exchange(ref _handoff, 2); }
                }, "GaussianSplatComponent.AdoptCloud"), JobPriority.Normal, JobAffinity.RenderThread,
                    Read.CancellationToken);
                try { await _dispatch.WaitAsync(Read.CancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { _ = ObserveDispatchAsync(_dispatch.WaitAsync()); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Gaussian cloud dispatch failed: {0}", error);
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _handoff, 2, 0) == 1)
                {
                    try { await _dispatch.WaitAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.TraceError("Gaussian adoption completion failed: {0}", error);
                    }
                }
                if (!Adopted && Cloud is { } cloud &&
                    (!_owner.TryGetTarget(out GaussianSplatComponent? currentOwner) ||
                     !ReferenceEquals(currentOwner.Cloud, cloud)))
                {
                    try { cloud.DiscardUnpublished(); }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.TraceError("Failed to release an unpublished Gaussian cloud: {0}", error);
                    }
                }
                _read?.Dispose();
                _read = null;
                _cancellation.Dispose();
                Cloud = null;
                Error = null;
                _dispatch = default;
                if (_owner.TryGetTarget(out GaussianSplatComponent? owner))
                    Interlocked.CompareExchange(ref owner._pendingCloudLoad, null, this);
            }
        }

        private static async Task ObserveDispatchAsync(Task completion)
        {
            try { await completion.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Gaussian adoption job failed: {0}", error);
            }
        }
    }
}
