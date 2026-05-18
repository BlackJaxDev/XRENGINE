using ImGuiNET;
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Numerics;
using XREngine;
using XREngine.Core.Files;
using XREngine.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private static string? _selectedMissingAssetKey;
        private static string _missingAssetReplacementPath = string.Empty;
        private static string? _autoRepairSaveStatus;
        private static bool _autoRepairSaveSucceeded;
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

            DrawRebasedAssetsSection();

            var missingAssets = AssetDiagnostics.GetTrackedMissingAssets();
            if (missingAssets.Count == 0)
            {
                ImGui.TextDisabled("No fully missing asset references have been tracked.");
                if (ImGui.Button("Clear Missing Asset Log"))
                    AssetDiagnostics.ClearTrackedMissingAssets();
                ClearMissingAssetSelection();
                return;
            }

            int totalHits = 0;
            foreach (var info in missingAssets)
                totalHits += info.Count;

            ImGui.TextUnformatted($"Fully missing references: {missingAssets.Count} | Hits: {totalHits}");

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

            ImGui.TextDisabled("Status: Fully missing - no readable file was found for this reference.");
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

        private static void DrawRebasedAssetsSection()
        {
            var rebased = AssetDiagnostics.GetTrackedRebasedAssets();
            if (rebased.Count == 0)
                return;

            if (!ImGui.CollapsingHeader($"Auto-Repaired Asset References ({rebased.Count})", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            int portableCount = 0;
            int foundPathCount = 0;
            foreach (var info in rebased)
            {
                if (info.RepairKind == AssetDiagnostics.AssetReferenceRepairKind.PathMadePortable)
                    portableCount++;
                else if (info.RepairKind == AssetDiagnostics.AssetReferenceRepairKind.FoundCurrentWorkspacePath)
                    foundPathCount++;
            }

            ImGui.TextWrapped("These references loaded successfully after auto-repair. Save them now to write the corrected portable references and avoid seeing this dialog for the same fixes next load.");
            ImGui.TextDisabled($"Path made portable: {portableCount} | Found current path: {foundPathCount} | Auto-repaired rows still missing: 0");

            var saveTargets = GetAutoRepairedSaveTargets(rebased, out int unsaveableRepairCount);
            bool canSaveRepairs = saveTargets.Count > 0 && unsaveableRepairCount == 0;
            using (new ImGuiDisabledScope(!canSaveRepairs))
            {
                if (ImGui.Button($"Save Auto-Repairs Now ({saveTargets.Count})"))
                    SaveAutoRepairedAssets(saveTargets);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (saveTargets.Count == 0)
                    ImGui.SetTooltip("No loaded owner asset file was found for these repair records.");
                else if (unsaveableRepairCount > 0)
                    ImGui.SetTooltip("Some repair records do not identify a loaded owner asset file to save.");
                else
                    ImGui.SetTooltip("Saves the asset files that contained these repaired references. Other unsaved changes in those files will be saved too.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear Auto-Repaired Log"))
            {
                AssetDiagnostics.ClearTrackedRebasedAssets();
                _autoRepairSaveStatus = null;
            }

            if (!string.IsNullOrWhiteSpace(_autoRepairSaveStatus))
            {
                Vector4 color = _autoRepairSaveSucceeded
                    ? new Vector4(0.55f, 0.85f, 0.55f, 1.0f)
                    : new Vector4(0.95f, 0.55f, 0.35f, 1.0f);
                ImGui.TextColored(color, _autoRepairSaveStatus);
            }

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            float height = MathF.Min(220.0f, MathF.Max(80.0f, 22.0f + rebased.Count * 22.0f));
            if (ImGui.BeginTable("RebasedAssetTable", 7, tableFlags, new Vector2(-1.0f, height)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Fix", ImGuiTableColumnFlags.WidthFixed, 150.0f);
                ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                ImGui.TableSetupColumn("Original Path", ImGuiTableColumnFlags.WidthStretch, 0.32f);
                ImGui.TableSetupColumn("Resolved Path", ImGuiTableColumnFlags.WidthStretch, 0.32f);
                ImGui.TableSetupColumn("Saved In", ImGuiTableColumnFlags.WidthStretch, 0.22f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                ImGui.TableHeadersRow();

                foreach (var info in rebased.OrderByDescending(static i => i.LastSeenUtc))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(GetAutoRepairKindLabel(info.RepairKind));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(GetAutoRepairKindDescription(info.RepairKind));

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(info.Category);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(info.OriginalPath);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(info.OriginalPath);

                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(info.ResolvedPath);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(info.ResolvedPath);
                        if (!string.IsNullOrWhiteSpace(info.LastContext))
                        {
                            ImGui.Separator();
                            ImGui.TextUnformatted(info.LastContext);
                        }
                        ImGui.EndTooltip();
                    }

                    ImGui.TableSetColumnIndex(4);
                    string saveTargetLabel = GetAutoRepairSaveTargetLabel(info);
                    ImGui.TextUnformatted(saveTargetLabel);
                    if (ImGui.IsItemHovered())
                        DrawAutoRepairSaveTargetTooltip(info);

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextUnformatted(info.Count.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableSetColumnIndex(6);
                    ImGui.TextUnformatted(info.LastSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                }

                ImGui.EndTable();
            }

            ImGui.Separator();
        }

        private static string GetAutoRepairKindLabel(AssetDiagnostics.AssetReferenceRepairKind repairKind)
            => repairKind switch
            {
                AssetDiagnostics.AssetReferenceRepairKind.PathMadePortable => "Path made portable",
                AssetDiagnostics.AssetReferenceRepairKind.FoundCurrentWorkspacePath => "Found current path",
                _ => "Auto-repaired",
            };

        private static string GetAutoRepairKindDescription(AssetDiagnostics.AssetReferenceRepairKind repairKind)
            => repairKind switch
            {
                AssetDiagnostics.AssetReferenceRepairKind.PathMadePortable => "The absolute path existed, so the loaded reference is valid and can be saved as a workspace-portable path.",
                AssetDiagnostics.AssetReferenceRepairKind.FoundCurrentWorkspacePath => "The original absolute path was stale, but the same asset was found under the current workspace asset roots.",
                _ => "The reference loaded after repair, but the specific repair category was not recorded.",
            };

        private static string GetAutoRepairSaveTargetLabel(in AssetDiagnostics.RebasedAssetInfo info)
        {
            if (info.SourceAssetPaths.Count == 0)
                return "<unknown>";

            string lastPath = info.LastSourceAssetPath ?? info.SourceAssetPaths[0];
            string label = Path.GetFileName(lastPath);
            if (info.SourceAssetPaths.Count <= 1)
                return label;

            return $"{label} (+{info.SourceAssetPaths.Count - 1})";
        }

        private static void DrawAutoRepairSaveTargetTooltip(in AssetDiagnostics.RebasedAssetInfo info)
        {
            ImGui.BeginTooltip();
            if (info.SourceAssetPaths.Count == 0)
            {
                ImGui.TextUnformatted("No owning asset file was recorded for this repair.");
            }
            else
            {
                ImGui.TextUnformatted("Owning asset file(s) to save:");
                foreach (string sourceAssetPath in info.SourceAssetPaths.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
                    ImGui.TextUnformatted(sourceAssetPath);
            }
            ImGui.EndTooltip();
        }

        private static List<XRAsset> GetAutoRepairedSaveTargets(IReadOnlyList<AssetDiagnostics.RebasedAssetInfo> rebased, out int unsaveableRepairCount)
        {
            unsaveableRepairCount = 0;
            var targetsByPath = new Dictionary<string, XRAsset>(StringComparer.OrdinalIgnoreCase);

            foreach (var info in rebased)
            {
                bool foundTargetForRepair = false;
                foreach (string sourceAssetPath in info.SourceAssetPaths)
                {
                    if (!TryGetLoadedAssetForAutoRepair(sourceAssetPath, out XRAsset? asset))
                        continue;

                    if (asset is null)
                        continue;

                    XRAsset root = asset.SourceAsset;
                    if (string.IsNullOrWhiteSpace(root.FilePath))
                        continue;

                    targetsByPath[root.FilePath] = root;
                    foundTargetForRepair = true;
                }

                if (!foundTargetForRepair)
                    unsaveableRepairCount++;
            }

            return targetsByPath.Values
                .OrderBy(static asset => EditorUnitTests.UserInterface.GetAssetDisplayName(asset), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryGetLoadedAssetForAutoRepair(string sourceAssetPath, out XRAsset? asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(sourceAssetPath))
                return false;

            var assets = Engine.Assets;
            if (assets is null)
                return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourceAssetPath);
            }
            catch
            {
                fullPath = sourceAssetPath;
            }

            if (assets.TryGetAssetByPath(fullPath, out asset))
                return true;

            foreach (var loaded in assets.LoadedAssetsByPathInternal.Values)
            {
                if (loaded is null || string.IsNullOrWhiteSpace(loaded.FilePath))
                    continue;

                if (string.Equals(loaded.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    asset = loaded;
                    return true;
                }
            }

            return false;
        }

        private static void SaveAutoRepairedAssets(IReadOnlyList<XRAsset> saveTargets)
        {
            var assets = Engine.Assets;
            if (assets is null)
            {
                _autoRepairSaveSucceeded = false;
                _autoRepairSaveStatus = "Asset system unavailable; auto-repairs were not saved.";
                return;
            }

            int savedCount = 0;
            List<string> failedTargets = [];
            foreach (XRAsset target in saveTargets)
            {
                try
                {
                    assets.Save(target, bypassJobThread: true);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    failedTargets.Add(EditorUnitTests.UserInterface.GetAssetDisplayName(target));
                    Debug.LogException(ex, $"Failed to save auto-repaired asset '{target.FilePath ?? target.Name}'.");
                }
            }

            if (failedTargets.Count == 0)
            {
                AssetDiagnostics.ClearTrackedRebasedAssets();
                _autoRepairSaveSucceeded = true;
                _autoRepairSaveStatus = savedCount == 1
                    ? "Saved 1 auto-repaired asset file."
                    : $"Saved {savedCount} auto-repaired asset files.";
                return;
            }

            _autoRepairSaveSucceeded = false;
            _autoRepairSaveStatus = $"Saved {savedCount}; failed {failedTargets.Count}: {string.Join(", ", failedTargets)}";
        }
}
