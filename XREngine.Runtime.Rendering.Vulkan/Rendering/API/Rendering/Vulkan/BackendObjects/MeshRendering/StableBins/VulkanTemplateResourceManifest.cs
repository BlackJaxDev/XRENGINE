using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable resource declaration for one resident template. This replaces
/// per-draw resource-use lowering: all entries are canonical, ordered, and
/// captured only when topology is published.
/// </summary>
internal sealed class VulkanTemplateResourceManifest
{
    private readonly VulkanResidentDrawDependency[] _resources;
    private readonly VulkanTemplateNativeResourceUse[] _nativeUses;
    private int _resourceCount;
    private int _nativeUseCount;

    private VulkanTemplateResourceManifest(
        VulkanResidentDrawDependency[] resources,
        VulkanTemplateNativeResourceUse[] nativeUses)
    {
        _resources = resources;
        _nativeUses = nativeUses;
        _resourceCount = resources.Length;
        _nativeUseCount = nativeUses.Length;
    }

    /// <summary>Preallocates one frame-stream manifest scratch slot.</summary>
    internal VulkanTemplateResourceManifest(
        int resourceCapacity,
        int nativeUseCapacity)
    {
        _resources = new VulkanResidentDrawDependency[resourceCapacity];
        _nativeUses = new VulkanTemplateNativeResourceUse[nativeUseCapacity];
    }

    internal ReadOnlySpan<VulkanResidentDrawDependency> Resources
        => _resources.AsSpan(0, _resourceCount);
    internal ReadOnlySpan<VulkanTemplateNativeResourceUse> NativeUses
        => _nativeUses.AsSpan(0, _nativeUseCount);
    internal int Count => _resourceCount;
    internal int NativeUseCount => _nativeUseCount;

    internal static VulkanTemplateResourceManifest Create(
        VulkanResidentDrawDependencyManifest dependencies,
        in VulkanResidentDrawTemplateNativeState nativeState)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ReadOnlySpan<VulkanResidentDrawDependency> source =
            dependencies.CanonicalDependencies;
        VulkanResidentDrawDependency[] copy = new VulkanResidentDrawDependency[source.Length];
        source.CopyTo(copy);
        int nativeCount = 1 + nativeState.VertexBufferCount;
        for (int primitiveIndex = 0; primitiveIndex < nativeState.PrimitiveCount; ++primitiveIndex)
        {
            VulkanPreparedMeshPrimitive primitive = nativeState.GetPrimitive(primitiveIndex);
            nativeCount += primitive.Indexed ? 2 : 1;
        }
        VulkanTemplateNativeResourceUse[] nativeUses =
            new VulkanTemplateNativeResourceUse[nativeCount];
        int cursor = 0;
        nativeUses[cursor++] = new(
            ObjectType.PipelineLayout,
            nativeState.PipelineLayout.Handle,
            VulkanTemplateResourceAccess.Read,
            PipelineStageFlags.VertexShaderBit,
            AccessFlags.ShaderReadBit,
            ImageLayout.Undefined,
            uint.MaxValue);
        for (int primitiveIndex = 0; primitiveIndex < nativeState.PrimitiveCount; ++primitiveIndex)
        {
            VulkanPreparedMeshPrimitive primitive = nativeState.GetPrimitive(primitiveIndex);
            nativeUses[cursor++] = new(
                ObjectType.Pipeline,
                primitive.Pipeline.Handle,
                VulkanTemplateResourceAccess.Read,
                PipelineStageFlags.VertexShaderBit,
                AccessFlags.ShaderReadBit,
                ImageLayout.Undefined,
                uint.MaxValue);
            if (primitive.Indexed)
            {
                nativeUses[cursor++] = new(
                    ObjectType.Buffer,
                    primitive.IndexBuffer.Handle,
                    VulkanTemplateResourceAccess.Read,
                    PipelineStageFlags.VertexInputBit,
                    AccessFlags.IndexReadBit,
                    ImageLayout.Undefined,
                    uint.MaxValue);
            }
        }
        for (int index = 0; index < nativeState.VertexBufferCount; ++index)
        {
            nativeUses[cursor++] = new(
                ObjectType.Buffer,
                nativeState.GetVertexBuffer(index).Handle,
                VulkanTemplateResourceAccess.Read,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.VertexAttributeReadBit,
                ImageLayout.Undefined,
                uint.MaxValue);
        }
        return new(copy, nativeUses);
    }

    /// <summary>
    /// Creates the exact manifest for one canonical visibility draw backed by
    /// packed advanced-scene vertex and index arenas.
    /// </summary>
    internal void ResetVisibilityGeometry(
        in AdvancedVisibilityPayload payload,
        in VulkanVisibilityPreparedVertexSource vertexSource,
        VulkanFrameDataSlice indexSlice)
    {
        if (_resources.Length < 3 || _nativeUses.Length < 2)
            throw new InvalidOperationException(
                "Visibility atlas manifest scratch capacity is incomplete.");
        _resources[0] = new(EBackendReadyCanonicalOwner.Draw, payload.Draw);
        _resources[1] = new(
            EBackendReadyCanonicalOwner.Geometry,
            payload.Geometry);
        _resources[2] = new(
            EBackendReadyCanonicalOwner.Material,
            payload.Material);
        _nativeUses[0] = new(
            ObjectType.Buffer,
            vertexSource.Buffer.Handle,
            VulkanTemplateResourceAccess.Read,
            PipelineStageFlags.VertexInputBit |
                PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.MeshShaderBitExt,
            AccessFlags.VertexAttributeReadBit |
                AccessFlags.ShaderReadBit,
            ImageLayout.Undefined,
            uint.MaxValue,
            vertexSource.Offset,
            vertexSource.Length,
            vertexSource.Generation,
            vertexSource.ElementStride);
        _nativeUses[1] = new(
            ObjectType.Buffer,
            indexSlice.Buffer.Handle,
            VulkanTemplateResourceAccess.Read,
            PipelineStageFlags.VertexInputBit |
                PipelineStageFlags.ComputeShaderBit,
            AccessFlags.IndexReadBit | AccessFlags.ShaderReadBit,
            ImageLayout.Undefined,
            uint.MaxValue,
            indexSlice.Offset,
            indexSlice.Length,
            indexSlice.Generation,
            sizeof(uint));
        _resourceCount = 3;
        _nativeUseCount = 2;
    }
    internal void CopyFrom(VulkanTemplateResourceManifest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > _resources.Length ||
            source.NativeUseCount > _nativeUses.Length)
        {
            throw new InvalidOperationException(
                "Template manifest scratch capacity is incomplete.");
        }
        source.Resources.CopyTo(_resources);
        source.NativeUses.CopyTo(_nativeUses);
        _resourceCount = source.Count;
        _nativeUseCount = source.NativeUseCount;
    }
}

/// <summary>Native synchronization declaration retained by a template manifest.</summary>
internal readonly record struct VulkanTemplateNativeResourceUse(
    ObjectType ObjectType,
    ulong Handle,
    VulkanTemplateResourceAccess Access,
    PipelineStageFlags Stages,
    AccessFlags AccessMask,
    ImageLayout RequiredLayout,
    uint QueueFamily,
    ulong Offset = 0u,
    ulong Length = 0u,
    ulong NativeGeneration = 0u,
    uint ElementStride = 0u);

/// <summary>Native access mode for template/bin resource manifests.</summary>
[Flags]
internal enum VulkanTemplateResourceAccess : byte
{
    None = 0,
    Read = 1,
    Write = 2,
}
