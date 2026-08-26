using System;
using XREngine.Networking;

namespace XREngine
{
    public sealed partial record ServerSessionContext(Guid SessionId, IRuntimeNetworkWorldContext WorldContext, WorldAssetIdentity? WorldAsset = null);
    public sealed partial record ServerSessionContext
    {
        public ServerSessionContext(Guid sessionId, object worldInstance, WorldAssetIdentity? worldAsset = null)
            : this(sessionId, RuntimeNetworkingHostServices.Current.CreateWorldContext(worldInstance)
                ?? throw new InvalidOperationException("The active runtime networking host cannot adapt the supplied world instance."), worldAsset)
        {
        }
    }
    public sealed record ServerJoinAdmissionResult(ServerSessionContext? SessionContext, AdmissionFailureReason FailureReason = AdmissionFailureReason.None, string? Message = null)
    {
        public bool Success => SessionContext is not null && FailureReason == AdmissionFailureReason.None;
    }

    public sealed record ServerSessionPlayerEvent(Guid SessionId, string ClientId, int ServerPlayerIndex, Guid TransformId);

}
