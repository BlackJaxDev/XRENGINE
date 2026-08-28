using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Internal parse failure carrying the single primary cache-rejection reason.
/// </summary>
internal sealed class ModelBinaryCacheFormatException : IOException
{
    public ModelBinaryCacheFormatException(CacheRejectReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public CacheRejectReason Reason { get; }
}
