using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares an animation component. Access reports unsupported until object and asset services exist.</summary>
public class Animator : Behaviour
{
    /// <summary>Reports unsupported until the worker can get or set a controller.</summary>
    public RuntimeAnimatorController? runtimeAnimatorController
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Animator.get_runtimeAnimatorController()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Animator.set_runtimeAnimatorController(UnityEngine.RuntimeAnimatorController)");
    }

    /// <summary>Reports unsupported until the worker can get a humanoid bone transform.</summary>
    public Transform GetBoneTransform(HumanBodyBones bone)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Animator.GetBoneTransform(UnityEngine.HumanBodyBones)");
}
