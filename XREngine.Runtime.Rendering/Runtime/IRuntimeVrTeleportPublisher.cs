namespace XREngine;

/// <summary>
/// Publishes a teleport of the local VR player to the pose discontinuity consumers (spectator follow, calibration, and tracker
/// binding). The application host installs it in <see cref="RuntimeVrState.TeleportPublisher"/>, because those consumers are in
/// projects that rendering-side code does not reference.
/// </summary>
public interface IRuntimeVrTeleportPublisher
{
    /// <summary>Publishes one teleport of the local player. It must not allocate and must not throw.</summary>
    void PublishLocalPlayerTeleport();
}
