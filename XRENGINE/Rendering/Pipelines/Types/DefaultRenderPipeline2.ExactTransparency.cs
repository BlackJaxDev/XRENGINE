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
    private const int PpllNodeStrideBytes = 32;
    private const int PpllResolveFragmentLimit = 16;
    private const int MaxDepthPeelingLayersSupported = 4;
    private const float DepthPeelingEpsilon = 1e-5f;

    private XRDataBuffer? _ppllNodeBuffer;
    private XRDataBuffer? _ppllCounterBuffer;
    private int _activeDepthPeelLayerIndex = -1;

    internal XRDataBuffer? PpllNodeBuffer => _ppllNodeBuffer;
    internal XRDataBuffer? PpllCounterBuffer => _ppllCounterBuffer;
    internal XRTexture? PpllHeadPointerTexture => GetTexture<XRTexture>(PpllHeadPointerTextureName);
    internal XRTexture? PreviousDepthPeelDepthTexture => _activeDepthPeelLayerIndex > 0
        ? GetTexture<XRTexture>(DepthPeelDepthTextureName(_activeDepthPeelLayerIndex - 1))
        : GetTexture<XRTexture>(DepthPeelDepthTextureName(0));
    internal int ActiveDepthPeelLayerIndex => _activeDepthPeelLayerIndex;
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

        EnsureExactTransparencyBuffers();

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

        if (EnablePerPixelLinkedListVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                PpllFragmentCountDebugFBOName,
                CreatePpllFragmentCountDebugFBO,
                GetDesiredFBOSizeInternal);
        }

        if (EnableDepthPeelingLayerVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DepthPeelingDebugFBOName,
                CreateDepthPeelingDebugFBO,
                GetDesiredFBOSizeInternal);
        }

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
        c.Add<VPRC_Manual>().ManualAction = ResetPpllResources;
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, false, false, false)))
        {
            c.Add<VPRC_ColorMask>().Set(false, false, false, false);
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, GPURenderDispatch);
            c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        }
        c.Add<VPRC_RenderQuadFBO>().SetOptions(PpllResolveFBOName, renderToSourceFrameBuffer: true);

        c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
        for (int layerIndex = 0; layerIndex < ActiveDepthPeelLayerCount; layerIndex++)
        {
            int capture = layerIndex;
            c.Add<VPRC_Manual>().ManualAction = () => _activeDepthPeelLayerIndex = capture;
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(DepthPeelLayerFboName(capture), true, true, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, GPURenderDispatch);
            }
        }
        c.Add<VPRC_RenderQuadFBO>().SetOptions(DepthPeelingResolveFBOName, renderToSourceFrameBuffer: true);
        c.Add<VPRC_Manual>().ManualAction = () => _activeDepthPeelLayerIndex = -1;
    }

    private void EnsureExactTransparencyBuffers()
    {
        uint nodeCapacity = ComputePpllNodeCapacity();
        if (_ppllNodeBuffer is null)
        {
            _ppllNodeBuffer = new XRDataBuffer(PpllNodeBufferName, EBufferTarget.ShaderStorageBuffer, nodeCapacity, EComponentType.Struct, PpllNodeStrideBytes, false, false)
            {
                Usage = EBufferUsage.DynamicCopy,
                BindingIndexOverride = 24u,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
            };
        }
        else if (_ppllNodeBuffer.ElementCount < nodeCapacity)
        {
            _ppllNodeBuffer.Resize(nodeCapacity, false, true);
        }

        if (_ppllCounterBuffer is null)
        {
            _ppllCounterBuffer = new XRDataBuffer(PpllCounterBufferName, EBufferTarget.ShaderStorageBuffer, 2u, EComponentType.UInt, 1u, false, true)
            {
                Usage = EBufferUsage.DynamicCopy,
                BindingIndexOverride = 25u,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
            };
        }
        else if (_ppllCounterBuffer.ElementCount < 2u)
        {
            _ppllCounterBuffer.Resize(2u, false, true);
        }
    }

    private void ResetPpllResources()
    {
        EnsureExactTransparencyBuffers();
        if (_ppllCounterBuffer is null)
            return;

        _ppllCounterBuffer.SetDataRawAtIndex(0, 0u);
        _ppllCounterBuffer.SetDataRawAtIndex(1, 0u);
        _ppllCounterBuffer.PushSubData();

        XRTexture? headTexture = GetTexture<XRTexture>(PpllHeadPointerTextureName);
        if (headTexture is null)
            return;

        XRRenderProgram program = new(false, false, XRShader.EngineShader("Scene3D/ClearPpllHeadPointers.comp", EShaderType.Compute));
        program.BindImageTexture(0u, headTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32UI);
        uint groupsX = (InternalWidth + 15u) / 16u;
        uint groupsY = (InternalHeight + 15u) / 16u;
        program.DispatchCompute(Math.Max(groupsX, 1u), Math.Max(groupsY, 1u), 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.ShaderStorage);
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
        material.SettingUniforms += PpllResolveMaterial_SettingUniforms;

        var fbo = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = PpllResolveFBOName };
        fbo.SettingUniforms += PpllResolveFBO_SettingUniforms;

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
            PpllFragmentCountDebugFBO_SettingUniforms,
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
        material.SettingUniforms += DepthPeelingResolveMaterial_SettingUniforms;

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
        material.SettingUniforms += DepthPeelingDebugMaterial_SettingUniforms;
        return new XRQuadFrameBuffer(material) { Name = DepthPeelingDebugFBOName };
    }

    private void PpllResolveFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? sceneColor = GetTexture<XRTexture>(TransparentSceneCopyTextureName);
        XRTexture? headPointers = GetTexture<XRTexture>(PpllHeadPointerTextureName);
        if (sceneColor is null || headPointers is null)
            return;

        program.Sampler(TransparentSceneCopyTextureName, sceneColor, 0);
        program.Sampler(PpllHeadPointerTextureName, headPointers, 1);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
        program.Uniform("PpllResolveFragmentLimit", PpllResolveFragmentLimit);
    }

    private void PpllResolveMaterial_SettingUniforms(XRMaterialBase _, XRRenderProgram program)
    {
        _ppllNodeBuffer?.BindTo(program, 24u);
        _ppllCounterBuffer?.BindTo(program, 25u);
    }

    private void PpllFragmentCountDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? fragmentCount = GetTexture<XRTexture>(PpllFragmentCountTextureName);
        if (fragmentCount is null)
            return;

        program.Sampler(PpllFragmentCountTextureName, fragmentCount, 0);
    }

    private void DepthPeelingResolveMaterial_SettingUniforms(XRMaterialBase _, XRRenderProgram program)
    {
        XRTexture? sceneColor = GetTexture<XRTexture>(TransparentSceneCopyTextureName);
        if (sceneColor is null)
            return;

        program.Sampler(TransparentSceneCopyTextureName, sceneColor, 0);
        program.Uniform("ActiveDepthPeelLayers", ActiveDepthPeelLayerCount);
        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            XRTexture? colorLayer = GetTexture<XRTexture>(DepthPeelColorTextureName(layerIndex));
            if (colorLayer is not null)
                program.Sampler($"DepthPeelColor{layerIndex}", colorLayer, layerIndex + 1);
        }
    }

    private void DepthPeelingDebugMaterial_SettingUniforms(XRMaterialBase _, XRRenderProgram program)
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
}
