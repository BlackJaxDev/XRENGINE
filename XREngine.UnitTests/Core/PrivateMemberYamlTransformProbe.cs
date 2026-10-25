using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Core;

/// <summary>Places an inline asset anchor in a private serialized property.</summary>
public sealed class PrivateMemberYamlTransformProbe : PrivateMemberYamlBaseTransformProbe
{
    [YamlMember(Alias = "PrivateAssetAlias", Order = -10)]
    private TextFile? PrivateAsset { get; set; }

    private int PrivateNumber { get; set; }

    public void SetPrivateMembers(TextFile asset, int number)
    {
        PrivateAsset = asset;
        PrivateNumber = number;
    }

    public TextFile? ReadPrivateAsset() => PrivateAsset;
    public int ReadPrivateNumber() => PrivateNumber;
}
