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
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgb16f;
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
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = NormalTextureName;
            t.SamplerName = NormalTextureName;
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
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = HDRSceneTextureName;
            t.Name = HDRSceneTextureName;
            return t;
        }
    }

    private XRTexture CreatePostProcessOutputTexture()
    {
        var (width, height) = GetDesiredFBOSizeFull();
        bool outputHdr = Engine.Rendering.Settings.OutputHDR;

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
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = PostProcessOutputTextureName;
        texture.Name = PostProcessOutputTextureName;
        return texture;
    }

    private XRTexture CreateFxaaOutputTexture()
    {
        var (width, height) = GetDesiredFBOSizeFull();
        bool outputHdr = Engine.Rendering.Settings.OutputHDR;

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
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = FxaaOutputTextureName;
        texture.Name = FxaaOutputTextureName;
        return texture;
    }
}
