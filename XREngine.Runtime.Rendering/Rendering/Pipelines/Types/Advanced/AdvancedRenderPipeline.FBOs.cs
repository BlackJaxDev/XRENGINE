using System;
using System.IO;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private string TransparentResolveShaderName()
        => Stereo ? "TransparentResolveStereo.fs" : "TransparentResolve.fs";

    private string TransparentAccumulationDebugShaderName()
        => Stereo ? "TransparentAccumulationDebugStereo.fs" : "TransparentAccumulationDebug.fs";

    private string TransparentRevealageDebugShaderName()
        => Stereo ? "TransparentRevealageDebugStereo.fs" : "TransparentRevealageDebug.fs";

    private string TransparentOverdrawDebugShaderName()
        => Stereo ? "TransparentOverdrawDebugStereo.fs" : "TransparentOverdrawDebug.fs";

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
    //    var uiFBO = new XRQuadFrameBuffer(hudMat, useMultiview: Stereo);

    //    if (hudTexture is not IFrameBufferAttachement hudAttach)
    //        throw new InvalidOperationException("HUD texture must be an FBO-attachable texture.");

    //    uiFBO.SetRenderTargets((hudAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));

    //    return uiFBO;
    //}

    private IIncrementalFrameBufferFactory CreatePostProcessFBOIncrementally()
        => new PostProcessFrameBufferFactory(this);

    private sealed class PostProcessFrameBufferFactory(AdvancedRenderPipeline owner) : IIncrementalFrameBufferFactory
    {
        private int _stage;
        private XRTexture[]? _textureReferences;
        private XRShader? _shader;
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _frameBuffer;
        private bool _transferred;

        public bool MoveNext(out XRFrameBuffer? frameBuffer)
        {
            frameBuffer = null;
            switch (_stage)
            {
                case 0:
                    PrepareTexturesAndShader();
                    _stage++;
                    return false;
                case 1:
                    CreateMaterial();
                    _stage++;
                    return false;
                case 2:
                    _frameBuffer = new XRQuadFrameBuffer(
                        _material ?? throw new InvalidOperationException("Post-process material was not prepared."),
                        deriveRenderTargetsFromMaterial: false,
                        useMultiview: owner.Stereo,
                        prepareForInitialRendering: false);
                    _stage++;
                    return false;
                case 3:
                    _frameBuffer!.PrepareInitialRenderingVersion();
                    _stage++;
                    return false;
                case 4:
                    CompleteFrameBuffer();
                    frameBuffer = _frameBuffer;
                    _frameBuffer = null;
                    _material = null;
                    _shader = null;
                    _textureReferences = null;
                    _transferred = true;
                    _stage++;
                    return true;
                default:
                    throw new InvalidOperationException("Post-process framebuffer factory was advanced after completion.");
            }
        }

        private void PrepareTexturesAndShader()
        {
            _textureReferences = owner.Stereo
                ?
                [
                    owner.RequirePostProcessTexture(HDRSceneTextureName),
                    owner.RequirePostProcessTexture(BloomBlurTextureName),
                    owner.RequirePostProcessTexture(DepthViewTextureName),
                    owner.RequirePostProcessTexture(StencilViewTextureName),
                    owner.RequirePostProcessTexture(AutoExposureTextureName),
                    owner.RequirePostProcessTexture(AdvancedVisibilityResourceNames.Metadata),
                ]
                :
                [
                    owner.RequirePostProcessTexture(HDRSceneTextureName),
                    owner.RequirePostProcessTexture(BloomBlurTextureName),
                    owner.RequirePostProcessTexture(DepthViewTextureName),
                    owner.RequirePostProcessTexture(StencilViewTextureName),
                    owner.RequirePostProcessTexture(AutoExposureTextureName),
                    owner.RequirePostProcessTexture(AtmosphereColorTextureName),
                    owner.RequirePostProcessTexture(VolumetricFogColorTextureName),
                    owner.RequirePostProcessTexture(AdvancedVisibilityResourceNames.Metadata),
                ];
            _shader = CreateAdvancedPostProcessShader(owner.PostProcessShaderName());
        }

        private void CreateMaterial()
        {
            _material = new XRMaterial(
                _textureReferences ?? throw new InvalidOperationException("Post-process textures were not prepared."),
                _shader ?? throw new InvalidOperationException("Post-process shader was not prepared."))
            {
                Name = PostProcessFBOName,
                RenderOptions = new RenderingParameters
                {
                    DepthTest = new DepthTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        Function = EComparison.Always,
                        UpdateDepth = false,
                    },
                    BlendModeAllDrawBuffers = BlendMode.Disabled(),
                    RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights | EUniformRequirements.RenderTime | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
                }
            };
        }

        private void CompleteFrameBuffer()
        {
            XRQuadFrameBuffer frameBuffer = _frameBuffer
                ?? throw new InvalidOperationException("Post-process framebuffer was not constructed.");
            XRMaterial material = _material
                ?? throw new InvalidOperationException("Post-process material was not constructed.");
            frameBuffer.PrepareForInitialRendering();
            frameBuffer.SettingUniforms += program => owner.ApplyPostProcessProgramBindings(material, program);
        }

        public void Dispose()
        {
            if (_transferred)
                return;

            _frameBuffer?.FullScreenMesh.Destroy();
            _frameBuffer?.Destroy();
            _material?.Destroy();
            _frameBuffer = null;
            _material = null;
            _shader = null;
            _textureReferences = null;
        }
    }

    private static XRShader CreateAdvancedPostProcessShader(string fileName)
        => ShaderHelper.CreateDefinedShaderVariant(
            XRShader.EngineShader(Path.Combine(SceneShaderPath, fileName), EShaderType.Fragment),
            "XR_ADVANCED_EDITOR_HIGHLIGHT_METADATA")
        ?? throw new InvalidOperationException($"Advanced post-process shader '{fileName}' is unavailable.");

    private XRTexture RequirePostProcessTexture(string resourceName)
        => GetTexture<XRTexture>(resourceName)
            ?? throw new InvalidOperationException($"Post-process sampler resource '{resourceName}' was not realized.");

    private XRFrameBuffer CreatePostProcessOutputFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(PostProcessOutputTextureName, CreatePostProcessOutputTexture);

        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
            }
        };

        XRQuadFrameBuffer fbo = new(material, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
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
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };
        return new XRQuadFrameBuffer(mat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = AtmosphereHalfDepthQuadFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHalfDepthFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfDepthTextureName, CreateAtmosphereHalfDepthTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.RenderTime | EUniformRequirements.ClipSpacePolicy,
            }
        };
        var fbo = new XRQuadFrameBuffer(scatterMat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = AtmosphereHalfScatterQuadFBOName
        };
        fbo.SettingUniforms += ApplyAtmosphereHalfScatterProgramBindings;
        return fbo;
    }

    private XRFrameBuffer CreateAtmosphereHalfScatterFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfScatterTextureName, CreateAtmosphereHalfScatterTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };
        var fbo = new XRQuadFrameBuffer(reprojectMat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = AtmosphereReprojectQuadFBOName
        };
        fbo.SettingUniforms += ApplyAtmosphereReprojectProgramBindings;
        return fbo;
    }

    private XRFrameBuffer CreateAtmosphereReprojectFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfTemporalTextureName, CreateAtmosphereHalfTemporalTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = AtmosphereReprojectFBOName
        };
    }

    private XRFrameBuffer CreateAtmosphereHistoryFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereHalfHistoryTextureName, CreateAtmosphereHalfHistoryTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };
        var fbo = new XRQuadFrameBuffer(upscaleMat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = AtmosphereUpscaleQuadFBOName
        };
        fbo.SettingUniforms += ApplyAtmosphereUpscaleProgramBindings;
        return fbo;
    }

    private XRFrameBuffer CreateAtmosphereUpscaleFBO()
    {
        IFrameBufferAttachement attach = EnsureTextureAttachment(AtmosphereColorTextureName, CreateAtmosphereColorTexture);
        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };

        var fbo = new XRQuadFrameBuffer(mat, useMultiview: Stereo)
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
            ForceOvrMultiview = Stereo,
            Name = FxaaFBOName
        };
    }

    /// <summary>
    /// Creates the TSR resolve FBO.
    /// Reads the internal-resolution final post-process result plus temporal inputs and writes
    /// the reconstructed full-resolution output to <see cref="TsrOutputTextureName"/>.
    /// </summary>
    private IIncrementalFrameBufferFactory CreateTsrUpscaleFBOIncrementally()
        => new TsrUpscaleFrameBufferFactory(this);

    private sealed class TsrUpscaleFrameBufferFactory(AdvancedRenderPipeline owner) : IIncrementalFrameBufferFactory
    {
        private int _stage;
        private XRTexture[]? _textureReferences;
        private XRTexture? _outputTexture;
        private XRShader? _shader;
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _frameBuffer;
        private bool _transferred;

        public bool MoveNext(out XRFrameBuffer? frameBuffer)
        {
            frameBuffer = null;
            switch (_stage)
            {
                case 0:
                    PrepareTexturesAndShader();
                    _stage++;
                    return false;
                case 1:
                    CreateMaterial();
                    _stage++;
                    return false;
                case 2:
                    _frameBuffer = new XRQuadFrameBuffer(
                        _material ?? throw new InvalidOperationException("TSR upscale material was not prepared."),
                        deriveRenderTargetsFromMaterial: false,
                        useMultiview: owner.Stereo,
                        prepareForInitialRendering: false)
                    {
                        Name = TsrUpscaleFBOName
                    };
                    _stage++;
                    return false;
                case 3:
                    _frameBuffer!.PrepareInitialRenderingVersion();
                    _stage++;
                    return false;
                case 4:
                    CompleteFrameBuffer();
                    frameBuffer = _frameBuffer;
                    _frameBuffer = null;
                    _material = null;
                    _shader = null;
                    _textureReferences = null;
                    _outputTexture = null;
                    _transferred = true;
                    _stage++;
                    return true;
                default:
                    throw new InvalidOperationException("TSR upscale framebuffer factory was advanced after completion.");
            }
        }

        private void PrepareTexturesAndShader()
        {
            _textureReferences =
            [
                GetTexture<XRTexture>(FinalPostProcessOutputTextureName)!,
                GetTexture<XRTexture>(VelocityTextureName)!,
                GetTexture<XRTexture>(DepthViewTextureName)!,
                GetTexture<XRTexture>(HistoryDepthViewTextureName)!,
                GetTexture<XRTexture>(TsrHistoryColorTextureName)!,
                GetTexture<XRTexture>(StencilViewTextureName)!,
                GetTexture<XRTexture>(AdvancedTemporalHistoryContract.ReactiveMaskResourceName)!,
            ];
            _outputTexture = GetTexture<XRTexture>(TsrOutputTextureName)!;
            _shader = CreateAdvancedTemporalShader(
                owner.Stereo ? "TemporalSuperResolutionStereo.fs" : "TemporalSuperResolution.fs");
        }

        private void CreateMaterial()
        {
            _material = new XRMaterial(
                _textureReferences ?? throw new InvalidOperationException("TSR upscale textures were not prepared."),
                _shader ?? throw new InvalidOperationException("TSR upscale shader was not prepared."))
            {
                RenderOptions = new RenderingParameters
                {
                    DepthTest = new DepthTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        Function = EComparison.Always,
                        UpdateDepth = false,
                    },
                    StencilTest = new StencilTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                    },
                    BlendModeAllDrawBuffers = BlendMode.Disabled(),
                    RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy
                        | EUniformRequirements.ViewportDimensions,
                }
            };
        }

        private void CompleteFrameBuffer()
        {
            XRQuadFrameBuffer frameBuffer = _frameBuffer
                ?? throw new InvalidOperationException("TSR upscale framebuffer was not constructed.");
            frameBuffer.PrepareForInitialRendering();
            if (_outputTexture is not IFrameBufferAttachement outputAttachment)
                throw new InvalidOperationException("TSR upscale output texture is not an FBO-attachable texture.");

            frameBuffer.SetRenderTargets((outputAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            frameBuffer.SettingUniforms += owner.ApplyTsrUpscaleProgramBindings;
        }

        public void Dispose()
        {
            if (_transferred)
                return;

            _frameBuffer?.Destroy();
            _material?.Destroy();
            _frameBuffer = null;
            _material = null;
            _shader = null;
            _textureReferences = null;
            _outputTexture = null;
        }
    }

    private XRFrameBuffer CreateTsrHistoryColorFBO()
    {
        XRTexture historyTexture = GetTexture<XRTexture>(TsrHistoryColorTextureName)!;
        if (historyTexture is not IFrameBufferAttachement historyAttach)
            throw new InvalidOperationException("TSR history color texture is not an FBO-attachable texture.");

        return new XRFrameBuffer((historyAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = TsrHistoryColorFBOName
        };
    }

    private XRFrameBuffer CreateTransparentSceneCopyFBO()
    {
        var colorAttachment = EnsureTextureAttachment(TransparentSceneCopyTextureName, CreateTransparentSceneCopyTexture);
        return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
                StencilTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
            }
        };

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo) { Name = DeferredTransparencyBlurFBOName };
        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        fbo.SetRenderTargets((hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreateTransparentAccumulationFBO()
    {
        var accumAttachment = EnsureTextureAttachment(TransparentAccumTextureName, CreateTransparentAccumTexture);
        var revealageAttachment = EnsureTextureAttachment(TransparentRevealageTextureName, CreateTransparentRevealageTexture);
        var depthAttachment = RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DepthStencil);

        return new XRFrameBuffer(
            (accumAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (revealageAttachment, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
                StencilTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
            }
        };

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo) { Name = TransparentResolveFBOName };

        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        fbo.SetRenderTargets((hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        fbo.SettingUniforms += ApplyTransparentResolveProgramBindings;
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
            ForceOvrMultiview = Stereo,
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

        var fbo = new XRQuadFrameBuffer(material, useMultiview: Stereo) { Name = FullOverdrawDebugFBOName };
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
                StencilTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };

        return new XRQuadFrameBuffer(material, useMultiview: Stereo) { Name = SceneCopyFBOName };
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

        var fbo = new XRQuadFrameBuffer(material, useMultiview: Stereo) { Name = name };
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

    private IIncrementalFrameBufferFactory CreateForwardPassFBOIncrementally()
        => new ForwardPassFrameBufferFactory(this);

    private sealed class ForwardPassFrameBufferFactory(AdvancedRenderPipeline owner) : IIncrementalFrameBufferFactory
    {
        private int _stage;
        private XRTexture? _hdrSceneTexture;
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _frameBuffer;
        private bool _transferred;

        public bool MoveNext(out XRFrameBuffer? frameBuffer)
        {
            frameBuffer = null;
            switch (_stage)
            {
                case 0:
                    CreateMaterial();
                    _stage++;
                    return false;
                case 1:
                    _frameBuffer = new XRQuadFrameBuffer(
                        _material ?? throw new InvalidOperationException("Forward-pass material was not prepared."),
                        useTriangle: false,
                        deriveRenderTargetsFromMaterial: false,
                        useMultiview: owner.Stereo,
                        prepareForInitialRendering: false);
                    _stage++;
                    return false;
                case 2:
                    _frameBuffer!.PrepareInitialRenderingVersion();
                    _stage++;
                    return false;
                case 3:
                    CompleteFrameBuffer();
                    frameBuffer = _frameBuffer;
                    _frameBuffer = null;
                    _material = null;
                    _hdrSceneTexture = null;
                    _transferred = true;
                    _stage++;
                    return true;
                default:
                    throw new InvalidOperationException("Forward-pass framebuffer factory was advanced after completion.");
            }
        }

        private void CreateMaterial()
        {
            _hdrSceneTexture = (XRTexture)owner.EnsureTextureAttachment(HDRSceneTextureName, owner.CreateHDRSceneTexture);
            XRShader sceneCopyShader = XRShader.EngineShader(
                Path.Combine(SceneShaderPath, owner.SceneCopyShaderName()),
                EShaderType.Fragment);
            _material = new XRMaterial([_hdrSceneTexture], sceneCopyShader)
            {
                Name = ForwardPassFBOName,
                RenderOptions = new RenderingParameters
                {
                    DepthTest = new DepthTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        Function = EComparison.Always,
                        UpdateDepth = false,
                    },
                    StencilTest = new StencilTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                    },
                    BlendModeAllDrawBuffers = BlendMode.Disabled(),
                    RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
                }
            };
        }

        private void CompleteFrameBuffer()
        {
            XRQuadFrameBuffer frameBuffer = _frameBuffer
                ?? throw new InvalidOperationException("Forward-pass framebuffer was not constructed.");
            XRTexture hdrSceneTexture = _hdrSceneTexture
                ?? throw new InvalidOperationException("Forward-pass HDR texture was not prepared.");
            frameBuffer.PrepareForInitialRendering();
            frameBuffer.SetRenderTargets(
                ((IFrameBufferAttachement)hdrSceneTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DepthStencil), EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
        }

        public void Dispose()
        {
            if (_transferred)
                return;

            _frameBuffer?.FullScreenMesh.Destroy();
            _frameBuffer?.Destroy();
            _material?.Destroy();
            _frameBuffer = null;
            _material = null;
            _hdrSceneTexture = null;
        }
    }

    private XRFrameBuffer CreateForwardPassMsaaFBO()
    {
        XRRenderBuffer colorBuffer = TryCurrentPipeline?.GetRenderBuffer(ForwardPassMsaaColorRenderBufferName)
            ?? throw new InvalidOperationException($"Missing declared renderbuffer '{ForwardPassMsaaColorRenderBufferName}'.");

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
            ForceOvrMultiview = Stereo,
            Name = GBufferFBOName
        };
    }

    private XRFrameBuffer CreateDeferredGBufferFBO()
    {
        IFrameBufferAttachement albedoAttach = EnsureTextureAttachment(AlbedoOpacityTextureName, CreateAlbedoOpacityTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(NormalTextureName, CreateNormalTexture);
        IFrameBufferAttachement rmseAttach = EnsureTextureAttachment(RMSETextureName, CreateRMSETexture);
        IFrameBufferAttachement transformIdAttach = EnsureTextureAttachment(TransformIdTextureName, CreateTransformIdTexture);
        IFrameBufferAttachement emissionAttach = EnsureTextureAttachment(EmissionColorTextureName, CreateEmissionColorTexture);
        IFrameBufferAttachement depthStencilAttach = EnsureTextureAttachment(DepthStencilTextureName, CreateDepthStencilTexture);

        return new XRFrameBuffer(
            (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
            (emissionAttach, EFrameBufferAttachment.ColorAttachment4, 0, -1),
            (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = DeferredGBufferFBOName
        };
    }

    private XRMaterial CreateMotionVectorsMaterial()
    {
        XRShader shader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, Stereo ? "MotionVectorsStereo.fs" : "MotionVectors.fs"),
            EShaderType.Fragment);
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
                StencilTest = new StencilTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
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

    private IFrameBufferAttachement EnsureTextureAttachment(string textureName, Func<XRTexture> factory)
    {
        _ = factory;
        XRTexture? texture = null;
        bool hasConcreteTexture = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Resources.TryGetTexture(textureName, out texture) == true;
        if (hasConcreteTexture && texture is IFrameBufferAttachement attachment)
            return attachment;

        string actual = hasConcreteTexture && texture is not null ? texture.GetType().Name : "missing";
        throw new InvalidOperationException(
            $"Declared framebuffer dependency '{textureName}' is not attachable (actual={actual}). " +
            "Resource factories must not repair or mutate the active registry.");
    }

    private XRFrameBuffer CreateVelocityFBO()
    {
        var velocityAttachment = EnsureTextureAttachment(VelocityTextureName, CreateVelocityTexture);
        var depthAttachment = EnsureTextureAttachment(AdvancedVisibilityResourceNames.DepthStencil, CreateDepthStencilTexture);

        return new XRFrameBuffer(
            (velocityAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
            Name = HistoryCaptureFBOName
        };
    }

    private XRFrameBuffer CreateTemporalInputFBO()
    {
        var colorAttachment = EnsureTextureAttachment(TemporalColorInputTextureName, CreateTemporalColorInputTexture);

        return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
            GetTexture<XRTexture>(AdvancedTemporalHistoryContract.ReactiveMaskResourceName)!,
        ];

        XRMaterial material = new(references,
            CreateAdvancedTemporalShader(Stereo ? "TemporalAccumulationStereo.fs" : "TemporalAccumulation.fs"))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                StencilTest = new StencilTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };

        var fbo = new XRQuadFrameBuffer(material, useMultiview: Stereo) { Name = TemporalAccumulationFBOName };

        var filteredAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        var exposureAttachment = EnsureTextureAttachment(TemporalExposureVarianceTextureName, CreateTemporalExposureVarianceTexture);

        fbo.SetRenderTargets(
            (filteredAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (exposureAttachment, EFrameBufferAttachment.ColorAttachment1, 0, -1));

        fbo.SettingUniforms += ApplyTemporalAccumulationProgramBindings;
        return fbo;
    }

    private XRFrameBuffer CreateMotionBlurCopyFBO()
    {
        var attachment = EnsureTextureAttachment(MotionBlurTextureName, CreateMotionBlurTexture);

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = MotionBlurCopyFBOName
        };
    }

    private IIncrementalFrameBufferFactory CreateMotionBlurFBOIncrementally()
        => new MotionBlurFrameBufferFactory(this);

    private sealed class MotionBlurFrameBufferFactory(AdvancedRenderPipeline owner) : IIncrementalFrameBufferFactory
    {
        private int _stage;
        private XRTexture[]? _textureReferences;
        private XRShader? _shader;
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _frameBuffer;
        private bool _transferred;

        public bool MoveNext(out XRFrameBuffer? frameBuffer)
        {
            frameBuffer = null;
            switch (_stage)
            {
                case 0:
                    _textureReferences =
                    [
                        GetTexture<XRTexture>(MotionBlurTextureName)!,
                        GetTexture<XRTexture>(VelocityTextureName)!,
                        GetTexture<XRTexture>(DepthViewTextureName)!,
                    ];
                    _stage++;
                    return false;
                case 1:
                    _shader = XRShader.EngineShader(
                        Path.Combine(SceneShaderPath, owner.Stereo ? "MotionBlurStereo.fs" : "MotionBlur.fs"),
                        EShaderType.Fragment);
                    _stage++;
                    return false;
                case 2:
                    CreateMaterial();
                    _stage++;
                    return false;
                case 3:
                    _frameBuffer = new XRQuadFrameBuffer(
                        _material ?? throw new InvalidOperationException("Motion-blur material was not prepared."),
                        deriveRenderTargetsFromMaterial: false,
                        useMultiview: owner.Stereo,
                        prepareForInitialRendering: false)
                    {
                        Name = MotionBlurFBOName
                    };
                    _stage++;
                    return false;
                case 4:
                    _frameBuffer!.PrepareInitialRenderingVersion();
                    _stage++;
                    return false;
                case 5:
                    CompleteFrameBuffer();
                    frameBuffer = _frameBuffer;
                    _frameBuffer = null;
                    _material = null;
                    _shader = null;
                    _textureReferences = null;
                    _transferred = true;
                    _stage++;
                    return true;
                default:
                    throw new InvalidOperationException("Motion-blur framebuffer factory was advanced after completion.");
            }
        }

        private void CreateMaterial()
        {
            _material = new XRMaterial(
                _textureReferences ?? throw new InvalidOperationException("Motion-blur textures were not prepared."),
                _shader ?? throw new InvalidOperationException("Motion-blur shader was not prepared."))
            {
                Name = MotionBlurFBOName,
                RenderOptions = new RenderingParameters
                {
                    DepthTest = new DepthTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        Function = EComparison.Always,
                        UpdateDepth = false,
                    },
                    RequiredEngineUniforms = EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
                }
            };
        }

        private void CompleteFrameBuffer()
        {
            XRQuadFrameBuffer frameBuffer = _frameBuffer
                ?? throw new InvalidOperationException("Motion-blur framebuffer was not constructed.");
            frameBuffer.PrepareForInitialRendering();
            frameBuffer.SettingUniforms += owner.ApplyMotionBlurProgramBindings;
        }

        public void Dispose()
        {
            if (_transferred)
                return;

            _frameBuffer?.FullScreenMesh.Destroy();
            _frameBuffer?.Destroy();
            _material?.Destroy();
            _frameBuffer = null;
            _material = null;
            _shader = null;
            _textureReferences = null;
        }
    }

    private XRFrameBuffer CreateDepthOfFieldCopyFBO()
    {
        var attachment = EnsureTextureAttachment(DepthOfFieldTextureName, CreateDepthOfFieldTexture);

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = DepthOfFieldCopyFBOName
        };
    }

    private IIncrementalFrameBufferFactory CreateDepthOfFieldFBOIncrementally()
        => new DepthOfFieldFrameBufferFactory(this);

    private sealed class DepthOfFieldFrameBufferFactory(AdvancedRenderPipeline owner) : IIncrementalFrameBufferFactory
    {
        private int _stage;
        private XRTexture[]? _textureReferences;
        private XRShader? _shader;
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _frameBuffer;
        private bool _transferred;

        public bool MoveNext(out XRFrameBuffer? frameBuffer)
        {
            frameBuffer = null;
            switch (_stage)
            {
                case 0:
                    _textureReferences =
                    [
                        GetTexture<XRTexture>(DepthOfFieldTextureName)!,
                        GetTexture<XRTexture>(DepthViewTextureName)!,
                    ];
                    _shader = XRShader.EngineShader(
                        Path.Combine(SceneShaderPath, owner.Stereo ? "DepthOfFieldStereo.fs" : "DepthOfField.fs"),
                        EShaderType.Fragment);
                    _stage++;
                    return false;
                case 1:
                    CreateMaterial();
                    _stage++;
                    return false;
                case 2:
                    _frameBuffer = new XRQuadFrameBuffer(
                        _material ?? throw new InvalidOperationException("Depth-of-field material was not prepared."),
                        deriveRenderTargetsFromMaterial: false,
                        useMultiview: owner.Stereo,
                        prepareForInitialRendering: false)
                    {
                        Name = DepthOfFieldFBOName
                    };
                    _stage++;
                    return false;
                case 3:
                    _frameBuffer!.PrepareInitialRenderingVersion();
                    _stage++;
                    return false;
                case 4:
                    XRQuadFrameBuffer completed = _frameBuffer
                        ?? throw new InvalidOperationException("Depth-of-field framebuffer was not constructed.");
                    completed.PrepareForInitialRendering();
                    completed.SettingUniforms += owner.ApplyDepthOfFieldProgramBindings;
                    frameBuffer = completed;
                    _frameBuffer = null;
                    _material = null;
                    _shader = null;
                    _textureReferences = null;
                    _transferred = true;
                    _stage++;
                    return true;
                default:
                    throw new InvalidOperationException("Depth-of-field framebuffer factory was advanced after completion.");
            }
        }

        private void CreateMaterial()
        {
            _material = new XRMaterial(
                _textureReferences ?? throw new InvalidOperationException("Depth-of-field textures were not prepared."),
                _shader ?? throw new InvalidOperationException("Depth-of-field shader was not prepared."))
            {
                RenderOptions = new RenderingParameters()
                {
                    DepthTest = new()
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        Function = EComparison.Always,
                        UpdateDepth = false,
                    },
                    RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy
                }
            };
        }

        public void Dispose()
        {
            if (_transferred)
                return;

            _frameBuffer?.FullScreenMesh.Destroy();
            _frameBuffer?.Destroy();
            _material?.Destroy();
            _frameBuffer = null;
            _material = null;
            _shader = null;
            _textureReferences = null;
        }
    }

    private XRFrameBuffer CreateHistoryExposureFBO()
    {
        var attachment = EnsureTextureAttachment(HistoryExposureVarianceTextureName, CreateHistoryExposureVarianceTexture);

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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

        return new XRQuadFrameBuffer(material, false, useMultiview: Stereo) { Name = DepthPreloadFBOName };
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
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
            Name = ForwardContactPrePassCopyFBOName
        };
    }

    private XRFrameBuffer CreateLightCombineFBO()
    {
        var diffuseTexture = GetTexture<XRTexture>(DiffuseTextureName)!;
        var lightingAccumTexture = GetTexture<XRTexture>(LightingAccumTextureName)!;

        XRTexture[] lightCombineTextures = [
            GetTexture<XRTexture>(AlbedoOpacityTextureName)!,
            GetTexture<XRTexture>(NormalTextureName)!,
            GetTexture<XRTexture>(RMSETextureName)!,
            GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
            lightingAccumTexture,
            GetTexture<XRTexture>(BRDFTextureName)!,
            GetTexture<XRTexture>(EmissionColorTextureName)!,
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
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
                BlendModeAllDrawBuffers = BlendMode.Disabled()
            }
        };
        lightCombineMat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);

        var lightCombineFBO = new XRQuadFrameBuffer(lightCombineMat, useTriangle: true, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo) { Name = LightCombineFBOName };

        if (diffuseTexture is not IFrameBufferAttachement attach)
            throw new InvalidOperationException($"Declared texture '{DiffuseTextureName}' is not FBO-attachable.");
        lightCombineFBO.SetRenderTargets((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1));

        return lightCombineFBO;
    }

    private XRFrameBuffer CreateLightingAccumFBO()
    {
        IFrameBufferAttachement lightingAttach = EnsureTextureAttachment(LightingAccumTextureName, CreateLightingAccumTexture);

        return new XRFrameBuffer((lightingAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = LightingAccumFBOName
        };
    }

    // --- MSAA Deferred GBuffer FBO ---

    private XRFrameBuffer CreateMsaaGBufferFBO()
    {
        IFrameBufferAttachement albedoAttach = EnsureTextureAttachment(MsaaAlbedoOpacityTextureName, CreateMsaaAlbedoOpacityTexture);
        IFrameBufferAttachement normalAttach = EnsureTextureAttachment(MsaaNormalTextureName, CreateMsaaNormalTexture);
        IFrameBufferAttachement rmseAttach = EnsureTextureAttachment(MsaaRMSETextureName, CreateMsaaRMSETexture);
        IFrameBufferAttachement transformIdAttach = EnsureTextureAttachment(MsaaTransformIdTextureName, CreateMsaaTransformIdTexture);
        IFrameBufferAttachement emissionAttach = EnsureTextureAttachment(MsaaEmissionColorTextureName, CreateMsaaEmissionColorTexture);
        IFrameBufferAttachement depthStencilAttach = EnsureTextureAttachment(MsaaDepthStencilTextureName, CreateMsaaDepthStencilTexture);

        return new XRFrameBuffer(
            (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
            (emissionAttach, EFrameBufferAttachment.ColorAttachment4, 0, -1),
            (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
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
            GetTexture<XRTexture>(MsaaEmissionColorTextureName)!,
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
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy
            }
        };
        mat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);

        var fbo = new XRQuadFrameBuffer(mat, true, false, useMultiview: Stereo) { Name = MsaaLightCombineFBOName };
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };
        return new XRQuadFrameBuffer(mat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
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
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.Lights | EUniformRequirements.RenderTime | EUniformRequirements.ClipSpacePolicy,
            }
        };
        var fbo = new XRQuadFrameBuffer(scatterMat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = VolumetricFogHalfScatterQuadFBOName
        };
        fbo.SettingUniforms += ApplyVolumetricFogHalfScatterProgramBindings;
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
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };
        var fbo = new XRQuadFrameBuffer(reprojectMat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = VolumetricFogReprojectQuadFBOName
        };
        fbo.SettingUniforms += ApplyVolumetricFogReprojectProgramBindings;
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
            ForceOvrMultiview = Stereo,
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
            ForceOvrMultiview = Stereo,
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
                RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
            }
        };
        var fbo = new XRQuadFrameBuffer(upscaleMat, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = VolumetricFogUpscaleQuadFBOName
        };
        fbo.SettingUniforms += ApplyVolumetricFogUpscaleProgramBindings;
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
            ForceOvrMultiview = Stereo,
            Name = VolumetricFogUpscaleFBOName
        };
    }
    private XRRenderBuffer CreateForwardPassMsaaColorRenderBuffer()
    {
        XRRenderBuffer colorBuffer = new(InternalWidth, InternalHeight, GetForwardMsaaColorFormat(), MsaaSampleCount)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            Name = ForwardPassMsaaColorRenderBufferName
        };
        colorBuffer.Allocate();
        return colorBuffer;
    }

    private XRFrameBuffer CreateSmaaEdgeFBO()
        => CreateDeclaredColorFBO(SmaaEdgeFBOName, SmaaEdgeTextureName);

    private XRFrameBuffer CreateSmaaBlendFBO()
        => CreateDeclaredColorFBO(SmaaBlendFBOName, SmaaBlendTextureName);

    private XRFrameBuffer CreateSmaaFBO()
        => CreateDeclaredColorFBO(SmaaFBOName, SmaaOutputTextureName);

    private XRFrameBuffer CreateDeclaredColorFBO(string frameBufferName, string textureName)
    {
        IFrameBufferAttachement attachment = GetTexture<XRTexture>(textureName) as IFrameBufferAttachement
            ?? throw new InvalidOperationException($"Missing declared attachable texture '{textureName}'.");
        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = frameBufferName
        };
    }
}
