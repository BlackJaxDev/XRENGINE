using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frame-scoped metadata placed in the canonical diagnostics storage binding.
/// The header is followed immediately by <see cref="VulkanAdvancedPassRecord"/>
/// rows, so the GPU never observes the legacy zero fallback for frame/pass data.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VulkanAdvancedFrameMetadataHeader
{
    internal ulong FrameId;
    internal ulong FrameGeneration;
    internal ulong SourceRevision;
    internal ulong DependencySignature;
    internal uint ViewCount;
    internal uint PassCount;
    internal uint DiagnosticCount;
    internal uint Reserved;
}

/// <summary>GPU representation of one exact canonical pass publication.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VulkanAdvancedPassRecord
{
    internal uint PassIndex;
    internal uint RequestedStrategy;
    internal uint ResolvedStrategy;
    internal uint Diagnostics;
    internal uint SubmissionFlags;
    internal ulong PassGeneration;
    internal ulong DependencySignature;
    internal ulong MembershipSignature;
    internal ulong SubmissionSignature;

    internal static VulkanAdvancedPassRecord FromCanonical(
        in BackendReadyCanonicalPassRecord source)
        => new()
        {
            PassIndex = checked((uint)Math.Max(source.PassIndex, 0)),
            RequestedStrategy = (uint)source.SubmissionResolution.Requested,
            ResolvedStrategy = (uint)source.SubmissionResolution.Resolved,
            Diagnostics = (uint)source.Diagnostics,
            SubmissionFlags =
                (source.SubmissionResolution.Downgraded ? 1u : 0u) |
                (source.SubmissionResolution.SupportsMeshletDispatch ? 2u : 0u),
            PassGeneration = source.PassGeneration,
            DependencySignature = source.DependencySignature,
            MembershipSignature = source.MembershipSignature,
            SubmissionSignature = source.SubmissionResolution.ResolutionSignature,
        };
}
