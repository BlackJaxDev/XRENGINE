using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private ImportedAnimationBindingDescriptor[] _importedGenericBindings = [];

    /// <summary>Typed generic and object-reference bindings emitted by Unity import.</summary>
    [MemoryPackIgnore]
    public ImportedAnimationBindingDescriptor[] ImportedGenericBindings
    {
        get => _importedGenericBindings;
        set => SetField(ref _importedGenericBindings, value ?? []);
    }
}
