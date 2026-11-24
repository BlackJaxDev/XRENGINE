using XREngine.Input;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Publishes a local-player teleport as <see cref="EVrPoseDiscontinuity.Teleport"/> through <see cref="RuntimeVrDiscontinuityServices"/>.</summary>
internal sealed class EngineRuntimeVrTeleportPublisher : IRuntimeVrTeleportPublisher
{
    private EngineRuntimeVrTeleportPublisher()
    {
    }

    /// <summary>The one instance.</summary>
    public static EngineRuntimeVrTeleportPublisher Instance { get; } = new();

    /// <inheritdoc/>
    public void PublishLocalPlayerTeleport()
        => RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.Teleport);
}
