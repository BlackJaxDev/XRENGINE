using XREngine.UnityScriptHost.Execution;

namespace UnityEngine.SceneManagement;

/// <summary>Identifies a Unity scene. Queries report unsupported until the worker has scene state.</summary>
public struct Scene
{
    /// <summary>Reports unsupported until the worker can check scene load state.</summary>
    public bool isLoaded => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SceneManagement.Scene.get_isLoaded()");

    /// <summary>Reports unsupported until the worker can list scene roots.</summary>
    public UnityEngine.GameObject[] GetRootGameObjects()
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SceneManagement.Scene.GetRootGameObjects()");
}
