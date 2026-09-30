namespace XREngine.Components;

/// <summary>Canonical avatar proportions in unscaled model-root units, excluding scene/playspace scale.</summary>
public readonly record struct AvatarBodyMeasurements(float EyeHeight, float ArmSpan, bool UsesEstimatedHandLength, string? Notice);
