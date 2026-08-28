using System.Numerics;
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

/// <summary>Converts the backend canonical view into the advanced shader ABI.</summary>
internal static class VulkanAdvancedViewRecordFactory
{
    internal static AdvancedViewRecord Create(
        in BackendReadyCanonicalViewRecord source)
    {
        Matrix4x4 projectionUnjittered = source.ProjectionUnjittered == default
            ? source.Projection
            : source.ProjectionUnjittered;
        Matrix4x4 viewProjectionJittered = source.ViewProjectionJittered == default
            ? source.View * source.Projection
            : source.ViewProjectionJittered;
        Matrix4x4 viewProjectionUnjittered = source.ViewProjectionUnjittered == default
            ? source.View * projectionUnjittered
            : source.ViewProjectionUnjittered;
        Matrix4x4 previousViewProjectionJittered = source.PreviousViewProjectionJittered == default
            ? viewProjectionJittered
            : source.PreviousViewProjectionJittered;
        Matrix4x4 previousViewProjectionUnjittered = source.PreviousViewProjectionUnjittered == default
            ? viewProjectionUnjittered
            : source.PreviousViewProjectionUnjittered;
        if (!Matrix4x4.Invert(viewProjectionJittered, out Matrix4x4 inverseViewProjectionJittered))
            inverseViewProjectionJittered = Matrix4x4.Identity;
        if (!Matrix4x4.Invert(viewProjectionUnjittered, out Matrix4x4 inverseViewProjectionUnjittered))
            inverseViewProjectionUnjittered = Matrix4x4.Identity;
        float width = Math.Max(source.ViewportWidth, 1);
        float height = Math.Max(source.ViewportHeight, 1);
        Vector4 cameraPositionAndNear = source.CameraPositionAndNear;
        Vector4 cameraForwardAndFar = source.CameraForwardAndFar;
        DeriveCameraVectors(source.View, ref cameraPositionAndNear, ref cameraForwardAndFar);
        return new AdvancedViewRecord
        {
            View = source.View,
            ProjectionJittered = source.Projection,
            ProjectionUnjittered = projectionUnjittered,
            ViewProjectionJittered = viewProjectionJittered,
            ViewProjectionUnjittered = viewProjectionUnjittered,
            // The package publishes the exact prior product. It does not
            // fabricate prior view/projection factors from that product.
            PreviousView = source.View,
            PreviousProjectionJittered = source.Projection,
            PreviousProjectionUnjittered = projectionUnjittered,
            PreviousViewProjectionJittered = previousViewProjectionJittered,
            PreviousViewProjectionUnjittered = previousViewProjectionUnjittered,
            InverseViewProjectionJittered = inverseViewProjectionJittered,
            InverseViewProjectionUnjittered = inverseViewProjectionUnjittered,
            CameraPositionAndNear = cameraPositionAndNear,
            CameraForwardAndFar = cameraForwardAndFar,
            RenderSizeAndInverse = new Vector4(width, height, 1.0f / width, 1.0f / height),
            OutputSizeAndInverse = new Vector4(width, height, 1.0f / width, 1.0f / height),
            CurrentAndPreviousJitter = source.CurrentAndPreviousJitter,
            DepthParams = source.DepthParams,
            ViewId = source.ViewId,
            OutputLayer = source.OutputLayer,
            Flags = source.Flags,
            HistoryKeyLo = unchecked((uint)source.HistoryKey),
            HistoryKeyHi = unchecked((uint)(source.HistoryKey >> 32)),
            ViewGeneration = checked((uint)source.ViewGeneration),
            ViewMaskLo = source.ViewMaskLo,
            ViewMaskHi = source.ViewMaskHi,
        };
    }

    private static void DeriveCameraVectors(
        in Matrix4x4 view,
        ref Vector4 positionAndNear,
        ref Vector4 forwardAndFar)
    {
        if ((positionAndNear != Vector4.Zero && forwardAndFar != Vector4.Zero) ||
            !Matrix4x4.Invert(view, out Matrix4x4 world))
            return;

        if (positionAndNear == Vector4.Zero)
            positionAndNear = new Vector4(world.Translation, 0.0f);
        if (forwardAndFar == Vector4.Zero)
            forwardAndFar = new Vector4(Vector3.Normalize(new Vector3(-world.M31, -world.M32, -world.M33)), 0.0f);
    }
}
