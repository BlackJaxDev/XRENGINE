using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact structural ownership identity used only while creating or replacing a
/// template. Hot lookup intentionally never calls <see cref="StructurallyEquals"/>.
/// </summary>
internal readonly struct VulkanResidentDrawTemplateStructuralIdentity
{
    internal VulkanResidentDrawTemplateStructuralIdentity(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        ulong programSignature,
        ulong pipelineSignature,
        ulong geometrySignature,
        ulong dependencySignature)
    {
        CanonicalDraw = canonicalDraw;
        ProgramSignature = programSignature;
        PipelineSignature = pipelineSignature;
        GeometrySignature = geometrySignature;
        DependencySignature = dependencySignature;
    }

    internal AdvancedGpuSceneDrawIdentitySnapshot CanonicalDraw { get; }
    internal ulong ProgramSignature { get; }
    internal ulong PipelineSignature { get; }
    internal ulong GeometrySignature { get; }
    internal ulong DependencySignature { get; }

    internal bool IsValid => CanonicalDraw.IsValid;

    internal bool StructurallyEquals(
        in VulkanResidentDrawTemplateStructuralIdentity other)
    {
        AdvancedGpuSceneDrawIdentity primary = CanonicalDraw.Primary;
        AdvancedGpuSceneDrawIdentity otherPrimary = other.CanonicalDraw.Primary;
        return ReferenceEquals(primary.Database, otherPrimary.Database) &&
            primary.DatabaseEpoch == otherPrimary.DatabaseEpoch &&
            primary.Handle == otherPrimary.Handle &&
            ReferenceEquals(CanonicalDraw.Handles, other.CanonicalDraw.Handles) &&
            ProgramSignature == other.ProgramSignature &&
            PipelineSignature == other.PipelineSignature &&
            GeometrySignature == other.GeometrySignature &&
            DependencySignature == other.DependencySignature;
    }
}
