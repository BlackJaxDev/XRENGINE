using XREngine.Core.Files.Caching;
using XREngine.Data;
using XREngine.Serialization;
using YamlDotNet.Serialization;

namespace XREngine.Animation;

/// <summary>Installs Animation-owned serializers, cache codecs, and importer identity.</summary>
public static class AnimationSerializationRegistration
{
    public static IDisposable Install()
    {
        AnimationClipMemoryPackRegistration.EnsureRegistered();
        AnimStateMachineMemoryPackRegistration.EnsureRegistered();
        BlendTreeMemoryPackRegistration.EnsureRegistered();

        return RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(AnimationCookedBinaryCodecs.Install());
            leases.Add(AnimationPublishedCookedAssetRegistration.Install());
            leases.Add(ThirdPartyCacheCodecRegistry.Install(new AnimationClipBinaryCacheCodec()));
            leases.Add(ThirdPartyAssetTypeRegistry.Install(nameof(XREngine.Animation), typeof(AnimationClip)));
            leases.Add(YamlSerializationContributions.Install(new AnimationYamlContribution()));
        });
    }

    private sealed class AnimationYamlContribution : IYamlSerializationContribution
    {
        public string OwnerName => nameof(XREngine.Animation);

        public IEnumerable<IYamlTypeConverter> CreateTypeConverters()
            =>
            [
                new AnimationCurveYamlTypeConverter(),
                new AnimationClipYamlTypeConverter(),
                new AnimStateMachineYamlTypeConverter(),
                new BlendTreeYamlTypeConverter(),
            ];
    }

}
