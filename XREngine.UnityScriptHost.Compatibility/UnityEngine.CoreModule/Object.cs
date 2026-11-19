using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares Unity object operations. Construction and behavior report unsupported until the worker has an object store.</summary>
public class Object
{
    /// <summary>Rejects construction until the worker supplies object identity and lifetime.</summary>
    public Object() => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object..ctor()");

    /// <summary>Reports unsupported until the worker can get or set the object name.</summary>
    public string name
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.get_name()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.set_name(System.String)");
    }

    /// <summary>Reports unsupported until the worker can apply object visibility and persistence flags.</summary>
    public HideFlags hideFlags
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.get_hideFlags()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.set_hideFlags(UnityEngine.HideFlags)");
    }

    /// <summary>Reports unsupported until the worker can get an object identity.</summary>
    public int GetInstanceID() => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.GetInstanceID()");

    /// <summary>Reports unsupported until the worker can clone an object.</summary>
    public static T Instantiate<T>(T original) where T : Object
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.Instantiate<T>(T)");

    /// <summary>Reports unsupported until the worker can clone an object.</summary>
    public static Object Instantiate(Object original)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.Instantiate(UnityEngine.Object)");

    /// <summary>Reports unsupported until the worker can remove an object.</summary>
    public static void Destroy(Object obj)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.Destroy(UnityEngine.Object)");

    /// <summary>Reports unsupported until the worker can remove an object immediately.</summary>
    public static void DestroyImmediate(Object obj)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.DestroyImmediate(UnityEngine.Object)");

    /// <summary>Reports unsupported until the worker can remove objects and enforce asset deletion policy.</summary>
    public static void DestroyImmediate(Object obj, bool allowDestroyingAssets)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.DestroyImmediate(UnityEngine.Object,System.Boolean)");

    /// <summary>Reports unsupported until the worker can find objects by type.</summary>
    public static T[] FindObjectsOfType<T>() where T : Object
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.FindObjectsOfType<T>()");

    /// <summary>Reports unsupported until the worker can compare object identities and destroyed states.</summary>
    public static bool operator ==(Object? left, Object? right)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.op_Equality(UnityEngine.Object,UnityEngine.Object)");

    /// <summary>Reports unsupported until the worker can compare object identities and destroyed states.</summary>
    public static bool operator !=(Object? left, Object? right)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.op_Inequality(UnityEngine.Object,UnityEngine.Object)");

    /// <summary>Reports unsupported until the worker can check object lifetime.</summary>
    public static implicit operator bool(Object? value)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.op_Implicit(UnityEngine.Object)");

    /// <summary>Reports unsupported until the worker can compare object identity.</summary>
    public override bool Equals(object? obj)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.Equals(System.Object)");

    /// <summary>Reports unsupported until the worker can hash object identity.</summary>
    public override int GetHashCode()
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Object.GetHashCode()");
}
