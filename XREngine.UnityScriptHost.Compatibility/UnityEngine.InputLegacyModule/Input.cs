using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares legacy input queries without reading device state.</summary>
public class Input
{
    /// <summary>Rejects construction because input state belongs to the live engine.</summary>
    public Input()
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Input..ctor()");

    /// <summary>Rejects mouse button reads until the worker has input state.</summary>
    public static bool GetMouseButton(int button)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Input.GetMouseButton(System.Int32)");

    /// <summary>Rejects mouse button edge reads until the worker has input state.</summary>
    public static bool GetMouseButtonDown(int button)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Input.GetMouseButtonDown(System.Int32)");

    /// <summary>Rejects mouse position reads until the worker has input state.</summary>
    public static Vector3 mousePosition
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Input.get_mousePosition()");
}
