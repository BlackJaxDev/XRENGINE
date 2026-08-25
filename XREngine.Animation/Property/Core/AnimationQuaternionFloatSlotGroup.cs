namespace XREngine.Animation;

/// <summary>
/// Identifies four scalar animation slots that together represent one quaternion.
/// </summary>
public readonly record struct AnimationQuaternionFloatSlotGroup(
    int XIndex,
    int YIndex,
    int ZIndex,
    int WIndex)
{
    public bool IsValid
        => XIndex >= 0 && YIndex >= 0 && ZIndex >= 0 && WIndex >= 0;
}
