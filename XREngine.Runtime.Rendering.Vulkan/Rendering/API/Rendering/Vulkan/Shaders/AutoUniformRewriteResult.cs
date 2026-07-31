namespace XREngine.Rendering.Vulkan;

internal readonly record struct AutoUniformRewriteResult(
    string Source,
    IReadOnlyList<AutoUniformBlockInfo> BlockInfos)
{
    internal AutoUniformRewriteResult(
        string source,
        AutoUniformBlockInfo? blockInfo)
        : this(
            source,
            blockInfo is null
                ? Array.Empty<AutoUniformBlockInfo>()
                : [blockInfo])
    {
    }

    public AutoUniformBlockInfo? BlockInfo
        => BlockInfos.Count == 0 ? null : BlockInfos[0];
}
