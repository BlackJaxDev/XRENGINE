using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares a material asset. Access reports unsupported until asset services exist.</summary>
public class Material : Object
{
    /// <summary>Reports unsupported until an asset provider can get or set the shader.</summary>
    public Shader shader
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Material.get_shader()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Material.set_shader(UnityEngine.Shader)");
    }

    /// <summary>Reports unsupported until an asset provider can get a scalar property.</summary>
    public float GetFloat(string name)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Material.GetFloat(System.String)");

    /// <summary>Reports unsupported until an asset workspace can set a scalar property.</summary>
    public void SetFloat(string name, float value)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Material.SetFloat(System.String,System.Single)");
}
