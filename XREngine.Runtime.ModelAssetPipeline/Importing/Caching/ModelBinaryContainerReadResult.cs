using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Non-throwing outcome from the defensive model-container reader.
/// </summary>
internal sealed class ModelBinaryContainerReadResult
{
    private ModelBinaryContainerReadResult(
        ModelBinaryContainer? container,
        CacheRejectReason reason,
        string? detail)
    {
        Container = container;
        Reason = reason;
        Detail = detail;
    }

    public bool IsSuccess => Container is not null;
    public ModelBinaryContainer? Container { get; }
    public CacheRejectReason Reason { get; }
    public string? Detail { get; }

    public static ModelBinaryContainerReadResult Success(ModelBinaryContainer container)
        => new(container, CacheRejectReason.None, null);

    public static ModelBinaryContainerReadResult Rejected(CacheRejectReason reason, string detail)
        => new(null, reason, detail);
}
