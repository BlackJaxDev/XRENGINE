using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>Finds the camera data of a bound node. The methods never allocate.</summary>
internal static class CutsceneCameraLookup
{
    /// <summary>Gets the camera component of a node, or null if the node has none.</summary>
    public static CameraComponent? FindCamera(SceneNode? node)
        => node?.GetComponent<CameraComponent>();

    /// <summary>Gets the perspective parameters of the camera on a node, or null if the node has none.</summary>
    public static XRPerspectiveCameraParameters? FindPerspective(SceneNode? node)
        => FindCamera(node)?.Camera.Parameters as XRPerspectiveCameraParameters;
}
