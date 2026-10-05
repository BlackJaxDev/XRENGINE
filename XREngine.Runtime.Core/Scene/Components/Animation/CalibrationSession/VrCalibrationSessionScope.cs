using XREngine.Input;

namespace XREngine.Components.Animation;

/// <summary>Exact application-player, avatar-asset, provider-session and reference-basis scope for transient calibration.</summary>
public readonly record struct VrCalibrationSessionScope(
    string PlayerIdentity, string AvatarIdentity, RuntimeVrRuntimeKind Provider, long ProviderGeneration, long ReferenceSpaceVersion);
