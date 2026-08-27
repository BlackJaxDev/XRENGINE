using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.Components;

public partial class AnimStateMachineComponent
{
    private UnityAnimationBindingRuntime? _unityAnimationBindingRuntime;
    private readonly HashSet<UnityAnimationBindingDescriptor> _failedUnityAnimationBindings = [];

    /// <summary>Applies one typed scalar channel imported from a Unity serialized binding.</summary>
    public void SetUnityAnimationFloat(UnityAnimationBindingDescriptor binding, float value)
    {
        UnityAnimationBindingRuntime runtime = GetUnityAnimationBindingRuntime();
        if (runtime.TrySetFloat(binding, value, out string diagnostic))
            return;
        ReportUnityAnimationBindingFailure(binding, diagnostic);
    }

    /// <summary>Applies one typed object-reference key imported from Unity.</summary>
    public void SetUnityAnimationObjectReference(
        UnityAnimationBindingDescriptor binding,
        UnityAssetReference value)
    {
        UnityAnimationBindingRuntime runtime = GetUnityAnimationBindingRuntime();
        if (runtime.TrySetObjectReference(binding, value, out string diagnostic))
            return;
        ReportUnityAnimationBindingFailure(binding, diagnostic);
    }

    private string? ValidateUnityAnimationClipBindings(AnimationClip clip)
        => GetUnityAnimationBindingRuntime().TryValidate(clip, out string diagnostic)
            ? null
            : diagnostic;

    private UnityAnimationBindingRuntime GetUnityAnimationBindingRuntime()
        => _unityAnimationBindingRuntime ??= new UnityAnimationBindingRuntime(this);

    private void ResetUnityAnimationBindings()
    {
        _unityAnimationBindingRuntime?.Clear();
        _failedUnityAnimationBindings.Clear();
    }

    private void ReportUnityAnimationBindingFailure(
        UnityAnimationBindingDescriptor binding,
        string diagnostic)
    {
        if (!_failedUnityAnimationBindings.Add(binding))
            return;
        Debug.Animation($"[UnityAnimationBinding] state machine failed '{binding.NodePath}:{binding.Attribute}': {diagnostic}");
    }
}
