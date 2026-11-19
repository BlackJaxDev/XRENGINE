using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares camera access without reading or changing a live scene.</summary>
public sealed class Camera : Behaviour
{
    /// <summary>Rejects camera enumeration until the worker has a scene camera store.</summary>
    public static Camera[] allCameras
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.get_allCameras()");

    /// <summary>Rejects camera type access until the worker stores camera state.</summary>
    public CameraType cameraType
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.get_cameraType()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.set_cameraType(UnityEngine.CameraType)");
    }

    /// <summary>Rejects display selection until the worker has a display service.</summary>
    public int targetDisplay
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.get_targetDisplay()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.set_targetDisplay(System.Int32)");
    }

    /// <summary>Rejects render target access until the worker has a render target store.</summary>
    public RenderTexture? targetTexture
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.get_targetTexture()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.set_targetTexture(UnityEngine.RenderTexture)");
    }

    /// <summary>Rejects ray projection until the worker has camera projection state.</summary>
    public Ray ScreenPointToRay(Vector3 position)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Camera.ScreenPointToRay(UnityEngine.Vector3)");
}
