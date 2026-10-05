using MemoryPack;
using XREngine.Animation;

namespace XREngine;

/// <summary>Closed keyframe state, with typed value payloads decoded by the published value codec.</summary>
[MemoryPackable]
internal sealed partial class PublishedAnimationKeyframeModel
{
    public float Second { get; set; }
    public int AuthoredFrameIndex { get; set; }
    public byte[]? Value { get; set; }
    public byte[]? InValue { get; set; }
    public byte[]? OutValue { get; set; }
    public byte[]? InTangent { get; set; }
    public byte[]? OutTangent { get; set; }
    public int InterpolationTypeIn { get; set; }
    public int InterpolationTypeOut { get; set; }
    public bool SyncInOutValues { get; set; }
    public bool SyncInOutTangentDirections { get; set; }
    public bool SyncInOutTangentMagnitudes { get; set; }
    public EKeyframeWeightedMode WeightedMode { get; set; }
    public float InWeight { get; set; }
    public float OutWeight { get; set; }
}
