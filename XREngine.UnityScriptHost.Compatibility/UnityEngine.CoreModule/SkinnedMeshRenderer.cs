using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares skin and blendshape data. Access reports unsupported until object and asset services exist.</summary>
public class SkinnedMeshRenderer : Renderer
{
    /// <summary>Reports unsupported until the worker can get or set the skin root.</summary>
    public Transform? rootBone
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.get_rootBone()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.set_rootBone(UnityEngine.Transform)");
    }

    /// <summary>Reports unsupported until the worker can get or set ordered skin bones.</summary>
    public Transform[] bones
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.get_bones()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.set_bones(UnityEngine.Transform[])");
    }

    /// <summary>Reports unsupported until the worker can set offscreen update policy.</summary>
    public bool updateWhenOffscreen
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.get_updateWhenOffscreen()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.set_updateWhenOffscreen(System.Boolean)");
    }

    /// <summary>Reports unsupported until the worker can get or set the mesh reference.</summary>
    public Mesh? sharedMesh
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.get_sharedMesh()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.set_sharedMesh(UnityEngine.Mesh)");
    }

    /// <summary>Reports unsupported until the worker can get a blendshape weight.</summary>
    public float GetBlendShapeWeight(int index)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.GetBlendShapeWeight(System.Int32)");

    /// <summary>Reports unsupported until the worker can set a blendshape weight.</summary>
    public void SetBlendShapeWeight(int index, float value)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SkinnedMeshRenderer.SetBlendShapeWeight(System.Int32,System.Single)");
}
