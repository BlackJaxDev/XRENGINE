using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;

namespace XREngine.Rendering
{
    public abstract partial class XRTexture
    {
#if !XRE_PUBLISHED
        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
        protected void WriteTextureAssetBase(CookedBinaryWriter writer)
            => writer.WriteBaseObject<XRAsset>(this);

#if !XRE_PUBLISHED
        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
        protected void ReadTextureAssetBase(CookedBinaryReader reader)
            => reader.ReadBaseObject<XRAsset>(this);

#if !XRE_PUBLISHED
        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
        protected long CalculateTextureAssetBaseSize()
            => CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset));
    }
}
