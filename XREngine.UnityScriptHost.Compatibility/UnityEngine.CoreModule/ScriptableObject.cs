using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares standalone Unity objects. Creation reports unsupported until the worker has an object store.</summary>
public class ScriptableObject : Object
{
    /// <summary>Creates an object through the active object store.</summary>
    public static T CreateInstance<T>() where T : ScriptableObject
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.ScriptableObject.CreateInstance<T>()");

    /// <summary>Creates an object from its managed type through the active object store.</summary>
    public static ScriptableObject CreateInstance(Type type)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.ScriptableObject.CreateInstance(System.Type)");
}
