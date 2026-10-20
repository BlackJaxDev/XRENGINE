using XREngine.Data;
using XREngine.Execution;
using XREngine.Rendering;
using XREngine.Rendering.Models;
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
                _deferredCloudRequest = request;
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
                    if (ReferenceEquals(_cloud, candidate))
                    {
                        // A notification may have changed SourcePath or Model while
                        // publishing. Restore only state still owned by this request.
                        SetField(ref _cloud, priorCloud);
                        if (ReferenceEquals(_cloud, priorCloud))
                            RestorePriorModelAfterRejectedLoad(request);
                    }
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
                        SetField(ref _cloud, priorCloud);
                        if (ReferenceEquals(_cloud, priorCloud))
                        {
                            _ownedCloud = priorOwned;
                            _loadedCloudPath = priorPath;
                            RestorePriorModelAfterRejectedLoad(request);
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        System.Diagnostics.Trace.TraceError("Failed to restore a Gaussian model after load adoption error: {0}", rollbackError);
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
            if (ReferenceEquals(_deferredCloudRequest, request))
                _deferredCloudRequest = null;
            if (!ReferenceEquals(Model, request.PriorModelBinding) &&
                !ReferenceEquals(_ownedGeneratedModel, request.PriorGeneratedModel))
                RetireGeneratedModel(request.PriorGeneratedModel, request.PriorGeneratedSubMesh,
                    request.PriorGeneratedMesh);
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

    private void RestorePriorModelAfterRejectedLoad(CloudLoadRequest request)
    {
        GeneratedOwnership prior = new(request.PriorGeneratedModel, request.PriorGeneratedSubMesh,
            request.PriorGeneratedMesh, request.PriorInstanceCount);
        GeneratedOwnership candidate = CurrentGeneratedOwnership;
        int candidateCount = _activeInstanceCount;
        if (!candidate.SameAssets(prior) && ReferenceEquals(Model, candidate.Model))
        {
            _activeInstanceCount = request.PriorInstanceCount;
            try { PublishGeneratedModel(request.PriorModelBinding); }
            finally { SettleRejectedModelRollback(request.PriorModelBinding, prior, candidate, candidateCount); }
        }
        else
            SettleRejectedModelRollback(request.PriorModelBinding, prior, candidate, candidateCount);
    }

    private void SettleRejectedModelRollback(Model? priorBinding, GeneratedOwnership prior,
        GeneratedOwnership candidate, int candidateCount)
    {
        GeneratedOwnership installed = CurrentGeneratedOwnership;
        if (ReferenceEquals(Model, priorBinding))
        {
            if (ReferenceEquals(priorBinding, prior.Model))
            {
                CurrentGeneratedOwnership = prior;
                _activeInstanceCount = prior.InstanceCount;
            }
            else
            {
                CurrentGeneratedOwnership = default;
                _activeInstanceCount = 0;
            }
        }
        else if (!candidate.IsEmpty && ReferenceEquals(Model, candidate.Model))
        {
            CurrentGeneratedOwnership = candidate;
            _activeInstanceCount = candidateCount;
        }
        else if (installed.IsEmpty || !ReferenceEquals(Model, installed.Model))
        {
            CurrentGeneratedOwnership = default;
            _activeInstanceCount = 0;
        }

        QueueGeneratedIfUnbound(prior);
        if (!candidate.SameAssets(prior))
            QueueGeneratedIfUnbound(candidate);
        if (!installed.SameAssets(prior) && !installed.SameAssets(candidate))
            QueueGeneratedIfUnbound(installed);
        FlushGeneratedRetirements();
    }

    private void QueueGeneratedIfUnbound(GeneratedOwnership ownership)
    {
        if (!ownership.IsEmpty && !ReferenceEquals(Model, ownership.Model))
            QueueGeneratedRetirement(ownership.Model, ownership.SubMesh, ownership.Mesh);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (!_cloudLoadTeardown && !string.IsNullOrWhiteSpace(_sourcePath) &&
            !_externalModelBinding && !_externalCloudBinding &&
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
        if (_ownedCloud is { } owned)
        {
            _ownedCloud = null;
            RetireOwnedCloud(owned);
        }
        // The cloud's decoded records are independent of already built meshes.
        // Base retirement may fail; retain geometry until its wrappers retire.
        RetireOwnedCloud(null);
        base.OnDestroying();
        Meshes.PostAnythingAdded -= MeshAdded;
        Meshes.PostAnythingRemoved -= MeshRemoved;
        PropertyChanged -= ModelBindingChanged;
        RetireGeneratedModel(_ownedGeneratedModel, _ownedGeneratedSubMesh, _ownedGeneratedMesh);
        _ownedGeneratedModel = null;
        _ownedGeneratedSubMesh = null;
        _ownedGeneratedMesh = null;
        if (_retiredOwnedClouds.Count != 0 || _retiredGeneratedModels.Count != 0)
            throw new InvalidOperationException("Gaussian assets could not be fully retired; destruction can be retried.");
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
        internal Model? PriorModelBinding { get; private set; }
        internal Model? PriorGeneratedModel { get; private set; }
        internal SubMesh? PriorGeneratedSubMesh { get; private set; }
        internal XRMesh? PriorGeneratedMesh { get; private set; }
        internal int PriorInstanceCount { get; private set; }

        internal void CapturePriorModel(Model? binding, Model? generated, SubMesh? subMesh, XRMesh? mesh,
            int instanceCount)
        {
            PriorModelBinding = binding;
            PriorGeneratedModel = generated;
            PriorGeneratedSubMesh = subMesh;
            PriorGeneratedMesh = mesh;
            PriorInstanceCount = instanceCount;
        }

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
                PriorModelBinding = null;
                PriorGeneratedModel = null;
                PriorGeneratedSubMesh = null;
                PriorGeneratedMesh = null;
                PriorInstanceCount = 0;
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
