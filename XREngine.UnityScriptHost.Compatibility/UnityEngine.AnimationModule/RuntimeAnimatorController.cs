using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares a controller asset. Asset access reports unsupported until a provider is available.</summary>
public class RuntimeAnimatorController : Object
{
    /// <summary>Reports unsupported until an asset provider can get controller clips.</summary>
    public AnimationClip[] animationClips
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.RuntimeAnimatorController.get_animationClips()");
}
