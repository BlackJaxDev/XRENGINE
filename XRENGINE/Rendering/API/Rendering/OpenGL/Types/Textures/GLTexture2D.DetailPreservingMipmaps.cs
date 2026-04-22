using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private const float DetailPreservingMipmapStrength = 0.35f;

    public override void GenerateMipmaps()
    {
        if (!TryGenerateMipmapsWithDetailPreservingCompute())
            base.GenerateMipmaps();
    }

    private bool TryGenerateMipmapsWithDetailPreservingCompute()
    {
        if (!Engine.Rendering.Settings.UseDetailPreservingComputeMipmaps)
            return false;

        if (IsMultisampleTarget || Data.SparseTextureStreamingEnabled)
            return false;

        if (!StorageSet || _allocatedLevels <= 1 || _allocatedWidth == 0 || _allocatedHeight == 0)
            return false;

        if (!TryGetDetailPreservingImageFormat(_allocatedInternalFormat, out XRRenderProgram.EImageFormat imageFormat))
            return false;

        XRRenderProgram? program = Renderer.GetOrCreateDetailPreservingMipmapProgram(imageFormat);
        if (program is null)
            return false;

        var previous = Renderer.BoundTexture;
        Bind();

        try
        {
            program.Use();
            program.Sampler("sourceTexture", Data, 0);
            program.Uniform("DetailPreserveStrength", DetailPreservingMipmapStrength);

            for (int dstMip = 1; dstMip < _allocatedLevels; ++dstMip)
            {
                uint mipWidth = Math.Max(1u, _allocatedWidth >> dstMip);
                uint mipHeight = Math.Max(1u, _allocatedHeight >> dstMip);

                program.Uniform("SrcMip", dstMip - 1);
                program.Uniform("DstMipSize", new IVector2((int)mipWidth, (int)mipHeight));
                program.BindImageTexture(1u, Data, dstMip, false, 0, XRRenderProgram.EImageAccess.WriteOnly, imageFormat);

                uint gx = Math.Max(1u, (mipWidth + 15u) / 16u);
                uint gy = Math.Max(1u, (mipHeight + 15u) / 16u);
                program.DispatchCompute(gx, gy, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.OpenGLWarning($"Detail-preserving compute mipmap generation fell back to GL for '{GetDescribingName()}': {ex.Message}");
            return false;
        }
        finally
        {
            if (previous is null || ReferenceEquals(previous, this))
                Unbind();
            else
                previous.Bind();
        }
    }

    private static bool TryGetDetailPreservingImageFormat(ESizedInternalFormat internalFormat, out XRRenderProgram.EImageFormat imageFormat)
    {
        imageFormat = internalFormat switch
        {
            ESizedInternalFormat.R8 => XRRenderProgram.EImageFormat.R8,
            ESizedInternalFormat.R16 => XRRenderProgram.EImageFormat.R16,
            ESizedInternalFormat.R16f => XRRenderProgram.EImageFormat.R16F,
            ESizedInternalFormat.R32f => XRRenderProgram.EImageFormat.R32F,
            ESizedInternalFormat.Rg8 => XRRenderProgram.EImageFormat.RG8,
            ESizedInternalFormat.Rg16 => XRRenderProgram.EImageFormat.RG16,
            ESizedInternalFormat.Rg16f => XRRenderProgram.EImageFormat.RG16F,
            ESizedInternalFormat.Rg32f => XRRenderProgram.EImageFormat.RG32F,
            ESizedInternalFormat.Rgb8 => XRRenderProgram.EImageFormat.RGB8,
            ESizedInternalFormat.Rgb16f => XRRenderProgram.EImageFormat.RGB16F,
            ESizedInternalFormat.Rgb32f => XRRenderProgram.EImageFormat.RGB32F,
            ESizedInternalFormat.Rgba8 => XRRenderProgram.EImageFormat.RGBA8,
            ESizedInternalFormat.Rgba16 => XRRenderProgram.EImageFormat.RGBA16,
            ESizedInternalFormat.Rgba16f => XRRenderProgram.EImageFormat.RGBA16F,
            ESizedInternalFormat.Rgba32f => XRRenderProgram.EImageFormat.RGBA32F,
            _ => default
        };

        return internalFormat is ESizedInternalFormat.R8
            or ESizedInternalFormat.R16
            or ESizedInternalFormat.R16f
            or ESizedInternalFormat.R32f
            or ESizedInternalFormat.Rg8
            or ESizedInternalFormat.Rg16
            or ESizedInternalFormat.Rg16f
            or ESizedInternalFormat.Rg32f
            or ESizedInternalFormat.Rgb8
            or ESizedInternalFormat.Rgb16f
            or ESizedInternalFormat.Rgb32f
            or ESizedInternalFormat.Rgba8
            or ESizedInternalFormat.Rgba16
            or ESizedInternalFormat.Rgba16f
            or ESizedInternalFormat.Rgba32f;
    }
}