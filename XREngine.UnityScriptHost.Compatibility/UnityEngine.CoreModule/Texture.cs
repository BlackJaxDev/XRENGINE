using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares a texture asset. Access reports unsupported until an asset provider exists.</summary>
public class Texture : Object
{
    /// <summary>Reports unsupported until an asset provider can get texture width in pixels.</summary>
    public int width => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Texture.get_width()");

    /// <summary>Reports unsupported until an asset provider can get texture height in pixels.</summary>
    public int height => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Texture.get_height()");
}
