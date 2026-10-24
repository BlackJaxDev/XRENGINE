namespace XREngine.Rendering.UI;

/// <summary>Frozen opt-in request for a canvas-prefix scene backdrop.</summary>
public readonly record struct UIBackdropRequest(bool Enabled, float BlurStrength)
{
    public bool IsDefined => Enabled && float.IsFinite(BlurStrength) && BlurStrength is >= 0.0f and <= 32.0f;
}
