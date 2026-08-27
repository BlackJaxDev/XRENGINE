using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private UnityAnimationBindingDescriptor[] _unityGenericBindings = [];

    /// <summary>Typed generic and object-reference bindings emitted by Unity import.</summary>
    [MemoryPackIgnore]
    public UnityAnimationBindingDescriptor[] UnityGenericBindings
    {
        get => _unityGenericBindings;
        set => SetField(ref _unityGenericBindings, value ?? []);
    }
}
