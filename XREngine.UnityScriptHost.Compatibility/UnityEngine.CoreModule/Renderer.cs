using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares renderer materials. Access reports unsupported until object and asset services exist.</summary>
public class Renderer : Component
{
    /// <summary>Reports unsupported until the worker can get or set material references.</summary>
    public Material[] sharedMaterials
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Renderer.get_sharedMaterials()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Renderer.set_sharedMaterials(UnityEngine.Material[])");
    }

    /// <summary>Reports unsupported until the worker can get or set the first material reference.</summary>
    public Material? sharedMaterial
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Renderer.get_sharedMaterial()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Renderer.set_sharedMaterial(UnityEngine.Material)");
    }
}
