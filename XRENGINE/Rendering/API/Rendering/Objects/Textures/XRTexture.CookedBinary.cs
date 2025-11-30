using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;

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
