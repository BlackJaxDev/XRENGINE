using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan.RenderGraph;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native and immutable descriptor closure used to disambiguate a
/// numeric stable-bin hash. Hashes only select a lookup bucket; this value
/// decides whether two submissions can actually share a Vulkan bind sequence.
/// </summary>
internal readonly struct VulkanRenderBinNativeCompatibility : IEquatable<VulkanRenderBinNativeCompatibility>
{
    private readonly ulong _pipelineLayoutHandle;
    private readonly VulkanPreparedMeshPrimitive _primitive0;
    private readonly VulkanPreparedMeshPrimitive _primitive1;
    private readonly VulkanPreparedMeshPrimitive _primitive2;
    private readonly byte _primitiveCount;
    private readonly int _vertexBufferCount;
    private readonly VkBufferHandle _vertexBuffer0;
    private readonly VkBufferHandle _vertexBuffer1;
    private readonly uint _vertexBinding0;
    private readonly uint _vertexBinding1;
    private readonly ReadOnlyMemory<VkBufferHandle> _vertexBuffers;
    private readonly ReadOnlyMemory<uint> _vertexBindings;
    private readonly object? _program;
    private readonly object? _bindingSnapshot;
    private readonly object? _materialOverride;

    internal VulkanRenderBinNativeCompatibility(
        in VulkanResidentDrawTemplateNativeState native)
    {
        _pipelineLayoutHandle = native.PipelineLayout.Handle;
        _primitive0 = native.Primitive0;
        _primitive1 = native.Primitive1;
        _primitive2 = native.Primitive2;
        _primitiveCount = native.PrimitiveCount;
        _vertexBufferCount = native.VertexBufferCount;
        _vertexBuffer0 = _vertexBufferCount > 0 ? native.GetVertexBuffer(0) : default;
        _vertexBuffer1 = _vertexBufferCount > 1 ? native.GetVertexBuffer(1) : default;
        _vertexBinding0 = _vertexBufferCount > 0 ? native.GetVertexBinding(0) : 0u;
        _vertexBinding1 = _vertexBufferCount > 1 ? native.GetVertexBinding(1) : 0u;
        _vertexBuffers = _vertexBufferCount > 2
            ? native.VertexBufferStorage
            : ReadOnlyMemory<VkBufferHandle>.Empty;
        _vertexBindings = _vertexBufferCount > 2
            ? native.VertexBindingStorage
            : ReadOnlyMemory<uint>.Empty;
        _program = native.DrawTemplate.PreparedProgram;
        _bindingSnapshot = native.DrawTemplate.ProgramBindingSnapshot;
        _materialOverride = native.DrawTemplate.MaterialOverride;
    }

    public bool Equals(VulkanRenderBinNativeCompatibility other)
    {
        if (_pipelineLayoutHandle != other._pipelineLayoutHandle ||
            _primitiveCount != other._primitiveCount ||
            _vertexBufferCount != other._vertexBufferCount ||
            !ReferenceEquals(_program, other._program) ||
            !ReferenceEquals(_bindingSnapshot, other._bindingSnapshot) ||
            !ReferenceEquals(_materialOverride, other._materialOverride))
        {
            return false;
        }

        for (int index = 0; index < _primitiveCount; ++index)
            if (GetPrimitive(index) != other.GetPrimitive(index))
                return false;
        for (int index = 0; index < _vertexBufferCount; ++index)
            if (GetVertexBuffer(index).Handle != other.GetVertexBuffer(index).Handle ||
                GetVertexBinding(index) != other.GetVertexBinding(index))
            {
                return false;
            }
        return true;
    }

    public override bool Equals(object? obj)
        => obj is VulkanRenderBinNativeCompatibility other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(_pipelineLayoutHandle);
        hash.Add(_primitiveCount);
        for (int index = 0; index < _primitiveCount; ++index)
            hash.Add(GetPrimitive(index));
        for (int index = 0; index < _vertexBufferCount; ++index)
        {
            hash.Add(GetVertexBuffer(index).Handle);
            hash.Add(GetVertexBinding(index));
        }
        hash.Add(GetReferenceHashCode(_program));
        hash.Add(GetReferenceHashCode(_bindingSnapshot));
        hash.Add(GetReferenceHashCode(_materialOverride));
        return hash.ToHashCode();
    }

    private static int GetReferenceHashCode(object? value)
        => value is null ? 0 : RuntimeHelpers.GetHashCode(value);

    private VulkanPreparedMeshPrimitive GetPrimitive(int index)
        => index switch
        {
            0 => _primitive0,
            1 => _primitive1,
            2 => _primitive2,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    private VkBufferHandle GetVertexBuffer(int index)
        => index switch
        {
            0 => _vertexBuffer0,
            1 => _vertexBuffer1,
            _ => _vertexBuffers.Span[index],
        };

    private uint GetVertexBinding(int index)
        => index switch
        {
            0 => _vertexBinding0,
            1 => _vertexBinding1,
            _ => _vertexBindings.Span[index],
        };
}

/// <summary>
/// Exact execution-scope closure. Scheduling-only fields are deliberately
/// excluded so a new output request does not grow stable-bin dictionaries each
/// frame, while every native target, view, descriptor and queue choice stays
/// part of bin compatibility.
/// </summary>
internal readonly record struct VulkanRenderBinContextCompatibility(
    int PipelineIdentity,
    int ViewportIdentity,
    int OutputTargetIdentity,
    int OutputFrameBufferIdentity,
    EVulkanFrameOpContextKind ContextKind,
    ulong LogicalViewId,
    ulong RecordingFingerprint,
    uint SubmissionQueueFamily,
    bool StereoEnabled,
    bool MultiviewEnabled,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    int? ResourceRegistrySignatureSnapshot,
    object? PipelineInstance,
    object? ResourceRegistry,
    object? OutputFrameBuffer,
    object? PassMetadata,
    string? OutputFrameBufferName,
    string? OutputTargetName)
{
    internal static VulkanRenderBinContextCompatibility Create(
        in FrameOpContext context)
        => new(
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.OutputTargetIdentity,
            context.OutputFrameBufferIdentity,
            context.ContextKind,
            context.LogicalViewId,
            context.RecordingFingerprint,
            context.SubmissionQueueFamily,
            context.StereoEnabled,
            context.MultiviewEnabled,
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.ResourceRegistrySignatureSnapshot,
            context.PipelineInstance,
            context.ResourceRegistry,
            context.OutputFrameBuffer,
            context.PassMetadata,
            context.OutputFrameBufferName,
            context.OutputTargetName);
}
