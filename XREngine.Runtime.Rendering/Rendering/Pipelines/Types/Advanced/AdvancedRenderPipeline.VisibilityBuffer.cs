using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private const uint VisibilityDrawCapacity = 65_536u;
    private const uint VisibilityViewCapacity = RenderFrameViewSet.MaxViewCount + 1u;
    private const uint DrawIndexedIndirectStride = 20u;
    private const uint DrawMeshTasksIndirectStride = 12u;
    // Sixteen diagnostic counter words plus ten packed lookup segments shared
    // by the set-1 preparation/raster ABI.
    private const uint VisibilityCounterStride = 144u;
    private const uint VisibilityRangeCapacity = 64u;

    private EAdvancedVisibilityDebugView _visibilityDebugView;
    private bool _enableVisibilityGpuValidation;

    /// <summary>
    /// Selects a decoded visibility-buffer capture view. Its resources are included in
    /// the immutable generation profile before frame execution.
    /// </summary>
    public EAdvancedVisibilityDebugView VisibilityDebugView
    {
        get => _visibilityDebugView;
        set
        {
            if (!SetField(ref _visibilityDebugView, value))
                return;
            InvalidateVisibilityResourceProfile();
        }
    }

    /// <summary>
    /// Enables shader-side table bounds checks and delayed decode diagnostics.
    /// </summary>
    public bool EnableVisibilityGpuValidation
    {
        get => _enableVisibilityGpuValidation;
        set
        {
            if (!SetField(ref _enableVisibilityGpuValidation, value))
                return;
            InvalidateVisibilityResourceProfile();
        }
    }

    private void InvalidateVisibilityResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("VisibilityResourceProfileChanged");

    private void DeclareVisibilityBufferResources(
        RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy depthTileSize =
            RenderResourceSizePolicy.InternalDividedRoundedUp(64u);
        uint layers = Math.Max(
            builder.Profile.ViewCount,
            builder.Profile.Stereo ? 2u : 1u);

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.Identity,
                internalSize,
                RenderPipelineResourceUsage.ColorAttachment |
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource,
                EPixelInternalFormat.RG32ui,
                EPixelFormat.RgInteger,
                EPixelType.UnsignedInt,
                ESizedInternalFormat.Rg32ui,
                EFrameBufferAttachment.ColorAttachment0,
                storage: true)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DebugLabel("Advanced visibility identity RG32_UINT v1")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.Metadata,
                internalSize,
                RenderPipelineResourceUsage.ColorAttachment |
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource,
                EPixelInternalFormat.R32ui,
                EPixelFormat.RedInteger,
                EPixelType.UnsignedInt,
                ESizedInternalFormat.R32ui,
                EFrameBufferAttachment.ColorAttachment1,
                storage: true)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(AdvancedVisibilityResourceNames.Identity)
            .DebugLabel("Advanced visibility metadata R32_UINT")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.Selection,
                internalSize,
                RenderPipelineResourceUsage.ColorAttachment |
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource,
                EPixelInternalFormat.R32ui,
                EPixelFormat.RedInteger,
                EPixelType.UnsignedInt,
                ESizedInternalFormat.R32ui,
                EFrameBufferAttachment.ColorAttachment2,
                storage: true)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced editor selection identity")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.DepthStencil,
                internalSize,
                RenderPipelineResourceUsage.DepthStencilAttachment |
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.TransferSource,
                EPixelInternalFormat.Depth32fStencil8,
                EPixelFormat.DepthStencil,
                EPixelType.Float32UnsignedInt248Rev,
                ESizedInternalFormat.Depth32fStencil8,
                EFrameBufferAttachment.DepthStencilAttachment,
                storage: false)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DebugLabel("Advanced visibility depth/stencil")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.CurrentDepthPyramid,
                depthTileSize,
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource,
                EPixelInternalFormat.R32f,
                EPixelFormat.Red,
                EPixelType.Float,
                ESizedInternalFormat.R32f,
                attachment: null,
                storage: true)
            .Layers(layers)
            .Mips(new RenderResourceMipPolicy(
                0u,
                6u,
                AutoGenerateMipmaps: false,
                RequireImmutableStorage: true))
            .StereoCompatible(layers > 1u)
            .DependsOn(AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced current per-view depth pyramid")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.PreviousDepthPyramid,
                depthTileSize,
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferDestination,
                EPixelInternalFormat.R32f,
                EPixelFormat.Red,
                EPixelType.Float,
                ESizedInternalFormat.R32f,
                attachment: null,
                storage: true)
            .Layers(layers)
            .Mips(new RenderResourceMipPolicy(
                0u,
                6u,
                AutoGenerateMipmaps: false,
                RequireImmutableStorage: true))
            .StereoCompatible(layers > 1u)
            .History(RenderResourceHistoryPolicy.PreserveWhenCompatible)
            .DependsOn(AdvancedVisibilityResourceNames.CurrentDepthPyramid)
            .DebugLabel("Advanced previous per-view depth-pyramid history")
            .Add();

        DeclareVisibilityPersistentBuffers(builder);
        DeclareVisibilityFrameSlotBuffers(builder);

        builder.FrameBuffer(AdvancedVisibilityResourceNames.FrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, AdvancedVisibilityResourceNames.Identity, layerIndex: -1)
            .Color(1, AdvancedVisibilityResourceNames.Metadata, layerIndex: -1)
            .Color(2, AdvancedVisibilityResourceNames.Selection, layerIndex: -1)
            .DepthStencil(
                AdvancedVisibilityResourceNames.DepthStencil,
                layerIndex: -1)
            .Factory(CreateVisibilityFrameBuffer)
            .DebugLabel("Advanced visibility early/late authoritative FBO")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedVisibilityResourceNames.DebugOutput,
                internalSize,
                RenderPipelineResourceUsage.ColorAttachment |
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.Rgba8,
                EFrameBufferAttachment.ColorAttachment0,
                storage: true)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile =>
                ((AdvancedVisibilityResourceFeature)profile.FeatureMask &
                 AdvancedVisibilityResourceFeature.DebugOutput) != 0)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata,
                AdvancedVisibilityResourceNames.Selection,
                AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced visibility decoded debug output")
            .Add();

        builder.FrameBuffer(AdvancedVisibilityResourceNames.DebugFrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, AdvancedVisibilityResourceNames.DebugOutput, layerIndex: -1)
            .When(static profile =>
                ((AdvancedVisibilityResourceFeature)profile.FeatureMask &
                 AdvancedVisibilityResourceFeature.DebugOutput) != 0)
            .Factory(CreateVisibilityDebugFrameBuffer)
            .DebugLabel("Advanced visibility debug FBO")
            .Add();
    }

    private static void DeclareVisibilityPersistentBuffers(
        RenderPipelineResourceLayoutBuilder builder)
    {
        VisibilityBuffer<AdvancedVisibilityCandidate>(
                builder,
                AdvancedVisibilityResourceNames.Candidates,
                VisibilityDrawCapacity,
                EBufferTarget.ShaderStorageBuffer,
                EBufferUsage.DynamicDraw)
            .Access(EBufferAccessPattern.ReadOnly)
            .DebugLabel("Advanced visibility candidates")
            .Add();
        VisibilityBuffer<AdvancedVisibilityPayload>(
                builder,
                AdvancedVisibilityResourceNames.Payloads,
                VisibilityDrawCapacity,
                EBufferTarget.ShaderStorageBuffer,
                EBufferUsage.DynamicDraw)
            .Access(EBufferAccessPattern.ReadOnly)
            .DebugLabel("Advanced producer-neutral visibility payloads")
            .Add();
        VisibilityBuffer<EAdvancedGeometryProducer>(
                builder,
                AdvancedVisibilityResourceNames.Producers,
                VisibilityDrawCapacity,
                EBufferTarget.ShaderStorageBuffer,
                EBufferUsage.DynamicDraw)
            .Access(EBufferAccessPattern.ReadOnly)
            .DebugLabel("Advanced visibility producer classes")
            .Add();
        VisibilityBuffer<AdvancedVisibilityPersistentRecord>(
                builder,
                AdvancedVisibilityResourceNames.PersistentState,
                checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                EBufferTarget.ShaderStorageBuffer,
                EBufferUsage.DynamicDraw)
            .DebugLabel("Advanced persistent per-view visibility state")
            .Add();
        VisibilityBuffer(
                builder,
                AdvancedVisibilityResourceNames.SourceArguments,
                DrawIndexedIndirectStride,
                VisibilityDrawCapacity,
                EBufferTarget.DrawIndirectBuffer,
                EBufferUsage.DynamicDraw)
            .Usage(
                RenderPipelineResourceUsage.StorageBuffer |
                RenderPipelineResourceUsage.IndirectBuffer)
            .Access(EBufferAccessPattern.ReadOnly)
            .DebugLabel("Advanced source indexed-draw arguments")
            .Add();
        VisibilityBuffer<uint>(
                builder,
                AdvancedVisibilityResourceNames.PayloadRangeIndices,
                VisibilityDrawCapacity,
                EBufferTarget.ShaderStorageBuffer,
                EBufferUsage.DynamicDraw)
            .Access(EBufferAccessPattern.ReadOnly)
            .DebugLabel("Advanced payload-to-compatible-range indices")
            .Add();
        VisibilityBuffer<uint>(
                builder,
                AdvancedVisibilityResourceNames.RangeArgumentOffsets,
                checked(VisibilityRangeCapacity * VisibilityViewCapacity),
                EBufferTarget.ShaderStorageBuffer,
                EBufferUsage.DynamicDraw)
            .Access(EBufferAccessPattern.ReadOnly)
            .DebugLabel("Advanced per-view compatible-range argument offsets")
            .Add();
    }

    private static void DeclareVisibilityFrameSlotBuffers(
        RenderPipelineResourceLayoutBuilder builder)
    {
        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
        {
            VisibilityBuffer(
                    builder,
                    AdvancedVisibilityResourceNames.EarlyArguments(slot),
                    DrawIndexedIndirectStride,
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.DrawIndirectBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .Usage(
                    RenderPipelineResourceUsage.StorageBuffer |
                    RenderPipelineResourceUsage.IndirectBuffer)
                .DebugLabel($"Advanced early indexed arguments slot {slot}")
                .Add();
            VisibilityBuffer(
                    builder,
                    AdvancedVisibilityResourceNames.LateArguments(slot),
                    DrawIndexedIndirectStride,
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.DrawIndirectBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .Usage(
                    RenderPipelineResourceUsage.StorageBuffer |
                    RenderPipelineResourceUsage.IndirectBuffer)
                .DebugLabel($"Advanced late indexed arguments slot {slot}")
                .Add();
            VisibilityBuffer(
                    builder,
                    AdvancedVisibilityResourceNames.EarlyMeshTaskArguments(slot),
                    DrawMeshTasksIndirectStride,
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.DrawIndirectBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .Usage(
                    RenderPipelineResourceUsage.StorageBuffer |
                    RenderPipelineResourceUsage.IndirectBuffer)
                .DebugLabel($"Advanced early mesh-task arguments slot {slot}")
                .Add();
            VisibilityBuffer(
                    builder,
                    AdvancedVisibilityResourceNames.LateMeshTaskArguments(slot),
                    DrawMeshTasksIndirectStride,
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.DrawIndirectBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .Usage(
                    RenderPipelineResourceUsage.StorageBuffer |
                    RenderPipelineResourceUsage.IndirectBuffer)
                .DebugLabel($"Advanced late mesh-task arguments slot {slot}")
                .Add();
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedVisibilityResourceNames.EarlyMeshPayloads(slot),
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced early mesh-command payload indices slot {slot}")
                .Add();
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedVisibilityResourceNames.LateMeshPayloads(slot),
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced late mesh-command payload indices slot {slot}")
                .Add();
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedVisibilityResourceNames.RangeCounts(slot),
                    checked(VisibilityRangeCapacity * VisibilityViewCapacity),
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced per-range GPU counts slot {slot}")
                .Add();
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedVisibilityResourceNames.DeferredCandidates(slot),
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced deferred candidates slot {slot}")
                .Add();
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedVisibilityResourceNames.EarlyVisiblePayloads(slot),
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced early payload indices slot {slot}")
                .Add();
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedVisibilityResourceNames.LateVisiblePayloads(slot),
                    checked(VisibilityDrawCapacity * VisibilityViewCapacity),
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicDraw)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced late payload indices slot {slot}")
                .Add();
            VisibilityBuffer(
                    builder,
                    AdvancedVisibilityResourceNames.Counters(slot),
                    VisibilityCounterStride,
                    VisibilityViewCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced delayed visibility counters slot {slot}")
                .Add();
        }
    }

    private RenderPipelineResourceLayoutBuilder.TextureSpecBuilder
        VisibilityTexture(
            RenderPipelineResourceLayoutBuilder builder,
            string name,
            RenderResourceSizePolicy size,
            RenderPipelineResourceUsage usage,
            EPixelInternalFormat internalFormat,
            EPixelFormat pixelFormat,
            EPixelType pixelType,
            ESizedInternalFormat sizedInternalFormat,
            EFrameBufferAttachment? attachment,
            bool storage)
        => builder.Texture(name)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(size)
            .Usage(usage)
            .Format(internalFormat, pixelFormat, pixelType)
            .SizedFormat(sizedInternalFormat)
            .RequiresStorageUsage(storage)
            .Factory(() => CreateVisibilityTexture(
                name,
                internalFormat,
                pixelFormat,
                pixelType,
                sizedInternalFormat,
                attachment,
                storage));

    private static RenderPipelineResourceLayoutBuilder.BufferSpecBuilder
        VisibilityBuffer<T>(
            RenderPipelineResourceLayoutBuilder builder,
            string name,
            uint elementCount,
            EBufferTarget target,
            EBufferUsage usage)
        where T : unmanaged
        => VisibilityBuffer(
            builder,
            name,
            checked((uint)Unsafe.SizeOf<T>()),
            elementCount,
            target,
            usage);

    private static RenderPipelineResourceLayoutBuilder.BufferSpecBuilder
        VisibilityBuffer(
            RenderPipelineResourceLayoutBuilder builder,
            string name,
            uint stride,
            uint elementCount,
            EBufferTarget target,
            EBufferUsage usage)
        => builder.Buffer(name)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat(
                checked((ulong)stride * elementCount),
                target,
                usage)
            .Elements(stride, elementCount)
            .Factory(() => CreateVisibilityBuffer(
                name,
                stride,
                elementCount,
                target,
                usage));

    private XRTexture CreateVisibilityTexture(
        string name,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedInternalFormat,
        EFrameBufferAttachment? attachment,
        bool storage)
    {
        uint layers = Stereo ? 2u : 1u;
        bool isDepthTileGrid = name is
            AdvancedVisibilityResourceNames.CurrentDepthPyramid or
            AdvancedVisibilityResourceNames.PreviousDepthPyramid;
        uint width = isDepthTileGrid ? DivideRoundUp(InternalWidth, 64u) : InternalWidth;
        uint height = isDepthTileGrid ? DivideRoundUp(InternalHeight, 64u) : InternalHeight;
        XRTexture texture;
        if (layers > 1u)
        {
            if (isDepthTileGrid)
            {
                XRTexture2D[] layerTextures = new XRTexture2D[layers];
                for (int i = 0; i < layers; i++)
                {
                    layerTextures[i] = new XRTexture2D(width, height, internalFormat, pixelFormat, pixelType, 6)
                    {
                        MinFilter = ETexMinFilter.NearestMipmapNearest,
                        MagFilter = ETexMagFilter.Nearest,
                        UWrap = ETexWrapMode.ClampToEdge,
                        VWrap = ETexWrapMode.ClampToEdge,
                        AutoGenerateMipmaps = false,
                        SizedInternalFormat = sizedInternalFormat,
                    };
                }
                texture = new XRTexture2DArray(layerTextures)
                {
                    MinFilter = ETexMinFilter.NearestMipmapNearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    AutoGenerateMipmaps = false,
                    SizedInternalFormat = sizedInternalFormat,
                    OVRMultiViewParameters = new(0, layers),
                };
            }
            else
            {
                texture = attachment.HasValue
                    ? XRTexture2DArray.CreateFrameBufferTexture(
                        layers,
                        width,
                        height,
                        internalFormat,
                        pixelFormat,
                        pixelType,
                        attachment.Value)
                    : XRTexture2DArray.CreateFrameBufferTexture(
                        layers,
                        width,
                        height,
                        internalFormat,
                        pixelFormat,
                        pixelType);
                if (texture is XRTexture2DArray arr)
                    arr.OVRMultiViewParameters = new(0, layers);
            }
        }
        else
        {
            if (isDepthTileGrid)
            {
                texture = new XRTexture2D(width, height, internalFormat, pixelFormat, pixelType, 6)
                {
                    MinFilter = ETexMinFilter.NearestMipmapNearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    AutoGenerateMipmaps = false,
                    SizedInternalFormat = sizedInternalFormat,
                };
            }
            else
            {
                texture = attachment.HasValue
                    ? XRTexture2D.CreateFrameBufferTexture(
                        width,
                        height,
                        internalFormat,
                        pixelFormat,
                        pixelType,
                        attachment.Value)
                    : XRTexture2D.CreateFrameBufferTexture(
                        width,
                        height,
                        internalFormat,
                        pixelFormat,
                        pixelType);
            }
        }

        bool usesMipChain = isDepthTileGrid;

        ConfigureVisibilityTexture(
            texture,
            sizedInternalFormat,
            usesMipChain);
        texture.RequiresStorageUsage = storage;
        texture.Name = name;
        texture.SamplerName = name;
        return texture;
    }


    private static void ConfigureVisibilityTexture(
        XRTexture texture,
        ESizedInternalFormat sizedInternalFormat,
        bool usesMipChain)
    {
        ETexMinFilter minFilter = usesMipChain
            ? ETexMinFilter.NearestMipmapNearest
            : ETexMinFilter.Nearest;
        switch (texture)
        {
            case XRTexture2D texture2D:
                texture2D.Resizable = false;
                texture2D.SizedInternalFormat = sizedInternalFormat;
                texture2D.MinFilter = minFilter;
                texture2D.MagFilter = ETexMagFilter.Nearest;
                break;
            case XRTexture2DArray textureArray:
                textureArray.Resizable = false;
                textureArray.SizedInternalFormat = sizedInternalFormat;
                textureArray.MinFilter = minFilter;
                textureArray.MagFilter = ETexMagFilter.Nearest;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported advanced visibility texture type '{texture.GetType().Name}'.");
        }
    }

    private static XRDataBuffer CreateVisibilityBuffer(
        string name,
        uint stride,
        uint elementCount,
        EBufferTarget target,
        EBufferUsage usage)
        => new(
            name,
            target,
            Math.Max(1u, elementCount),
            EComponentType.Struct,
            Math.Max(1u, stride),
            normalize: false,
            integral: true)
        {
            Usage = usage,
        };

    private static uint DivideRoundUp(uint value, uint divisor)
        => checked((Math.Max(value, 1u) + divisor - 1u) / divisor);

    private XRFrameBuffer CreateVisibilityFrameBuffer()
        => new(
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.Identity), EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.Metadata), EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.Selection), EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DepthStencil), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = AdvancedVisibilityResourceNames.FrameBuffer,
        };

    private XRFrameBuffer CreateVisibilityDebugFrameBuffer()
        => new(
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DebugOutput), EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = AdvancedVisibilityResourceNames.DebugFrameBuffer,
        };

    private static IFrameBufferAttachement RequireVisibilityAttachment(
        string textureName)
        => GetTexture<XRTexture>(textureName) as IFrameBufferAttachement
            ?? throw new InvalidOperationException(
                $"Advanced visibility attachment '{textureName}' is missing or not framebuffer-attachable.");

}
