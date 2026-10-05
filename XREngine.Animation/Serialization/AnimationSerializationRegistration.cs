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
#if !XRE_PUBLISHED
        AnimationClipMemoryPackRegistration.EnsureRegistered();
        AnimStateMachineMemoryPackRegistration.EnsureRegistered();
        BlendTreeMemoryPackRegistration.EnsureRegistered();
#endif

        return RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(global::XREngine.Generated.GeneratedRuntimeContracts_XREngine_Animation.Install());
#if !XRE_PUBLISHED
            leases.Add(AnimationCookedBinaryCodecs.Install());
#endif
            leases.Add(ThirdPartyCacheCodecRegistry.Install(new AnimationClipBinaryCacheCodec()));
            leases.Add(ThirdPartyAssetTypeRegistry.Install(nameof(XREngine.Animation), typeof(AnimationClip)));
#if !XRE_PUBLISHED
            leases.Add(YamlSerializationContributions.Install(new AnimationYamlContribution()));
#endif
        });
    }

#if !XRE_PUBLISHED
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
#endif

}
