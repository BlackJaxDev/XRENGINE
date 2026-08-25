using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Animation;

/// <summary>Composes Animation-owned cooked-binary codecs into the lower serializer.</summary>
public static class AnimationCookedBinaryCodecs
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(CookedBinarySerializer.InstallFeatureCodec(new AnimationClipCookedBinaryCodec()));
            leases.Add(CookedBinarySerializer.InstallFeatureCodec(new BlendTreeCookedBinaryCodec()));
            leases.Add(CookedBinarySerializer.InstallFeatureCodec(new AnimStateMachineCookedBinaryCodec()));
        });
}
