using System.Diagnostics.CodeAnalysis;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Serialization;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class RegistrationLeaseGroupTests : IAssetTypeHintProvider
{
    [Test]
    public void Create_WhenInstallationFails_RollsBackCompletedRegistrations()
    {
        Should.Throw<InvalidOperationException>(() =>
            RegistrationLeaseGroup.Create(group =>
            {
                group.Add(AssetTypeHintProviders.Install(this));
                group.Add(AssetTypeHintProviders.Install(this));
            }));

        using IDisposable lease = AssetTypeHintProviders.Install(this);
    }

    public bool TryResolveLegacyRootKey(
        string rootKey,
        Type expectedType,
        [NotNullWhen(true)] out Type? assetType)
    {
        assetType = null;
        return false;
    }
}
