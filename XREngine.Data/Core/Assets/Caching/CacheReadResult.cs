using System;

namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Represents a typed cache-read decision, including the loaded asset on a hit and the
    /// primary rejection reason when an existing payload is incompatible.
    /// </summary>
    public readonly record struct CacheReadResult
    {
        private CacheReadResult(
            CacheReadStatus status,
            XRAsset? asset,
            CacheRejectReason reason,
            string? detail)
        {
            Status = status;
            Asset = asset;
            Reason = reason;
            Detail = detail;
        }

        /// <summary>
        /// Gets the read outcome.
        /// </summary>
        public CacheReadStatus Status { get; }

        /// <summary>
        /// Gets the hydrated asset when <see cref="Status"/> is <see cref="CacheReadStatus.Hit"/>.
        /// </summary>
        public XRAsset? Asset { get; }

        /// <summary>
        /// Gets the primary rejection reason, or <see cref="CacheRejectReason.None"/> when
        /// the outcome is not a rejection.
        /// </summary>
        public CacheRejectReason Reason { get; }

        /// <summary>
        /// Gets optional diagnostic detail associated with the decision.
        /// </summary>
        public string? Detail { get; }

        /// <summary>
        /// Creates a successful cache-hit result.
        /// </summary>
        public static CacheReadResult Hit(XRAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);
            return new CacheReadResult(CacheReadStatus.Hit, asset, CacheRejectReason.None, detail: null);
        }

        /// <summary>
        /// Creates a cache-miss result.
        /// </summary>
        public static CacheReadResult Miss(string? detail = null)
            => new(CacheReadStatus.Miss, asset: null, CacheRejectReason.None, detail);

        /// <summary>
        /// Creates a rejected-cache result with one primary reason.
        /// </summary>
        public static CacheReadResult Rejected(CacheRejectReason reason, string? detail = null)
        {
            if (reason == CacheRejectReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "A rejected cache result requires a reason.");

            return new CacheReadResult(CacheReadStatus.Rejected, asset: null, reason, detail);
        }
    }
}
