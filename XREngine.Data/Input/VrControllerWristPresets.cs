using System.Numerics;

namespace XREngine.Input;

/// <summary>Explicitly unqualified geometric starting offsets, with per-profile player overrides.</summary>
public static class VrControllerWristPresets
{
    // Grip is modeled at the palm. This neutral estimate places the wrist 6 cm behind and 2 cm below it.
    // These are engine geometry assumptions, not device-vendor measurements or hardware acceptance.
    public static readonly Vector3 GeometricDefault = new(0, -0.02f, 0.06f);
    public static bool IsSupportedProfile(string? profile) => profile is
        "/interaction_profiles/valve/index_controller" or "/interaction_profiles/htc/vive_controller" or
        "/interaction_profiles/khr/simple_controller" or "/interaction_profiles/oculus/touch_controller" or
        "/interaction_profiles/microsoft/motion_controller";

    public static Vector3 Resolve(string? profile, UserSettings? settings)
    {
        if (profile is not null && settings?.VrControllerWristOffsets is { } offsets
            && offsets.TryGetValue(profile, out Vector3 configured) && IsValid(configured))
            return configured;
        return GeometricDefault;
    }

    public static bool IsValid(Vector3 offset)
        => float.IsFinite(offset.X) && float.IsFinite(offset.Y) && float.IsFinite(offset.Z) && offset.LengthSquared() <= 0.09f;
}
