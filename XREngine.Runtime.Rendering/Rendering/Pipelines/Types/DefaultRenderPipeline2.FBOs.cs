using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2
{
    //private XRFrameBuffer CreateUserInterfaceFBO()
    //{
    //    var hudTexture = GetTexture<XRTexture>(UserInterfaceTextureName)!;
    //    XRShader hudShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, HudFBOShaderName()), EShaderType.Fragment);
    //    XRMaterial hudMat = new([hudTexture], hudShader)
    //    {
    //        RenderOptions = new RenderingParameters()
    //        {
    //            DepthTest = new()
    //            {
    //                Enabled = ERenderParamUsage.Unchanged,
    //                Function = EComparison.Always,
    //                UpdateDepth = false,
    //            },
    //        }
    //    };
    //    var uiFBO = new XRQuadFrameBuffer(hudMat);

    //    if (hudTexture is not IFrameBufferAttachement hudAttach)
    //        throw new InvalidOperationException("HUD texture must be an FBO-attachable texture.");

    //    uiFBO.SetRenderTargets((hudAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));

    //    return uiFBO;
    //}

    private XRFrameBuffer CreatePostProcessFBO()
    {
        // Texture array order must match the shader's sampler declaration order in PostProcess.fs,
        // because Vulkan binds by index (binding N → Textures[N]), not by name.
        // Atmospheric and volumetric bindings are mono-only; stereo PostProcessStereo.fs
        // does not declare those samplers and omits the slots to keep Vulkan binding indices aligned.
        XRTexture[] postProcessRefs = Stereo
            ?
            [
                GetTexture<XRTexture>(HDRSceneTextureName)!,       // binding 0: sampler2DArray HDRSceneTex
                GetTexture<XRTexture>(BloomBlurTextureName)!,      // binding 1: sampler2DArray BloomBlurTexture
                GetTexture<XRTexture>(DepthViewTextureName)!,      // binding 2: sampler2DArray DepthView
                GetTexture<XRTexture>(StencilViewTextureName)!,    // binding 3: usampler2DArray StencilView
                GetTexture<XRTexture>(AutoExposureTextureName)!,   // binding 4: sampler2D AutoExposureTex
            ]
            :
            [
                GetTexture<XRTexture>(HDRSceneTextureName)!,       // binding 0: sampler2D HDRSceneTex
                GetTexture<XRTexture>(BloomBlurTextureName)!,      // binding 1: sampler2D BloomBlurTexture
                GetTexture<XRTexture>(DepthViewTextureName)!,      // binding 2: sampler2D DepthView
                GetTexture<XRTexture>(StencilViewTextureName)!,    // binding 3: usampler2D StencilView
                GetTexture<XRTexture>(AutoExposureTextureName)!,   // binding 4: sampler2D AutoExposureTex
                GetTexture<XRTexture>(AtmosphereColorTextureName)!, // binding 5: sampler2D AtmosphereColor
                GetTexture<XRTexture>(VolumetricFogColorTextureName)!, // binding 6: sampler2D VolumetricFogColor
            ];
        XRShader postProcessShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, PostProcessShaderName()), EShaderType.Fragment);
        XRMaterial postProcessMat = new(postProcessRefs, postProcessShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights | EUniformRequirements.RenderTime,
            }
        };
        var PostProcessFBO = new XRQuadFrameBuffer(postProcessMat, deriveRenderTargetsFromMaterial: false);
        PostProcessFBO.SettingUniforms += ApplyPostProcessProgramBindings;
        return PostProcessFBO;
    }

    private XRFrameBuffer CreatePostProcessOutputFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(PostProcessOutputTextureName, CreatePostProcessOutputTexture);
        IFrameBufferAttachement depthStencilAttach = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);

        return new XRFrameBuffer(
            (attach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = PostProcessOutputFBOName
        };
    }

    private XRFrameBuffer CreateFinalPostProcessFBO()
    {
        XRTexture source = GetTexture<XRTexture>(PostProcessOutputTextureName)!;
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, FinalPostProcessShaderName()), EShaderType.Fragment);
        XRMaterial material = new([source], shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera,
            }
        };

        XRQuadFrameBuffer fbo = new(material, deriveRenderTargetsFromMaterial: false)
        {
            Name = FinalPostProcessFBOName
        };
        fbo.SettingUniforms += ApplyFinalPostProcessProgramBindings;
        return fbo;
    }

    private XRFrameBuffer CreateFinalPostProcessOutputFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(FinalPostProcessOutputTextureName, CreateFinalPostProcessOutputTexture);

        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = FinalPostProcessOutputFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHalfDepthQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(DepthViewTextureName)!,
        ];
        XRShader downsampleShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "Atmosphere", "AtmosphereHalfDepthDownsample.fs"),
            EShaderType.Fragment);
        XRMaterial mat = new(refs, downsampleShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        return new XRQuadFrameBuffer(mat, deriveRenderTargetsFromMaterial: false)
        {
            Name = AtmosphereHalfDepthQuadFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHalfDepthFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfDepthTextureName, CreateAtmosphereHalfDepthTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = AtmosphereHalfDepthFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHalfScatterQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(AtmosphereHalfDepthTextureName)!,
        ];
        XRShader scatterShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "Atmosphere", "AtmosphereAerialPerspective.fs"),
            EShaderType.Fragment);
        XRMaterial scatterMat = new(refs, scatterShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.RenderTime,
            }
        };
        return new XRQuadFrameBuffer(scatterMat, deriveRenderTargetsFromMaterial: false)
        {
            Name = AtmosphereHalfScatterQuadFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHalfScatterFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfScatterTextureName, CreateAtmosphereHalfScatterTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = AtmosphereHalfScatterFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereReprojectQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(AtmosphereHalfScatterTextureName)!,
            GetTexture<XRTexture>(AtmosphereHalfHistoryTextureName)!,
            GetTexture<XRTexture>(AtmosphereHalfDepthTextureName)!,
        ];
        XRShader reprojectShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "Atmosphere", "AtmosphereReproject.fs"),
            EShaderType.Fragment);
        XRMaterial reprojectMat = new(refs, reprojectShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        return new XRQuadFrameBuffer(reprojectMat, deriveRenderTargetsFromMaterial: false)
        {
            Name = AtmosphereReprojectQuadFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereReprojectFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfTemporalTextureName, CreateAtmosphereHalfTemporalTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = AtmosphereReprojectFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHistoryFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfHistoryTextureName, CreateAtmosphereHalfHistoryTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = AtmosphereHistoryFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereUpscaleQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(AtmosphereHalfTemporalTextureName)!,
            GetTexture<XRTexture>(AtmosphereHalfDepthTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
        ];
        XRShader upscaleShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "Atmosphere", "AtmosphereUpscale.fs"),
            EShaderType.Fragment);
        XRMaterial upscaleMat = new(refs, upscaleShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        return new XRQuadFrameBuffer(upscaleMat, deriveRenderTargetsFromMaterial: false)
        {
            Name = AtmosphereUpscaleQuadFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereUpscaleFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereColorTextureName, CreateAtmosphereColorTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = AtmosphereUpscaleFBOName
        };
    }

    private XRFrameBuffer CreateTransformIdDebugOutputFBO()
    {
        XRTexture outputTexture = GetTexture<XRTexture>(TransformIdDebugOutputTextureName)!;
        if (outputTexture is not IFrameBufferAttachement attach)
            throw new InvalidOperationException("TransformId debug output texture must be FBO attachable.");

        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = TransformIdDebugOutputFBOName
        };
    }

    private XRFrameBuffer CreateTransformIdDebugQuadFBO()
    {
        XRTexture transformIdTexture = GetTexture<XRTexture>(TransformIdTextureName)!;
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DebugTransformId.fs"), EShaderType.Fragment);
        XRMaterial mat = new([transformIdTexture], shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(mat)
        {
            Name = TransformIdDebugQuadFBOName
        };
        return fbo;
    }

    private XRFrameBuffer CreateFxaaFBO()
    {
        IFrameBufferAttachement fxaaAttach = EnsureTextureAttachment(FxaaOutputTextureName, CreateFxaaOutputTexture);
        return new XRFrameBuffer((fxaaAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = FxaaFBOName
        };
    }

    /// <summary>
    /// Creates the TSR resolve FBO.
    /// Reads the internal-resolution final post-process result plus temporal inputs and writes
    /// the reconstructed full-resolution output to <see cref="TsrOutputTextureName"/>.
    /// </summary>
    private XRFrameBuffer CreateTsrUpscaleFBO()
    {
        XRTexture sourceTexture = GetTexture<XRTexture>(FinalPostProcessOutputTextureName)!;
        XRTexture velocityTexture = GetTexture<XRTexture>(VelocityTextureName)!;
        XRTexture depthTexture = GetTexture<XRTexture>(DepthViewTextureName)!;
        XRTexture historyDepthTexture = GetTexture<XRTexture>(HistoryDepthViewTextureName)!;
        XRTexture historyColorTexture = GetTexture<XRTexture>(TsrHistoryColorTextureName)!;
        XRTexture stencilTexture = GetTexture<XRTexture>(StencilViewTextureName)!;
        XRTexture outputTexture = GetTexture<XRTexture>(TsrOutputTextureName)!;
        XRShader upscaleShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "TemporalSuperResolution.fs"), EShaderType.Fragment);
        XRMaterial upscaleMaterial = new([sourceTexture, velocityTexture, depthTexture, historyDepthTexture, historyColorTexture, stencilTexture], upscaleShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };
        var fbo = new XRQuadFrameBuffer(upscaleMaterial, deriveRenderTargetsFromMaterial: false)
        {
            Name = TsrUpscaleFBOName
        };
        if (outputTexture is not IFrameBufferAttachement outputAttach)
            throw new InvalidOperationException("TSR upscale output texture is not an FBO-attachable texture.");

        fbo.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreateTsrHistoryColorFBO()
    {
        XRTexture historyTexture = GetTexture<XRTexture>(TsrHistoryColorTextureName)!;
        if (historyTexture is not IFrameBufferAttachement historyAttach)
            throw new InvalidOperationException("TSR history color texture is not an FBO-attachable texture.");

        return new XRFrameBuffer((historyAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = TsrHistoryColorFBOName
        };
    }

    private XRFrameBuffer CreateTransparentSceneCopyFBO()
    {
        var colorAttachment = EnsureTextureAttachment(TransparentSceneCopyTextureName, CreateTransparentSceneCopyTexture);
        return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = TransparentSceneCopyFBOName
        };
    }

    private static void ApplyTransformIdDebugProgramBindings(XRRenderProgram program)
    {
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
    }

    private XRFrameBuffer CreateDeferredTransparencyBlurFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(TransparentSceneCopyTextureName)!,
            GetTexture<XRTexture>(AlbedoOpacityTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
        ];

        XRMaterial material = new(
            references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, DeferredTransparencyBlurShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = DeferredTransparencyBlurFBOName };
        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        fbo.SetRenderTargets((hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreateTransparentAccumulationFBO()
    {
        var accumAttachment = EnsureTextureAttachment(TransparentAccumTextureName, CreateTransparentAccumTexture);
        var revealageAttachment = EnsureTextureAttachment(TransparentRevealageTextureName, CreateTransparentRevealageTexture);
        var depthAttachment = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);

        return new XRFrameBuffer(
            (accumAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (revealageAttachment, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = TransparentAccumulationFBOName
        };
    }

    private XRFrameBuffer CreateTransparentResolveFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(TransparentSceneCopyTextureName)!,
            GetTexture<XRTexture>(TransparentAccumTextureName)!,
            GetTexture<XRTexture>(TransparentRevealageTextureName)!,
        ];

        XRMaterial material = new(
            references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, TransparentResolveShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = TransparentResolveFBOName };

        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        fbo.SetRenderTargets((hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreateTransparentAccumulationDebugFBO()
        => CreateTransparencyDebugFBO(
            TransparentAccumulationDebugFBOName,
            TransparentAccumulationDebugShaderName(),
            GetTexture<XRTexture>(TransparentAccumTextureName)!);

    private XRFrameBuffer CreateTransparentRevealageDebugFBO()
        => CreateTransparencyDebugFBO(
            TransparentRevealageDebugFBOName,
            TransparentRevealageDebugShaderName(),
            GetTexture<XRTexture>(TransparentRevealageTextureName)!);

    private XRFrameBuffer CreateTransparentOverdrawDebugFBO()
        => CreateTransparencyDebugFBO(
            TransparentOverdrawDebugFBOName,
            TransparentOverdrawDebugShaderName(),
            GetTexture<XRTexture>(TransparentRevealageTextureName)!,
            GetTexture<XRTexture>(TransparentAccumTextureName)!);

    private XRFrameBuffer CreateFullOverdrawCountFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(FullOverdrawCountTextureName, CreateFullOverdrawCountTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = FullOverdrawCountFBOName
        };
    }

    private XRFrameBuffer CreateFullOverdrawDebugFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(FullOverdrawCountTextureName)!,
            GetTexture<XRTexture>(PostProcessOutputTextureName)!,
        ];

        XRMaterial material = new(
            references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "FullOverdrawDebug.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = FullOverdrawDebugFBOName };
        fbo.SettingUniforms += FullOverdrawDebugFBO_SettingUniforms;
        return fbo;
    }

    private XRFrameBuffer CreateSceneCopyFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(HDRSceneTextureName)!,
        ];

        XRMaterial material = new(
            references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, SceneCopyShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        return new XRQuadFrameBuffer(material) { Name = SceneCopyFBOName };
    }

    private XRFrameBuffer CreateTransparencyDebugFBO(
        string name,
        string shaderName,
        params XRTexture[] textures)
    {
        XRMaterial material = new(
            textures,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, shaderName), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = name };
        return fbo;
    }

    private static void ApplyTransparentResolveProgramBindings(XRRenderProgram program)
    {
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
    }

    private void FullOverdrawDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? count = GetTexture<XRTexture>(FullOverdrawCountTextureName);
        XRTexture? scene = GetTexture<XRTexture>(PostProcessOutputTextureName);
        if (count is null || scene is null)
            return;

        GpuBvhDebugSettings? settings = ResolveDebugVisualizationSettings();
        int saturationCount = settings?.FullOverdrawSaturationCount
            ?? GpuBvhDebugSettings.DefaultFullOverdrawSaturationCount;
        float overlayOpacity = settings?.FullOverdrawOverlayOpacity ?? 1.0f;

        program.Sampler(FullOverdrawCountTextureName, count, 0);
        program.Sampler(PostProcessOutputTextureName, scene, 1);
        program.Uniform("OverdrawMaxCount", (float)Math.Max(1, saturationCount));
        program.Uniform("OverlayOpacity", Math.Clamp(overlayOpacity, 0.0f, 1.0f));
    }

    private XRFrameBuffer CreateForwardPassFBO()
    {
        XRTexture hdrSceneTex = (XRTexture)EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);

        XRMaterial sceneCopyMat = new(
            [hdrSceneTex],
            XRShader.EngineShader(Path.Combine(SceneShaderPath, SceneCopyShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(sceneCopyMat, useTriangle: false, deriveRenderTargetsFromMaterial: false);

        IFrameBufferAttachement hdrAttach = (IFrameBufferAttachement)hdrSceneTex;
        IFrameBufferAttachement dsAttach = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);

        fbo.SetRenderTargets(
            (hdrAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (dsAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

        return fbo;
    }

    private XRFrameBuffer CreateForwardPassMsaaFBO()
    {
        XRRenderBuffer colorBuffer = new(InternalWidth, InternalHeight, GetForwardMsaaColorFormat(), MsaaSampleCount)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            Name = $"{ForwardPassMsaaFBOName}_Color"
        };
        colorBuffer.Allocate();

        IFrameBufferAttachement depthAttach = EnsureTextureAttachment(ForwardPassMsaaDepthStencilTextureName, CreateForwardPassMsaaDepthStencilTexture);

        XRFrameBuffer fbo = new(
            (colorBuffer, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = ForwardPassMsaaFBOName
        };

        return fbo;
    }

    private XRFrameBuffer CreateGBufferFBO()
    {
        if (GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName) is not IFrameBufferAttachement aoAttach)
            throw new InvalidOperationException("AO intensity texture must be FBO-attachable.");

        return new XRFrameBuffer((aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = GBufferFBOName
        };
    }

    private XRFrameBuffer CreateDeferredGBufferFBO()
    {
        IFrameBufferAttachement albedoAttach = EnsureTextureAttachment(AlbedoOpacityTextureName, CreateAlbedoOpacityTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(NormalTextureName, CreateNormalTexture);
        IFrameBufferAttachement rmseAttach = EnsureTextureAttachment(RMSETextureName, CreateRMSETexture);
        IFrameBufferAttachement transformIdAttach = EnsureTextureAttachment(TransformIdTextureName, CreateTransformIdTexture);
        IFrameBufferAttachement depthStencilAttach = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);

        return new XRFrameBuffer(
            (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
            (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = DeferredGBufferFBOName
        };
    }

    private XRMaterial CreateMotionVectorsMaterial()
    {
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "MotionVectors.fs"), EShaderType.Fragment);
        XRMaterial material = new(Array.Empty<XRTexture?>(), shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.None
            }
        };

        return material;
    }

    private XRMaterial CreateDepthNormalPrePassMaterial()
    {
        XRShader shader = XRShader.EngineShader(Path.Combine("Common", "DepthNormalPrePass.fs"), EShaderType.Fragment);
        return new XRMaterial(Array.Empty<XRTexture?>(), shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                    UpdateDepth = true,
                },
                RequiredEngineUniforms = EUniformRequirements.None
            }
        };
    }

    private XRMaterial CreateFullOverdrawCountMaterial()
    {
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "FullOverdrawCount.fs"), EShaderType.Fragment);
        return new XRMaterial(Array.Empty<XRTexture?>(), shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                CullMode = ECullMode.Back,
                BlendModeAllDrawBuffers = new BlendMode()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd
                },
                RequiredEngineUniforms = EUniformRequirements.None
            }
        };
    }

    private void ApplyMotionVectorsProgramBindings(XRRenderProgram program)
    {
        if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporal))
        {
            //Debug.Out($"[Velocity] Using temporal uniforms. HistoryReady={temporal.HistoryReady}, ExposureReady={temporal.HistoryExposureReady}, Size={temporal.Width}x{temporal.Height}");
            program.Uniform("CurrViewProjection", temporal.CurrViewProjectionUnjittered);
            program.Uniform(
                "PrevViewProjection",
                temporal.HistoryReady
                    ? temporal.PrevViewProjectionUnjittered
                    : temporal.CurrViewProjectionUnjittered);
            return;
        }

        var camera = RuntimeEngine.Rendering.State.RenderingCamera;
        if (camera is not null)
        {
            Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
            Matrix4x4 projMatrix = camera.ProjectionMatrix;
            Matrix4x4 viewProj = viewMatrix * projMatrix;
            Debug.RenderingEvery(
                "Velocity.V2.NoTemporalData",
                TimeSpan.FromSeconds(2),
                "[Velocity] Temporal data unavailable; using current camera matrices for motion vectors.");
            program.Uniform("CurrViewProjection", viewProj);
            program.Uniform("PrevViewProjection", viewProj);
        }
        else
        {
            Debug.RenderingEvery(
                "Velocity.V2.NoCamera",
                TimeSpan.FromSeconds(2),
                "[Velocity] No camera available; motion vectors will be zeroed.");
            program.Uniform("CurrViewProjection", Matrix4x4.Identity);
            program.Uniform("PrevViewProjection", Matrix4x4.Identity);
        }
    }

    private IFrameBufferAttachement EnsureTextureAttachment(string textureName, Func<XRTexture> factory)
    {
        XRTexture? texture = null;
        bool hasConcreteTexture = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Resources.TryGetTexture(textureName, out texture) == true;
        if (hasConcreteTexture && texture is IFrameBufferAttachement attachment)
            return attachment;

        // These attachment names refer to concrete pipeline resources. After cache
        // invalidation, the registry can be empty or hold a stale non-attachable
        // survivor under the same name. Rebuild the concrete texture instead of
        // trusting variable aliases or dangling view instances.
        texture = factory();
        SetTexture(texture);
        return texture as IFrameBufferAttachement
            ?? throw new InvalidOperationException($"Factory for '{textureName}' produced a non-FBO-attachable texture.");
    }

    private XRFrameBuffer CreateVelocityFBO()
    {
        var velocityAttachment = EnsureTextureAttachment(VelocityTextureName, CreateVelocityTexture);
        var depthAttachment = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);

        return new XRFrameBuffer(
            (velocityAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = VelocityFBOName
        };
    }

    private XRFrameBuffer CreateHistoryCaptureFBO()
    {
        var colorAttachment = EnsureTextureAttachment(HistoryColorTextureName, CreateHistoryColorTexture);
        var depthAttachment = EnsureTextureAttachment(HistoryDepthStencilTextureName, CreateHistoryDepthStencilTexture);

        return new XRFrameBuffer(
            (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = HistoryCaptureFBOName
        };
    }

    private XRFrameBuffer CreateTemporalInputFBO()
    {
        var colorAttachment = EnsureTextureAttachment(TemporalColorInputTextureName, CreateTemporalColorInputTexture);

        return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = TemporalInputFBOName
        };
    }

    private XRFrameBuffer CreateTemporalAccumulationFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(TemporalColorInputTextureName)!,
            GetTexture<XRTexture>(HistoryColorTextureName)!,
            GetTexture<XRTexture>(VelocityTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
            GetTexture<XRTexture>(HistoryDepthViewTextureName)!,
            GetTexture<XRTexture>(HistoryExposureVarianceTextureName)!,
        ];

        XRMaterial material = new(references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "TemporalAccumulation.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = TemporalAccumulationFBOName };

        var filteredAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        var exposureAttachment = EnsureTextureAttachment(TemporalExposureVarianceTextureName, CreateTemporalExposureVarianceTexture);

        fbo.SetRenderTargets(
            (filteredAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (exposureAttachment, EFrameBufferAttachment.ColorAttachment1, 0, -1));

        return fbo;
    }

    private XRFrameBuffer CreateMotionBlurCopyFBO()
    {
        var attachment = EnsureTextureAttachment(MotionBlurTextureName, CreateMotionBlurTexture);

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = MotionBlurCopyFBOName
        };
    }

    private XRFrameBuffer CreateMotionBlurFBO()
    {
        XRTexture motionBlurCopy = GetTexture<XRTexture>(MotionBlurTextureName)!;
        XRTexture velocityTex = GetTexture<XRTexture>(VelocityTextureName)!;
        XRTexture depthTex = GetTexture<XRTexture>(DepthViewTextureName)!;

        XRMaterial material = new(
            [motionBlurCopy, velocityTex, depthTex],
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "MotionBlur.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = MotionBlurFBOName };
        return fbo;
    }

    private XRFrameBuffer CreateDepthOfFieldCopyFBO()
    {
        var attachment = EnsureTextureAttachment(DepthOfFieldTextureName, CreateDepthOfFieldTexture);

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = DepthOfFieldCopyFBOName
        };
    }

    private XRFrameBuffer CreateDepthOfFieldFBO()
    {
        XRTexture dofSource = GetTexture<XRTexture>(DepthOfFieldTextureName)!;
        XRTexture depthTex = GetTexture<XRTexture>(DepthViewTextureName)!;

        XRMaterial material = new(
            [dofSource, depthTex],
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "DepthOfField.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = DepthOfFieldFBOName };
        return fbo;
    }

    private XRFrameBuffer CreateHistoryExposureFBO()
    {
        var attachment = EnsureTextureAttachment(HistoryExposureVarianceTextureName, CreateHistoryExposureVarianceTexture);

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = HistoryExposureFBOName
        };
    }

    private ERenderBufferStorage GetForwardMsaaColorFormat()
        // ForwardPassMSAAFBO always resolves into HDRSceneTex, which is fixed at RGBA16F.
        => ERenderBufferStorage.Rgba16f;

    private XRFrameBuffer CreateDepthPreloadFBO()
    {
        XRTexture depthViewTexture = GetTexture<XRTexture>(DepthViewTextureName)!;

        XRMaterial material = new(
            [depthViewTexture],
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "CopyDepthFromTexture.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    // Must be Enabled — OpenGL silently disables depth writes when
                    // GL_DEPTH_TEST is disabled, preventing gl_FragDepth from reaching
                    // the MSAA depth buffer. Always comparison ensures all fragments pass.
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Always,
                    UpdateDepth = true,
                },
                WriteRed = false,
                WriteGreen = false,
                WriteBlue = false,
                WriteAlpha = false,
            }
        };

        return new XRQuadFrameBuffer(material, false) { Name = DepthPreloadFBOName };
    }

    /// <summary>
    /// Dedicated forward-only depth+normal FBO for inspection/debugging.
    /// This target is cleared before the forward pre-pass, so it contains only
    /// opaque/masked forward geometry.
    /// </summary>
    private XRFrameBuffer CreateForwardDepthPrePassFBO()
    {
        var dsAttach = EnsureTextureAttachment(ForwardPrePassDepthStencilTextureName, CreateForwardPrePassDepthStencilTexture);
        var normalAttach = EnsureTextureAttachment(ForwardPrePassNormalTextureName, CreateForwardPrePassNormalTexture);

        return new XRFrameBuffer(
            (normalAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (dsAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = ForwardDepthPrePassFBOName
        };
    }

    /// <summary>
    /// Shared depth+normal FBO that reuses the main GBuffer Normal + DepthStencil textures.
    /// The forward pre-pass is replayed into this target without clearing so AO still sees
    /// both deferred and forward geometry.
    /// </summary>
    private XRFrameBuffer CreateForwardDepthPrePassMergeFBO()
    {
        IFrameBufferAttachement dsAttach = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(NormalTextureName, CreateNormalTexture);

        return new XRFrameBuffer(
            (normalAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (dsAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = ForwardDepthPrePassMergeFBOName
        };
    }

    private XRFrameBuffer CreateDeferredGBufferPreForwardCopyFBO()
    {
        IFrameBufferAttachement dsAttach = EnsureTextureAttachment(
            DeferredGBufferPreForwardDepthStencilTextureName,
            CreateDeferredGBufferPreForwardDepthStencilTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(
            DeferredGBufferPreForwardNormalTextureName,
            CreateDeferredGBufferPreForwardNormalTexture);

        return new XRFrameBuffer(
            (normalAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (dsAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = DeferredGBufferPreForwardCopyFBOName
        };
    }

    private XRFrameBuffer CreateForwardContactPrePassCopyFBO()
    {
        IFrameBufferAttachement dsAttach = EnsureTextureAttachment(ForwardContactDepthStencilTextureName, CreateForwardContactDepthStencilTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(ForwardContactNormalTextureName, CreateForwardContactNormalTexture);

        return new XRFrameBuffer(
            (normalAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (dsAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = ForwardContactPrePassCopyFBOName
        };
    }

    private XRFrameBuffer CreateLightCombineFBO()
    {
        var diffuseTexture = GetTexture<XRTexture>(DiffuseTextureName)!;

        XRTexture[] lightCombineTextures = [
            GetTexture<XRTexture>(AlbedoOpacityTextureName)!,
            GetTexture<XRTexture>(NormalTextureName)!,
            GetTexture<XRTexture>(RMSETextureName)!,
            GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
            diffuseTexture,
            GetTexture<XRTexture>(BRDFTextureName)!,
        ];
        XRShader lightCombineShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, DeferredLightCombineShaderName()), EShaderType.Fragment);
        XRMaterial lightCombineMat = new(lightCombineTextures, lightCombineShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera
            }
        };

        var lightCombineFBO = new XRQuadFrameBuffer(lightCombineMat, useTriangle: true, deriveRenderTargetsFromMaterial: false) { Name = LightCombineFBOName };

        if (diffuseTexture is not IFrameBufferAttachement attach)
        {
            diffuseTexture = CreateLightingTexture();
            SetTexture(diffuseTexture);
            attach = (IFrameBufferAttachement)diffuseTexture;
        }
        lightCombineFBO.SetRenderTargets((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1));

        return lightCombineFBO;
    }

    private XRFrameBuffer CreateRestirCompositeFBO()
    {
        XRTexture restirTexture = GetTexture<XRTexture>(RestirGITextureName)!;
        XRShader restirCompositeShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "RestirComposite.fs"), EShaderType.Fragment);
        BlendMode additiveBlend = new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };
        XRMaterial restirCompositeMaterial = new([restirTexture], restirCompositeShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = additiveBlend
            }
        };
        return new XRQuadFrameBuffer(restirCompositeMaterial) { Name = RestirCompositeFBOName };
    }

    private XRFrameBuffer CreateSurfelGICompositeFBO()
    {
        XRTexture giTexture = GetTexture<XRTexture>(SurfelGITextureName)!;
        XRShader compositeShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "SurfelGIComposite.fs"), EShaderType.Fragment);
        BlendMode additiveBlend = new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };

        XRMaterial material = new([giTexture], compositeShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = additiveBlend
            }
        };

        return new XRQuadFrameBuffer(material) { Name = SurfelGICompositeFBOName };
    }

    private XRFrameBuffer CreateLightVolumeCompositeFBO()
    {
        XRTexture giTexture = GetTexture<XRTexture>(LightVolumeGITextureName)!;
        XRShader compositeShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "LightVolumeComposite.fs"), EShaderType.Fragment);
        BlendMode additiveBlend = new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };

        XRMaterial material = new([giTexture], compositeShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = additiveBlend
            }
        };

        return new XRQuadFrameBuffer(material) { Name = LightVolumeCompositeFBOName };
    }

    private XRFrameBuffer CreateRadianceCascadeCompositeFBO()
    {
        XRTexture giTexture = GetTexture<XRTexture>(RadianceCascadeGITextureName)!;
        XRTexture depthTexture = GetTexture<XRTexture>(DepthViewTextureName)!;
        XRTexture normalTexture = GetTexture<XRTexture>(NormalTextureName)!;
        XRShader compositeShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "RadianceCascadeComposite.fs"), EShaderType.Fragment);
        BlendMode additiveBlend = new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };

        XRMaterial material = new([giTexture, depthTexture, normalTexture], compositeShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = additiveBlend,
                //RequiredEngineUniforms = EUniformRequirements.Camera
            }
        };

        return new XRQuadFrameBuffer(material) { Name = RadianceCascadeCompositeFBOName };
    }

    private void ApplyRadianceCascadeCompositeProgramBindings(XRRenderProgram program)
    {
        XRTexture? radianceCascadeGI = GetTexture<XRTexture>(RadianceCascadeGITextureName);
        XRTexture? depthView = GetTexture<XRTexture>(DepthViewTextureName);
        XRTexture? normalView = GetTexture<XRTexture>(NormalTextureName);
        if (radianceCascadeGI is null || depthView is null || normalView is null)
        {
            Debug.LogWarning("Radiance Cascade Composite FBO: One or more required textures are missing; skipping uniform setup.");
            return;
        }
        program.Sampler("RadianceCascadeGITexture", radianceCascadeGI, 0);
        program.Sampler("DepthView", depthView, 1);
        program.Sampler("Normal", normalView, 2);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
    }

    // --- MSAA Deferred GBuffer FBO ---

    private XRFrameBuffer CreateMsaaGBufferFBO()
    {
        IFrameBufferAttachement albedoAttach = EnsureTextureAttachment(MsaaAlbedoOpacityTextureName, CreateMsaaAlbedoOpacityTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(MsaaNormalTextureName, CreateMsaaNormalTexture);
        IFrameBufferAttachement rmseAttach = EnsureTextureAttachment(MsaaRMSETextureName, CreateMsaaRMSETexture);
        IFrameBufferAttachement transformIdAttach = EnsureTextureAttachment(MsaaTransformIdTextureName, CreateMsaaTransformIdTexture);
        IFrameBufferAttachement depthStencilAttach = EnsureTextureAttachment(MsaaDepthStencilTextureName, CreateMsaaDepthStencilTexture);

        return new XRFrameBuffer(
            (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
            (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = MsaaGBufferFBOName
        };
    }

    /// <summary>
    /// Creates an MSAA lighting FBO for deferred MSAA per-light accumulation.
    /// The MSAA depth-stencil is attached so stencil-based complex pixel testing works.
    /// </summary>
    private XRFrameBuffer CreateMsaaLightingFBO()
    {
        IFrameBufferAttachement lightingAttach = EnsureTextureAttachment(MsaaLightingTextureName, CreateMsaaLightingTexture);
        IFrameBufferAttachement depthStencilAttach = EnsureTextureAttachment(MsaaDepthStencilTextureName, CreateMsaaDepthStencilTexture);

        return new XRFrameBuffer(
            (lightingAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = MsaaLightingFBOName
        };
    }

    /// <summary>
    /// Creates a per-sample variant of the DeferredLightCombine QuadFBO for MSAA deferred.
    /// Reads direct light from the MSAA lighting texture via sampler2DMS + gl_SampleID
    /// so each sample receives its own direct-light contribution. This prevents premature
    /// MSAA resolve from averaging sky-samples with geometry lighting at silhouette edges.
    /// </summary>
    private XRFrameBuffer CreateMsaaLightCombineFBO()
    {
        var msaaLightingTexture = GetTexture<XRTexture>(MsaaLightingTextureName)!;

        XRTexture[] textures = [
            GetTexture<XRTexture>(MsaaAlbedoOpacityTextureName)!,
            GetTexture<XRTexture>(MsaaNormalTextureName)!,
            GetTexture<XRTexture>(MsaaRMSETextureName)!,
            GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName)!,
            GetTexture<XRTexture>(MsaaDepthViewTextureName)!,
            msaaLightingTexture,
            GetTexture<XRTexture>(BRDFTextureName)!,
        ];

        XRShader baseShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, DeferredLightCombineShaderName()), EShaderType.Fragment);
        XRShader msaaShader = ShaderHelper.CreateDefinedShaderVariant(baseShader, MsaaDeferredDefine) ?? baseShader;

        XRMaterial mat = new(textures, msaaShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                StencilTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera
            }
        };

        var fbo = new XRQuadFrameBuffer(mat, true, false) { Name = MsaaLightCombineFBOName };
        return fbo;
    }

    /// <summary>
    /// Quad FBO that runs <c>VolumetricFogHalfDepthDownsample.fs</c> at half
    /// internal resolution, writing <see cref="VolumetricFogHalfDepthTextureName"/>
    /// from the full-res <see cref="DepthViewTextureName"/>. Raw depth is
    /// preserved so the scatter shader's <c>XRENGINE_ResolveDepth</c> path
    /// still handles reversed-Z correctly.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogHalfDepthQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(DepthViewTextureName)!, // binding 0: sampler2D DepthView
        ];
        XRShader downsampleShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "VolumetricFog", "VolumetricFogHalfDepthDownsample.fs"),
            EShaderType.Fragment);
        XRMaterial mat = new(refs, downsampleShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                // Downsample only reads the depth sampler; no engine uniforms required.
            }
        };
        return new XRQuadFrameBuffer(mat, deriveRenderTargetsFromMaterial: false)
        {
            Name = VolumetricFogHalfDepthQuadFBOName
        };
    }

    /// <summary>
    /// Destination FBO that wraps <see cref="VolumetricFogHalfDepthTextureName"/>
    /// as color0 for the half-resolution depth downsample pass.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogHalfDepthFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(VolumetricFogHalfDepthTextureName, CreateVolumetricFogHalfDepthTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = VolumetricFogHalfDepthFBOName
        };
    }

    /// <summary>
    /// Quad FBO for the half-resolution scatter raymarch. Material binding 0
    /// is <see cref="VolumetricFogHalfDepthTextureName"/>; ShadowMap /
    /// ShadowMapArray flow via <see cref="EUniformRequirements.Lights"/>.
    /// Settings and fragment-only camera uniforms are pushed via
    /// <see cref="ApplyVolumetricFogHalfScatterProgramBindings"/>.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogHalfScatterQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(VolumetricFogHalfDepthTextureName)!, // binding 0: sampler2D VolumetricFogHalfDepth
        ];
        XRShader scatterShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "VolumetricFog", "VolumetricFogScatter.fs"),
            EShaderType.Fragment);
        XRMaterial scatterMat = new(refs, scatterShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Lights | EUniformRequirements.RenderTime,
            }
        };
        var fbo = new XRQuadFrameBuffer(scatterMat, deriveRenderTargetsFromMaterial: false)
        {
            Name = VolumetricFogHalfScatterQuadFBOName
        };
        return fbo;
    }

    /// <summary>
    /// Destination FBO for the half-resolution scatter pass. Wraps
    /// <see cref="VolumetricFogHalfScatterTextureName"/> as color0.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogHalfScatterFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(VolumetricFogHalfScatterTextureName, CreateVolumetricFogHalfScatterTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = VolumetricFogHalfScatterFBOName
        };
    }

    /// <summary>
    /// Quad FBO that temporally reprojects the current half-res scatter result
    /// against the previous half-res fog history.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogReprojectQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(VolumetricFogHalfScatterTextureName)!, // binding 0: sampler2D VolumetricFogHalfScatter
            GetTexture<XRTexture>(VolumetricFogHalfHistoryTextureName)!, // binding 1: sampler2D VolumetricFogHalfHistory
            GetTexture<XRTexture>(VolumetricFogHalfDepthTextureName)!,   // binding 2: sampler2D VolumetricFogHalfDepth
        ];
        XRShader reprojectShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "VolumetricFog", "VolumetricFogReproject.fs"),
            EShaderType.Fragment);
        XRMaterial reprojectMat = new(refs, reprojectShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        var fbo = new XRQuadFrameBuffer(reprojectMat, deriveRenderTargetsFromMaterial: false)
        {
            Name = VolumetricFogReprojectQuadFBOName
        };
        return fbo;
    }

    /// <summary>
    /// Destination FBO for the current-frame temporally reprojected fog result.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogReprojectFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(VolumetricFogHalfTemporalTextureName, CreateVolumetricFogHalfTemporalTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = VolumetricFogReprojectFBOName
        };
    }

    /// <summary>
    /// Persistent previous-frame history target for the fog temporal pass.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogHistoryFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(VolumetricFogHalfHistoryTextureName, CreateVolumetricFogHalfHistoryTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = VolumetricFogHistoryFBOName
        };
    }

    /// <summary>
    /// Quad FBO that drives the bilateral upscale. Reads the temporal half-res fog,
    /// half-res depth, and full-res depth, emitting the full-resolution
    /// <see cref="VolumetricFogColorTextureName"/> consumed by PostProcess.fs.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogUpscaleQuadFBO()
    {
        XRTexture[] refs =
        [
            GetTexture<XRTexture>(VolumetricFogHalfTemporalTextureName)!, // binding 0: sampler2D VolumetricFogHalfTemporal
            GetTexture<XRTexture>(VolumetricFogHalfDepthTextureName)!,   // binding 1: sampler2D VolumetricFogHalfDepth
            GetTexture<XRTexture>(DepthViewTextureName)!,                // binding 2: sampler2D DepthView
        ];
        XRShader upscaleShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "VolumetricFog", "VolumetricFogUpscale.fs"),
            EShaderType.Fragment);
        XRMaterial upscaleMat = new(refs, upscaleShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        var fbo = new XRQuadFrameBuffer(upscaleMat, deriveRenderTargetsFromMaterial: false)
        {
            Name = VolumetricFogUpscaleQuadFBOName
        };
        return fbo;
    }

    /// <summary>
    /// Destination FBO for the volumetric fog upscale pass. Wraps
    /// <see cref="VolumetricFogColorTextureName"/> as color0 and is the texture
    /// the post-process composite binds.
    /// </summary>
    private XRFrameBuffer CreateVolumetricFogUpscaleFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(VolumetricFogColorTextureName, CreateVolumetricFogColorTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = VolumetricFogUpscaleFBOName
        };
    }
}
