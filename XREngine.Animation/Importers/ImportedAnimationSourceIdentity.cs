using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Path-independent source and settings identity for a Unity AnimationClip.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationSourceIdentity
{
    public string SourceFormat { get; set; } = "UnityYamlAnimationClip";
    public int SerializedVersion { get; set; }
    public string SourceContentSha256 { get; set; } = string.Empty;
    public string ImportSettingsSha256 { get; set; } = string.Empty;
}
