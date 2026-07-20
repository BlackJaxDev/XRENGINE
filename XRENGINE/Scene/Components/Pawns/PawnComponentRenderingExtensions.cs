namespace XREngine.Components;

/// <summary>Rendering conveniences for the host-independent <see cref="PawnComponent"/>.</summary>
public static class PawnComponentRenderingExtensions
{
    /// <summary>Resolves the pawn's live camera through the configured runtime host.</summary>
    public static CameraComponent? GetCamera(this PawnComponent pawn)
        => ((XREngine.Input.IRuntimeInputControllablePawn)pawn).RuntimeCameraComponent as CameraComponent;
}
