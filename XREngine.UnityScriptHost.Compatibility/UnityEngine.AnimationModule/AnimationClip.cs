using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares an animation clip. Asset access reports unsupported until a provider is available.</summary>
public class AnimationClip : Motion
{
    /// <summary>Reports unsupported until an asset provider can get clip duration.</summary>
    public float length => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.AnimationClip.get_length()");
}
