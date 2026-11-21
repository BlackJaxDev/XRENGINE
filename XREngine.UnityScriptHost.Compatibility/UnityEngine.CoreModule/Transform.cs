using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares local and world transforms. Access reports unsupported until the worker has an object store.</summary>
public class Transform : Component
{
    /// <summary>Reports unsupported until world directions have a native authoring mapping.</summary>
    public Vector3 forward
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_forward()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_forward(UnityEngine.Vector3)");
    }

    /// <summary>Reports unsupported until local points can map to native world transforms.</summary>
    public Vector3 TransformPoint(Vector3 position)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.TransformPoint(UnityEngine.Vector3)");

    /// <summary>Reports unsupported until world points can map to native local transforms.</summary>
    public Vector3 InverseTransformPoint(Vector3 position)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.InverseTransformPoint(UnityEngine.Vector3)");

    /// <summary>Reports unsupported until the worker can find the hierarchy root.</summary>
    public Transform root => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_root()");

    /// <summary>Reports unsupported until the worker can calculate world scale from the hierarchy.</summary>
    public Vector3 lossyScale => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_lossyScale()");

    /// <summary>Reports unsupported until the worker can get or set the parent transform.</summary>
    public Transform? parent
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_parent()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_parent(UnityEngine.Transform)");
    }

    /// <summary>Reports unsupported until the worker can get or set world position.</summary>
    public Vector3 position
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_position()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_position(UnityEngine.Vector3)");
    }

    /// <summary>Reports unsupported until the worker can get or set local position.</summary>
    public Vector3 localPosition
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_localPosition()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_localPosition(UnityEngine.Vector3)");
    }

    /// <summary>Reports unsupported until the worker can get or set world rotation.</summary>
    public Quaternion rotation
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_rotation()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_rotation(UnityEngine.Quaternion)");
    }

    /// <summary>Reports unsupported until the worker can get or set local rotation.</summary>
    public Quaternion localRotation
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_localRotation()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_localRotation(UnityEngine.Quaternion)");
    }

    /// <summary>Reports unsupported until the worker can get or set local scale.</summary>
    public Vector3 localScale
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_localScale()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.set_localScale(UnityEngine.Vector3)");
    }

    /// <summary>Reports unsupported until the worker can count children.</summary>
    public int childCount => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.get_childCount()");

    /// <summary>Reports unsupported until the worker can get a child by index.</summary>
    public Transform GetChild(int index)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.GetChild(System.Int32)");

    /// <summary>Reports unsupported until the worker can check hierarchy ancestry.</summary>
    public bool IsChildOf(Transform parent)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.IsChildOf(UnityEngine.Transform)");

    /// <summary>Reports unsupported until the worker can change hierarchy parentage.</summary>
    public void SetParent(Transform? parent, bool worldPositionStays = true)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Transform.SetParent(UnityEngine.Transform,System.Boolean)");
}
