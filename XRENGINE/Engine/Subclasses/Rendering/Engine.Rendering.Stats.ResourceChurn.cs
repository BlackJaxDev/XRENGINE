using System;
using System.Collections.Generic;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class ResourceChurn
                {
                    public readonly record struct SnapshotRow(
                        string ResourceKind,
                        string ResourceName,
                        string EventName,
                        string Reason,
                        int Count);

                    private readonly record struct ChurnKey(
                        string ResourceKind,
                        string ResourceName,
                        string EventName,
                        string Reason);

                    private const int MaxRows = 32;
                    private static readonly object _lock = new();
                    private static readonly Dictionary<ChurnKey, int> _currentRows = new();
                    private static SnapshotRow[] _lastRows = [];
                    private static int _createdCount;
                    private static int _recreatedCount;
                    private static int _resizedCount;
                    private static int _destroyedCount;
                    private static int _lastCreatedCount;
                    private static int _lastRecreatedCount;
                    private static int _lastResizedCount;
                    private static int _lastDestroyedCount;

                    public static int CreatedCount => Volatile.Read(ref _lastCreatedCount);
                    public static int RecreatedCount => Volatile.Read(ref _lastRecreatedCount);
                    public static int ResizedCount => Volatile.Read(ref _lastResizedCount);
                    public static int DestroyedCount => Volatile.Read(ref _lastDestroyedCount);

                    public static void Record(string resourceKind, string resourceName, string eventName, string? reason = null)
                    {
                        if (!EnableTracking)
                            return;

                        if (string.IsNullOrWhiteSpace(resourceKind))
                            resourceKind = "Resource";
                        if (string.IsNullOrWhiteSpace(resourceName))
                            resourceName = "<unnamed>";
                        if (string.IsNullOrWhiteSpace(eventName))
                            eventName = "Changed";

                        reason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;

                        switch (eventName)
                        {
                            case "Created":
                                Interlocked.Increment(ref _createdCount);
                                break;
                            case "Recreated":
                                Interlocked.Increment(ref _recreatedCount);
                                break;
                            case "Resized":
                                Interlocked.Increment(ref _resizedCount);
                                break;
                            case "Destroyed":
                                Interlocked.Increment(ref _destroyedCount);
                                break;
                        }

                        lock (_lock)
                        {
                            ChurnKey key = new(resourceKind, resourceName, eventName, reason);
                            _currentRows.TryGetValue(key, out int count);
                            _currentRows[key] = count + 1;
                        }
                    }

                    public static SnapshotRow[] GetLastFrameRows()
                    {
                        lock (_lock)
                            return _lastRows.Length == 0 ? [] : (SnapshotRow[])_lastRows.Clone();
                    }

                    internal static void SnapshotAndReset()
                    {
                        Volatile.Write(ref _lastCreatedCount, Interlocked.Exchange(ref _createdCount, 0));
                        Volatile.Write(ref _lastRecreatedCount, Interlocked.Exchange(ref _recreatedCount, 0));
                        Volatile.Write(ref _lastResizedCount, Interlocked.Exchange(ref _resizedCount, 0));
                        Volatile.Write(ref _lastDestroyedCount, Interlocked.Exchange(ref _destroyedCount, 0));

                        lock (_lock)
                        {
                            if (_currentRows.Count == 0)
                            {
                                _lastRows = [];
                                return;
                            }

                            List<KeyValuePair<ChurnKey, int>> rows = new(_currentRows);
                            rows.Sort(static (a, b) =>
                            {
                                int countCompare = b.Value.CompareTo(a.Value);
                                if (countCompare != 0)
                                    return countCompare;
                                int eventCompare = string.CompareOrdinal(a.Key.EventName, b.Key.EventName);
                                if (eventCompare != 0)
                                    return eventCompare;
                                return string.CompareOrdinal(a.Key.ResourceName, b.Key.ResourceName);
                            });

                            int take = Math.Min(MaxRows, rows.Count);
                            SnapshotRow[] snapshot = new SnapshotRow[take];
                            for (int i = 0; i < take; i++)
                            {
                                var row = rows[i];
                                snapshot[i] = new SnapshotRow(
                                    row.Key.ResourceKind,
                                    row.Key.ResourceName,
                                    row.Key.EventName,
                                    row.Key.Reason,
                                    row.Value);
                            }

                            _lastRows = snapshot;
                            _currentRows.Clear();
                        }
                    }
                }
            }
        }
    }
}
