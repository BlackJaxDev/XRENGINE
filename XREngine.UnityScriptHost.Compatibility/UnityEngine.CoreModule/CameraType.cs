namespace UnityEngine;

/// <summary>Identifies the camera type stored by Unity.</summary>
[Flags]
public enum CameraType
{
    Game = 1,
    SceneView = 2,
    Preview = 4,
    VR = 8,
    Reflection = 16,
}
