using System.IO;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private XRTexture CreateBRDFTexture()
    {
        var tex = PrecomputeBRDF();
        tex.Name ??= BRDFTextureName;
        tex.SamplerName ??= BRDFTextureName;
        return tex;
    }

    private XRTexture CreateDepthStencilTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = DepthStencilTextureName;
            t.SamplerName = DepthStencilTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = DepthStencilTextureName;
            t.SamplerName = DepthStencilTextureName;
            return t;
        }
    }

    private XRTexture CreateForwardPrePassDepthStencilTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardPrePassDepthStencilTextureName;
            t.SamplerName = ForwardPrePassDepthStencilTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardPrePassDepthStencilTextureName;
            t.SamplerName = ForwardPrePassDepthStencilTextureName;
            return t;
        }
    }

    private XRTexture CreateForwardContactDepthStencilTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardContactDepthStencilTextureName;
            t.SamplerName = ForwardContactDepthStencilTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardContactDepthStencilTextureName;
            t.SamplerName = ForwardContactDepthStencilTextureName;
            return t;
        }
    }

    private XRTexture CreateDepthViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(DepthStencilTextureName)!,
                0u, 1u,
                0u, 2u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = DepthViewTextureName,
                SamplerName = DepthViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(DepthStencilTextureName)!,
                0u, 1u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = DepthViewTextureName,
                SamplerName = DepthViewTextureName,
            };
        }
    }

    private XRTexture CreateForwardContactDepthViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(ForwardContactDepthStencilTextureName)!,
                0u, 1u,
                0u, 2u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = ForwardContactDepthViewTextureName,
                SamplerName = ForwardContactDepthViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(ForwardContactDepthStencilTextureName)!,
                0u, 1u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = ForwardContactDepthViewTextureName,
                SamplerName = ForwardContactDepthViewTextureName,
            };
        }
    }

    private XRTexture CreateStencilViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(DepthStencilTextureName)!,
                0u, 1u,
                0u, 2u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Stencil,
                Name = StencilViewTextureName,
                SamplerName = StencilViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(DepthStencilTextureName)!,
                0u, 1u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Stencil,
                Name = StencilViewTextureName,
                SamplerName = StencilViewTextureName,
            };
        }
    }

    private XRTexture CreateHistoryDepthStencilTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryDepthStencilTextureName;
            t.SamplerName = HistoryDepthStencilTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryDepthStencilTextureName;
            t.SamplerName = HistoryDepthStencilTextureName;
            return t;
        }
    }

    private XRTexture CreateHistoryDepthViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(HistoryDepthStencilTextureName)!,
                0u, 1u,
                0u, 2u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = HistoryDepthViewTextureName,
                SamplerName = HistoryDepthViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(HistoryDepthStencilTextureName)!,
                0u, 1u,
                ESizedInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = HistoryDepthViewTextureName,
                SamplerName = HistoryDepthViewTextureName,
            };
        }
    }

    private XRTexture CreateAlbedoOpacityTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = AlbedoOpacityTextureName;
            t.SamplerName = AlbedoOpacityTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = AlbedoOpacityTextureName;
            t.SamplerName = AlbedoOpacityTextureName;
            return t;
        }
    }

    private XRTexture CreateNormalTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = NormalTextureName;
            t.SamplerName = NormalTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = NormalTextureName;
            t.SamplerName = NormalTextureName;
            return t;
        }
    }

    private XRTexture CreateForwardPrePassNormalTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardPrePassNormalTextureName;
            t.SamplerName = ForwardPrePassNormalTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardPrePassNormalTextureName;
            t.SamplerName = ForwardPrePassNormalTextureName;
            return t;
        }
    }

    private XRTexture CreateForwardContactNormalTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardContactNormalTextureName;
            t.SamplerName = ForwardContactNormalTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = ForwardContactNormalTextureName;
            t.SamplerName = ForwardContactNormalTextureName;
            return t;
        }
    }

    private XRTexture CreateRMSETexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.Name = RMSETextureName;
            t.SamplerName = RMSETextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte);
            t.Name = RMSETextureName;
            t.SamplerName = RMSETextureName;
            return t;
        }
    }

    private XRTexture CreateTransformIdTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.R32ui,
                EPixelFormat.RedInteger,
                EPixelType.UnsignedInt);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.R32ui;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TransformIdTextureName;
            t.SamplerName = TransformIdTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.R32ui,
                EPixelFormat.RedInteger,
                EPixelType.UnsignedInt);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TransformIdTextureName;
            t.SamplerName = TransformIdTextureName;
            return t;
        }
    }

    private XRTexture CreateLightingTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgb16f;
            t.Name = DiffuseTextureName;
            t.SamplerName = DiffuseTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            t.Name = DiffuseTextureName;
            t.SamplerName = DiffuseTextureName;
            return t;
        }
    }

    // --- MSAA GBuffer texture creation (non-Stereo only) ---

    private XRTexture CreateMsaaAlbedoOpacityTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat);
        t.MultiSampleCount = MsaaSampleCount;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = MsaaAlbedoOpacityTextureName;
        t.SamplerName = AlbedoOpacityTextureName;
        return t;
    }

    private XRTexture CreateMsaaNormalTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.RG16f,
            EPixelFormat.Rg,
            EPixelType.HalfFloat);
        t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
        t.MultiSampleCount = MsaaSampleCount;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = MsaaNormalTextureName;
        t.SamplerName = NormalTextureName;
        return t;
    }

    private XRTexture CreateMsaaRMSETexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte);
        t.MultiSampleCount = MsaaSampleCount;
        t.Name = MsaaRMSETextureName;
        t.SamplerName = RMSETextureName;
        return t;
    }

    private XRTexture CreateMsaaDepthStencilTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Depth24Stencil8,
            EPixelFormat.DepthStencil,
            EPixelType.UnsignedInt248,
            EFrameBufferAttachment.DepthStencilAttachment);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
        t.MultiSampleCount = MsaaSampleCount;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = MsaaDepthStencilTextureName;
        t.SamplerName = MsaaDepthStencilTextureName;
        return t;
    }

    private XRTexture CreateMsaaDepthViewTexture()
    {
        return new XRTexture2DView(
            GetTexture<XRTexture2D>(MsaaDepthStencilTextureName)!,
            0u, 1u,
            ESizedInternalFormat.Depth24Stencil8,
            false, true)
        {
            DepthStencilViewFormat = EDepthStencilFmt.Depth,
            Name = MsaaDepthViewTextureName,
            SamplerName = DepthViewTextureName,
        };
    }

    private XRTexture CreateForwardPassMsaaDepthStencilTexture()
    {
        XRTexture2D t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Depth24Stencil8,
            EPixelFormat.DepthStencil,
            EPixelType.UnsignedInt248,
            EFrameBufferAttachment.DepthStencilAttachment);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
        t.MultiSampleCount = MsaaSampleCount;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = ForwardPassMsaaDepthStencilTextureName;
        t.SamplerName = ForwardPassMsaaDepthStencilTextureName;
        return t;
    }

    private XRTexture CreateForwardPassMsaaDepthViewTexture()
    {
        return new XRTexture2DView(
            GetTexture<XRTexture2D>(ForwardPassMsaaDepthStencilTextureName)!,
            0u, 1u,
            ESizedInternalFormat.Depth24Stencil8,
            false, true)
        {
            DepthStencilViewFormat = EDepthStencilFmt.Depth,
            Name = ForwardPassMsaaDepthViewTextureName,
            SamplerName = ForwardPassMsaaDepthViewTextureName,
        };
    }

    private XRTexture CreateMsaaTransformIdTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.R32ui,
            EPixelFormat.RedInteger,
            EPixelType.UnsignedInt);
        t.MultiSampleCount = MsaaSampleCount;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = MsaaTransformIdTextureName;
        t.SamplerName = MsaaTransformIdTextureName;
        return t;
    }

    private XRTexture CreateMsaaLightingTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgb16f,
            EPixelFormat.Rgb,
            EPixelType.HalfFloat);
        t.MultiSampleCount = MsaaSampleCount;
        t.Name = MsaaLightingTextureName;
        t.SamplerName = "LightingTextureMS";
        return t;
    }

    private XRTexture CreateVelocityTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = VelocityTextureName;
            t.SamplerName = VelocityTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.Name = VelocityTextureName;
            t.SamplerName = VelocityTextureName;
            return t;
        }
    }

    private XRTexture CreateHistoryColorTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryColorTextureName;
            t.SamplerName = HistoryColorTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryColorTextureName;
            t.SamplerName = HistoryColorTextureName;
            return t;
        }
    }

    private XRTexture CreateTemporalColorInputTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalColorInputTextureName;
            t.SamplerName = TemporalColorInputTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalColorInputTextureName;
            t.SamplerName = TemporalColorInputTextureName;
            return t;
        }
    }

    private XRTexture CreateTemporalExposureVarianceTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalExposureVarianceTextureName;
            t.SamplerName = TemporalExposureVarianceTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalExposureVarianceTextureName;
            t.SamplerName = TemporalExposureVarianceTextureName;
            return t;
        }
    }

    private XRTexture CreateHistoryExposureVarianceTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryExposureVarianceTextureName;
            t.SamplerName = HistoryExposureVarianceTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryExposureVarianceTextureName;
            t.SamplerName = HistoryExposureVarianceTextureName;
            return t;
        }
    }

    private XRTexture CreateMotionBlurTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = MotionBlurTextureName;
            t.SamplerName = MotionBlurTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = MotionBlurTextureName;
            t.SamplerName = MotionBlurTextureName;
            return t;
        }
    }

    private XRTexture CreateDepthOfFieldTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.Name = DepthOfFieldTextureName;
            t.SamplerName = "ColorSource";
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.Name = DepthOfFieldTextureName;
            t.SamplerName = "ColorSource";
            return t;
        }
    }

    private XRTexture CreateRestirGITexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = RestirGITextureName;
            t.Name = RestirGITextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = RestirGITextureName;
            t.Name = RestirGITextureName;
            return t;
        }
    }

    private XRTexture CreateLightVolumeGITexture()
    {
        // Matches Restir GI format for straightforward blending.
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = LightVolumeGITextureName;
            t.Name = LightVolumeGITextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = LightVolumeGITextureName;
            t.Name = LightVolumeGITextureName;
            return t;
        }
    }

    private XRTexture CreateVoxelConeTracingVolumeTexture()
    {
        XRTexture3D texture = XRTexture3D.Create(128, 128, 128, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        texture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.WWrap = ETexWrapMode.ClampToEdge;
        texture.AutoGenerateMipmaps = true;
        texture.SamplerName = VoxelConeTracingVolumeTextureName;
        texture.Name = VoxelConeTracingVolumeTextureName;
        return texture;
    }

    private XRMaterial CreateVoxelConeTracingVoxelizationMaterial()
    {
        XRShader vertexShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "VoxelConeTracing", "voxelization.vert"), EShaderType.Vertex);
        XRShader geometryShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "VoxelConeTracing", "voxelization.geom"), EShaderType.Geometry);
        XRShader fragmentShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "VoxelConeTracing", "voxelization.frag"), EShaderType.Fragment);

        XRMaterial material = new(vertexShader, geometryShader, fragmentShader)
        {
            Name = "VoxelConeTracingVoxelization"
        };

        var options = material.RenderOptions;
        options.RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights;
        options.CullMode = ECullMode.None;
        options.WriteRed = false;
        options.WriteGreen = false;
        options.WriteBlue = false;
        options.WriteAlpha = false;
        options.DepthTest.Enabled = ERenderParamUsage.Disabled;
        options.DepthTest.UpdateDepth = false;
        options.DepthTest.Function = EComparison.Always;

        return material;
    }

    private static bool NeedsRecreateVoxelVolumeTexture(XRTexture texture)
        => texture is not XRTexture3D tex3D || tex3D.Width != 128 || tex3D.Height != 128 || tex3D.Depth != 128;

    private static void ResizeVoxelVolumeTexture(XRTexture texture)
    {
        if (texture is XRTexture3D tex3D)
            tex3D.Resize(128, 128, 128);
    }

    private XRTexture CreateHDRSceneTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            // Auto exposure samples the smallest mip of the HDR scene texture.
            // Recent GL texture parameter clamping requires this flag so mip levels are generated/accessible.
            t.AutoGenerateMipmaps = true;
            t.SamplerName = HDRSceneTextureName;
            t.Name = HDRSceneTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            t.Resizable = false;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            // Auto exposure samples the smallest mip of the HDR scene texture.
            // Immutable storage + AutoGenerateMipmaps ensures all mip levels are allocated
            // so glGenerateMipmap and texelFetch at the smallest mip work correctly.
            t.AutoGenerateMipmaps = true;
            t.SamplerName = HDRSceneTextureName;
            t.Name = HDRSceneTextureName;
            return t;
        }
    }

    private XRTexture CreateTransparentSceneCopyTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = TransparentSceneCopyTextureName;
            t.Name = TransparentSceneCopyTextureName;
            return t;
        }

        var texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = TransparentSceneCopyTextureName;
        texture.Name = TransparentSceneCopyTextureName;
        return texture;
    }

    private XRTexture CreateTransparentAccumTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = TransparentAccumTextureName;
            t.Name = TransparentAccumTextureName;
            return t;
        }

        var texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = TransparentAccumTextureName;
        texture.Name = TransparentAccumTextureName;
        return texture;
    }

    private XRTexture CreateTransparentRevealageTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment1);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.R16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = TransparentRevealageTextureName;
            t.Name = TransparentRevealageTextureName;
            return t;
        }

        var texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment1);
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SizedInternalFormat = ESizedInternalFormat.R16f;
        texture.SamplerName = TransparentRevealageTextureName;
        texture.Name = TransparentRevealageTextureName;
        return texture;
    }

    private XRTexture CreateAutoExposureTexture()
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            1u,
            1u,
            EPixelInternalFormat.R32f,
            EPixelFormat.Red,
            EPixelType.Float);
        texture.Resizable = false;
        texture.SizedInternalFormat = ESizedInternalFormat.R32f;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = AutoExposureTextureName;
        texture.Name = AutoExposureTextureName;
        texture.AutoGenerateMipmaps = false;
        texture.RequiresStorageUsage = true;
        return texture;
    }

    /// <summary>
    /// RGBA16F full-internal-resolution texture that the bilateral upscale writes
    /// and PostProcess.fs composites from (sampler name <c>VolumetricFogColor</c>).
    /// rgb = in-scattered radiance, a = transmittance. Mono only in Phase 2;
    /// stereo skips the scatter chain.
    /// </summary>
    private XRTexture CreateVolumetricFogColorTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        t.MinFilter = ETexMinFilter.Linear;
        t.MagFilter = ETexMagFilter.Linear;
        t.UWrap = ETexWrapMode.ClampToEdge;
        t.VWrap = ETexWrapMode.ClampToEdge;
        t.AutoGenerateMipmaps = false;
        t.SamplerName = VolumetricFogColorTextureName;
        t.Name = VolumetricFogColorTextureName;
        return t;
    }

    /// <summary>
    /// R32F half-internal-resolution depth view used by the half-res scatter
    /// pass. Stores raw (un-resolved) depth so <c>XRENGINE_ResolveDepth</c>
    /// works identically to the full-res path.
    /// </summary>
    private XRTexture CreateVolumetricFogHalfDepthTexture()
    {
        (uint w, uint h) = GetDesiredFBOSizeHalfInternal();
        var t = XRTexture2D.CreateFrameBufferTexture(
            w, h,
            EPixelInternalFormat.R32f,
            EPixelFormat.Red,
            EPixelType.Float,
            EFrameBufferAttachment.ColorAttachment0);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.R32f;
        // Nearest sampling keeps per-pixel depth crisp for bilateral weighting.
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.UWrap = ETexWrapMode.ClampToEdge;
        t.VWrap = ETexWrapMode.ClampToEdge;
        t.AutoGenerateMipmaps = false;
        t.SamplerName = VolumetricFogHalfDepthTextureName;
        t.Name = VolumetricFogHalfDepthTextureName;
        return t;
    }

    /// <summary>
    /// RGBA16F half-internal-resolution target that the scatter raymarch writes
    /// into. Read by the bilateral upscale shader alongside the full-res
    /// <see cref="DepthViewTextureName"/> to produce <see cref="VolumetricFogColorTextureName"/>.
    /// </summary>
    private XRTexture CreateVolumetricFogHalfScatterTexture()
    {
        (uint w, uint h) = GetDesiredFBOSizeHalfInternal();
        var t = XRTexture2D.CreateFrameBufferTexture(
            w, h,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        // Linear filtering keeps the bilateral upscale's intra-tap interpolation
        // well-behaved when depth weights fall back to pure spatial taps.
        t.MinFilter = ETexMinFilter.Linear;
        t.MagFilter = ETexMagFilter.Linear;
        t.UWrap = ETexWrapMode.ClampToEdge;
        t.VWrap = ETexWrapMode.ClampToEdge;
        t.AutoGenerateMipmaps = false;
        t.SamplerName = VolumetricFogHalfScatterTextureName;
        t.Name = VolumetricFogHalfScatterTextureName;
        return t;
    }

    /// <summary>
    /// RGBA16F half-internal-resolution target containing the temporally
    /// reprojected fog result for the current frame. Read by the full-res
    /// bilateral upscale shader.
    /// </summary>
    private XRTexture CreateVolumetricFogHalfTemporalTexture()
    {
        (uint width, uint height) = GetDesiredFBOSizeHalfInternal();
        var texture = XRTexture2D.CreateFrameBufferTexture(
            width, height,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = false;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.AutoGenerateMipmaps = false;
        texture.SamplerName = VolumetricFogHalfTemporalTextureName;
        texture.Name = VolumetricFogHalfTemporalTextureName;
        return texture;
    }

    /// <summary>
    /// RGBA16F half-internal-resolution history texture sampled by
    /// VolumetricFogReproject.fs. The current temporal output is copied into
    /// this target after the upscale consumes it.
    /// </summary>
    private XRTexture CreateVolumetricFogHalfHistoryTexture()
    {
        (uint width, uint height) = GetDesiredFBOSizeHalfInternal();
        var texture = XRTexture2D.CreateFrameBufferTexture(
            width, height,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = false;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.AutoGenerateMipmaps = false;
        texture.SamplerName = VolumetricFogHalfHistoryTextureName;
        texture.Name = VolumetricFogHalfHistoryTextureName;
        return texture;
    }

    private XRTexture CreatePostProcessOutputTexture()
    {
        // Use internal resolution - FXAA pass will upscale to full resolution
        var (width, height) = GetDesiredFBOSizeInternal();
        EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();
        EPixelType pixelType = ResolvePostProcessIntermediatePixelType();
        ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = true;
        texture.SizedInternalFormat = sized;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = PostProcessOutputTextureName;
        texture.Name = PostProcessOutputTextureName;
        return texture;
    }

    private XRTexture CreateTransformIdDebugOutputTexture()
    {
        var (width, height) = GetDesiredFBOSizeInternal();
        bool outputHdr = ResolveOutputHDR();

        EPixelInternalFormat internalFormat = outputHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8;
        EPixelType pixelType = outputHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte;
        ESizedInternalFormat sized = outputHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8;

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = true;
        texture.SizedInternalFormat = sized;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = TransformIdDebugOutputTextureName;
        texture.Name = TransformIdDebugOutputTextureName;
        return texture;
    }

    private XRTexture CreateFxaaOutputTexture()
    {
        var (width, height) = GetDesiredFBOSizeFull();
        EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();
        EPixelType pixelType = ResolvePostProcessIntermediatePixelType();
        ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = true;
        texture.SizedInternalFormat = sized;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = FxaaOutputTextureName;
        texture.Name = FxaaOutputTextureName;
        return texture;
    }

    private XRTexture CreateTsrHistoryColorTexture()
    {
        var (width, height) = GetDesiredFBOSizeFull();
        EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();
        EPixelType pixelType = ResolvePostProcessIntermediatePixelType();
        ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = true;
        texture.SizedInternalFormat = sized;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = TsrHistoryColorTextureName;
        texture.Name = TsrHistoryColorTextureName;
        return texture;
    }

    private XRTexture CreateRadianceCascadeGITexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = RadianceCascadeGITextureName;
            t.Name = RadianceCascadeGITextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = RadianceCascadeGITextureName;
            t.Name = RadianceCascadeGITextureName;
            return t;
        }
    }

    private XRTexture CreateSurfelGITexture()
    {
        // Matches other GI buffers for straightforward blending.
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = SurfelGITextureName;
            t.Name = SurfelGITextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = SurfelGITextureName;
            t.Name = SurfelGITextureName;
            return t;
        }
    }
}
