using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>An executable Unity AnimationEvent retained without Unity runtime types.</summary>
[MemoryPackable]
public sealed partial class UnityAnimationEvent
{
    public float Time { get; set; }
    public string FunctionName { get; set; } = string.Empty;
    public string StringParameter { get; set; } = string.Empty;
    public float FloatParameter { get; set; }
    public int IntParameter { get; set; }
    public UnityAssetReference ObjectReferenceParameter { get; set; }
    public EUnityAnimationEventMessageOptions MessageOptions { get; set; }
    public int SourceOrder { get; set; }
}
