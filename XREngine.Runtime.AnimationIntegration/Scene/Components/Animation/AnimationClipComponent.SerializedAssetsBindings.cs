using XREngine.Animation;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

public partial class AnimationClipComponent
{
    private ImportedAnimationBindingRuntime? _importedAnimationBindingRuntime;
    private readonly HashSet<ImportedAnimationBindingDescriptor> _failedImportedAnimationBindings = [];

    /// <summary>Applies one typed scalar channel imported from a Unity serialized binding.</summary>
    public void SetImportedAnimationFloat(ImportedAnimationBindingDescriptor binding, float value)
    {
        ImportedAnimationBindingRuntime runtime = GetImportedAnimationBindingRuntime();
        if (runtime.TrySetFloat(binding, value, out string diagnostic))
            return;
        ReportImportedAnimationBindingFailure(binding, diagnostic);
    }

    /// <summary>Applies one typed object-reference key imported from Unity.</summary>
    public void SetImportedAnimationObjectReference(
        ImportedAnimationBindingDescriptor binding,
        SourceAssetReference value)
    {
        ImportedAnimationBindingRuntime runtime = GetImportedAnimationBindingRuntime();
        if (runtime.TrySetObjectReference(binding, value, out string diagnostic))
            return;
        ReportImportedAnimationBindingFailure(binding, diagnostic);
    }

    /// <summary>
    /// Ignores Animated Animator Parameter curves when an imported clip plays directly
    /// outside an <see cref="AnimStateMachineComponent"/>.
    /// </summary>
    public void SetAnimatedFloatParameter(string parameterName, float value)
    {
    }

    private bool TryValidateImportedAnimationBindings(AnimationClip clip, out string diagnostic)
        => GetImportedAnimationBindingRuntime().TryValidate(clip, out diagnostic);

    private ImportedAnimationBindingRuntime GetImportedAnimationBindingRuntime()
        => _importedAnimationBindingRuntime ??= new ImportedAnimationBindingRuntime(this);

    private void ResetImportedAnimationBindings()
    {
        _importedAnimationBindingRuntime?.Clear();
        _failedImportedAnimationBindings.Clear();
    }

    private void ReportImportedAnimationBindingFailure(
        ImportedAnimationBindingDescriptor binding,
        string diagnostic)
    {
        if (!_failedImportedAnimationBindings.Add(binding))
            return;
        Debug.Animation($"[UnityAnimationBinding] '{Animation?.Name}' failed '{binding.NodePath}:{binding.Attribute}': {diagnostic}");
    }
}
