using System;
using System.Collections.Generic;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
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
    private const int PpllNodeStrideBytes = PpllCapacityContract.NodeStrideBytes;
    private const int PpllResolveFragmentLimit = PpllCapacityContract.ResolveFragmentLimit;
    private const int MaxDepthPeelingLayersSupported = 4;
    private const float DepthPeelingEpsilon = 1e-5f;

    internal XRDataBuffer? PpllNodeBuffer => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetBuffer(PpllNodeBufferName);
    internal XRDataBuffer? PpllCounterBuffer => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetBuffer(PpllCounterBufferName);
    internal XRTexture? PpllHeadPointerTexture => GetTexture<XRTexture>(PpllHeadPointerTextureName);
    internal XRTexture? PreviousDepthPeelDepthTexture => ActiveDepthPeelLayerIndex > 0
        ? GetTexture<XRTexture>(DepthPeelDepthTextureName(ActiveDepthPeelLayerIndex - 1))
        : GetTexture<XRTexture>(DepthPeelDepthTextureName(0));
    internal int ActiveDepthPeelLayerIndex => ResolveActiveDepthPeelLayerIndex();
    internal float ActiveDepthPeelingEpsilon => DepthPeelingEpsilon;
    internal uint PpllMaxNodeCount => PpllCapacityContract.ResolveActualNodeCapacity(
        PpllNodeBuffer,
        ComputePpllNodeCapacity());

    private bool ExactTransparencyEnabled
        => AllowsLateTransparency &&
           !Stereo &&
           !UseOpenXrVulkanDesktopStartupSafePath &&
           RuntimeEngine.EditorPreferences.Debug.EnableExactTransparencyTechniques;

    private int ActiveDepthPeelLayerCount
        => Math.Clamp(RuntimeEngine.EditorPreferences.Debug.DepthPeelingMaxLayers, 1, MaxDepthPeelingLayersSupported);

    private static string DepthPeelColorTextureName(int layerIndex)
        => $"DepthPeelColorTex_{layerIndex}";

    private static string DepthPeelDepthTextureName(int layerIndex)
        => $"DepthPeelDepthTex_{layerIndex}";

    private static string DepthPeelLayerFboName(int layerIndex)
        => $"DepthPeelLayerFBO_{layerIndex}";

    private string PpllResolveShaderName() => "AdvancedPerPixelLinkedListResolve.fs";
    private string PpllFragmentCountDebugShaderName() => "PerPixelLinkedListFragmentCountDebug.fs";
    private string DepthPeelingResolveShaderName() => "AdvancedDepthPeelingResolve.fs";
    private string DepthPeelingDebugShaderName() => "DepthPeelingDebug.fs";

    private uint ComputePpllNodeCapacity()
        => PpllCapacityContract.ComputeNodeCapacity(InternalWidth, InternalHeight);

    private static uint ComputePpllNodeCapacity(RenderPipelineResourceProfile profile)
        => PpllCapacityContract.ComputeNodeCapacity(
            profile.InternalWidth,
            profile.InternalHeight);

    private static int ResolveActiveDepthPeelLayerIndex()
    {
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        return pipeline is not null && pipeline.Variables.TryGet(ActiveDepthPeelLayerVariableName, out int value)
            ? value
            : -1;
    }

    private void AppendExactTransparencyCommands(ViewportRenderCommandContainer c)
    {
        // A reusable chain is built before the current visibility collection.
        // Decide admission when executing it so later-visible consumers run.
        var ppll = c.Add<VPRC_IfElse>();
        ppll.Label = "AdvancedPpllActive";
        ppll.ConditionEvaluator = () => ShouldRunAdvancedLatePass(
            (int)EDefaultRenderPass.PerPixelLinkedListForward);
        ppll.TrueCommands = new ViewportRenderCommandContainer(this);
        AppendPpllCommands(ppll.TrueCommands);

        var peeling = c.Add<VPRC_IfElse>();
        peeling.Label = "AdvancedDepthPeelingActive";
        peeling.ConditionEvaluator = () => ShouldRunAdvancedLatePass(
            (int)EDefaultRenderPass.DepthPeelingForward);
        peeling.TrueCommands = new ViewportRenderCommandContainer(this);
        AppendDepthPeelingCommands(peeling.TrueCommands);
    }

    private void AppendPpllCommands(ViewportRenderCommandContainer c)
    {
        var resetPpll = c.Add<VPRC_ResetPpllResources>();
        resetPpll.CounterBufferName = PpllCounterBufferName;
        resetPpll.HeadPointerTextureName = PpllHeadPointerTextureName;
        resetPpll.ClearHeadPointersComputeShaderPath = "Scene3D/ClearAdvancedPpllResources.comp";
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
            VPRC_RenderMeshesPass producer = c.Add<VPRC_RenderMeshesPass>();
            producer.SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, EMeshSubmissionStrategy.CpuDirect);
            producer.EnforceAdvancedLatePassEligibility = true;
            producer.SetReadWriteBuffers(PpllNodeBufferName, PpllCounterBufferName);
            producer.SetReadWriteTextures(PpllHeadPointerTextureName);
            c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        }
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
            c.Add<VPRC_RenderQuadToFBO>()
                .SetOptions(PpllResolveFBOName, renderToSourceFrameBuffer: true)
                .SetRenderGraphResources(CreatePpllResolveResources());
        }
    }

    private void AppendDepthPeelingCommands(ViewportRenderCommandContainer c)
    {
        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            int capture = layerIndex;
            var activeLayer = c.Add<VPRC_IfElse>();
            activeLayer.Label = $"AdvancedDepthPeelLayer{capture}";
            activeLayer.ConditionEvaluator = () => capture < ActiveDepthPeelLayerCount;
            var layerCommands = new ViewportRenderCommandContainer(this);
            activeLayer.TrueCommands = layerCommands;
            var setLayer = layerCommands.Add<VPRC_SetVariable>();
            setLayer.VariableName = ActiveDepthPeelLayerVariableName;
            setLayer.IntValue = capture;
            // A missing fragment must remain transparent. Clear color independently
            // of the depth clear so this remains true even if the renderer's global
            // framebuffer clear color changes.
            layerCommands.Add<VPRC_ClearTextureByName>()
                .SetOptions(DepthPeelColorTextureName(capture), ColorF4.Transparent);
            // Every layer begins from the native opaque depth. The fixed-function
            // depth test therefore rejects opaque-occluded transparent fragments
            // before the previous-layer shader test is evaluated.
            layerCommands.Add<VPRC_BlitFrameBuffer>().SetOptions(
                ForwardPassFBOName,
                DepthPeelLayerFboName(capture),
                EReadBufferMode.ColorAttachment0,
                blitColor: false,
                blitDepth: true,
                blitStencil: false,
                linearFilter: false);
            using (layerCommands.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyDepthPeelingForwardProgramBindings))
            using (layerCommands.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(
                DepthPeelLayerFboName(capture),
                true,
                clearColor: false,
                clearDepth: false,
                clearStencil: false)))
            {
                layerCommands.Add<VPRC_DepthTest>().Enable = true;
                layerCommands.Add<VPRC_DepthWrite>().Allow = true;
                VPRC_RenderMeshesPass producer = layerCommands.Add<VPRC_RenderMeshesPass>();
                producer.SetOptions((int)EDefaultRenderPass.DepthPeelingForward, EMeshSubmissionStrategy.CpuDirect);
                producer.EnforceAdvancedLatePassEligibility = true;
            }
        }
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyDepthPeelingResolveProgramBindings))
        using (c.AddUsing<VPRC_PushBlendState>(x =>
        {
            // The resolve emits premultiplied color for every captured layer.
            // Blend over the current HDR target, which may already include PPLL,
            // weighted OIT, or sorted transparency, instead of replaying an
            // obsolete opaque-scene snapshot.
            x.SrcRGB = EBlendingFactor.One;
            x.DstRGB = EBlendingFactor.OneMinusSrcAlpha;
            x.SrcAlpha = EBlendingFactor.One;
            x.DstAlpha = EBlendingFactor.OneMinusSrcAlpha;
        }))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetOptions(DepthPeelingResolveFBOName, renderToSourceFrameBuffer: true)
                .SetRenderGraphResources(CreateDepthPeelingResolveResources());
        }
        var clearLayer = c.Add<VPRC_SetVariable>();
        clearLayer.VariableName = ActiveDepthPeelLayerVariableName;
        clearLayer.IntValue = -1;
    }

    private bool HasAdvancedExactTransparencyConsumers()
        => ExactTransparencyEnabled &&
           (HasRenderPassCommands((int)EDefaultRenderPass.PerPixelLinkedListForward)
            || HasRenderPassCommands((int)EDefaultRenderPass.DepthPeelingForward));

    private XRDataBuffer CreatePpllNodeBuffer()
        => new(PpllNodeBufferName, EBufferTarget.ShaderStorageBuffer, ComputePpllNodeCapacity(), EComponentType.Struct, PpllNodeStrideBytes, false, false)
        {
            Usage = EBufferUsage.DynamicCopy,
            BindingIndexOverride = 24u,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };

    private static XRDataBuffer CreatePpllCounterBuffer()
        => new(PpllCounterBufferName, EBufferTarget.ShaderStorageBuffer, PpllCapacityContract.CounterWordCount, EComponentType.UInt, 1u, false, true)
        {
            Usage = EBufferUsage.DynamicCopy,
            BindingIndexOverride = 25u,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };

    private bool NeedsPpllNodeBufferResize(XRDataBuffer buffer)
        => buffer.ElementCount < ComputePpllNodeCapacity();

    private static bool NeedsPpllCounterBufferResize(XRDataBuffer buffer)
        => buffer.ElementCount < PpllCapacityContract.CounterWordCount;

    private void ApplyPpllForwardProgramBindings(XRRenderProgram program)
    {
        XRTexture? headPointers = PpllHeadPointerTexture;
        if (headPointers is null)
            return;

        program.BindImageTexture(0u, headPointers, 0, false, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.R32UI);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("PpllMaxNodes", PpllMaxNodeCount);
    }

    private void ApplyPpllResolveProgramBindings(XRRenderProgram program)
    {
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("PpllResolveFragmentLimit", PpllResolveFragmentLimit);
        program.Uniform("PpllMaxNodes", PpllMaxNodeCount);
        program.Uniform(
            "PpllReversedDepth",
            RuntimeEngine.Rendering.State.RenderingCamera?.IsReversedDepth == true);
    }

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreatePpllResolveResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(PpllHeadPointerTextureName)
            .ReadBuffer(PpllNodeBufferName)
            .ReadWriteBuffer(PpllCounterBufferName);

    private void ApplyDepthPeelingForwardProgramBindings(XRRenderProgram program)
    {
        XRTexture? previousDepth = PreviousDepthPeelDepthTexture;
        if (previousDepth is not null)
            program.Sampler("PrevPeelDepth", previousDepth, 8);

        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("DepthPeelLayerIndex", ActiveDepthPeelLayerIndex);
        program.Uniform("DepthPeelEpsilon", ActiveDepthPeelingEpsilon);
        program.Uniform("DepthPeelReversedDepth", RuntimeEngine.Rendering.State.RenderingCamera?.IsReversedDepth == true);
    }

    private void ApplyDepthPeelingResolveProgramBindings(XRRenderProgram program)
    {
        program.Uniform("ActiveDepthPeelLayers", ActiveDepthPeelLayerCount);
        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            XRTexture? colorLayer = GetTexture<XRTexture>(DepthPeelColorTextureName(layerIndex));
            if (colorLayer is not null)
                program.Sampler($"DepthPeelColor{layerIndex}", colorLayer, layerIndex + 1);
        }
    }

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateDepthPeelingResolveResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(DepthPeelColorTextureName(0))
            .SampleTexture(DepthPeelColorTextureName(1))
            .SampleTexture(DepthPeelColorTextureName(2))
            .SampleTexture(DepthPeelColorTextureName(3));

    private void ApplyDepthPeelingDebugProgramBindings(XRRenderProgram program)
    {
        int layerIndex = Math.Clamp(RuntimeEngine.EditorPreferences.Debug.DepthPeelingPreviewLayer, 0, MaxDepthPeelingLayersSupported - 1);
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
            GetTexture<XRTexture>(PpllHeadPointerTextureName)!,
        ];

        XRMaterial material = new(
            references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, PpllResolveShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                BlendModeAllDrawBuffers = null,
                BlendModesPerDrawBuffer = new Dictionary<uint, BlendMode>
                {
                    [0u] = new BlendMode()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        RgbSrcFactor = EBlendingFactor.One,
                        RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                        AlphaSrcFactor = EBlendingFactor.One,
                        AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                    },
                    [1u] = BlendMode.Disabled(),
                },
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo) { Name = PpllResolveFBOName };

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
            ForceOvrMultiview = Stereo,
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

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo) { Name = DepthPeelingResolveFBOName };
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
        return new XRQuadFrameBuffer(material, useMultiview: Stereo) { Name = DepthPeelingDebugFBOName };
    }
}
