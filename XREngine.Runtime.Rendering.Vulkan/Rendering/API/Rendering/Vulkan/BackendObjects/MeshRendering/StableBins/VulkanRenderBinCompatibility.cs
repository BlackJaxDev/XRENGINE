using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native and immutable descriptor closure used to disambiguate a
/// numeric stable-bin hash. Hashes only select a lookup bucket; this value
/// decides whether two submissions can actually share a Vulkan bind sequence.
/// </summary>
internal readonly struct VulkanRenderBinNativeCompatibility : IEquatable<VulkanRenderBinNativeCompatibility>
{
    private readonly VulkanResidentDrawTemplateNativeState _native;
    private readonly object? _program;
    private readonly object? _bindingSnapshot;
    private readonly object? _materialOverride;

    internal VulkanRenderBinNativeCompatibility(
        in VulkanResidentDrawTemplateNativeState native)
    {
        _native = native;
        _program = native.DrawTemplate.PreparedProgram;
        _bindingSnapshot = native.DrawTemplate.ProgramBindingSnapshot;
        _materialOverride = native.DrawTemplate.MaterialOverride;
    }

    public bool Equals(VulkanRenderBinNativeCompatibility other)
    {
        if (_native.PipelineLayout.Handle != other._native.PipelineLayout.Handle ||
            _native.PrimitiveCount != other._native.PrimitiveCount ||
            _native.VertexBufferCount != other._native.VertexBufferCount ||
            !ReferenceEquals(_program, other._program) ||
            !ReferenceEquals(_bindingSnapshot, other._bindingSnapshot) ||
            !ReferenceEquals(_materialOverride, other._materialOverride))
        {
            return false;
        }

        for (int index = 0; index < _native.PrimitiveCount; ++index)
            if (_native.GetPrimitive(index) != other._native.GetPrimitive(index))
                return false;
        for (int index = 0; index < _native.VertexBufferCount; ++index)
            if (_native.GetVertexBuffer(index).Handle != other._native.GetVertexBuffer(index).Handle ||
                _native.GetVertexBinding(index) != other._native.GetVertexBinding(index))
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
        hash.Add(_native.PipelineLayout.Handle);
        hash.Add(_native.PrimitiveCount);
        for (int index = 0; index < _native.PrimitiveCount; ++index)
            hash.Add(_native.GetPrimitive(index));
        for (int index = 0; index < _native.VertexBufferCount; ++index)
        {
            hash.Add(_native.GetVertexBuffer(index).Handle);
            hash.Add(_native.GetVertexBinding(index));
        }
        hash.Add(GetReferenceHashCode(_program));
        hash.Add(GetReferenceHashCode(_bindingSnapshot));
        hash.Add(GetReferenceHashCode(_materialOverride));
        return hash.ToHashCode();
    }

    private static int GetReferenceHashCode(object? value)
        => value is null ? 0 : RuntimeHelpers.GetHashCode(value);
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
