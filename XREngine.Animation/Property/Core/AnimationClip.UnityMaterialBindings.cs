using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private UnityMaterialAnimationBinding[] _sourceMaterialBindings = [];
    private string[] _materialBindingDiagnostics = [];

    /// <summary>
    /// Original Unity material paths, slots, property names, and value kinds.
    /// This metadata survives YAML asset serialization and is intentionally
    /// excluded only from the legacy MemoryPack payload.
    /// </summary>
    [MemoryPackIgnore]
    public UnityMaterialAnimationBinding[] SourceMaterialBindings
    {
        get => _sourceMaterialBindings;
        set => SetField(ref _sourceMaterialBindings, value ?? []);
    }

    /// <summary>
    /// Detailed import diagnostics for bindings that were preserved but could
    /// not be turned into a runtime animation member.
    /// </summary>
    [MemoryPackIgnore]
    public string[] MaterialBindingDiagnostics
    {
        get => _materialBindingDiagnostics;
        set => SetField(ref _materialBindingDiagnostics, value ?? []);
    }
}
