using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private const string TextureFilterAnisotropicExtension = "GL_EXT_texture_filter_anisotropic";
    private const float DesiredMaxAnisotropy = 8.0f;
    private const GLEnum TextureMaxAnisotropyExt = (GLEnum)0x84FE;
    private const GLEnum MaxTextureMaxAnisotropyExt = (GLEnum)0x84FF;

    private static bool? _supportsTextureFilterAnisotropic;
    private static float _maxSupportedTextureAnisotropy = 1.0f;

    private int _progressiveVisibleBaseLevel = -1;
    private int _progressiveVisibleMaxLevel = -1;

    private int ResolveMaxMipLevel(int baseLevel)
    {
        if (IsMultisampleTarget)
            return baseLevel;

        if (Data.SparseTextureStreamingEnabled && Data.SparseTextureStreamingLogicalMipCount > 0)
        {
            int sparseConfiguredMaxLevel = Math.Max(baseLevel, Data.SmallestAllowedMipmapLevel);
            int logicalMaxLevel = Math.Max(baseLevel, Data.SparseTextureStreamingLogicalMipCount - 1);
            int sparseAllocatedMaxLevel = _allocatedLevels > 0
                ? Math.Max(baseLevel, (int)_allocatedLevels - 1)
                : logicalMaxLevel;
            return Math.Max(baseLevel, Math.Min(sparseAllocatedMaxLevel, Math.Max(sparseConfiguredMaxLevel, logicalMaxLevel)));
        }

        int configuredMaxLevel = Math.Max(baseLevel, Data.SmallestAllowedMipmapLevel);
        int naturalMaxLevel = Data.SmallestMipmapLevel;
        int allocatedMaxLevel = _allocatedLevels > 0
            ? Math.Max(baseLevel, (int)_allocatedLevels - 1)
            : naturalMaxLevel; // Mutable storage: glGenerateMipmap/texelFetch can use up to the natural max.

        if (Data.AutoGenerateMipmaps)
            return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, naturalMaxLevel));

        // When multiple Mipmaps entries exist they represent actual uploaded
        // mip data, so cap to the available count. A single entry (the default
        // from CreateFrameBufferTexture and similar) is just the mip-0 descriptor
        // and must NOT clamp GL_TEXTURE_MAX_LEVEL — immutable-storage FBO textures
        // allocate their full mip chain via glTexStorage2D and write individual
        // levels through FBO render targets.
        if (Mipmaps is not null && Mipmaps.Length > 1)
            return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, Math.Min(Mipmaps.Length - 1, configuredMaxLevel)));

        return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, configuredMaxLevel));
    }

    private void ApplyMipRangeParameters()
    {
        int baseLevel = Math.Max(0, Data.LargestMipmapLevel);
        int maxLevel = ResolveMaxMipLevel(baseLevel);

        if (_progressiveVisibleBaseLevel >= 0)
        {
            baseLevel = Math.Max(baseLevel, _progressiveVisibleBaseLevel);
            maxLevel = Math.Min(maxLevel, Math.Max(baseLevel, _progressiveVisibleMaxLevel));
        }

        Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
        Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);
    }

    private void SetProgressiveVisibleMipRange(int baseLevel, int maxLevel)
    {
        _progressiveVisibleBaseLevel = Math.Max(0, baseLevel);
        _progressiveVisibleMaxLevel = Math.Max(_progressiveVisibleBaseLevel, maxLevel);
    }

    private void ClearProgressiveVisibleMipRange()
    {
        _progressiveVisibleBaseLevel = -1;
        _progressiveVisibleMaxLevel = -1;
    }

    private bool TryGetSupportedTextureAnisotropy(out float maxSupportedAnisotropy)
    {
        if (_supportsTextureFilterAnisotropic.HasValue)
        {
            maxSupportedAnisotropy = _maxSupportedTextureAnisotropy;
            return _supportsTextureFilterAnisotropic.Value;
        }

        string[] extensions = Engine.Rendering.State.OpenGLExtensions;
        bool supported = Array.IndexOf(extensions, TextureFilterAnisotropicExtension) >= 0;
        float driverMax = 1.0f;
        if (supported)
        {
            try
            {
                driverMax = MathF.Max(1.0f, Api.GetFloat(MaxTextureMaxAnisotropyExt));
            }
            catch
            {
                supported = false;
            }
        }

        _supportsTextureFilterAnisotropic = supported;
        _maxSupportedTextureAnisotropy = driverMax;
        maxSupportedAnisotropy = driverMax;
        return supported;
    }

    private static bool UsesMipmapFiltering(ETexMinFilter minFilter)
        => minFilter is
            ETexMinFilter.NearestMipmapNearest or
            ETexMinFilter.LinearMipmapNearest or
            ETexMinFilter.NearestMipmapLinear or
            ETexMinFilter.LinearMipmapLinear;

    private void ApplyTextureAnisotropy()
    {
        if (!TryGetSupportedTextureAnisotropy(out float maxSupportedAnisotropy))
            return;

        float anisotropy = UsesMipmapFiltering(Data.MinFilter)
            ? MathF.Min(maxSupportedAnisotropy, DesiredMaxAnisotropy)
            : 1.0f;

        Api.TextureParameter(BindingId, TextureMaxAnisotropyExt, anisotropy);
    }

    protected override void SetParameters()
    {
        base.SetParameters();

        if (IsMultisampleTarget)
            return;

        Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);

        //int dsmode = Data.DepthStencilFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
        //Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);

        int magFilter = (int)ToGLEnum(Data.MagFilter);
        Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

        int minFilter = (int)ToGLEnum(Data.MinFilter);
        Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);
        ApplyTextureAnisotropy();

        int uWrap = (int)ToGLEnum(Data.UWrap);
        Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

        int vWrap = (int)ToGLEnum(Data.VWrap);
        Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

        // Depth-comparison mode for hardware PCF (sampler2DShadow).
        int compareMode = (int)(Data.EnableComparison ? GLEnum.CompareRefToTexture : GLEnum.None);
        Api.TextureParameterI(BindingId, GLEnum.TextureCompareMode, in compareMode);
        if (Data.EnableComparison)
        {
            int compareFunc = (int)ToGLEnum(Data.CompareFunc);
            Api.TextureParameterI(BindingId, GLEnum.TextureCompareFunc, in compareFunc);
        }

        // Clamp base/max mip level to what we actually have.
        // This is critical for render-target textures (e.g., shadow maps) that only define mip 0.
        // Leaving maxLevel at a large default (e.g., 1000) can make the driver treat the attachment as incomplete.
        ApplyMipRangeParameters();

        if (Data.RuntimeManagedProgressiveFinalizePending)
        {
            Data.RuntimeManagedProgressiveFinalizePending = false;
            Data.RuntimeManagedProgressiveUploadActive = false;
            ClearProgressiveVisibleMipRange();
        }
    }
}