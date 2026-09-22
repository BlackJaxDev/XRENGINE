namespace XREngine.Editor;

internal sealed class ToolbarIconPreparationResult(
    ToolbarIconCacheKey key,
    long sessionId,
    int requestRevision,
    ToolbarIconPreparedPixels? preparedPixels,
    string? failureReason)
{
    public ToolbarIconCacheKey Key { get; } = key;
    public long SessionId { get; } = sessionId;
    public int RequestRevision { get; } = requestRevision;
    public ToolbarIconPreparedPixels? PreparedPixels { get; } = preparedPixels;
    public string? FailureReason { get; } = failureReason;
}
