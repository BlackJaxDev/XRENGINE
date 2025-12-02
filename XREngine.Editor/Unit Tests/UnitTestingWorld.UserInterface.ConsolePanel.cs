using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static bool _showConsole;
        private static ELogCategory _consoleSelectedTab = ELogCategory.General;
        private static bool _consoleAutoScroll = true;
        private static string _consoleFilter = string.Empty;
        private static readonly byte[] _consoleFilterBuffer = new byte[256];
        private static List<LogEntry> _consoleCachedEntries = new();
        private static DateTime _consoleLastRefresh = DateTime.MinValue;
        private static readonly TimeSpan _consoleRefreshInterval = TimeSpan.FromMilliseconds(250);

        private static void DrawConsolePanel()
        {
            if (!_showConsole) return;
            if (!ImGui.Begin("Console", ref _showConsole, ImGuiWindowFlags.MenuBar))
            {
                ImGui.End();
                return;
            }

            DrawConsoleMenuBar();
            DrawConsoleTabs();
            ImGui.End();
        }

        private static void DrawConsoleMenuBar()
        {
            if (ImGui.BeginMenuBar())
            {
                if (ImGui.MenuItem("Clear"))
                {
                    Debug.ClearConsoleEntries();
                    _consoleCachedEntries.Clear();
                }
                if (ImGui.MenuItem("Clear Category"))
                {
                    Debug.ClearConsoleEntries(_consoleSelectedTab);
                    RefreshConsoleCache();
                }
                ImGui.Separator();
                ImGui.Checkbox("Auto-scroll", ref _consoleAutoScroll);
                ImGui.EndMenuBar();
            }
        }

        private static void DrawConsoleTabs()
        {
            if (ImGui.BeginTabBar("ConsoleTabs"))
            {
                DrawConsoleTab("All", null);
                DrawConsoleTab("General", ELogCategory.General);
                DrawConsoleTab("Rendering", ELogCategory.Rendering);
                DrawConsoleTab("OpenGL", ELogCategory.OpenGL);
                DrawConsoleTab("Physics", ELogCategory.Physics);
                ImGui.EndTabBar();
            }
        }

        private static void DrawConsoleTab(string label, ELogCategory? category)
        {
            if (ImGui.BeginTabItem(label))
            {
                if (category.HasValue)
                    _consoleSelectedTab = category.Value;

                DrawConsoleFilterBar();
                DrawConsoleContent(category);
                ImGui.EndTabItem();
            }
        }

        private static void DrawConsoleFilterBar()
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("Filter", _consoleFilterBuffer, (uint)_consoleFilterBuffer.Length))
            {
                _consoleFilter = System.Text.Encoding.UTF8.GetString(_consoleFilterBuffer).TrimEnd('\0');
            }
            ImGui.SameLine();
            if (ImGui.Button("X##ClearFilter"))
            {
                _consoleFilter = string.Empty;
                Array.Clear(_consoleFilterBuffer, 0, _consoleFilterBuffer.Length);
            }
        }

        private static void DrawConsoleContent(ELogCategory? category)
        {
            RefreshConsoleCacheIfNeeded();

            var entries = _consoleCachedEntries;
            float footerHeight = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing();
            
            if (ImGui.BeginChild("ConsoleScrollRegion", new Vector2(0, -footerHeight), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            {
                bool hasFilter = !string.IsNullOrWhiteSpace(_consoleFilter);
                int displayedCount = 0;

                foreach (var entry in entries)
                {
                    if (category.HasValue && entry.Category != category.Value)
                        continue;

                    if (hasFilter && !entry.Message.Contains(_consoleFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DrawConsoleEntry(entry);
                    displayedCount++;
                }

                if (displayedCount == 0)
                {
                    ImGui.TextDisabled("No log entries.");
                }

                if (_consoleAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();

            // Footer with entry count
            int totalCount = entries.Count;
            int filteredCount = 0;
            foreach (var entry in entries)
            {
                if (category.HasValue && entry.Category != category.Value)
                    continue;
                if (!string.IsNullOrWhiteSpace(_consoleFilter) && !entry.Message.Contains(_consoleFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                filteredCount++;
            }

            if (category.HasValue || !string.IsNullOrWhiteSpace(_consoleFilter))
                ImGui.Text($"{filteredCount} / {totalCount} entries");
            else
                ImGui.Text($"{totalCount} entries");
        }

        private static void DrawConsoleEntry(LogEntry entry)
        {
            Vector4 color = GetCategoryColor(entry.Category);
            string prefix = GetCategoryPrefix(entry.Category);
            string timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            
            // Show repeat count if message repeated
            string repeatSuffix = entry.RepeatCount > 1 ? $" (x{entry.RepeatCount})" : "";
            
            // Wrap long messages
            ImGui.TextWrapped($"[{timestamp}] {prefix} {entry.Message}{repeatSuffix}");
            
            ImGui.PopStyleColor();

            // Context menu for copying
            if (ImGui.BeginPopupContextItem($"ConsoleEntry_{entry.GetHashCode()}"))
            {
                if (ImGui.MenuItem("Copy Message"))
                    ImGui.SetClipboardText(entry.Message);
                if (ImGui.MenuItem("Copy Full Entry"))
                    ImGui.SetClipboardText($"[{timestamp}] {prefix} {entry.Message}");
                ImGui.EndPopup();
            }
        }

        private static Vector4 GetCategoryColor(ELogCategory category)
        {
            return category switch
            {
                ELogCategory.General => new Vector4(0.9f, 0.9f, 0.9f, 1.0f),   // White/Gray
                ELogCategory.Rendering => new Vector4(0.4f, 0.8f, 1.0f, 1.0f), // Light Blue
                ELogCategory.OpenGL => new Vector4(0.4f, 1.0f, 0.4f, 1.0f),    // Light Green
                ELogCategory.Physics => new Vector4(1.0f, 0.8f, 0.4f, 1.0f),   // Orange
                _ => new Vector4(0.9f, 0.9f, 0.9f, 1.0f),
            };
        }

        private static string GetCategoryPrefix(ELogCategory category)
        {
            return category switch
            {
                ELogCategory.General => "[General]",
                ELogCategory.Rendering => "[Render]",
                ELogCategory.OpenGL => "[OpenGL]",
                ELogCategory.Physics => "[Physics]",
                _ => "[Unknown]",
            };
        }

        private static void RefreshConsoleCacheIfNeeded()
        {
            var now = DateTime.UtcNow;
            if (now - _consoleLastRefresh >= _consoleRefreshInterval)
            {
                RefreshConsoleCache();
                _consoleLastRefresh = now;
            }
        }

        private static void RefreshConsoleCache()
        {
            _consoleCachedEntries = Debug.GetConsoleEntries();
        }
    }
}
