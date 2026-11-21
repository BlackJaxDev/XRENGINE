using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares a shader asset identity. Lookup reports unsupported until an asset provider exists.</summary>
public class Shader : Object
{
    /// <summary>Reports unsupported until an asset provider can find a shader.</summary>
    public static Shader? Find(string name)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Shader.Find(System.String)");
}
