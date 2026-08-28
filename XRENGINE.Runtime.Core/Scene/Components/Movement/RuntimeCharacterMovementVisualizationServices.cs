namespace XREngine.Components.Movement;

/// <summary>
/// Optional rendering bridge for character movement diagnostics. Runtime.Core
/// only publishes lifecycle; Rendering owns the visual implementation.
/// </summary>
public interface IRuntimeCharacterMovementVisualizationServices
{
    void Attach(CharacterMovement3DComponent movement);
    void Detach(CharacterMovement3DComponent movement);
}

/// <summary>Process-wide optional character-movement visualization seam.</summary>
public static class RuntimeCharacterMovementVisualizationServices
{
    public static IRuntimeCharacterMovementVisualizationServices? Current { get; set; }
}
