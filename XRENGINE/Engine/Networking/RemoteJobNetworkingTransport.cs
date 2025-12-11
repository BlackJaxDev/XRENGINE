using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine
{
    /// <summary>
    /// Networking-backed transport for remote job dispatch using the engine's state-change channel.
    /// </summary>
    public sealed class RemoteJobNetworkingTransport : IRemoteJobTransport, IDisposable
    {
        private readonly Engine.BaseNetworkingManager _networking;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteJobResponse>> _pending = new();
        private bool _disposed;

        public RemoteJobNetworkingTransport(Engine.BaseNetworkingManager networking)
        {
            _networking = networking ?? throw new ArgumentNullException(nameof(networking));
            _networking.RemoteJobResponseReceived += OnResponse;
        }

        public bool IsConnected => _networking.UDPServerConnectionEstablished;

        public Task<RemoteJobResponse> SendAsync(RemoteJobRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var tcs = new TaskCompletionSource<RemoteJobResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.JobId, tcs))
                throw new InvalidOperationException($"A remote request with id {request.JobId} is already pending.");

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static stateObj =>
                {
                    var (self, jobId, token) = ((RemoteJobNetworkingTransport, Guid, CancellationToken))stateObj!;
                    self.TryCancelPending(jobId, token);
                }, (this, request.JobId, cancellationToken));
            }

            var enrichedRequest = new RemoteJobRequest
            {
                JobId = request.JobId,
                Operation = request.Operation,
                TransferMode = request.TransferMode,
                Payload = request.Payload,
                Metadata = request.Metadata,
                SenderId = _networking.LocalPeerId,
                TargetId = request.TargetId,
            };

            try
            {
                _networking.BroadcastRemoteJobRequest(enrichedRequest, compress: true);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(request.JobId, out _);
                tcs.TrySetException(ex);
                return tcs.Task;
            }

            return tcs.Task;
        }

        private void OnResponse(RemoteJobResponse response)
        {
            if (!string.IsNullOrWhiteSpace(response.TargetId) && !string.Equals(response.TargetId, _networking.LocalPeerId, StringComparison.OrdinalIgnoreCase))
                return;

            if (!_pending.TryRemove(response.JobId, out var tcs))
                return;

            if (!response.Success)
            {
                tcs.TrySetException(new InvalidOperationException(response.Error ?? "Remote job failed."));
                return;
            }

            tcs.TrySetResult(response);
        }

        private void TryCancelPending(Guid jobId, CancellationToken token)
        {
            if (!_pending.TryRemove(jobId, out var tcs))
                return;

            if (token.IsCancellationRequested)
                tcs.TrySetCanceled(token);
        }
        
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _networking.RemoteJobResponseReceived -= OnResponse;

            foreach (var pair in _pending.ToArray())
                pair.Value.TrySetCanceled();

            _pending.Clear();
        }
    }
}
