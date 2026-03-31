using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
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
        XRTexture[] postProcessRefs =
        [
            GetTexture<XRTexture>(HDRSceneTextureName)!,       // binding 0: sampler2D HDRSceneTex
            GetTexture<XRTexture>(BloomBlurTextureName)!,      // binding 1: sampler2D BloomBlurTexture
            GetTexture<XRTexture>(DepthViewTextureName)!,      // binding 2: sampler2D DepthView
            GetTexture<XRTexture>(StencilViewTextureName)!,    // binding 3: usampler2D StencilView
            GetTexture<XRTexture>(AutoExposureTextureName)!,   // binding 4: sampler2D AutoExposureTex
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
            }
        };
        var PostProcessFBO = new XRQuadFrameBuffer(postProcessMat);
        PostProcessFBO.SettingUniforms += PostProcessFBO_SettingUniforms;
        return PostProcessFBO;
    }

    private XRFrameBuffer CreatePostProcessOutputFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(PostProcessOutputTextureName, CreatePostProcessOutputTexture);

        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = PostProcessOutputFBOName
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
        fbo.SettingUniforms += TransformIdDebugQuadFBO_SettingUniforms;
        return fbo;
    }

    private XRFrameBuffer CreateFxaaFBO()
    {
        XRTexture fxaaSource = GetTexture<XRTexture>(PostProcessOutputTextureName)!;
        XRTexture fxaaOutput = GetTexture<XRTexture>(FxaaOutputTextureName)!;
        XRShader fxaaShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "FXAA.fs"), EShaderType.Fragment);
        XRMaterial fxaaMaterial = new([fxaaSource], fxaaShader)
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
        XRQuadFrameBuffer fxaaFbo = new(fxaaMaterial)
        {
            Name = FxaaFBOName
        };
        if (fxaaOutput is not IFrameBufferAttachement fxaaAttach)
            throw new InvalidOperationException("FXAA output texture is not an FBO-attachable texture.");

        fxaaFbo.SetRenderTargets((fxaaAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        fxaaFbo.SettingUniforms += FxaaFBO_SettingUniforms;
        return fxaaFbo;
    }

    /// <summary>
    /// Creates the TSR resolve FBO.
    /// Reads the internal-resolution post-process result plus temporal inputs and writes
    /// the reconstructed full-resolution output to <see cref="FxaaOutputTextureName"/>.
    /// </summary>
    private XRFrameBuffer CreateTsrUpscaleFBO()
    {
        XRTexture sourceTexture = GetTexture<XRTexture>(PostProcessOutputTextureName)!;
        XRTexture velocityTexture = GetTexture<XRTexture>(VelocityTextureName)!;
        XRTexture depthTexture = GetTexture<XRTexture>(DepthViewTextureName)!;
        XRTexture historyDepthTexture = GetTexture<XRTexture>(HistoryDepthViewTextureName)!;
        XRTexture historyColorTexture = GetTexture<XRTexture>(TsrHistoryColorTextureName)!;
        XRTexture outputTexture = GetTexture<XRTexture>(FxaaOutputTextureName)!;
        XRShader upscaleShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "TemporalSuperResolution.fs"), EShaderType.Fragment);
        XRMaterial upscaleMaterial = new([sourceTexture, velocityTexture, depthTexture, historyDepthTexture, historyColorTexture], upscaleShader)
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
        fbo.SettingUniforms += TsrUpscaleFBO_SettingUniforms;
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

    private void TransformIdDebugQuadFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? transformId = GetTexture<XRTexture>(TransformIdTextureName);
        if (transformId is null)
            return;

        program.Sampler(TransformIdTextureName, transformId, 0);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
    }

    private XRFrameBuffer CreateTransparentSceneCopyFBO()
    {
        var colorAttachment = EnsureTextureAttachment(TransparentSceneCopyTextureName, CreateTransparentSceneCopyTexture);
        return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = TransparentSceneCopyFBOName
        };
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

        var fbo = new XRQuadFrameBuffer(material) { Name = DeferredTransparencyBlurFBOName };
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

        var fbo = new XRQuadFrameBuffer(material) { Name = TransparentResolveFBOName };
        fbo.SettingUniforms += TransparentResolveFBO_SettingUniforms;

        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        fbo.SetRenderTargets((hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreateTransparentAccumulationDebugFBO()
        => CreateTransparencyDebugFBO(
            TransparentAccumulationDebugFBOName,
            TransparentAccumulationDebugShaderName(),
            TransparentAccumulationDebugFBO_SettingUniforms,
            GetTexture<XRTexture>(TransparentAccumTextureName)!);

    private XRFrameBuffer CreateTransparentRevealageDebugFBO()
        => CreateTransparencyDebugFBO(
            TransparentRevealageDebugFBOName,
            TransparentRevealageDebugShaderName(),
            TransparentRevealageDebugFBO_SettingUniforms,
            GetTexture<XRTexture>(TransparentRevealageTextureName)!);

    private XRFrameBuffer CreateTransparentOverdrawDebugFBO()
        => CreateTransparencyDebugFBO(
            TransparentOverdrawDebugFBOName,
            TransparentOverdrawDebugShaderName(),
            TransparentOverdrawDebugFBO_SettingUniforms,
            GetTexture<XRTexture>(TransparentRevealageTextureName)!,
            GetTexture<XRTexture>(TransparentAccumTextureName)!);

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
        DelSetUniforms setUniforms,
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
        fbo.SettingUniforms += setUniforms;
        return fbo;
    }

    private void TransparentResolveFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? sceneColor = GetTexture<XRTexture>(TransparentSceneCopyTextureName);
        XRTexture? accum = GetTexture<XRTexture>(TransparentAccumTextureName);
        XRTexture? revealage = GetTexture<XRTexture>(TransparentRevealageTextureName);
        if (sceneColor is null || accum is null || revealage is null)
            return;

        program.Sampler(TransparentSceneCopyTextureName, sceneColor, 0);
        program.Sampler(TransparentAccumTextureName, accum, 1);
        program.Sampler(TransparentRevealageTextureName, revealage, 2);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
    }

    private void TransparentAccumulationDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? accum = GetTexture<XRTexture>(TransparentAccumTextureName);
        if (accum is null)
            return;

        program.Sampler(TransparentAccumTextureName, accum, 0);
    }

    private void TransparentRevealageDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? revealage = GetTexture<XRTexture>(TransparentRevealageTextureName);
        if (revealage is null)
            return;

        program.Sampler(TransparentRevealageTextureName, revealage, 0);
    }

    private void TransparentOverdrawDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? revealage = GetTexture<XRTexture>(TransparentRevealageTextureName);
        XRTexture? accum = GetTexture<XRTexture>(TransparentAccumTextureName);
        if (revealage is null || accum is null)
            return;

        program.Sampler(TransparentRevealageTextureName, revealage, 0);
        program.Sampler(TransparentAccumTextureName, accum, 1);
    }

    private XRFrameBuffer CreateForwardPassFBO()
    {
        XRTexture hdrSceneTex = (XRTexture)EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);

        XRTexture[] brightRefs = [hdrSceneTex];
        XRMaterial brightMat = new(
            [
                new ShaderFloat(1.0f, "BloomIntensity"),
                new ShaderFloat(1.0f, "BloomThreshold"),
                new ShaderFloat(0.5f, "SoftKnee"),
                new ShaderVector3(Engine.Rendering.Settings.DefaultLuminance, "Luminance")
            ],
            brightRefs,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, BrightPassShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(brightMat, false);
        fbo.SettingUniforms += BrightPassFBO_SettingUniforms;

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

        material.SettingUniforms += MotionVectorsMaterial_SettingUniforms;
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

    private void MotionVectorsMaterial_SettingUniforms(XRMaterialBase material, XRRenderProgram program)
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

        var camera = Engine.Rendering.State.RenderingCamera;
        if (camera is not null)
        {
            Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
            Matrix4x4 projMatrix = camera.ProjectionMatrix;
            Matrix4x4 viewProj = viewMatrix * projMatrix;
            Debug.Out("[Velocity] Temporal data unavailable; using current camera matrices for motion vectors.");
            program.Uniform("CurrViewProjection", viewProj);
            program.Uniform("PrevViewProjection", viewProj);
        }
        else
        {
            Debug.Out("[Velocity] No camera available; motion vectors will be zeroed.");
            program.Uniform("CurrViewProjection", Matrix4x4.Identity);
            program.Uniform("PrevViewProjection", Matrix4x4.Identity);
        }
    }

    private IFrameBufferAttachement EnsureTextureAttachment(string textureName, Func<XRTexture> factory)
    {
        XRTexture? texture = null;
        bool hasConcreteTexture = Engine.Rendering.State.CurrentRenderingPipeline?.Resources.TryGetTexture(textureName, out texture) == true;
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

        fbo.SettingUniforms += TemporalAccumulationFBO_SettingUniforms;
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
        fbo.SettingUniforms += MotionBlurFBO_SettingUniforms;
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
        fbo.SettingUniforms += DepthOfFieldFBO_SettingUniforms;
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
        => ResolveOutputRenderBufferStorage();

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
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera
            }
        };

        // Wire probe/AO binding through the material's SettingUniforms so that probe sampler
        // locations are registered in _boundSamplerLocations BEFORE BindFallbackSamplers runs.
        // (XRQuadFrameBuffer.SettingUniforms fires after BindFallbackSamplers, which would
        //  override layout(binding=) probe samplers with fallback textures.)
        lightCombineMat.SettingUniforms += (_, program) => LightCombineFBO_SettingUniforms(program);

        var lightCombineFBO = new XRQuadFrameBuffer(lightCombineMat) { Name = LightCombineFBOName };

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
        var fbo = new XRQuadFrameBuffer(restirCompositeMaterial) { Name = RestirCompositeFBOName };
        fbo.SettingUniforms += RestirCompositeFBO_SettingUniforms;
        return fbo;
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

        var fbo = new XRQuadFrameBuffer(material) { Name = SurfelGICompositeFBOName };
        fbo.SettingUniforms += SurfelGICompositeFBO_SettingUniforms;
        return fbo;
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

        var fbo = new XRQuadFrameBuffer(material) { Name = LightVolumeCompositeFBOName };
        fbo.SettingUniforms += LightVolumeCompositeFBO_SettingUniforms;
        return fbo;
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

        var fbo = new XRQuadFrameBuffer(material) { Name = RadianceCascadeCompositeFBOName };
        fbo.SettingUniforms += RadianceCascadeCompositeFBO_SettingUniforms;
        return fbo;
    }

    private void RadianceCascadeCompositeFBO_SettingUniforms(XRRenderProgram program)
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
                    Enabled = ERenderParamUsage.Unchanged,
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

        // Wire through material SettingUniforms (same reason as non-MSAA path above).
        mat.SettingUniforms += (_, program) => LightCombineFBO_SettingUniforms(program);

        var fbo = new XRQuadFrameBuffer(mat, true, false) { Name = MsaaLightCombineFBOName };
        return fbo;
    }
}
