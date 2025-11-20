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
                _lastProfilerCaptureTime = frameSnapshot.FrameTime;
            }
            else
            {
                UpdateProfilerThreadCache(Array.Empty<Engine.CodeProfiler.ProfilerThreadSnapshot>());
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

            ImGui.Checkbox("Sort by Time", ref _profilerSortByTime);

            ImGui.Separator();

            var nowUtc = DateTime.UtcNow;

            if (ImGui.CollapsingHeader("Thread Graphs", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var (threadId, entry) in _profilerThreadCache.OrderBy(static kvp => kvp.Key))
                {
                    var threadSnapshot = entry.Snapshot;
                    bool isStale = nowUtc - entry.LastSeen > ProfilerThreadStaleThreshold;
                    float totalTimeMs = threadSnapshot?.TotalTimeMs ?? 0f;
                    string headerLabel = $"Thread {threadId} ({totalTimeMs:F3} ms)";
                    if (isStale)
                        headerLabel += " (inactive)";

                    if (isStale)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));

                    ImGui.Text(headerLabel);

                    if (isStale)
                        ImGui.PopStyleColor();

                    float[] samplesToPlot = Array.Empty<float>();
                    if (history.TryGetValue(threadId, out var samples) && samples.Length > 0)
                    {
                        if (entry.Samples.Length != samples.Length)
                            entry.Samples = new float[samples.Length];
                        Array.Copy(samples, entry.Samples, samples.Length);
                        samplesToPlot = entry.Samples;
                    }
                    else if (entry.Samples.Length > 0)
                    {
                        samplesToPlot = entry.Samples;
                    }

                    if (samplesToPlot.Length > 0)
                    {
                        float min = samplesToPlot.Min();
                        float max = samplesToPlot.Max();
                        if (!float.IsFinite(min) || !float.IsFinite(max))
                        {
                            min = 0.0f;
                            max = 0.0f;
                        }
                        if (MathF.Abs(max - min) < 0.001f)
                            max = min + 0.001f;

                        ImGui.PlotLines($"##ProfilerThreadPlot{threadId}", ref samplesToPlot[0], samplesToPlot.Length, 0, null, min, max, new Vector2(-1.0f, 40.0f));
                    }
                }
            }

            ImGui.Separator();
            ImGui.Text("Hierarchy");

            if (ImGui.BeginTable("ProfilerHierarchies", Math.Max(1, _profilerThreadCache.Count), ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX))
            {
                foreach (var (threadId, entry) in _profilerThreadCache.OrderBy(static kvp => kvp.Key))
                {
                    ImGui.TableSetupColumn($"Thread {threadId}");
                }
                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                foreach (var (threadId, entry) in _profilerThreadCache.OrderBy(static kvp => kvp.Key))
                {
                    ImGui.TableNextColumn();
                    var threadSnapshot = entry.Snapshot;
                    if (threadSnapshot is not null)
                    {
                        if (ImGui.BeginTable($"HierarchyTable_{threadId}", 3, ImGuiTableFlags.BordersInner | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg))
                        {
                            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60f);
                            ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 40f);
                            ImGui.TableHeadersRow();

                            foreach (var root in threadSnapshot.RootNodes)
                                DrawProfilerNode(root, $"T{threadId}");

                            ImGui.EndTable();
                        }
                    }
                }
                ImGui.EndTable();
            }
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

        private static void DrawProfilerNode(Engine.CodeProfiler.ProfilerNodeSnapshot node, string idSuffix)
        {
            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (node.Children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf;

            // Check cache for open state
            string nodeKey = $"{node.Name}_{idSuffix}";
            if (_profilerNodeOpenCache.TryGetValue(nodeKey, out bool isOpen) && isOpen)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            bool nodeOpen = ImGui.TreeNodeEx($"{node.Name}##{idSuffix}", flags);
            if (ImGui.IsItemToggledOpen())
                _profilerNodeOpenCache[nodeKey] = nodeOpen;

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{node.ElapsedMs:F3}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text("1"); // Calls not available in snapshot

            if (nodeOpen)
            {
                var children = node.Children;
                if (_profilerSortByTime)
                    children = children.OrderByDescending(c => c.ElapsedMs).ToList();

                foreach (var child in children)
                    DrawProfilerNode(child, idSuffix + "_" + node.Name);
                ImGui.TreePop();
            }
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
