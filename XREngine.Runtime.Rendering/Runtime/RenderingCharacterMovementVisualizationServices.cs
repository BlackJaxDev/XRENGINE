using XREngine.Components.Movement;

namespace XREngine.Rendering;

/// <summary>Creates and tears down rendering-only movement diagnostic companions.</summary>
public sealed class RenderingCharacterMovementVisualizationServices : IRuntimeCharacterMovementVisualizationServices
{
    public void Attach(CharacterMovement3DComponent movement)
    {
        if (movement.SceneNode is { IsDestroyed: false } node &&
            node.GetComponent<CharacterMovementDebugRenderComponent>() is null)
        {
            node.AddComponent<CharacterMovementDebugRenderComponent>();
        }
    }

    public void Detach(CharacterMovement3DComponent movement)
        => movement.SceneNode?.GetComponent<CharacterMovementDebugRenderComponent>()?.Destroy();
}
