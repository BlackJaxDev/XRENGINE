using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares hierarchy and component operations. All operations report unsupported until the worker has an object store.</summary>
public class GameObject : Object
{
    /// <summary>Reports unsupported until the worker can create a hierarchy node.</summary>
    public GameObject() => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject..ctor()");

    /// <summary>Reports unsupported until the worker can create a named hierarchy node.</summary>
    public GameObject(string name) => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject..ctor(System.String)");

    /// <summary>Reports unsupported until the worker can get the node's transform.</summary>
    public Transform transform => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.get_transform()");

    /// <summary>Reports unsupported until the worker can get the node's local active state.</summary>
    public bool activeSelf => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.get_activeSelf()");

    /// <summary>Reports unsupported until the worker can get the node's effective active state.</summary>
    public bool activeInHierarchy => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.get_activeInHierarchy()");

    /// <summary>Reports unsupported until the worker can set the node's local active state.</summary>
    public void SetActive(bool value)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.SetActive(System.Boolean)");

    /// <summary>Reports unsupported until the worker can find one component by type.</summary>
    public T? GetComponent<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.GetComponent<T>()");

    /// <summary>Reports unsupported until the worker can list components by type.</summary>
    public T[] GetComponents<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.GetComponents<T>()");

    /// <summary>Reports unsupported until the worker can search child components by type.</summary>
    public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.GetComponentsInChildren<T>(System.Boolean)");

    /// <summary>Reports unsupported until the worker can search parent components by type.</summary>
    public T? GetComponentInParent<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.GetComponentInParent<T>()");

    /// <summary>Reports unsupported until the worker can find one component by managed type.</summary>
    public Component? GetComponent(Type type)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.GetComponent(System.Type)");

    /// <summary>Reports unsupported until the worker can add a component of the requested type.</summary>
    public T AddComponent<T>() where T : Component
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.AddComponent<T>()");

    /// <summary>Reports unsupported until the worker can add a component by managed type.</summary>
    public Component AddComponent(Type type)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.GameObject.AddComponent(System.Type)");
}
