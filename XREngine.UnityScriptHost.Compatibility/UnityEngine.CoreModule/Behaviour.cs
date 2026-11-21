using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares component enabled state. Access reports unsupported until the worker has an object store.</summary>
public class Behaviour : Component
{
    /// <summary>Reports unsupported until the worker can get or set enabled state.</summary>
    public bool enabled
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Behaviour.get_enabled()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Behaviour.set_enabled(System.Boolean)");
    }

    /// <summary>Reports unsupported until the worker can check effective enabled state.</summary>
    public bool isActiveAndEnabled
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Behaviour.get_isActiveAndEnabled()");
}
