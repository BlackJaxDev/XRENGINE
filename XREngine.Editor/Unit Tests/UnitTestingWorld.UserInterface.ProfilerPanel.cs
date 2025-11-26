using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        // Cache for root method aggregation across threads
        private static readonly Dictionary<string, ProfilerRootMethodAggregate> _profilerRootMethodCache = new();
        private static float _profilerPersistenceSeconds = 5.0f;
        private static DateTime _lastProfilerUIUpdate = DateTime.MinValue;
        private static float _profilerUpdateIntervalSeconds = 0.5f;

        private static void DrawProfilerPanel()
        {
            if (!_showProfiler) return;
            if (!ImGui.Begin("Profiler", ref _showProfiler))
            {
                ImGui.End();
                return;
            }
            DrawProfilerTabContent();
            ImGui.End();
        }

        private static void DrawProfilerTabContent()
        {
            var frameSnapshot = Engine.Profiler.GetLastFrameSnapshot();
            var history = Engine.Profiler.GetThreadHistorySnapshot();

            Engine.CodeProfiler.ProfilerFrameSnapshot? snapshotForDisplay = null;
            float hierarchyFrameMs = 0.0f;
            bool showingWorstWindowSample = false;

            if (frameSnapshot is not null && frameSnapshot.Threads.Count > 0)
            {
                UpdateWorstFrameStatistics(frameSnapshot);
                snapshotForDisplay = GetSnapshotForHierarchy(frameSnapshot, out hierarchyFrameMs, out showingWorstWindowSample);
                UpdateProfilerThreadCache(frameSnapshot.Threads);
                UpdateRootMethodCache(frameSnapshot, history);
                _lastProfilerCaptureTime = frameSnapshot.FrameTime;
            }
            else
            {
                UpdateProfilerThreadCache(Array.Empty<Engine.CodeProfiler.ProfilerThreadSnapshot>());
            }

            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastProfilerUIUpdate > TimeSpan.FromSeconds(_profilerUpdateIntervalSeconds))
            {
                UpdateDisplayValues();
                _lastProfilerUIUpdate = nowUtc;
            }

            if (_profilerThreadCache.Count == 0)
            {
                ImGui.Text("No profiler samples captured yet.");
                return;
            }

            if (snapshotForDisplay is not null)
            {
                ImGui.Text($"Captured at {_lastProfilerCaptureTime:F3}s");
                ImGui.Text($"Worst frame (0.5s window): {hierarchyFrameMs:F3} ms");
                if (showingWorstWindowSample)
                    ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");
            }
            else
            {
                ImGui.Text($"Awaiting fresh profiler samples… (last capture at {_lastProfilerCaptureTime:F3}s)");
                ImGui.Text($"Worst frame (0.5s window): {_worstFrameDisplayMs:F3} ms");
            }

            // Display rendering statistics
            ImGui.Separator();
            if (ImGui.CollapsingHeader("Rendering Statistics", ImGuiTreeNodeFlags.DefaultOpen))
            {
                int drawCalls = Engine.Rendering.Stats.DrawCalls;
                int multiDrawCalls = Engine.Rendering.Stats.MultiDrawCalls;
                int triangles = Engine.Rendering.Stats.TrianglesRendered;

                ImGui.Text($"Draw Calls: {drawCalls:N0}");
                ImGui.Text($"Multi-Draw Calls: {multiDrawCalls:N0}");
                ImGui.Text($"Triangles Rendered: {triangles:N0}");
            }

            ImGui.Checkbox("Sort by Time", ref _profilerSortByTime);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.DragFloat("Update Interval (s)", ref _profilerUpdateIntervalSeconds, 0.05f, 0.1f, 2.0f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.DragFloat("Persistence (s)", ref _profilerPersistenceSeconds, 0.1f, 0.5f, 10.0f);

            ImGui.Separator();

            // Group graphs by root method name
            if (ImGui.CollapsingHeader("Root Method Graphs", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var rootMethodAggregates = _profilerRootMethodCache.Values
                    .Where(rm => rm.DisplayTotalTimeMs >= 0.1f || (nowUtc - rm.LastSeen).TotalSeconds < _profilerPersistenceSeconds)
                    .OrderBy(static rm => rm.Name)
                    .ToList();

                foreach (var rootMethod in rootMethodAggregates)
                {
                    bool isStale = nowUtc - rootMethod.LastSeen > TimeSpan.FromSeconds(_profilerUpdateIntervalSeconds);
                    float fps = rootMethod.DisplayTotalTimeMs > 0.001f ? 1000.0f / rootMethod.DisplayTotalTimeMs : 0.0f;
                    string headerLabel = $"{rootMethod.Name} ({rootMethod.DisplayTotalTimeMs:F3} ms, {fps:F1} FPS, {rootMethod.ThreadIds.Count} thread(s))";
                    if (isStale)
                        headerLabel += " (inactive)";

                    if (isStale)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));

                    ImGui.Text(headerLabel);

                    if (isStale)
                        ImGui.PopStyleColor();

                    // Plot aggregated samples for this root method
                    if (rootMethod.Samples.Length > 0)
                    {
                        float min = rootMethod.Samples.Min();
                        float max = rootMethod.Samples.Max();
                        if (!float.IsFinite(min) || !float.IsFinite(max))
                        {
                            min = 0.0f;
                            max = 0.0f;
                        }
                        if (MathF.Abs(max - min) < 0.001f)
                            max = min + 0.001f;

                        ImGui.PlotLines($"##ProfilerRootMethodPlot_{rootMethod.Name}", ref rootMethod.Samples[0], rootMethod.Samples.Length, 0, null, min, max, new Vector2(-1.0f, 40.0f));
                    }
                }
            }

            ImGui.Separator();
            ImGui.Text("Hierarchy by Root Method");

            // Create single hierarchy table
            var rootMethods = _profilerRootMethodCache.Values
                .Where(rm => rm.DisplayTotalTimeMs >= 0.1f || (DateTime.UtcNow - rm.LastSeen).TotalSeconds < _profilerPersistenceSeconds)
                .AsEnumerable();
                
            if (_profilerSortByTime)
                rootMethods = rootMethods.OrderByDescending(rm => rm.DisplayTotalTimeMs);
            else
                rootMethods = rootMethods.OrderBy(rm => rm.Name);

            var sortedRootMethods = rootMethods.ToList();

            if (sortedRootMethods.Count > 0)
            {
                if (ImGui.BeginTable("ProfilerHierarchy", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60f);
                    ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 40f);
                    ImGui.TableHeadersRow();

                    foreach (var rootMethod in sortedRootMethods)
                    {
                        if (rootMethod.RootNodes.Count > 0)
                        {
                            DrawAggregatedRootMethodHierarchy(rootMethod);
                        }
                    }
                    ImGui.EndTable();
                }
            }
            else
            {
                ImGui.Text("No root methods captured.");
            }
        }

        private static void UpdateRootMethodCache(Engine.CodeProfiler.ProfilerFrameSnapshot frameSnapshot, Dictionary<int, float[]> history)
        {
            var now = DateTime.UtcNow;
            
            // Reset current frame data but keep structure
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                entry.RootNodes.Clear();
                entry.ThreadIds.Clear();
                entry.TotalTimeMs = 0;
                ResetAggregatedChildrenStats(entry.Children);
            }

            // Aggregate root nodes by method name across all threads
            foreach (var thread in frameSnapshot.Threads)
            {
                foreach (var rootNode in thread.RootNodes)
                {
                    if (!_profilerRootMethodCache.TryGetValue(rootNode.Name, out var aggregate))
                    {
                        aggregate = new ProfilerRootMethodAggregate { Name = rootNode.Name };
                        _profilerRootMethodCache[rootNode.Name] = aggregate;
                    }

                    aggregate.RootNodes.Add(rootNode);
                    aggregate.ThreadIds.Add(thread.ThreadId);
                    aggregate.TotalTimeMs += rootNode.ElapsedMs;
                    aggregate.LastSeen = now;
                    
                    UpdateAggregatedChildrenRecursive(rootNode.Children, aggregate.Children, now);
                }
            }

            // Update accumulated max values for the current window
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                entry.AccumulatedMaxTotalTimeMs = Math.Max(entry.AccumulatedMaxTotalTimeMs, entry.TotalTimeMs);
                UpdateAggregatedChildrenMaxRecursive(entry.Children);
            }

            // Prune stale children
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                PruneAggregatedChildren(entry.Children, now);
            }

            // Build aggregated samples for each root method from thread history
            // We sum up the samples from all threads that have this root method
            foreach (var aggregate in _profilerRootMethodCache.Values)
            {
                if (aggregate.ThreadIds.Count == 0)
                    continue;

                // Get the max sample length from participating threads
                int maxLength = 0;
                foreach (var threadId in aggregate.ThreadIds)
                {
                    if (history.TryGetValue(threadId, out var samples))
                        maxLength = Math.Max(maxLength, samples.Length);
                }

                if (maxLength > 0)
                {
                    if (aggregate.Samples.Length != maxLength)
                        aggregate.Samples = new float[maxLength];
                    else
                        Array.Clear(aggregate.Samples, 0, aggregate.Samples.Length);

                    // Sum samples from all threads (simple aggregation)
                    foreach (var threadId in aggregate.ThreadIds)
                    {
                        if (history.TryGetValue(threadId, out var samples))
                        {
                            for (int i = 0; i < samples.Length && i < maxLength; i++)
                                aggregate.Samples[i] += samples[i];
                        }
                    }
                }
            }

            // Remove stale entries that haven't been seen recently
            var toRemove = _profilerRootMethodCache
                .Where(kvp => now - kvp.Value.LastSeen > TimeSpan.FromSeconds(_profilerPersistenceSeconds))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toRemove)
                _profilerRootMethodCache.Remove(key);
        }

        private static void UpdateDisplayValues()
        {
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                entry.DisplayTotalTimeMs = entry.AccumulatedMaxTotalTimeMs;
                entry.AccumulatedMaxTotalTimeMs = 0;
                UpdateAggregatedChildrenDisplayRecursive(entry.Children);
            }
        }

        private static void UpdateAggregatedChildrenDisplayRecursive(Dictionary<string, AggregatedChildNode> children)
        {
            foreach (var child in children.Values)
            {
                child.DisplayTotalElapsedMs = child.AccumulatedMaxTotalElapsedMs;
                child.DisplayCallCount = child.AccumulatedMaxCallCount;
                child.AccumulatedMaxTotalElapsedMs = 0;
                child.AccumulatedMaxCallCount = 0;
                UpdateAggregatedChildrenDisplayRecursive(child.Children);
            }
        }

        private static void UpdateAggregatedChildrenMaxRecursive(Dictionary<string, AggregatedChildNode> children)
        {
            foreach (var child in children.Values)
            {
                child.AccumulatedMaxTotalElapsedMs = Math.Max(child.AccumulatedMaxTotalElapsedMs, child.TotalElapsedMs);
                child.AccumulatedMaxCallCount = Math.Max(child.AccumulatedMaxCallCount, child.CallCount);
                UpdateAggregatedChildrenMaxRecursive(child.Children);
            }
        }

        private static void ResetAggregatedChildrenStats(Dictionary<string, AggregatedChildNode> children)
        {
            foreach (var child in children.Values)
            {
                child.TotalElapsedMs = 0;
                child.CallCount = 0;
                ResetAggregatedChildrenStats(child.Children);
            }
        }

        private static void UpdateAggregatedChildrenRecursive(IReadOnlyList<Engine.CodeProfiler.ProfilerNodeSnapshot> sourceChildren, Dictionary<string, AggregatedChildNode> targetChildren, DateTime now)
        {
            foreach (var child in sourceChildren)
            {
                if (!targetChildren.TryGetValue(child.Name, out var aggChild))
                {
                    aggChild = new AggregatedChildNode { Name = child.Name };
                    targetChildren[child.Name] = aggChild;
                }
                
                aggChild.TotalElapsedMs += child.ElapsedMs;
                aggChild.CallCount++;
                aggChild.LastSeen = now;
                
                UpdateAggregatedChildrenRecursive(child.Children, aggChild.Children, now);
            }
        }

        private static void PruneAggregatedChildren(Dictionary<string, AggregatedChildNode> children, DateTime now)
        {
            var toRemove = children.Where(kvp => now - kvp.Value.LastSeen > TimeSpan.FromSeconds(_profilerPersistenceSeconds)).Select(kvp => kvp.Key).ToList();
            foreach (var key in toRemove)
                children.Remove(key);
                
            foreach (var child in children.Values)
                PruneAggregatedChildren(child.Children, now);
        }

        private static void DrawAggregatedRootMethodHierarchy(ProfilerRootMethodAggregate rootMethod)
        {
            // Use persistent children
            var allChildren = rootMethod.Children.Values;
            float childrenTotalTime = allChildren.Sum(c => c.DisplayTotalElapsedMs);
            float untrackedTime = rootMethod.DisplayTotalTimeMs - childrenTotalTime;

            var children = allChildren
                .Where(c => c.DisplayTotalElapsedMs >= 0.1f || (DateTime.UtcNow - c.LastSeen).TotalSeconds < _profilerPersistenceSeconds)
                .ToList();

            // Draw the root method summary row
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            
            ImGuiTreeNodeFlags rootFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (children.Count == 0 && (untrackedTime < 0.1f || allChildren.Count == 0))
                rootFlags |= ImGuiTreeNodeFlags.Leaf;

            string rootKey = $"Root_{rootMethod.Name}";
            if (_profilerNodeOpenCache.TryGetValue(rootKey, out bool isRootOpen) && isRootOpen)
                rootFlags |= ImGuiTreeNodeFlags.DefaultOpen;

            bool rootNodeOpen = ImGui.TreeNodeEx($"{rootMethod.Name} (aggregated)##{rootKey}", rootFlags);
            if (ImGui.IsItemToggledOpen())
                _profilerNodeOpenCache[rootKey] = rootNodeOpen;

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{rootMethod.DisplayTotalTimeMs:F3}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{rootMethod.RootNodes.Count}");

            if (rootNodeOpen)
            {
                if (_profilerSortByTime)
                    children = children.OrderByDescending(c => c.DisplayTotalElapsedMs).ToList();

                foreach (var child in children)
                    DrawAggregatedChildNode(child, rootKey);
                
                if (untrackedTime >= 0.1f && allChildren.Count > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TreeNodeEx($"(untracked time)##Untracked_{rootKey}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{untrackedTime:F3}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text("-");
                }

                ImGui.TreePop();
            }
        }

        private static void DrawAggregatedChildNode(AggregatedChildNode node, string idSuffix)
        {
            var allChildren = node.Children.Values;
            float childrenTotalTime = allChildren.Sum(c => c.DisplayTotalElapsedMs);
            float untrackedTime = node.DisplayTotalElapsedMs - childrenTotalTime;

            var children = allChildren
                .Where(c => c.DisplayTotalElapsedMs >= 0.1f || (DateTime.UtcNow - c.LastSeen).TotalSeconds < _profilerPersistenceSeconds)
                .ToList();

            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (children.Count == 0 && (untrackedTime < 0.1f || allChildren.Count == 0))
                flags |= ImGuiTreeNodeFlags.Leaf;

            string nodeKey = $"{node.Name}_{idSuffix}";
            if (_profilerNodeOpenCache.TryGetValue(nodeKey, out bool isOpen) && isOpen)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            bool nodeOpen = ImGui.TreeNodeEx($"{node.Name}##{nodeKey}", flags);
            if (ImGui.IsItemToggledOpen())
                _profilerNodeOpenCache[nodeKey] = nodeOpen;

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{node.DisplayTotalElapsedMs:F3}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{node.DisplayCallCount}");

            if (nodeOpen)
            {
                if (_profilerSortByTime)
                    children = children.OrderByDescending(c => c.DisplayTotalElapsedMs).ToList();

                foreach (var child in children)
                    DrawAggregatedChildNode(child, nodeKey);
                
                if (untrackedTime >= 0.1f && allChildren.Count > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TreeNodeEx($"(untracked time)##Untracked_{nodeKey}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{untrackedTime:F3}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text("-");
                }

                ImGui.TreePop();
            }
        }

        // Helper class to aggregate child nodes across multiple root nodes
        private sealed class AggregatedChildNode
        {
            public string Name { get; set; } = string.Empty;
            public float TotalElapsedMs { get; set; }
            public int CallCount { get; set; }
            
            public float AccumulatedMaxTotalElapsedMs { get; set; }
            public int AccumulatedMaxCallCount { get; set; }
            
            public float DisplayTotalElapsedMs { get; set; }
            public int DisplayCallCount { get; set; }

            public Dictionary<string, AggregatedChildNode> Children { get; } = new();
            public DateTime LastSeen { get; set; }
        }

        // Cache entry for root method aggregation
        private sealed class ProfilerRootMethodAggregate
        {
            public string Name { get; set; } = string.Empty;
            public float TotalTimeMs { get; set; }
            
            public float AccumulatedMaxTotalTimeMs { get; set; }
            public float DisplayTotalTimeMs { get; set; }

            public HashSet<int> ThreadIds { get; } = new();
            public List<Engine.CodeProfiler.ProfilerNodeSnapshot> RootNodes { get; } = new();
            public float[] Samples { get; set; } = Array.Empty<float>();
            public DateTime LastSeen { get; set; }
            public Dictionary<string, AggregatedChildNode> Children { get; } = new();
        }

        private static void UpdateProfilerThreadCache(IReadOnlyList<Engine.CodeProfiler.ProfilerThreadSnapshot> threads)
        {
            var now = DateTime.UtcNow;
            // Mark existing as stale
            foreach (var entry in _profilerThreadCache.Values)
            {
                if (now - entry.LastSeen > ProfilerThreadStaleThreshold)
                    entry.IsStale = true;
            }

            // Update or add
            foreach (var thread in threads)
            {
                if (!_profilerThreadCache.TryGetValue(thread.ThreadId, out var entry))
                {
                    entry = new ProfilerThreadCacheEntry { ThreadId = thread.ThreadId };
                    _profilerThreadCache[thread.ThreadId] = entry;
                }
                entry.LastSeen = now;
                entry.IsStale = false;
                entry.Snapshot = thread;
            }

            // Remove old
            var toRemove = _profilerThreadCache.Where(kvp => now - kvp.Value.LastSeen > ProfilerThreadCacheTimeout).Select(kvp => kvp.Key).ToList();
            foreach (var key in toRemove)
                _profilerThreadCache.Remove(key);
        }

        private static Engine.CodeProfiler.ProfilerFrameSnapshot GetSnapshotForHierarchy(Engine.CodeProfiler.ProfilerFrameSnapshot currentSnapshot, out float frameMs, out bool usingWorstSnapshot)
        {
            if (_worstFrameDisplaySnapshot is not null)
            {
                usingWorstSnapshot = true;
                frameMs = _worstFrameDisplayMs;
                return _worstFrameDisplaySnapshot;
            }

            usingWorstSnapshot = false;
            frameMs = currentSnapshot.Threads.Max(t => t.TotalTimeMs);
            return currentSnapshot;
        }

        private static void UpdateWorstFrameStatistics(Engine.CodeProfiler.ProfilerFrameSnapshot snapshot)
        {
            var now = DateTime.UtcNow;
            if (_worstFrameWindowStart == DateTime.MinValue)
                _worstFrameWindowStart = now;

            float currentFrameMs = snapshot.Threads.Max(t => t.TotalTimeMs);
            if (_worstFrameWindowSnapshot is null || currentFrameMs > _worstFrameWindowMaxMs)
            {
                _worstFrameWindowMaxMs = currentFrameMs;
                _worstFrameWindowSnapshot = snapshot;
            }

            if (now - _worstFrameWindowStart >= WorstFrameWindowDuration)
            {
                _worstFrameDisplayMs = _worstFrameWindowMaxMs;
                _worstFrameDisplaySnapshot = _worstFrameWindowSnapshot;

                _worstFrameWindowMaxMs = currentFrameMs;
                _worstFrameWindowSnapshot = snapshot;
                _worstFrameWindowStart = now;
            }
        }
    }
}
