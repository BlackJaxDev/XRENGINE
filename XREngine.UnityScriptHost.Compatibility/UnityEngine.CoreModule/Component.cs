using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares component access. Every access reports unsupported until the worker has an object store.</summary>
public class Component : Object
{
    /// <summary>Reports unsupported until the worker can get the owning game object.</summary>
    public GameObject gameObject => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.get_gameObject()");

    /// <summary>Reports unsupported until the worker can get the owning transform.</summary>
    public Transform transform => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.get_transform()");

    /// <summary>Reports unsupported until the worker can find one component by type.</summary>
    public T? GetComponent<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.GetComponent<T>()");

    /// <summary>Reports unsupported until the worker can find components by type.</summary>
    public T[] GetComponents<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.GetComponents<T>()");

    /// <summary>Reports unsupported until the worker can search child components by type.</summary>
    public T? GetComponentInChildren<T>(bool includeInactive = false) where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.GetComponentInChildren<T>(System.Boolean)");

    /// <summary>Reports unsupported until the worker can search parent components by type.</summary>
    public T? GetComponentInParent<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.GetComponentInParent<T>()");

    /// <summary>Reports unsupported until the worker can search child components by type.</summary>
    public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Component.GetComponentsInChildren<T>(System.Boolean)");
}
