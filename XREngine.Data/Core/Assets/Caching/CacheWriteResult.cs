using System;

namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Represents a typed cache-write decision.
    /// </summary>
    public readonly record struct CacheWriteResult
    {
        private CacheWriteResult(
            CacheWriteStatus status,
            CacheRejectReason reason,
            string? detail,
            Exception? exception)
        {
            Status = status;
            Reason = reason;
            Detail = detail;
            Exception = exception;
        }

        /// <summary>
        /// Gets the write outcome.
        /// </summary>
        public CacheWriteStatus Status { get; }

        /// <summary>
        /// Gets the primary reason associated with a skipped or failed write.
        /// </summary>
        public CacheRejectReason Reason { get; }

        /// <summary>
        /// Gets optional diagnostic detail associated with the outcome.
        /// </summary>
        public string? Detail { get; }

        /// <summary>
        /// Gets the exception captured by a failed write, when available.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Creates a successful write result.
        /// </summary>
        public static CacheWriteResult Written()
            => new(CacheWriteStatus.Written, CacheRejectReason.None, detail: null, exception: null);

        /// <summary>
        /// Creates an intentional no-write result.
        /// </summary>
        public static CacheWriteResult Skipped(
            CacheRejectReason reason = CacheRejectReason.None,
            string? detail = null)
            => new(CacheWriteStatus.Skipped, reason, detail, exception: null);

        /// <summary>
        /// Creates a failed write result.
        /// </summary>
        public static CacheWriteResult Failed(
            CacheRejectReason reason,
            string? detail = null,
            Exception? exception = null)
        {
            if (reason == CacheRejectReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "A failed cache write requires a reason.");

            return new CacheWriteResult(CacheWriteStatus.Failed, reason, detail, exception);
        }
    }
}
