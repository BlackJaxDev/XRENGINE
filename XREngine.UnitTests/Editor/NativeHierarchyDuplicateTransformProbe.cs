using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Editor;

/// <summary>Exposes direct asset aliases and one source serialization callback.</summary>
public sealed class NativeHierarchyDuplicateTransformProbe : NativeHierarchyDuplicateAssetBaseProbe
{
    public TextFile? First;
    public TextFile? Second;
    public TextFile? AutoAsset { get; set; }
    public List<TextFile>? Assets;
    [YamlMember(Alias = "PrivateAssetAlias")]
    private TextFile? PrivateAsset { get; set; }
    public void SetPrivateAsset(TextFile? asset) => PrivateAsset = asset;
    public TextFile? ReadPrivateAsset() => PrivateAsset;
    [YamlIgnore] public Action? Serializing;
    public int SerializationGate
    {
        get { Serializing?.Invoke(); return 1; }
        set { }
    }
}
