using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

internal static class SamplerConversions
{
    public static (Filter filter, SamplerMipmapMode mipmap) FromMinFilter(ETexMinFilter filter)
        => filter switch
        {
            ETexMinFilter.Nearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
            ETexMinFilter.Linear => (Filter.Linear, SamplerMipmapMode.Nearest),
            ETexMinFilter.NearestMipmapNearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
            ETexMinFilter.LinearMipmapNearest => (Filter.Linear, SamplerMipmapMode.Nearest),
            ETexMinFilter.NearestMipmapLinear => (Filter.Nearest, SamplerMipmapMode.Linear),
            ETexMinFilter.LinearMipmapLinear => (Filter.Linear, SamplerMipmapMode.Linear),
            _ => (Filter.Linear, SamplerMipmapMode.Linear),
        };

    public static Filter FromMagFilter(ETexMagFilter filter)
        => filter switch
        {
            ETexMagFilter.Nearest => Filter.Nearest,
            ETexMagFilter.Linear => Filter.Linear,
            _ => Filter.Linear,
        };

    public static SamplerAddressMode FromWrap(ETexWrapMode mode)
        => mode switch
        {
            ETexWrapMode.Repeat => SamplerAddressMode.Repeat,
            ETexWrapMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
            ETexWrapMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
            ETexWrapMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
            _ => SamplerAddressMode.Repeat,
        };

    public static CompareOp FromCompareOp(ETextureCompareFunc func)
        => func switch
        {
            ETextureCompareFunc.Never => CompareOp.Never,
            ETextureCompareFunc.Less => CompareOp.Less,
            ETextureCompareFunc.Equal => CompareOp.Equal,
            ETextureCompareFunc.LessOrEqual => CompareOp.LessOrEqual,
            ETextureCompareFunc.Greater => CompareOp.Greater,
            ETextureCompareFunc.NotEqual => CompareOp.NotEqual,
            ETextureCompareFunc.GreaterOrEqual => CompareOp.GreaterOrEqual,
            ETextureCompareFunc.Always => CompareOp.Always,
            _ => CompareOp.Never,
        };
}
