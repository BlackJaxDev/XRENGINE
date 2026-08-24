namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable planner facts captured with a frame operation. These facts are
/// supplied by the frame planner/recording boundary and never retained by a
/// render-program wrapper.
/// </summary>
internal readonly record struct VulkanProgramPlannerRequest(int DescriptorViewFamilyIdentity)
{
    internal static VulkanProgramPlannerRequest From(in FrameOpContext context)
        => new(context.OutputTargetIdentity != 0
            ? context.OutputTargetIdentity
            : context.ViewportIdentity);
}
