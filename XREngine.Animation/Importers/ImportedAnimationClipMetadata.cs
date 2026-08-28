using System.Numerics;
using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>Behaviorally relevant AnimationClip header metadata.</summary>
[MemoryPackable]
public sealed partial class ImportedAnimationClipMetadata
{
    public int SampleRate { get; set; } = 30;
    public EImportedAnimationWrapMode WrapMode { get; set; }
    public bool Legacy { get; set; }
    public bool Compressed { get; set; }
    public bool UseHighQualityCurve { get; set; }
    public Vector3 BoundsCenter { get; set; }
    public Vector3 BoundsExtents { get; set; }
}
