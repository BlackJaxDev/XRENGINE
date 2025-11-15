using System;
using System.Collections.Generic;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public readonly struct OpenGLDebugErrorInfo
    {
        public int Id { get; init; }
        public string Source { get; init; }
        public string Type { get; init; }
        public string Severity { get; init; }
        public string Message { get; init; }
        public int Count { get; init; }
        public DateTime FirstSeenUtc { get; init; }
        public DateTime LastSeenUtc { get; init; }
    }

    private sealed class OpenGLDebugErrorAggregate
    {
        public int Id { get; init; }
        public string Source { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Count { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    private static readonly object _glErrorTrackerLock = new();
    private static readonly Dictionary<int, OpenGLDebugErrorAggregate> _glErrorTracker = new();

    private static void RecordOpenGLError(int id, string source, string type, string severity, string message, bool shouldTrack)
    {
        if (!shouldTrack)
            return;

        var nowUtc = DateTime.UtcNow;
        lock (_glErrorTrackerLock)
        {
            if (!_glErrorTracker.TryGetValue(id, out var aggregate))
            {
                aggregate = new OpenGLDebugErrorAggregate
                {
                    Id = id,
                    FirstSeenUtc = nowUtc
                };
                _glErrorTracker[id] = aggregate;
            }

            aggregate.Count++;
            aggregate.Source = source;
            aggregate.Type = type;
            aggregate.Severity = severity;
            aggregate.Message = message;
            aggregate.LastSeenUtc = nowUtc;
        }
    }

    public static IReadOnlyList<OpenGLDebugErrorInfo> GetTrackedOpenGLErrors()
    {
        lock (_glErrorTrackerLock)
        {
            if (_glErrorTracker.Count == 0)
                return Array.Empty<OpenGLDebugErrorInfo>();

            var snapshot = new OpenGLDebugErrorInfo[_glErrorTracker.Count];
            int index = 0;
            foreach (var aggregate in _glErrorTracker.Values)
            {
                snapshot[index++] = new OpenGLDebugErrorInfo
                {
                    Id = aggregate.Id,
                    Source = aggregate.Source,
                    Type = aggregate.Type,
                    Severity = aggregate.Severity,
                    Message = aggregate.Message,
                    Count = aggregate.Count,
                    FirstSeenUtc = aggregate.FirstSeenUtc,
                    LastSeenUtc = aggregate.LastSeenUtc
                };
            }

            return snapshot;
        }
    }

    public static void ClearTrackedOpenGLErrors()
    {
        lock (_glErrorTrackerLock)
        {
            _glErrorTracker.Clear();
        }
    }
}
