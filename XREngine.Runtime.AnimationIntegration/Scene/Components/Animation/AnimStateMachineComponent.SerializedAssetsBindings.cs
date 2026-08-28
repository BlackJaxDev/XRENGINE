using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.Components;

public partial class AnimStateMachineComponent
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

    private string? ValidateImportedAnimationClipBindings(AnimationClip clip)
        => GetImportedAnimationBindingRuntime().TryValidate(clip, out string diagnostic)
            ? null
            : diagnostic;

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
        Debug.Animation($"[UnityAnimationBinding] state machine failed '{binding.NodePath}:{binding.Attribute}': {diagnostic}");
    }
}
