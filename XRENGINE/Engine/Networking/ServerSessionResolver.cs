using System;
using XREngine.Networking;
using XREngine.Rendering;

namespace XREngine
{
    public sealed record ServerSessionContext(Guid SessionId, XRWorldInstance WorldInstance, WorldAssetIdentity? WorldAsset = null);
    public sealed record ServerJoinAdmissionResult(ServerSessionContext? SessionContext, AdmissionFailureReason FailureReason = AdmissionFailureReason.None, string? Message = null)
    {
        public bool Success => SessionContext is not null && FailureReason == AdmissionFailureReason.None;
    }

    public sealed record ServerSessionPlayerEvent(Guid SessionId, string ClientId, int ServerPlayerIndex, Guid TransformId);

    public static partial class Engine
    {
        /// <summary>
        /// Allows a host to supply a concrete realtime session for an incoming direct join.
        /// </summary>
        public static Func<PlayerJoinRequest, ServerSessionContext?>? ServerSessionResolver { get; set; }
        public static Func<PlayerJoinRequest, ServerJoinAdmissionResult?>? ServerJoinAdmissionResolver { get; set; }
        public static Action<ServerSessionPlayerEvent>? ServerPlayerConnected { get; set; }
        public static Action<ServerSessionPlayerEvent>? ServerPlayerDisconnected { get; set; }
        public static Action<ServerSessionPlayerEvent>? ServerPlayerHeartbeatObserved { get; set; }
    }
}
