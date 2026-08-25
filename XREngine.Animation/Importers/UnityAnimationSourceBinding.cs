using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Normalized identity and execution result for one serialized Unity binding.
/// </summary>
[MemoryPackable]
public sealed partial class UnityAnimationSourceBinding
{
    public EUnityAnimationDataDomain Domain { get; set; }
    public EUnityAnimationCapabilityState State { get; set; }
    public string SourceField { get; set; } = string.Empty;
    public string NodePath { get; set; } = string.Empty;
    public string Attribute { get; set; } = string.Empty;
    public int? ClassId { get; set; }
    public string RuntimeTarget { get; set; } = string.Empty;
    public string Diagnostic { get; set; } = string.Empty;
}
