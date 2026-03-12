using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeWorldObjectServices : IRuntimeWorldObjectServices
{
    public bool IsClient => Engine.Networking?.IsClient ?? false;

    public float CurrentTimeSeconds => Engine.Time.Timer.Time();

    public float DefaultTimeBetweenReplications => Engine.EffectiveSettings.TimeBetweenReplications;

    public bool HasLocalPlayerAuthority(int owningPlayerServerIndex)
        => Engine.State.LocalPlayers.Any(static player => player is not null)
            && Engine.State.LocalPlayers.Any(player =>
                player is not null
                && owningPlayerServerIndex >= 0
                && owningPlayerServerIndex == player.PlayerInfo.ServerIndex);

    public void OnRuntimeObjectActivated(RuntimeWorldObjectBase worldObject)
        => SyncRenderableWorldBinding(worldObject, worldObject.World, isActiveInHierarchy: true);

    public void OnRuntimeObjectDeactivated(RuntimeWorldObjectBase worldObject)
        => SyncRenderableWorldBinding(worldObject, worldContext: null, isActiveInHierarchy: false);

    public void OnRuntimeObjectWorldChanged(RuntimeWorldObjectBase worldObject, IRuntimeWorldContext? worldContext, bool isActiveInHierarchy)
        => SyncRenderableWorldBinding(worldObject, worldContext, isActiveInHierarchy);

    public void ReplicateObject(RuntimeWorldObjectBase worldObject, bool compress, bool resendOnFailedAck, float maxAckWaitSec)
    {
        if (worldObject is XRWorldObjectBase runtimeObject)
            Engine.Networking?.ReplicateObject(runtimeObject, compress, resendOnFailedAck, maxAckWaitSec);
    }

    public void ReplicatePropertyUpdated<T>(RuntimeWorldObjectBase worldObject, string? propertyName, T value, bool compress, bool resendOnFailedAck, float maxAckWaitSec)
    {
        if (worldObject is XRWorldObjectBase runtimeObject)
            Engine.Networking?.ReplicatePropertyUpdated(runtimeObject, propertyName, value, compress, resendOnFailedAck, maxAckWaitSec);
    }

    public void ReplicateData(RuntimeWorldObjectBase worldObject, byte[] data, string id, bool compress, bool resendOnFailedAck, float maxAckWaitSec)
    {
        if (worldObject is XRWorldObjectBase runtimeObject)
            Engine.Networking?.ReplicateData(runtimeObject, data, id, compress, resendOnFailedAck, maxAckWaitSec);
    }

    private static void SyncRenderableWorldBinding(RuntimeWorldObjectBase worldObject, IRuntimeWorldContext? worldContext, bool isActiveInHierarchy)
    {
        if (worldObject is not IRenderable renderable)
            return;

        XRWorldInstance? worldInstance = isActiveInHierarchy ? worldContext as XRWorldInstance : null;
        foreach (var renderInfo in renderable.RenderedObjects)
            renderInfo.WorldInstance = worldInstance;
    }
}
