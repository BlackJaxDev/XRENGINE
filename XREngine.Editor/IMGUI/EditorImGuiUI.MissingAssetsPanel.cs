using ImGuiNET;
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Numerics;
using XREngine;
using XREngine.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private static string? _selectedMissingAssetKey;
        private static string _missingAssetReplacementPath = string.Empty;
        private const float MissingAssetEditorMinHeight = 140.0f;
        private const float MissingAssetListMinHeight = 110.0f;

        private static void DrawMissingAssetsPanel()
        {
            if (!_showMissingAssets) return;
            if (!ImGui.Begin("Missing Assets", ref _showMissingAssets))
            {
                ImGui.End();
                return;
            }
            DrawMissingAssetsTabContent();
            ImGui.End();
        }

        private static void DrawMissingAssetsTabContent()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawMissingAssetsTabContent");

            var missingAssets = AssetDiagnostics.GetTrackedMissingAssets();
            if (missingAssets.Count == 0)
            {
                ImGui.TextDisabled("No missing assets have been tracked.");
                if (ImGui.Button("Clear Missing Asset Log"))
                    AssetDiagnostics.ClearTrackedMissingAssets();
                ClearMissingAssetSelection();
                return;
            }

            int totalHits = 0;
            foreach (var info in missingAssets)
                totalHits += info.Count;

            ImGui.TextUnformatted($"Entries: {missingAssets.Count} | Hits: {totalHits}");

            if (ImGui.Button("Clear Missing Asset Log"))
            {
                AssetDiagnostics.ClearTrackedMissingAssets();
                ClearMissingAssetSelection();
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Sorted by most recent");

            var ordered = missingAssets.OrderByDescending(static i => i.LastSeenUtc).ToList();

            AssetDiagnostics.MissingAssetInfo selectedInfo = default;
            bool hasSelectedInfo = false;
            if (!string.IsNullOrEmpty(_selectedMissingAssetKey))
            {
                foreach (var info in ordered)
                {
                    if (!string.Equals(_selectedMissingAssetKey, BuildMissingAssetSelectionKey(info.AssetPath, info.Category), StringComparison.Ordinal))
                        continue;

                    selectedInfo = info;
                    hasSelectedInfo = true;
                    break;
                }

                if (!hasSelectedInfo)
                    ClearMissingAssetSelection();
            }

            float availableHeight = MathF.Max(ImGui.GetContentRegionAvail().Y, MissingAssetListMinHeight + MissingAssetEditorMinHeight);
            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float editorHeight = hasSelectedInfo ? MathF.Max(MissingAssetEditorMinHeight, availableHeight * 0.35f) : 0.0f;
            float listHeight = hasSelectedInfo
                ? MathF.Max(MissingAssetListMinHeight, availableHeight - editorHeight - spacing)
                : availableHeight;

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;

            bool selectionFoundThisFrame = false;

            if (ImGui.BeginChild("MissingAssetList", new Vector2(-1.0f, listHeight), ImGuiChildFlags.Border))
            {
                if (ImGui.BeginTable("ProfilerMissingAssetTable", 6, tableFlags, new Vector2(-1.0f, -1.0f)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                    ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("Last Context", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                    ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                    ImGui.TableSetupColumn("First Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                    ImGui.TableHeadersRow();

                    int rowIndex = 0;
                    foreach (var info in ordered)
                    {
                        string rowKey = BuildMissingAssetSelectionKey(info.AssetPath, info.Category);
                        bool isSelected = !string.IsNullOrEmpty(_selectedMissingAssetKey)
                            && string.Equals(_selectedMissingAssetKey, rowKey, StringComparison.Ordinal);

                        if (isSelected)
                        {
                            selectedInfo = info;
                            selectionFoundThisFrame = true;
                        }

                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        ImGui.PushID(rowIndex);
                        string label = $"{info.Category}##MissingAssetRow";
                        if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                        {
                            if (!string.Equals(_selectedMissingAssetKey, rowKey, StringComparison.Ordinal))
                            {
                                _missingAssetReplacementPath = File.Exists(info.AssetPath)
                                    ? info.AssetPath
                                    : string.Empty;
                            }

                            _selectedMissingAssetKey = rowKey;
                            selectedInfo = info;
                            selectionFoundThisFrame = true;
                            isSelected = true;
                        }
                        ImGui.PopID();

                        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            RevealMissingAssetLocation(info.AssetPath);

                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(info.AssetPath);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(info.AssetPath);

                        ImGui.TableSetColumnIndex(2);
                        ImGui.TextUnformatted(info.Count.ToString(CultureInfo.InvariantCulture));

                        ImGui.TableSetColumnIndex(3);
                        string contextLabel = string.IsNullOrWhiteSpace(info.LastContext) ? "<none>" : info.LastContext;
                        ImGui.TextUnformatted(contextLabel);
                        if (info.Contexts.Count > 1 && ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted("Contexts:");
                            foreach (var ctx in info.Contexts.OrderBy(static c => c))
                                ImGui.TextUnformatted(ctx);
                            ImGui.EndTooltip();
                        }

                        ImGui.TableSetColumnIndex(4);
                        ImGui.TextUnformatted(info.LastSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                        ImGui.TableSetColumnIndex(5);
                        ImGui.TextUnformatted(info.FirstSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                        rowIndex++;
                    }

                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }

            if (!selectionFoundThisFrame && !string.IsNullOrEmpty(_selectedMissingAssetKey))
            {
                ClearMissingAssetSelection();
                hasSelectedInfo = false;
            }
            else
            {
                hasSelectedInfo = selectionFoundThisFrame;
            }

            if (hasSelectedInfo && editorHeight > 0.0f)
            {
                ImGui.Dummy(new Vector2(0.0f, spacing));
                if (ImGui.BeginChild("MissingAssetEditor", new Vector2(-1.0f, editorHeight), ImGuiChildFlags.Border))
                {
                    DrawMissingAssetReplacementEditor(selectedInfo);
                    ImGui.EndChild();
                }
            }
        }

        private static string BuildMissingAssetSelectionKey(string assetPath, string category)
        {
            string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
            string normalizedPath = string.IsNullOrWhiteSpace(assetPath) ? string.Empty : assetPath;
            return string.Concat(normalizedCategory, "::", normalizedPath);
        }

        private static void ClearMissingAssetSelection()
        {
            _selectedMissingAssetKey = null;
            _missingAssetReplacementPath = string.Empty;
        }

        private static void DrawMissingAssetReplacementEditor(in AssetDiagnostics.MissingAssetInfo info)
        {
            ImGui.TextUnformatted("Selected Missing Asset");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reveal"))
                RevealMissingAssetLocation(info.AssetPath);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path"))
                ImGui.SetClipboardText(info.AssetPath);

            ImGui.Separator();

            ImGui.TextUnformatted($"Category: {info.Category}");
            ImGui.TextUnformatted($"Hits: {info.Count}");
            ImGui.TextUnformatted($"Last Seen: {info.LastSeenUtc.ToLocalTime():g}");
            ImGui.TextUnformatted($"First Seen: {info.FirstSeenUtc.ToLocalTime():g}");

            ImGui.Spacing();

            ImGui.TextUnformatted("Contexts:");
            if (info.Contexts.Count == 0)
            {
                ImGui.TextDisabled("<none>");
            }
            else
            {
                foreach (var ctx in info.Contexts.OrderBy(static c => c))
                    ImGui.BulletText(ctx);
            }

            ImGui.Spacing();

            string replacement = _missingAssetReplacementPath;
            if (ImGui.InputTextWithHint("##MissingAssetReplacement", "Replacement path...", ref replacement, 512u))
                _missingAssetReplacementPath = replacement.Trim();

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload(ImGuiAssetUtilities.AssetPayloadType);
                if (payload.Data != IntPtr.Zero && payload.DataSize > 0)
                {
                    string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
                    if (!string.IsNullOrEmpty(path))
                        _missingAssetReplacementPath = path;
                }
                ImGui.EndDragDropTarget();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Use Missing Path"))
                _missingAssetReplacementPath = info.AssetPath;

            ImGui.Spacing();

            bool hasReplacement = !string.IsNullOrWhiteSpace(_missingAssetReplacementPath);
            using (new ImGuiDisabledScope(!hasReplacement))
            {
                if (ImGui.Button("Copy Replacement File"))
                {
                    if (hasReplacement && TryCopyMissingAssetReplacement(_missingAssetReplacementPath, info.AssetPath))
                    {
                        if (AssetDiagnostics.RemoveTrackedMissingAsset(info.AssetPath, info.Category))
                            Debug.Out($"Replaced missing asset '{info.AssetPath}'.");
                        ClearMissingAssetSelection();
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Mark Resolved"))
            {
                if (AssetDiagnostics.RemoveTrackedMissingAsset(info.AssetPath, info.Category))
                {
                    Debug.Out($"Removed missing asset '{info.AssetPath}' from diagnostics.");
                    ClearMissingAssetSelection();
                }
                else
                {
                    Debug.LogWarning($"Failed to remove missing asset '{info.AssetPath}'.");
                }
            }
        }

        private static bool TryCopyMissingAssetReplacement(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                Debug.LogWarning("No replacement file selected.");
                return false;
            }

            if (!File.Exists(sourcePath))
            {
                Debug.LogWarning($"Replacement file '{sourcePath}' does not exist.");
                return false;
            }

            try
            {
                string? directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.Copy(sourcePath, destinationPath, true);
                Debug.Out($"Copied replacement file '{sourcePath}' to '{destinationPath}'.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to copy replacement file to '{destinationPath}'.");
                return false;
            }
        }

        private static void RevealMissingAssetLocation(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            try
            {
                if (File.Exists(assetPath))
                {
                    OpenPathInExplorer(assetPath, false);
                    return;
                }

                string? directory = Path.GetDirectoryName(assetPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    OpenPathInExplorer(directory, true);
                }
                else
                {
                    Debug.LogWarning($"Directory for missing asset '{assetPath}' could not be located.");
                }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"Failed to reveal location for '{assetPath}'.");
        }
    }
}
