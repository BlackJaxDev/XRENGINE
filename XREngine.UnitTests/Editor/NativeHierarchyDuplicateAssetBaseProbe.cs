using XREngine.Core.Files;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Editor;

/// <summary>Exercises inherited serialized and ignored asset members.</summary>
public class NativeHierarchyDuplicateAssetBaseProbe : Transform
{
    [YamlMember(Alias = "InheritedAssetAlias")]
    public TextFile? InheritedAsset { get; set; }
    [YamlIgnore]
    public TextFile? IgnoredAsset { get; set; }
}
