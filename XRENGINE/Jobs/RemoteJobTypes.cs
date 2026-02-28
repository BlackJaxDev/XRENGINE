using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine
{
    /// <summary>
    /// Describes how data should flow for a remote job dispatch.
    /// </summary>
    public enum RemoteJobTransferMode
    {
        /// <summary>Request the remote machine to load or gather the data on its own.</summary>
        RequestFromRemote = 0,
        /// <summary>Send the local payload to the remote machine to be processed.</summary>
        PushDataToRemote = 1,
    }

    /// <summary>
    /// An envelope describing a remote job request that can be forwarded to another machine.
    /// </summary>
    public sealed class RemoteJobRequest
    {
        public Guid JobId { get; init; } = Guid.NewGuid();
        public string Operation { get; init; } = string.Empty;
        public RemoteJobTransferMode TransferMode { get; init; } = RemoteJobTransferMode.RequestFromRemote;
        public byte[]? Payload { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
        /// <summary>Sender identity (peer id) to allow targeted responses.</summary>
        public string? SenderId { get; init; }
        /// <summary>Optional target peer; null means broadcast.</summary>
        public string? TargetId { get; init; }

        /// <summary>
        /// Well-known operation labels for remote job routing.
        /// </summary>
        public static class Operations
        {
            public const string AssetLoad = "asset/load";
        }
    }

    /// <summary>
    /// Response payload for a remote job execution.
    /// </summary>
    public sealed class RemoteJobResponse
    {
        public Guid JobId { get; init; }
        public bool Success { get; init; }
        public byte[]? Payload { get; init; }
        public string? Error { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
        public string? SenderId { get; init; }
        public string? TargetId { get; init; }

        public static RemoteJobResponse FromError(Guid id, string message)
            => new() { JobId = id, Success = false, Error = message };
    }

    /// <summary>
    /// Transport abstraction for shipping remote jobs to another PC.
    /// </summary>
    public interface IRemoteJobTransport
    {
        bool IsConnected { get; }
        Task<RemoteJobResponse> SendAsync(RemoteJobRequest request, CancellationToken cancellationToken);
    }

}
