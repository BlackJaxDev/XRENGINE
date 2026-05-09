using System;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2
{
    public const string PpllHeadPointerTextureName = "PpllHeadPointerTex";
    public const string PpllFragmentCountTextureName = "PpllFragmentCountTex";
    public const string PpllResolveFBOName = "PpllResolveFBO";
    public const string PpllFragmentCountDebugFBOName = "PpllFragmentCountDebugFBO";
    public const string DepthPeelingResolveFBOName = "DepthPeelingResolveFBO";
    public const string DepthPeelingDebugFBOName = "DepthPeelingDebugFBO";

    private const string PpllNodeBufferName = "PpllNodeBuffer";
    private const string PpllCounterBufferName = "PpllCounterBuffer";
    private const string ActiveDepthPeelLayerVariableName = "ActiveDepthPeelLayer";
    private const int PpllNodeStrideBytes = 32;
    private const int PpllResolveFragmentLimit = 16;
    private const int MaxDepthPeelingLayersSupported = 4;
    private const float DepthPeelingEpsilon = 1e-5f;

    internal XRDataBuffer? PpllNodeBuffer => Engine.Rendering.State.CurrentRenderingPipeline?.GetBuffer(PpllNodeBufferName);
    internal XRDataBuffer? PpllCounterBuffer => Engine.Rendering.State.CurrentRenderingPipeline?.GetBuffer(PpllCounterBufferName);
    internal XRTexture? PpllHeadPointerTexture => GetTexture<XRTexture>(PpllHeadPointerTextureName);
    internal XRTexture? PreviousDepthPeelDepthTexture => ActiveDepthPeelLayerIndex > 0
        ? GetTexture<XRTexture>(DepthPeelDepthTextureName(ActiveDepthPeelLayerIndex - 1))
        : GetTexture<XRTexture>(DepthPeelDepthTextureName(0));
    internal int ActiveDepthPeelLayerIndex => ResolveActiveDepthPeelLayerIndex();
    internal float ActiveDepthPeelingEpsilon => DepthPeelingEpsilon;
    internal uint PpllMaxNodeCount => ComputePpllNodeCapacity();

    private bool ExactTransparencyEnabled
        => !Stereo && Engine.EditorPreferences.Debug.EnableExactTransparencyTechniques;

    private int ActiveDepthPeelLayerCount
        => Math.Clamp(Engine.EditorPreferences.Debug.DepthPeelingMaxLayers, 1, MaxDepthPeelingLayersSupported);

    private static string DepthPeelColorTextureName(int layerIndex)
        => $"DepthPeelColorTex_{layerIndex}";

    private static string DepthPeelDepthTextureName(int layerIndex)
        => $"DepthPeelDepthTex_{layerIndex}";

    private static string DepthPeelLayerFboName(int layerIndex)
        => $"DepthPeelLayerFBO_{layerIndex}";

    private string PpllResolveShaderName() => "PerPixelLinkedListResolve.fs";
    private string PpllFragmentCountDebugShaderName() => "PerPixelLinkedListFragmentCountDebug.fs";
    private string DepthPeelingResolveShaderName() => "DepthPeelingResolve.fs";
    private string DepthPeelingDebugShaderName() => "DepthPeelingDebug.fs";

    private uint ComputePpllNodeCapacity()
    {
        uint pixelCount = Math.Max(InternalWidth * InternalHeight, 1u);
        return Math.Max(pixelCount * 2u, 1024u);
    }

    private static int ResolveActiveDepthPeelLayerIndex()
    {
        XRRenderPipelineInstance? pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
        return pipeline is not null && pipeline.Variables.TryGet(ActiveDepthPeelLayerVariableName, out int value)
            ? value
            : -1;
    }

    private void CacheExactTransparencyTextures(ViewportRenderCommandContainer c)
    {
        if (Stereo)
            return;

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            PpllHeadPointerTextureName,
            CreatePpllHeadPointerTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            PpllFragmentCountTextureName,
            CreatePpllFragmentCountTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            int capture = layerIndex;
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                DepthPeelColorTextureName(capture),
                () => CreateDepthPeelColorTexture(capture),
                NeedsRecreateTextureInternalSize,
                ResizeTextureInternalSize);

            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                DepthPeelDepthTextureName(capture),
                () => CreateDepthPeelDepthTexture(capture),
                NeedsRecreateTextureInternalSize,
                ResizeTextureInternalSize);
        }
    }

    private void AppendExactTransparencyCommands(ViewportRenderCommandContainer c)
    {
        if (!ExactTransparencyEnabled)
            return;

        c.Add<VPRC_CacheOrCreateBuffer>().SetOptions(
            PpllNodeBufferName,
            CreatePpllNodeBuffer,
            NeedsPpllNodeBufferResize);

        c.Add<VPRC_CacheOrCreateBuffer>().SetOptions(
            PpllCounterBufferName,
            CreatePpllCounterBuffer,
            NeedsPpllCounterBufferResize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            PpllResolveFBOName,
            CreatePpllResolveFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthPeelingResolveFBOName,
            CreateDepthPeelingResolveFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        AppendConditionalDebugFboCache(
            c,
            DebugVizPpllFragmentsVariableName,
            PpllFragmentCountDebugFBOName,
            CreatePpllFragmentCountDebugFBO);

        AppendConditionalDebugFboCache(
            c,
            DebugVizDepthPeelingLayerVariableName,
            DepthPeelingDebugFBOName,
            CreateDepthPeelingDebugFBO);

        for (int layerIndex = 0; layerIndex < ActiveDepthPeelLayerCount; layerIndex++)
        {
            int capture = layerIndex;
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DepthPeelLayerFboName(capture),
                () => CreateDepthPeelLayerFBO(capture),
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);
        }

        c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
        var resetPpll = c.Add<VPRC_ResetPpllResources>();
        resetPpll.CounterBufferName = PpllCounterBufferName;
        resetPpll.HeadPointerTextureName = PpllHeadPointerTextureName;
        resetPpll.ClearHeadPointersComputeShaderPath = "Scene3D/ClearPpllHeadPointers.comp";
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = PpllNodeBufferName;
            x.BindingLocation = 24u;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = PpllCounterBufferName;
            x.BindingLocation = 25u;
        }))
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyPpllForwardProgramBindings))
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, false, false, false)))
        {
            c.Add<VPRC_ColorMask>().Set(false, false, false, false);
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, MeshSubmissionStrategy);
            c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        }
        using (c.AddUsing<VPRC_BindTexture>(x =>
        {
            x.TextureName = TransparentSceneCopyTextureName;
            x.TextureUnit = 0;
        }))
        using (c.AddUsing<VPRC_BindTexture>(x =>
        {
            x.TextureName = PpllHeadPointerTextureName;
            x.TextureUnit = 1;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = PpllNodeBufferName;
            x.BindingLocation = 24u;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = PpllCounterBufferName;
            x.BindingLocation = 25u;
        }))
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyPpllResolveProgramBindings))
        {
            c.Add<VPRC_RenderQuadFBO>().SetOptions(PpllResolveFBOName, renderToSourceFrameBuffer: true);
        }

        c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
        for (int layerIndex = 0; layerIndex < ActiveDepthPeelLayerCount; layerIndex++)
        {
            int capture = layerIndex;
            var setLayer = c.Add<VPRC_SetVariable>();
            setLayer.VariableName = ActiveDepthPeelLayerVariableName;
            setLayer.IntValue = capture;
            using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyDepthPeelingForwardProgramBindings))
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(DepthPeelLayerFboName(capture), true, true, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, MeshSubmissionStrategy);
            }
        }
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyDepthPeelingResolveProgramBindings))
        {
            c.Add<VPRC_RenderQuadFBO>().SetOptions(DepthPeelingResolveFBOName, renderToSourceFrameBuffer: true);
        }
        var clearLayer = c.Add<VPRC_SetVariable>();
        clearLayer.VariableName = ActiveDepthPeelLayerVariableName;
        clearLayer.IntValue = -1;
    }

    private XRDataBuffer CreatePpllNodeBuffer()
        => new(PpllNodeBufferName, EBufferTarget.ShaderStorageBuffer, ComputePpllNodeCapacity(), EComponentType.Struct, PpllNodeStrideBytes, false, false)
        {
            Usage = EBufferUsage.DynamicCopy,
            BindingIndexOverride = 24u,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };

    private static XRDataBuffer CreatePpllCounterBuffer()
        => new(PpllCounterBufferName, EBufferTarget.ShaderStorageBuffer, 2u, EComponentType.UInt, 1u, false, true)
        {
            Usage = EBufferUsage.DynamicCopy,
            BindingIndexOverride = 25u,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };

    private bool NeedsPpllNodeBufferResize(XRDataBuffer buffer)
        => buffer.ElementCount < ComputePpllNodeCapacity();

    private static bool NeedsPpllCounterBufferResize(XRDataBuffer buffer)
        => buffer.ElementCount < 2u;

    private void ApplyPpllForwardProgramBindings(XRRenderProgram program)
    {
        XRTexture? headPointers = PpllHeadPointerTexture;
        if (headPointers is null)
            return;

        program.BindImageTexture(0u, headPointers, 0, false, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.R32UI);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("PpllMaxNodes", (int)PpllMaxNodeCount);
    }

    private void ApplyPpllResolveProgramBindings(XRRenderProgram program)
    {
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("PpllResolveFragmentLimit", PpllResolveFragmentLimit);
    }

    private void ApplyDepthPeelingForwardProgramBindings(XRRenderProgram program)
    {
        XRTexture? previousDepth = PreviousDepthPeelDepthTexture;
        if (previousDepth is not null)
            program.Sampler("PrevPeelDepth", previousDepth, 8);

        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("DepthPeelLayerIndex", ActiveDepthPeelLayerIndex);
        program.Uniform("DepthPeelEpsilon", ActiveDepthPeelingEpsilon);
    }

    private void ApplyDepthPeelingResolveProgramBindings(XRRenderProgram program)
    {
        XRTexture? sceneColor = GetTexture<XRTexture>(TransparentSceneCopyTextureName);
        if (sceneColor is not null)
            program.Sampler(TransparentSceneCopyTextureName, sceneColor, 0);

        program.Uniform("ActiveDepthPeelLayers", ActiveDepthPeelLayerCount);
        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            XRTexture? colorLayer = GetTexture<XRTexture>(DepthPeelColorTextureName(layerIndex));
            if (colorLayer is not null)
                program.Sampler($"DepthPeelColor{layerIndex}", colorLayer, layerIndex + 1);
        }
    }

    private void ApplyDepthPeelingDebugProgramBindings(XRRenderProgram program)
    {
        int layerIndex = Math.Clamp(Engine.EditorPreferences.Debug.DepthPeelingPreviewLayer, 0, MaxDepthPeelingLayersSupported - 1);
        for (int i = 0; i < MaxDepthPeelingLayersSupported; i++)
        {
            XRTexture? colorLayer = GetTexture<XRTexture>(DepthPeelColorTextureName(i));
            if (colorLayer is not null)
                program.Sampler($"DepthPeelColor{i}", colorLayer, i);
        }

        program.Uniform("PreviewLayer", layerIndex);
    }

    private XRTexture CreatePpllHeadPointerTexture()
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.R32ui,
            EPixelFormat.RedInteger,
            EPixelType.UnsignedInt);
        texture.Resizable = true;
        texture.SizedInternalFormat = ESizedInternalFormat.R32ui;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.RequiresStorageUsage = true;
        texture.SamplerName = PpllHeadPointerTextureName;
        texture.Name = PpllHeadPointerTextureName;
        return texture;
    }

    private XRTexture CreatePpllFragmentCountTexture()
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment1);
        texture.Resizable = true;
        texture.SizedInternalFormat = ESizedInternalFormat.R16f;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = PpllFragmentCountTextureName;
        texture.Name = PpllFragmentCountTextureName;
        return texture;
    }

    private XRTexture CreateDepthPeelColorTexture(int layerIndex)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = true;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = DepthPeelColorTextureName(layerIndex);
        texture.Name = DepthPeelColorTextureName(layerIndex);
        return texture;
    }

    private XRTexture CreateDepthPeelDepthTexture(int layerIndex)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.DepthComponent32,
            EPixelFormat.DepthComponent,
            EPixelType.Float,
            EFrameBufferAttachment.DepthAttachment);
        texture.Resizable = true;
        texture.SizedInternalFormat = ESizedInternalFormat.DepthComponent32f;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = DepthPeelDepthTextureName(layerIndex);
        texture.Name = DepthPeelDepthTextureName(layerIndex);
        return texture;
    }

    private XRFrameBuffer CreatePpllResolveFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(TransparentSceneCopyTextureName)!,
            GetTexture<XRTexture>(PpllHeadPointerTextureName)!,
        ];

        XRMaterial material = new(
            references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, PpllResolveShaderName()), EShaderType.Fragment))
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

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = PpllResolveFBOName };

        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        var fragmentCountAttachment = EnsureTextureAttachment(PpllFragmentCountTextureName, CreatePpllFragmentCountTexture);
        fbo.SetRenderTargets(
            (hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (fragmentCountAttachment, EFrameBufferAttachment.ColorAttachment1, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreatePpllFragmentCountDebugFBO()
        => CreateTransparencyDebugFBO(
            PpllFragmentCountDebugFBOName,
            PpllFragmentCountDebugShaderName(),
            GetTexture<XRTexture>(PpllFragmentCountTextureName)!);

    private XRFrameBuffer CreateDepthPeelLayerFBO(int layerIndex)
    {
        var colorAttachment = EnsureTextureAttachment(DepthPeelColorTextureName(layerIndex), () => CreateDepthPeelColorTexture(layerIndex));
        var depthAttachment = EnsureTextureAttachment(DepthPeelDepthTextureName(layerIndex), () => CreateDepthPeelDepthTexture(layerIndex));
        return new XRFrameBuffer(
            (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthAttachment, 0, -1))
        {
            Name = DepthPeelLayerFboName(layerIndex)
        };
    }

    private XRFrameBuffer CreateDepthPeelingResolveFBO()
    {
        XRMaterial material = new(Array.Empty<XRTexture?>(), XRShader.EngineShader(Path.Combine(SceneShaderPath, DepthPeelingResolveShaderName()), EShaderType.Fragment))
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

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = DepthPeelingResolveFBOName };
        var hdrAttachment = EnsureTextureAttachment(HDRSceneTextureName, CreateHDRSceneTexture);
        fbo.SetRenderTargets((hdrAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        return fbo;
    }

    private XRFrameBuffer CreateDepthPeelingDebugFBO()
    {
        XRMaterial material = new(Array.Empty<XRTexture?>(), XRShader.EngineShader(Path.Combine(SceneShaderPath, DepthPeelingDebugShaderName()), EShaderType.Fragment))
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
        return new XRQuadFrameBuffer(material) { Name = DepthPeelingDebugFBOName };
    }
}
