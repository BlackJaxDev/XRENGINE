using System;
using System.Collections.Generic;
using System.Threading;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static partial class ResourceChurn
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
                    private static readonly KeyValuePair<ChurnKey, int>[] _topRows = new KeyValuePair<ChurnKey, int>[MaxRows];
                    private static SnapshotRow[] _lastRows = new SnapshotRow[MaxRows];
                    private static SnapshotRow[] _nextRows = new SnapshotRow[MaxRows];
                    private static int _lastRowCount;
                    private static int _createdCount;
                    private static int _recreatedCount;
                    private static int _resizedCount;
                    private static int _destroyedCount;
                    private static int _lastCreatedCount;
                    private static int _lastRecreatedCount;
                    private static int _lastResizedCount;
                    private static int _lastDestroyedCount;
                    private static int _lastOmittedRowCount;
                    private static long _lastRevision;

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

                        lock (_lock)
                        {
                            switch (eventName)
                            {
                                case "Created": _createdCount++; break;
                                case "Recreated": _recreatedCount++; break;
                                case "Resized": _resizedCount++; break;
                                case "Destroyed": _destroyedCount++; break;
                            }
                            ChurnKey key = new(resourceKind, resourceName, eventName, reason);
                            _currentRows.TryGetValue(key, out int count);
                            _currentRows[key] = count + 1;
                        }
                    }

                    /// <summary>Copies a coherent, bounded last-frame publication without cloning its rows.</summary>
                    public static int CopyLastFrameRows(Span<SnapshotRow> destination,
                        out long revision, out SnapshotCounts counts, out int omittedRowCount)
                    {
                        lock (_lock)
                        {
                            revision = _lastRevision;
                            counts = new(_lastCreatedCount, _lastRecreatedCount,
                                _lastResizedCount, _lastDestroyedCount);
                            omittedRowCount = _lastOmittedRowCount;
                            int copied = Math.Min(destination.Length, _lastRowCount);
                            _lastRows.AsSpan(0, copied).CopyTo(destination);
                            return _lastRowCount;
                        }
                    }

                    public static SnapshotRow[] GetLastFrameRows()
                    {
                        lock (_lock)
                        {
                            if (_lastRowCount == 0)
                                return [];
                            SnapshotRow[] copy = new SnapshotRow[_lastRowCount];
                            Array.Copy(_lastRows, copy, _lastRowCount);
                            return copy;
                        }
                    }

                    internal static void SnapshotAndReset()
                    {
                        lock (_lock)
                        {
                            int omitted = 0;
                            int take = 0;
                            if (_currentRows.Count != 0)
                            {
                                foreach (KeyValuePair<ChurnKey, int> row in _currentRows)
                                {
                                    int insert = 0;
                                    while (insert < take && CompareRows(_topRows[insert], row) <= 0)
                                        insert++;
                                    if (insert >= MaxRows)
                                        continue;
                                    if (take < MaxRows)
                                        take++;
                                    for (int move = take - 1; move > insert; move--)
                                        _topRows[move] = _topRows[move - 1];
                                    _topRows[insert] = row;
                                }

                                omitted = _currentRows.Count - take;
                                for (int i = 0; i < take; i++)
                                {
                                    var row = _topRows[i];
                                    _nextRows[i] = new SnapshotRow(
                                        row.Key.ResourceKind,
                                        row.Key.ResourceName,
                                        row.Key.EventName,
                                        row.Key.Reason,
                                        row.Value);
                                }
                                Array.Clear(_topRows);
                            }

                            (_lastRows, _nextRows) = (_nextRows, _lastRows);
                            Array.Clear(_nextRows);
                            _lastRowCount = take;
                            _lastOmittedRowCount = omitted;
                            Volatile.Write(ref _lastCreatedCount, _createdCount);
                            Volatile.Write(ref _lastRecreatedCount, _recreatedCount);
                            Volatile.Write(ref _lastResizedCount, _resizedCount);
                            Volatile.Write(ref _lastDestroyedCount, _destroyedCount);
                            _createdCount = 0;
                            _recreatedCount = 0;
                            _resizedCount = 0;
                            _destroyedCount = 0;
                            _currentRows.Clear();
                            _lastRevision++;
                        }
                    }

                    private static int CompareRows(KeyValuePair<ChurnKey, int> left,
                        KeyValuePair<ChurnKey, int> right)
                    {
                        int result = right.Value.CompareTo(left.Value);
                        if (result != 0) return result;
                        result = string.CompareOrdinal(left.Key.EventName, right.Key.EventName);
                        if (result != 0) return result;
                        result = string.CompareOrdinal(left.Key.ResourceName, right.Key.ResourceName);
                        if (result != 0) return result;
                        result = string.CompareOrdinal(left.Key.ResourceKind, right.Key.ResourceKind);
                        return result != 0 ? result : string.CompareOrdinal(left.Key.Reason, right.Key.Reason);
                    }
                }
            }
        }
    }
}
