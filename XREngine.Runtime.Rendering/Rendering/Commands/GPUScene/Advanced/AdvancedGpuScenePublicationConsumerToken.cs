namespace XREngine.Rendering.Commands;

/// <summary>
/// Generation-checked slot assigned to one ordered publication consumer.
/// </summary>
public readonly record struct AdvancedGpuScenePublicationConsumerToken(
    uint Index,
    uint Generation)
{
    public static AdvancedGpuScenePublicationConsumerToken Invalid => default;

    public bool IsValid => Index != 0u && Generation != 0u;
}
