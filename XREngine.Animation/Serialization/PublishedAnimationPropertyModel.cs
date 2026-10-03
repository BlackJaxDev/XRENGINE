using MemoryPack;
using XREngine.Animation;

namespace XREngine;

/// <summary>Closed runtime state for a built-in property animation.</summary>
[MemoryPackable]
internal sealed partial class PublishedAnimationPropertyModel
{
    public byte Kind { get; set; }
    public float LengthInSeconds { get; set; }
    public bool Looped { get; set; }
    public float Speed { get; set; }
    public int AuthoredFrameCount { get; set; }
    public int AuthoredFramesPerSecond { get; set; }
    public byte[]? DefaultValue { get; set; }
    public int BakedFramesPerSecond { get; set; }
    public int BakedFrameCount { get; set; }
    public EAnimationValueCompressionAlgorithm RequestedCompression { get; set; }
    public EAnimationValueCompressionAlgorithm EncodedCompression { get; set; }
    public bool IsBaked { get; set; }
    public byte[][]? BakedValues { get; set; }
    public EKeyframeInfinityMode PreInfinityMode { get; set; }
    public EKeyframeInfinityMode PostInfinityMode { get; set; }
    public bool ConstrainKeyframedFPS { get; set; }
    public bool LerpConstrainedFPS { get; set; }
    public bool UseTangentRelativeSpeed { get; set; }
    public PropAnimObject.EDiscreteValueRounding DiscreteValueRounding { get; set; }
    public List<PublishedAnimationKeyframeModel> Keyframes { get; set; } = [];
}
