namespace XREngine.Rendering.Vulkan;

internal readonly record struct AutoUniformRewriteResult(
    string Source,
    AutoUniformBlockInfo? BlockInfo);
