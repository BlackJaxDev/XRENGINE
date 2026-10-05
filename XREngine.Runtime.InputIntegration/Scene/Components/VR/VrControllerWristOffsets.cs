using System.Numerics;

namespace XREngine.Components.VR;

/// <summary>Engine grip-space wrist convention. Grip poses already normalize the physical controller profile.</summary>
public static class VrControllerWristOffsets
{
    /// <summary>
    /// Engine-authored presets in OpenXR's normalized grip space. These are anatomical starting values,
    /// not vendor measurements; the player component permits per-hand overrides for validated hardware.
    /// </summary>
    public static Matrix4x4 ForHand(string? interactionProfile, bool leftHand)
    {
        Vector3 wrist = interactionProfile switch
        {
            "/interaction_profiles/valve/index_controller" => new(0.015f, -0.025f, 0.055f),
            "/interaction_profiles/htc/vive_controller" => new(0.015f, -0.025f, 0.055f),
            "/interaction_profiles/khr/simple_controller" => new(0.015f, -0.025f, 0.055f),
            "/interaction_profiles/oculus/touch_controller" => new(0.015f, -0.025f, 0.055f),
            "/interaction_profiles/microsoft/motion_controller" => new(0.015f, -0.025f, 0.055f),
            _ => new(0.015f, -0.025f, 0.055f),
        };
        if (leftHand) wrist.X = -wrist.X;
        return Matrix4x4.CreateTranslation(wrist);
    }
}
