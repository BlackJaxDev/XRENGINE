using System;
using System.Collections.Concurrent;

namespace XREngine.Editor.Mcp
{
    public sealed class McpRateLimiter
    {
        private readonly ConcurrentDictionary<string, FixedWindowCounter> _counters = new(StringComparer.OrdinalIgnoreCase);

        public bool TryAcquire(string clientKey, int maxRequests, TimeSpan window, DateTimeOffset nowUtc, out TimeSpan retryAfter)
        {
            retryAfter = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(clientKey) || maxRequests <= 0 || window <= TimeSpan.Zero)
                return true;

            var counter = _counters.GetOrAdd(clientKey, _ => new FixedWindowCounter(nowUtc, 0));

            lock (counter.Sync)
            {
                if (nowUtc - counter.WindowStart >= window)
                {
                    counter.WindowStart = nowUtc;
                    counter.Count = 0;
                }

                if (counter.Count >= maxRequests)
                {
                    retryAfter = (counter.WindowStart + window) - nowUtc;
                    if (retryAfter < TimeSpan.Zero)
                        retryAfter = TimeSpan.Zero;
                    return false;
                }

                counter.Count++;
                return true;
            }
        }

        private sealed class FixedWindowCounter
        {
            public FixedWindowCounter(DateTimeOffset windowStart, int count)
            {
                WindowStart = windowStart;
                Count = count;
            }

            public object Sync { get; } = new();
            public DateTimeOffset WindowStart { get; set; }
            public int Count { get; set; }
        }
    }
}
