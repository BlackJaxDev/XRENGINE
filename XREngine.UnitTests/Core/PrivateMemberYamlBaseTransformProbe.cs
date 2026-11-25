using XREngine.Core.Files;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Core;

/// <summary>Provides inherited and ignored members for canonical YAML roundtrips.</summary>
public class PrivateMemberYamlBaseTransformProbe : Transform
{
    [YamlMember(Alias = "InheritedAssetAlias", Order = 10)]
    public TextFile? InheritedAsset { get; set; }

    [YamlIgnore]
    public TextFile? IgnoredAsset { get; set; }
}
