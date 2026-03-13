using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;

namespace XREngine.Rendering
{
    public abstract partial class XRTexture
    {
        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
        protected void WriteTextureAssetBase(CookedBinaryWriter writer)
            => writer.WriteBaseObject<XRAsset>(this);

        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
        protected void ReadTextureAssetBase(CookedBinaryReader reader)
            => reader.ReadBaseObject<XRAsset>(this);

        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
        protected long CalculateTextureAssetBaseSize()
            => CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset));
    }
}
