namespace XREngine.Rendering.Vulkan;

internal static class AttachmentRoleClassifier
{
    public static bool IsColorLike(AttachmentRole role)
        => role is AttachmentRole.Color or AttachmentRole.Resolve;
}
