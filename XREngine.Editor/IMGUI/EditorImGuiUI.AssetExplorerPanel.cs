using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;
using YamlDotNet.RepresentationModel;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private const float AssetExplorerTileBaseEdge = 112.0f;
        private const float AssetExplorerTileMinEdge = 48.0f;
        private const float AssetExplorerTileMaxEdge = 320.0f;
        private const float AssetExplorerTilePadding = 8.0f;
        private const float AssetExplorerPreviewFallbackEdge = 64.0f;

        private static bool _assetExplorerShowDirectories = true;
        private static bool _assetExplorerShowFiles = true;
        private static string _assetExplorerExtensionFilter = string.Empty;
        private static readonly HashSet<string> _assetExplorerExtensionFilterSet = new(StringComparer.OrdinalIgnoreCase);
        private static bool _assetExplorerExtensionFilterHasWildcard;
        private static readonly Dictionary<string, AssetTypeDescriptor?> _assetExplorerAssetTypeCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> _assetExplorerYamlKeyCache = new(StringComparer.OrdinalIgnoreCase);
        private static MethodInfo? _assetManagerLoadMethod;

        private static partial void DrawAssetExplorerPanel()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawAssetExplorerPanel");
            if (!_showAssetExplorer) return;

            if (!ImGui.Begin("Assets", ref _showAssetExplorer))
            {
                ImGui.End();
                return;
            }

            var viewport = ImGui.GetMainViewport();
            const float minHeight = 220.0f;
            const float reservedVerticalMargin = 110.0f;

            DrawAssetExplorerHeader(viewport, false, false, false, false, minHeight, reservedVerticalMargin);

            if (ImGui.BeginTabBar("AssetExplorerTabs"))
            {
                if (ImGui.BeginTabItem("Game Project"))
                {
                    DrawAssetExplorerTab(_assetExplorerGameState, Engine.Assets.GameAssetsPath);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Engine Common"))
                {
                    DrawAssetExplorerTab(_assetExplorerEngineState, Engine.Assets.EngineAssetsPath);
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        private static partial void DrawAssetExplorerHeader(ImGuiViewportPtr viewport, bool headerAtBottom, bool dockedTop, bool dockedBottom, bool isDocked, float minHeight, float reservedVerticalMargin)
        {
            ImGui.PushID(headerAtBottom ? "AssetExplorerHeaderBottom" : "AssetExplorerHeaderTop");

            EnsureAssetExplorerCategoryFilters();

            if (headerAtBottom)
                ImGui.Separator();

            ImGui.TextUnformatted("Assets");

            ImGui.SameLine(0f, 6f);
            ImGui.SetNextItemWidth(240.0f);
            if (ImGui.InputTextWithHint("##AssetExplorerSearch", "Search...", ref _assetExplorerSearchTerm, 256u))
                _assetExplorerSearchTerm = _assetExplorerSearchTerm.Trim();

            ImGui.SameLine(0f, 6f);
            ImGui.SetNextItemWidth(160.0f);
            if (ImGui.BeginCombo("##AssetExplorerSearchScope", GetAssetExplorerSearchScopeLabel()))
            {
                bool scopeName = _assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Name);
                if (ImGui.Checkbox("Name", ref scopeName))
                    SetAssetExplorerSearchScopeFlag(AssetExplorerSearchScope.Name, scopeName);

                bool scopePath = _assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Path);
                if (ImGui.Checkbox("Path", ref scopePath))
                    SetAssetExplorerSearchScopeFlag(AssetExplorerSearchScope.Path, scopePath);

                bool scopeMetadata = _assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Metadata);
                if (ImGui.Checkbox("Metadata", ref scopeMetadata))
                    SetAssetExplorerSearchScopeFlag(AssetExplorerSearchScope.Metadata, scopeMetadata);

                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Choose where the search term applies. Multiple options can be enabled.");

            ImGui.SameLine(0f, 6f);
            bool matchCase = _assetExplorerSearchCaseSensitive;
            if (ImGui.Checkbox("Match Case", ref matchCase))
                _assetExplorerSearchCaseSensitive = matchCase;

            ImGui.SameLine(0f, 6f);
            ImGui.SetNextItemWidth(180.0f);
            if (ImGui.InputTextWithHint("##AssetExplorerExtensionFilter", "Extensions (.asset;png)", ref _assetExplorerExtensionFilter, 128u))
            {
                _assetExplorerExtensionFilter = _assetExplorerExtensionFilter.Trim();
                UpdateAssetExplorerExtensionFilterSet();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Filter files by extension. Separate values with commas or semicolons. Use '*' for all extensions.");

            ImGui.SameLine(0f, 6f);
            if (ImGui.BeginCombo("##AssetExplorerCategoryFilter", _assetExplorerCategoryFilterLabel))
            {
                bool changed = false;
                bool allSelected = AreAllAssetExplorerCategoriesSelected();
                bool anySelected = AreAnyAssetExplorerCategoriesSelected();

                if (ImGui.MenuItem("Select All", null, false, !allSelected))
                {
                    SetAllAssetExplorerCategorySelections(true);
                    changed = true;
                }

                if (ImGui.MenuItem("Select None", null, false, anySelected))
                {
                    SetAllAssetExplorerCategorySelections(false);
                    changed = true;
                }

                if (_assetExplorerCategoryFilterOrder.Count > 0)
                    ImGui.Separator();

                foreach (var category in _assetExplorerCategoryFilterOrder)
                {
                    bool selected = _assetExplorerCategoryFilterSelections.TryGetValue(category, out bool current) ? current : true;
                    if (ImGui.Checkbox(category, ref selected))
                    {
                        _assetExplorerCategoryFilterSelections[category] = selected;
                        changed = true;
                    }
                }

                if (changed)
                    UpdateAssetExplorerCategoryFilterLabel();

                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Limit visible files by asset category.");

            ImGui.SameLine(0f, 6f);
            bool showFiles = _assetExplorerShowFiles;
            if (ImGui.Checkbox("Files", ref showFiles))
                _assetExplorerShowFiles = showFiles;

            ImGui.SameLine(0f, 6f);
            bool showDirectories = _assetExplorerShowDirectories;
            if (ImGui.Checkbox("Directories", ref showDirectories))
                _assetExplorerShowDirectories = showDirectories;

            ImGui.SameLine(0f, 6f);
            ImGui.TextDisabled("Filters apply to the current directory.");

            if (!headerAtBottom)
                ImGui.Separator();

            ImGui.PopID();
        }

        private static partial void DrawAssetExplorerTab(AssetExplorerTabState state, string rootPath)
        {
            using var profilerScope = Engine.Profiler.Start($"UI.DrawAssetExplorerTab.{state.Id}");
            EnsureAssetExplorerState(state, rootPath);

            if (string.IsNullOrEmpty(state.RootPath))
            {
                ImGui.TextDisabled($"Directory not found: {rootPath}");
                return;
            }

            Vector2 contentRegion = ImGui.GetContentRegionAvail();
            float directoryPaneWidth = Math.Clamp(contentRegion.X * 0.32f, 220.0f, 360.0f);

            if (ImGui.BeginChild($"{state.Id}DirectoryPane", new Vector2(directoryPaneWidth, 0f), ImGuiChildFlags.Border))
            {
                DrawAssetExplorerDirectoryTree(state, state.RootPath);
            }
            ImGui.EndChild();

            ImGui.SameLine();

            if (ImGui.BeginChild($"{state.Id}FilePane", Vector2.Zero, ImGuiChildFlags.Border))
            {
                DrawAssetExplorerFileList(state);
            }
            ImGui.EndChild();
        }

        private static partial void DrawAssetExplorerDirectoryTree(AssetExplorerTabState state, string rootPath)
        {
            using var profilerScope = Engine.Profiler.Start($"UI.AssetExplorer.DirectoryTree.{state.Id}");
            ImGui.PushID(state.Id);

            ImGuiTreeNodeFlags rootFlags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
            if (string.Equals(state.CurrentDirectory, rootPath, StringComparison.OrdinalIgnoreCase))
                rootFlags |= ImGuiTreeNodeFlags.Selected;

            bool rootOpen = ImGui.TreeNodeEx(state.DisplayName, rootFlags);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                state.CurrentDirectory = rootPath;
                ClearAssetExplorerSelection(state);
                CancelAssetExplorerRename(state);
            }

            if (rootOpen)
            {
                DrawAssetExplorerDirectoryChildren(state, rootPath);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        private static partial void DrawAssetExplorerDirectoryChildren(AssetExplorerTabState state, string directory)
        {
            using var profilerScope = Engine.Profiler.Start("UI.AssetExplorer.DirectoryChildren");
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                return;
            }

            Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);

            foreach (var subdir in subdirectories)
            {
                string name = Path.GetFileName(subdir) ?? subdir;
                string normalized = NormalizeAssetExplorerPath(subdir);
                bool hasChildren = DirectoryHasChildren(normalized);

                ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
                if (!hasChildren)
                    flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.Bullet;

                if (string.Equals(state.CurrentDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                    flags |= ImGuiTreeNodeFlags.Selected;

                string childPrefix = normalized + Path.DirectorySeparatorChar;
                if (state.CurrentDirectory.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                    ImGui.SetNextItemOpen(true, ImGuiCond.Once);

                ImGui.PushID(normalized);
                bool open = ImGui.TreeNodeEx(name, flags);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    state.CurrentDirectory = normalized;
                    ClearAssetExplorerSelection(state);
                    CancelAssetExplorerRename(state);
                }

                if (open && hasChildren)
                {
                    DrawAssetExplorerDirectoryChildren(state, normalized);
                    ImGui.TreePop();
                }

                ImGui.PopID();
            }
        }

        private static partial void DrawAssetExplorerFileList(AssetExplorerTabState state)
        {
            using var profilerScope = Engine.Profiler.Start($"UI.AssetExplorer.FileList.{state.Id}");
            string directory = Directory.Exists(state.CurrentDirectory) ? state.CurrentDirectory : state.RootPath;
            directory = NormalizeAssetExplorerPath(directory);
            if (!string.Equals(state.CurrentDirectory, directory, StringComparison.OrdinalIgnoreCase))
                state.CurrentDirectory = directory;

            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(state.RootPath, directory);
            }
            catch
            {
                relativePath = directory;
            }

            if (string.Equals(relativePath, ".", StringComparison.Ordinal))
                relativePath = state.DisplayName;

            ImGui.TextUnformatted($"Directory: {relativePath}");

            bool directoryChanged = false;
            if (!string.Equals(directory, state.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Up##{state.Id}"))
                {
                    string? parent = Path.GetDirectoryName(directory);
                    if (!string.IsNullOrEmpty(parent) && parent.StartsWith(state.RootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        state.CurrentDirectory = NormalizeAssetExplorerPath(parent);
                            ClearAssetExplorerSelection(state);
                        CancelAssetExplorerRename(state);
                        directoryChanged = true;
                    }
                }
            }

            if (directoryChanged)
                return;

            ImGui.SameLine();
            bool useTileView = state.UseTileView;
            if (ImGui.Checkbox($"Tile View##{state.Id}", ref useTileView))
                state.UseTileView = useTileView;

            if (state.UseTileView)
            {
                ImGui.SameLine();
                float scale = state.TileViewScale;
                ImGui.SetNextItemWidth(120.0f);
                if (ImGui.SliderFloat($"Size##{state.Id}", ref scale, 0.5f, 3.0f, "%.1fx"))
                    state.TileViewScale = Math.Clamp(scale, 0.5f, 3.0f);
            }

            ImGui.Separator();

            _assetExplorerScratchEntries.Clear();

            EnsureAssetExplorerCategoryFilters();
            bool descriptorNeeded = AssetExplorerFiltersNeedDescriptor();

            try
            {
                if (_assetExplorerShowDirectories)
                {
                    foreach (var subdir in Directory.GetDirectories(directory))
                    {
                        string normalized = NormalizeAssetExplorerPath(subdir);
                        string name = Path.GetFileName(normalized) ?? normalized;

                        if (!MatchesAssetExplorerSearch(normalized, name, true, null))
                            continue;

                        if (!MatchesAssetExplorerFilters(normalized, true, null))
                            continue;

                        DateTime modifiedUtc;
                        try
                        {
                            modifiedUtc = Directory.GetLastWriteTimeUtc(subdir);
                        }
                        catch
                        {
                            modifiedUtc = DateTime.MinValue;
                        }

                        _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, normalized, true, 0L, modifiedUtc));
                    }
                }

                if (_assetExplorerShowFiles)
                {
                    foreach (var file in Directory.GetFiles(directory))
                    {
                        string normalized = NormalizeAssetExplorerPath(file);
                        string name = Path.GetFileName(normalized) ?? normalized;
                        AssetTypeDescriptor? descriptor = descriptorNeeded ? ResolveAssetTypeForPath(normalized) : null;

                        if (!MatchesAssetExplorerSearch(normalized, name, false, descriptor))
                            continue;

                        if (!ShouldIncludeFileByExtension(normalized))
                            continue;

                        if (!MatchesAssetExplorerFilters(normalized, false, descriptor))
                            continue;

                        long size = 0L;
                        DateTime modifiedUtc;
                        try
                        {
                            var info = new FileInfo(file);
                            size = info.Length;
                            modifiedUtc = info.LastWriteTimeUtc;
                        }
                        catch
                        {
                            modifiedUtc = DateTime.MinValue;
                        }

                        _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, normalized, false, size, modifiedUtc));
                    }
                }
            }
            catch (Exception ex)
            {
                ImGui.TextDisabled($"Unable to read '{directory}': {ex.Message}");
                return;
            }

            _assetExplorerScratchEntries.Sort(AssetExplorerEntryComparer.Instance);

            if (_assetExplorerScratchEntries.Count == 0)
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_assetExplorerSearchTerm) ? "Folder is empty." : "No entries match the current filter.");
                return;
            }

            bool changedViaView = state.UseTileView
                ? DrawAssetExplorerTileView(state)
                : DrawAssetExplorerTableView(state);

            if (changedViaView)
                return;

            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && !ImGui.IsAnyItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                RequestAssetExplorerContextPopupForDirectory(state, directory);

            DrawAssetExplorerContextPopup();
            DrawAssetExplorerDeleteConfirmation();

            if (!string.IsNullOrEmpty(state.SelectedPath))
            {
                ImGui.Separator();
                ImGui.TextUnformatted(Path.GetFileName(state.SelectedPath));
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(state.SelectedPath);
                ImGui.PopTextWrapPos();
            }
        }

        private static bool DrawAssetExplorerTableView(AssetExplorerTabState state)
        {
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.BordersOuter;

            Vector2 tableSize = ImGui.GetContentRegionAvail();
            if (!ImGui.BeginTable($"{state.Id}FileTable", 4, tableFlags, tableSize))
                return false;

            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100.0f);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 110.0f);
            ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthFixed, 170.0f);
            ImGui.TableHeadersRow();

            bool directoryChanged = false;

            foreach (var entry in _assetExplorerScratchEntries)
            {
                ImGui.TableNextRow();
                if (DrawAssetExplorerTableRow(state, entry))
                {
                    directoryChanged = true;
                    break;
                }
            }

            ImGui.EndTable();
            return directoryChanged;
        }

        private static bool DrawAssetExplorerTableRow(AssetExplorerTabState state, AssetExplorerEntry entry)
        {
            ImGui.TableSetColumnIndex(0);
            if (DrawAssetExplorerNameCell(state, entry))
                return true;

            ImGui.TableSetColumnIndex(1);
            if (entry.IsDirectory)
            {
                ImGui.TextUnformatted("Directory");
            }
            else
            {
                string extension = Path.GetExtension(entry.Name);
                ImGui.TextUnformatted(string.IsNullOrEmpty(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant());
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(entry.IsDirectory ? "—" : FormatFileSize(entry.Size));

            ImGui.TableSetColumnIndex(3);
            if (entry.ModifiedUtc == DateTime.MinValue)
                ImGui.TextUnformatted("—");
            else
                ImGui.TextUnformatted(entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

            return false;
        }

        private static bool DrawAssetExplorerNameCell(AssetExplorerTabState state, AssetExplorerEntry entry)
        {
            bool isRenaming = string.Equals(state.RenamingPath, entry.Path, StringComparison.OrdinalIgnoreCase);
            bool isFile = !entry.IsDirectory;
            bool isSelected = isFile && string.Equals(state.SelectedPath, entry.Path, StringComparison.OrdinalIgnoreCase);

            if (isRenaming)
            {
                if (isFile)
                    SetAssetExplorerSelection(state, entry.Path, true);

                uint highlight = ImGui.GetColorU32(ImGuiCol.Header);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, highlight);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, highlight);

                DrawAssetExplorerRenameField(state, entry, -1f);

                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    RequestAssetExplorerContextPopup(state, entry);
                }

                return false;
            }

            ImGuiSelectableFlags selectableFlags = ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick;
            string label = entry.IsDirectory ? $"[DIR] {entry.Name}" : entry.Name;
            bool activated = ImGui.Selectable(label, isSelected, selectableFlags);
            bool hovered = ImGui.IsItemHovered();

            if (hovered)
                ImGui.SetTooltip(entry.Path);

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                RequestAssetExplorerContextPopup(state, entry);
            }

            if (entry.IsDirectory)
            {
                if (activated || (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)))
                {
                    state.CurrentDirectory = entry.Path;
                    ClearAssetExplorerSelection(state);
                    CancelAssetExplorerRename(state);
                    return true;
                }
            }
            else if (activated)
            {
                SetAssetExplorerSelection(state, entry.Path);
            }

            if (!entry.IsDirectory && hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                HandleAssetExplorerFileActivation(state, entry);
            }

            if (!entry.IsDirectory && ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                ImGuiAssetUtilities.SetPathPayload(entry.Path);
                ImGui.TextUnformatted(entry.Name);
                ImGui.EndDragDropSource();
            }

            return false;
        }

        private static void DrawAssetExplorerRenameField(AssetExplorerTabState state, AssetExplorerEntry entry, float width)
        {
            if (width > 0f)
                ImGui.SetNextItemWidth(width);
            else
                ImGui.SetNextItemWidth(-1f);

            if (state.RenameFocusRequested && _assetExplorerRenameFocusRequested)
            {
                ImGui.SetKeyboardFocusHere();
                state.RenameFocusRequested = false;
                _assetExplorerRenameFocusRequested = false;
            }

            bool submitted = ImGui.InputText("##AssetRename", _assetExplorerRenameBuffer, (uint)_assetExplorerRenameBuffer.Length,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            bool cancel = ImGui.IsKeyPressed(ImGuiKey.Escape);
            bool deactivated = ImGui.IsItemDeactivated();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.Path);

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                RequestAssetExplorerContextPopup(state, entry);
            }

            if (cancel)
            {
                CancelAssetExplorerRename(state);
            }
            else if (submitted || deactivated)
            {
                ApplyAssetExplorerRename(state);
            }
        }

        private static bool DrawAssetExplorerTileView(AssetExplorerTabState state)
        {
            float scale = Math.Clamp(state.TileViewScale, 0.5f, 3.0f);
            if (Math.Abs(scale - state.TileViewScale) > float.Epsilon)
                state.TileViewScale = scale;

            float previewEdge = Math.Clamp(AssetExplorerTileBaseEdge * scale, AssetExplorerTileMinEdge, AssetExplorerTileMaxEdge);
            float padding = AssetExplorerTilePadding;
            float tileWidth = previewEdge + padding * 2f;
            float labelHeight = ImGui.GetTextLineHeightWithSpacing() * 2.0f;
            float tileHeight = previewEdge + padding * 2f + labelHeight;

            float availableWidth = Math.Max(1.0f, ImGui.GetContentRegionAvail().X);
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            int columns = Math.Max(1, (int)MathF.Floor((availableWidth + spacing) / (tileWidth + spacing)));

            int columnIndex = 0;
            for (int i = 0; i < _assetExplorerScratchEntries.Count; i++)
            {
                var entry = _assetExplorerScratchEntries[i];
                if (DrawAssetExplorerTile(state, entry, tileWidth, tileHeight, previewEdge, labelHeight, padding))
                    return true;

                columnIndex++;
                if (columnIndex < columns)
                {
                    ImGui.SameLine(0f, spacing);
                }
                else
                {
                    columnIndex = 0;
                }
            }

            return false;
        }

        private static bool DrawAssetExplorerTile(AssetExplorerTabState state, AssetExplorerEntry entry, float tileWidth, float tileHeight, float previewEdge, float labelHeight, float padding)
        {
            ImGui.PushID(entry.Path);
            ImGui.BeginGroup();

            Vector2 tileSize = new(tileWidth, tileHeight);
            Vector2 tilePos = ImGui.GetCursorScreenPos();

            ImGui.InvisibleButton("##TileHitbox", tileSize);
            bool hovered = ImGui.IsItemHovered();
            bool leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            bool rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            bool doubleClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

            if (hovered)
                ImGui.SetTooltip(entry.Path);

            if (rightClicked)
            {
                RequestAssetExplorerContextPopup(state, entry);
            }

            bool directoryChanged = false;

            if (entry.IsDirectory)
            {
                if (doubleClicked)
                {
                    state.CurrentDirectory = entry.Path;
                    ClearAssetExplorerSelection(state);
                    CancelAssetExplorerRename(state);
                    directoryChanged = true;
                }
            }
            else if (leftClicked)
            {
                Debug.Out($"[AssetExplorer] Left click on file: {entry.Path}");
                SetAssetExplorerSelection(state, entry.Path);
            }
            else if (!entry.IsDirectory && doubleClicked)
            {
                Debug.Out($"[AssetExplorer] Double click on file: {entry.Path}");
                HandleAssetExplorerFileActivation(state, entry);
            }

            if (!entry.IsDirectory && hovered && ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                ImGuiAssetUtilities.SetPathPayload(entry.Path);
                ImGui.TextUnformatted(entry.Name);
                ImGui.EndDragDropSource();
            }

            bool isSelected = !entry.IsDirectory && string.Equals(state.SelectedPath, entry.Path, StringComparison.OrdinalIgnoreCase);
            bool isRenaming = string.Equals(state.RenamingPath, entry.Path, StringComparison.OrdinalIgnoreCase);

            var drawList = ImGui.GetWindowDrawList();
            uint baseColor = ImGui.GetColorU32(isSelected || isRenaming ? ImGuiCol.Header : hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg);
            uint borderColor = ImGui.GetColorU32(ImGuiCol.Border);

            drawList.AddRectFilled(tilePos, tilePos + tileSize, baseColor, 6f);
            drawList.AddRect(tilePos, tilePos + tileSize, borderColor, 6f);

            Vector2 previewSize = new(previewEdge, previewEdge);
            Vector2 previewPos = tilePos + new Vector2(padding, padding);

            if (entry.IsDirectory)
            {
                uint folderColor = ImGui.GetColorU32(ImGuiCol.TableHeaderBg);
                drawList.AddRectFilled(previewPos, previewPos + previewSize, folderColor, 4f);
                string dirLabel = "DIR";
                Vector2 textSize = ImGui.CalcTextSize(dirLabel);
                Vector2 textPos = previewPos + (previewSize - textSize) * 0.5f;
                drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), dirLabel);
            }
            else if (AssetExplorerEntryIsTexture(entry.Path))
            {
                var preview = GetOrCreatePreviewEntry(state, entry.Path);
                uint desiredSize = (uint)Math.Clamp(previewEdge, 32f, 512f);
                RequestAssetExplorerPreview(preview, desiredSize);

                if (preview.Texture is not null && TryGetTexturePreviewHandle(preview.Texture, previewEdge, out nint handle, out Vector2 displaySize))
                {
                    Vector2 imagePos = previewPos + (previewSize - displaySize) * 0.5f;
                    ImGui.SetCursorScreenPos(imagePos);
                    ImGui.Image(handle, displaySize);
                }
                else
                {
                    uint fillColor = ImGui.GetColorU32(ImGuiCol.FrameBgActive);
                    drawList.AddRectFilled(previewPos, previewPos + previewSize, fillColor, 4f);
                    string ext = Path.GetExtension(entry.Name);
                    ext = string.IsNullOrEmpty(ext) ? "IMG" : ext.TrimStart('.').ToUpperInvariant();
                    Vector2 textSize = ImGui.CalcTextSize(ext);
                    Vector2 textPos = previewPos + (previewSize - textSize) * 0.5f;
                    drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), ext);
                }
            }
            else
            {
                uint fillColor = ImGui.GetColorU32(ImGuiCol.FrameBgActive);
                drawList.AddRectFilled(previewPos, previewPos + previewSize, fillColor, 4f);
                string ext = Path.GetExtension(entry.Name);
                ext = string.IsNullOrEmpty(ext) ? "FILE" : ext.TrimStart('.').ToUpperInvariant();
                Vector2 textSize = ImGui.CalcTextSize(ext);
                Vector2 textPos = previewPos + (previewSize - textSize) * 0.5f;
                drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), ext);
            }

            Vector2 labelPos = tilePos + new Vector2(padding, tileHeight - labelHeight + padding * 0.5f);
            float labelWidth = tileWidth - padding * 2f;
            ImGui.SetCursorScreenPos(labelPos);

            if (isRenaming)
            {
                DrawAssetExplorerRenameField(state, entry, labelWidth);
            }
            else
            {
                ImGui.PushTextWrapPos(labelPos.X + labelWidth);
                ImGui.TextUnformatted(entry.Name);
                ImGui.PopTextWrapPos();
            }

            ImGui.EndGroup();
            ImGui.PopID();
            return directoryChanged;
        }

        private static void RequestAssetExplorerContextPopup(AssetExplorerTabState state, AssetExplorerEntry entry)
        {
            SetAssetExplorerContextEntry(state, entry.Path, entry.IsDirectory, true, entry.IsDirectory);
            _assetExplorerContextPopupRequested = true;
        }

        private static void RequestAssetExplorerContextPopupForDirectory(AssetExplorerTabState state, string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            SetAssetExplorerContextEntry(state, directory, true, false, true);
            _assetExplorerContextPopupRequested = true;
        }

        private static void SetAssetExplorerContextEntry(AssetExplorerTabState state, string path, bool isDirectory, bool selectIfFile, bool allowCreate)
        {
            _assetExplorerContextState = state;
            _assetExplorerContextPath = path;
            _assetExplorerContextIsDirectory = isDirectory;
            _assetExplorerContextAllowCreate = allowCreate;

            if (!isDirectory && selectIfFile)
                SetAssetExplorerSelection(state, path);
        }

        private static void DrawAssetExplorerContextPopup()
        {
            if (_assetExplorerContextPopupRequested)
            {
                ImGui.OpenPopup("AssetExplorerContext");
                _assetExplorerContextPopupRequested = false;
            }

            if (!ImGui.BeginPopup("AssetExplorerContext"))
                return;

            if (_assetExplorerContextState is null || _assetExplorerContextPath is null)
            {
                ImGui.TextDisabled("No selection");
                ImGui.EndPopup();
                return;
            }

            var state = _assetExplorerContextState;
            string path = _assetExplorerContextPath;
            bool isDirectory = _assetExplorerContextIsDirectory;

            bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
            {
                ImGui.TextDisabled("Item no longer exists.");
                if (ImGui.MenuItem("Close"))
                    ImGui.CloseCurrentPopup();

                ImGui.EndPopup();
                return;
            }

            if (_assetExplorerContextAllowCreate)
            {
                string? targetDirectory = GetAssetExplorerContextDirectory(state, path, isDirectory);
                bool canCreate = !string.IsNullOrEmpty(targetDirectory);

                if (ImGui.BeginMenu("Create New...", canCreate))
                {
                    if (targetDirectory is not null)
                        DrawAssetExplorerCreateMenu(state, targetDirectory);
                    ImGui.EndMenu();
                }
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Rename"))
            {
                BeginAssetExplorerRename(state, path, isDirectory);
                ImGui.CloseCurrentPopup();
            }

            if (ImGui.MenuItem(isDirectory ? "Open in Explorer" : "Show in Explorer"))
            {
                OpenPathInExplorer(path, isDirectory);
                ImGui.CloseCurrentPopup();
            }

            if (ImGui.MenuItem("Delete..."))
            {
                RequestAssetExplorerDelete(state, path, isDirectory);
                ImGui.CloseCurrentPopup();
            }

            if (!isDirectory)
            {
                var descriptor = ResolveAssetTypeForPath(path);
                var extraActions = GetAssetExplorerActions(path).ToList();
                bool hasTypeActions = descriptor?.ContextMenuAttributes.Count > 0;
                bool hasGeneralActions = extraActions.Count > 0;

                if (hasTypeActions || hasGeneralActions)
                {
                    ImGui.Separator();

                    if (hasTypeActions && descriptor is not null)
                    {
                        if (DrawAssetTypeContextMenuItems(descriptor, path))
                        {
                            ImGui.CloseCurrentPopup();
                            ImGui.EndPopup();
                            return;
                        }

                        if (hasGeneralActions)
                            ImGui.Separator();
                    }

                    foreach (var action in extraActions)
                    {
                        if (!ImGui.MenuItem(action.Label))
                            continue;

                        try
                        {
                            action.Handler(path);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, $"Asset explorer action '{action.Label}' failed for '{path}'.");
                        }

                        ImGui.CloseCurrentPopup();
                        break;
                    }
                }
            }

            ImGui.EndPopup();

            if (!ImGui.IsPopupOpen("AssetExplorerContext"))
            {
                _assetExplorerContextState = null;
                _assetExplorerContextPath = null;
                _assetExplorerContextAllowCreate = false;
            }
        }

        private static void DrawAssetExplorerCreateMenu(AssetExplorerTabState state, string targetDirectory)
        {
            var descriptors = EnsureAssetTypeCache();
            if (descriptors.Count == 0)
            {
                ImGui.TextDisabled("No XRAsset types available.");
                return;
            }

            var grouped = descriptors
                .GroupBy(d => string.IsNullOrWhiteSpace(d.Category) ? "General" : d.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped)
            {
                if (!ImGui.BeginMenu(group.Key))
                    continue;

                foreach (var descriptor in group.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    if (ImGui.MenuItem(descriptor.DisplayName))
                        CreateAssetInDirectory(state, targetDirectory, descriptor);

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                        ImGui.SetTooltip(descriptor.FullName);
                }

                ImGui.EndMenu();
            }
        }

        private static string? GetAssetExplorerContextDirectory(AssetExplorerTabState state, string path, bool isDirectory)
        {
            if (isDirectory)
                return NormalizeAssetExplorerPath(path);

            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                directory = state.CurrentDirectory;

            return string.IsNullOrEmpty(directory) ? null : NormalizeAssetExplorerPath(directory);
        }

        private static void CreateAssetInDirectory(AssetExplorerTabState state, string directory, AssetTypeDescriptor descriptor)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            var assets = Engine.Assets;
            if (assets is null)
                return;

            try
            {
                XRAsset? asset = descriptor.CreateInstance();
                if (asset is null)
                {
                    Debug.LogWarning($"Failed to instantiate asset type '{descriptor.FullName}'.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(asset.Name))
                    asset.Name = descriptor.DisplayName;

                string normalizedDirectory = NormalizeAssetExplorerPath(directory);
                if (!AssetPathWithinRoot(state, normalizedDirectory))
                {
                    Debug.LogWarning($"Cannot create assets outside of the root directory. ({directory})");
                    return;
                }

                assets.SaveTo(asset, normalizedDirectory);
                string? savedPath = asset.FilePath;
                if (string.IsNullOrEmpty(savedPath))
                    return;

                string normalizedPath = NormalizeAssetExplorerPath(savedPath);
                _assetExplorerAssetTypeCache[normalizedPath] = descriptor;
                _assetExplorerYamlKeyCache.Remove(normalizedPath);

                UpdateAssetExplorerSelection(state, normalizedPath, true);
                BeginAssetExplorerRename(state, normalizedPath, false);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to create asset of type '{descriptor.FullName}' in '{directory}'.");
            }
        }

        private static void UpdateAssetExplorerSelection(AssetExplorerTabState state, string? path, bool force)
        {
            Debug.Out($"[AssetExplorer] UpdateAssetExplorerSelection called: path='{path}', force={force}");
            string? normalized = string.IsNullOrWhiteSpace(path) ? null : NormalizeAssetExplorerPath(path);
            if (!force && string.Equals(state.SelectedPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Out($"[AssetExplorer] UpdateAssetExplorerSelection: Same path, skipping");
                return;
            }

            state.SelectedPath = normalized;

            if (string.IsNullOrEmpty(normalized))
            {
                ClearInspectorStandaloneTarget();
                return;
            }

            if (!TryShowAssetInInspector(normalized))
                ClearInspectorStandaloneTarget();
        }

        private static void HandleAssetExplorerFileActivation(AssetExplorerTabState state, AssetExplorerEntry entry)
        {
            if (entry.IsDirectory)
                return;

            if (TryOpenSceneAsset(entry.Path))
                UpdateAssetExplorerSelection(state, entry.Path, true);
        }

        private static bool TryOpenSceneAsset(string path)
        {
            var descriptor = ResolveAssetTypeForPath(path);
            if (descriptor?.Type is null || !typeof(XRScene).IsAssignableFrom(descriptor.Type))
                return false;

            var assets = Engine.Assets;
            if (assets is null)
                return false;

            XRScene? scene = assets.Load<XRScene>(path);
            if (scene is null)
                return false;

            return TryAddSceneToActiveWorld(scene);
        }

        private static bool TryAddSceneToActiveWorld(XRScene scene)
        {
            var world = TryGetActiveWorldInstance();
            if (world is null)
            {
                Debug.LogWarning($"Unable to open scene '{scene.Name ?? scene.FilePath}'. No active world instance.");
                return false;
            }

            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
            {
                Debug.LogWarning("Unable to open scene because the active world instance has no target world asset.");
                return false;
            }

            bool newlyAdded = !targetWorld.Scenes.Contains(scene);
            if (newlyAdded)
            {
                targetWorld.Scenes.Add(scene);
                Undo.TrackScene(scene);
                world.LoadScene(scene);
            }

            scene.IsVisible = true;
            targetWorld.MarkDirty();
            return true;
        }

        private static void SetAssetExplorerSelection(AssetExplorerTabState state, string path, bool force = false)
            => UpdateAssetExplorerSelection(state, path, force);

        private static void ClearAssetExplorerSelection(AssetExplorerTabState state)
            => UpdateAssetExplorerSelection(state, null, true);

        private static bool TryShowAssetInInspector(string path)
        {
            Debug.Out($"[AssetExplorer] TryShowAssetInInspector called with path='{path}'");
            var descriptor = ResolveAssetTypeForPath(path);
            if (descriptor is null)
            {
                Debug.Out($"[AssetExplorer] ResolveAssetTypeForPath returned null for path='{path}'");
                return false;
            }

            Debug.Out($"[AssetExplorer] ResolveAssetTypeForPath returned descriptor: {descriptor.FullName}");
            XRAsset? asset = LoadAssetForInspector(descriptor, path);
            if (asset is null)
            {
                Debug.Out($"[AssetExplorer] LoadAssetForInspector returned null for path='{path}'");
                return false;
            }

            Debug.Out($"[AssetExplorer] LoadAssetForInspector succeeded: {asset.GetType().Name}");
            string displayTitle = string.IsNullOrWhiteSpace(asset.Name)
                ? descriptor.DisplayName
                : asset.Name!;

            string fileLabel = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(fileLabel))
                displayTitle = string.Concat(displayTitle, " [", fileLabel, "]");

            SetInspectorStandaloneTarget(asset, displayTitle, null);
            return true;
        }

        private static XRAsset? LoadAssetForInspector(AssetTypeDescriptor descriptor, string path)
        {
            var assets = Engine.Assets;
            if (assets is null)
            {
                Debug.Out($"[AssetExplorer] LoadAssetForInspector: Engine.Assets is null");
                return null;
            }

            if (assets.TryGetAssetByPath(path, out XRAsset? cached))
            {
                Debug.Out($"[AssetExplorer] LoadAssetForInspector: Found cached asset for path='{path}'");
                return cached;
            }

            Debug.Out($"[AssetExplorer] LoadAssetForInspector: Loading asset from disk, path='{path}', type={descriptor.FullName}");
            try
            {
                MethodInfo loadMethod = GetAssetManagerLoadMethod();
                MethodInfo generic = loadMethod.MakeGenericMethod(descriptor.Type);
                if (generic.Invoke(assets, new object[] { path }) is XRAsset loaded)
                {
                    Debug.Out($"[AssetExplorer] LoadAssetForInspector: Successfully loaded asset");
                    return loaded;
                }
                Debug.Out($"[AssetExplorer] LoadAssetForInspector: Load returned null or wrong type");
            }
            catch (Exception ex)
            {
                Debug.Out($"[AssetExplorer] LoadAssetForInspector: Exception: {ex.Message}");
                Debug.LogException(ex, $"Failed to load asset '{path}' as '{descriptor.FullName}'.");;
            }

            return null;
        }

        private static MethodInfo GetAssetManagerLoadMethod()
        {
            if (_assetManagerLoadMethod is not null)
                return _assetManagerLoadMethod;

            var method = typeof(AssetManager).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == nameof(AssetManager.Load)
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));

            if (method is null)
                throw new InvalidOperationException("Unable to locate AssetManager.Load(string) method.");

            _assetManagerLoadMethod = method;
            return method;
        }

        private static AssetTypeDescriptor? ResolveAssetTypeForPath(string path)
        {
            Debug.Out($"[AssetExplorer] ResolveAssetTypeForPath: path='{path}'");
            string normalized = NormalizeAssetExplorerPath(path);
            if (_assetExplorerAssetTypeCache.TryGetValue(normalized, out var cached))
            {
                Debug.Out($"[AssetExplorer] ResolveAssetTypeForPath: Found cached descriptor={cached?.FullName ?? "null"}");
                return cached;
            }

            string extension = Path.GetExtension(normalized);
            Debug.Out($"[AssetExplorer] ResolveAssetTypeForPath: extension='{extension}'");
            var descriptors = EnsureAssetTypeCache();

            if (string.IsNullOrEmpty(extension))
            {
                _assetExplorerAssetTypeCache[normalized] = null;
                return null;
            }

            var thirdPartyMatches = descriptors
                .Where(d => !extension.Equals(".asset", StringComparison.OrdinalIgnoreCase) && d.SupportsExtension(extension))
                .ToList();

            if (thirdPartyMatches.Count == 1)
            {
                _assetExplorerAssetTypeCache[normalized] = thirdPartyMatches[0];
                return thirdPartyMatches[0];
            }

            if (!extension.Equals(".asset", StringComparison.OrdinalIgnoreCase))
            {
                _assetExplorerAssetTypeCache[normalized] = thirdPartyMatches.Count > 0 ? thirdPartyMatches[0] : null;
                return _assetExplorerAssetTypeCache[normalized];
            }

            if (!TryGetYamlKeys(normalized, out var yamlKeys))
            {
                // Try to read __assetType directly as fallback
                var assetTypeFromFile = TryReadAssetTypeFromFile(normalized);
                if (assetTypeFromFile is not null)
                {
                    var descriptor = descriptors.FirstOrDefault(d => 
                        string.Equals(d.FullName, assetTypeFromFile, StringComparison.Ordinal) ||
                        string.Equals(d.Type.Name, assetTypeFromFile, StringComparison.Ordinal));
                    if (descriptor is not null)
                    {
                        Debug.Out($"[AssetExplorer] Found type via __assetType field: {descriptor.FullName}");
                        _assetExplorerAssetTypeCache[normalized] = descriptor;
                        return descriptor;
                    }
                }
                Debug.Out($"[AssetExplorer] TryGetYamlKeys failed for path='{normalized}'");
                _assetExplorerAssetTypeCache[normalized] = null;
                return null;
            }

            Debug.Out($"[AssetExplorer] TryGetYamlKeys succeeded, found {yamlKeys.Count} keys: {string.Join(", ", yamlKeys.Take(10))}");
            
            // First check if __assetType is in the keys - direct type match
            if (yamlKeys.Contains("__assetType"))
            {
                var assetTypeFromFile = TryReadAssetTypeFromFile(normalized);
                if (assetTypeFromFile is not null)
                {
                    var descriptor = descriptors.FirstOrDefault(d => 
                        string.Equals(d.FullName, assetTypeFromFile, StringComparison.Ordinal) ||
                        string.Equals(d.Type.Name, assetTypeFromFile, StringComparison.Ordinal));
                    if (descriptor is not null)
                    {
                        Debug.Out($"[AssetExplorer] Found type via __assetType field: {descriptor.FullName}");
                        _assetExplorerAssetTypeCache[normalized] = descriptor;
                        return descriptor;
                    }
                }
            }
            
            AssetTypeDescriptor? best = null;
            int bestScore = 0;

            foreach (var descriptor in descriptors)
            {
                if (!descriptor.SupportsExtension(".asset"))
                    continue;

                int score = descriptor.GetMatchScore(yamlKeys);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = descriptor;
                }
                else if (score == bestScore && best is not null && descriptor.PropertyCount < best.PropertyCount)
                {
                    best = descriptor;
                }
            }

            Debug.Out($"[AssetExplorer] Best match: {best?.FullName ?? "null"}, score={bestScore}");
            if (best is null && descriptors.Count == 1)
                best = descriptors[0];

            _assetExplorerAssetTypeCache[normalized] = best;
            return best;
        }

        private static bool TryGetYamlKeys(string path, out HashSet<string> keys)
        {
            if (_assetExplorerYamlKeyCache.TryGetValue(path, out keys))
                return keys.Count > 0;

            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var yaml = new YamlStream();
                yaml.Load(reader);

                if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode mapping)
                {
                    foreach (var keyNode in mapping.Children.Keys.OfType<YamlScalarNode>())
                    {
                        if (!string.IsNullOrWhiteSpace(keyNode.Value))
                            keys.Add(keyNode.Value);
                    }
                }
                Debug.Out($"[AssetExplorer] TryGetYamlKeys: Parsed {yaml.Documents.Count} documents, {keys.Count} keys");
            }
            catch (Exception ex)
            {
                Debug.Out($"[AssetExplorer] TryGetYamlKeys exception: {ex.Message}");
                keys.Clear();
            }

            if (keys.Count > 0)
                _assetExplorerYamlKeyCache[path] = keys;
            else
                _assetExplorerYamlKeyCache.Remove(path);

            return keys.Count > 0;
        }

        private static string? TryReadAssetTypeFromFile(string path)
        {
            try
            {
                // Read first few lines looking for __assetType
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                for (int i = 0; i < 10 && !reader.EndOfStream; i++)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                        break;
                    
                    // Look for __assetType: TypeName pattern
                    if (line.StartsWith("__assetType:", StringComparison.Ordinal))
                    {
                        var value = line.Substring("__assetType:".Length).Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            Debug.Out($"[AssetExplorer] Found __assetType in file: '{value}'");
                            return value;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            return null;
        }

        private static void UpdateAssetExplorerExtensionFilterSet()
        {
            _assetExplorerExtensionFilterSet.Clear();
            _assetExplorerExtensionFilterHasWildcard = false;

            if (string.IsNullOrWhiteSpace(_assetExplorerExtensionFilter))
                return;

            var tokens = _assetExplorerExtensionFilter
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                string normalized = token.Trim();
                if (normalized.Length == 0)
                    continue;

                if (normalized == "*")
                {
                    _assetExplorerExtensionFilterHasWildcard = true;
                    _assetExplorerExtensionFilterSet.Clear();
                    return;
                }

                if (!normalized.StartsWith(".", StringComparison.Ordinal))
                    normalized = "." + normalized;

                _assetExplorerExtensionFilterSet.Add(normalized.ToLowerInvariant());
            }
        }

        private static bool ShouldIncludeFileByExtension(string path)
        {
            if (_assetExplorerExtensionFilterHasWildcard)
                return true;

            if (_assetExplorerExtensionFilterSet.Count == 0)
                return true;

            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
                return false;

            return _assetExplorerExtensionFilterSet.Contains(extension.ToLowerInvariant());
        }

        private static void EnsureAssetExplorerCategoryFilters()
        {
            if (!_assetExplorerCategoryFiltersDirty && _assetExplorerCategoryFilterSelections.Count > 0)
                return;

            _assetExplorerCategoryFiltersDirty = false;

            var descriptors = EnsureAssetTypeCache();
            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var descriptor in descriptors)
            {
                string category = string.IsNullOrWhiteSpace(descriptor.Category) ? "General" : descriptor.Category;
                categories.Add(category);
            }

            foreach (var category in _assetExplorerExtensionCategoryMap.Values)
                categories.Add(category);

            categories.Add("General");
            categories.Add("Other");

            var previousSelections = new Dictionary<string, bool>(_assetExplorerCategoryFilterSelections, StringComparer.OrdinalIgnoreCase);

            _assetExplorerCategoryFilterSelections.Clear();
            _assetExplorerCategoryFilterOrder.Clear();

            foreach (var category in categories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                bool selected = previousSelections.TryGetValue(category, out bool value) ? value : true;
                _assetExplorerCategoryFilterSelections[category] = selected;
                _assetExplorerCategoryFilterOrder.Add(category);
            }

            UpdateAssetExplorerCategoryFilterLabel();
            _assetExplorerCategoryFiltersDirty = false;
        }

        private static void UpdateAssetExplorerCategoryFilterLabel()
        {
            if (_assetExplorerCategoryFilterSelections.Count == 0)
            {
                _assetExplorerCategoryFilterLabel = "Categories: All";
                _assetExplorerCategoryFilterActive = false;
                return;
            }

            var selected = _assetExplorerCategoryFilterSelections
                .Where(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int selectedCount = selected.Count;

            _assetExplorerCategoryFilterActive = selectedCount != _assetExplorerCategoryFilterSelections.Count;

            if (selectedCount == 0)
            {
                _assetExplorerCategoryFilterLabel = "Categories: None";
                return;
            }

            if (selectedCount == _assetExplorerCategoryFilterSelections.Count)
            {
                _assetExplorerCategoryFilterLabel = "Categories: All";
                _assetExplorerCategoryFilterActive = false;
                return;
            }

            if (selectedCount <= 2)
            {
                _assetExplorerCategoryFilterLabel = "Categories: " + string.Join(", ", selected);
                return;
            }

            string preview = string.Join(", ", selected.Take(2));
            _assetExplorerCategoryFilterLabel = $"Categories: {preview} +{selectedCount - 2}";
        }

        private static bool AreAllAssetExplorerCategoriesSelected()
            => _assetExplorerCategoryFilterSelections.Count > 0
               && _assetExplorerCategoryFilterSelections.All(kvp => kvp.Value);

        private static bool AreAnyAssetExplorerCategoriesSelected()
            => _assetExplorerCategoryFilterSelections.Values.Any(v => v);

        private static void SetAllAssetExplorerCategorySelections(bool value)
        {
            foreach (var key in _assetExplorerCategoryFilterOrder)
                _assetExplorerCategoryFilterSelections[key] = value;
        }

        private static string GetAssetExplorerSearchScopeLabel()
        {
            if (_assetExplorerSearchScope == (AssetExplorerSearchScope.Name | AssetExplorerSearchScope.Path | AssetExplorerSearchScope.Metadata))
                return "Search In: All";

            var parts = new List<string>(3);
            if (_assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Name))
                parts.Add("Name");
            if (_assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Path))
                parts.Add("Path");
            if (_assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Metadata))
                parts.Add("Metadata");

            if (parts.Count == 0)
                parts.Add("Name");

            return "Search In: " + string.Join(", ", parts);
        }

        private static void SetAssetExplorerSearchScopeFlag(AssetExplorerSearchScope scope, bool enabled)
        {
            if (enabled)
                _assetExplorerSearchScope |= scope;
            else
                _assetExplorerSearchScope &= ~scope;

            if (_assetExplorerSearchScope == 0)
                _assetExplorerSearchScope = AssetExplorerSearchScope.Name;
        }

        private static bool MatchesAssetExplorerSearch(string path, string name, bool isDirectory, AssetTypeDescriptor? descriptor)
        {
            if (string.IsNullOrWhiteSpace(_assetExplorerSearchTerm))
                return true;

            StringComparison comparison = _assetExplorerSearchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            string term = _assetExplorerSearchTerm;

            if (_assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Name)
                && name.IndexOf(term, comparison) >= 0)
                return true;

            if (_assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Path)
                && path.IndexOf(term, comparison) >= 0)
                return true;

            if (!isDirectory && _assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Metadata)
                && AssetExplorerEntryMatchesMetadata(path, descriptor, term, comparison))
                return true;

            return false;
        }

        private static bool AssetExplorerEntryMatchesMetadata(string path, AssetTypeDescriptor? descriptor, string term, StringComparison comparison)
        {
            if (descriptor is not null)
            {
                if (!string.IsNullOrEmpty(descriptor.DisplayName) && descriptor.DisplayName.IndexOf(term, comparison) >= 0)
                    return true;

                if (!string.IsNullOrEmpty(descriptor.FullName) && descriptor.FullName.IndexOf(term, comparison) >= 0)
                    return true;

                if (!string.IsNullOrEmpty(descriptor.Category) && descriptor.Category.IndexOf(term, comparison) >= 0)
                    return true;
            }

            if (TryGetYamlKeys(path, out var keys))
            {
                foreach (var key in keys)
                {
                    if (key.IndexOf(term, comparison) >= 0)
                        return true;
                }
            }

            return false;
        }

        private static bool MatchesAssetExplorerFilters(string path, bool isDirectory, AssetTypeDescriptor? descriptor)
        {
            EnsureAssetExplorerCategoryFilters();

            if (!_assetExplorerCategoryFilterActive || isDirectory)
                return true;

            string category = GetAssetExplorerCategory(path, descriptor);
            if (!_assetExplorerCategoryFilterSelections.TryGetValue(category, out bool allowed))
                return true;

            return allowed;
        }

        private static string GetAssetExplorerCategory(string path, AssetTypeDescriptor? descriptor)
        {
            if (descriptor is not null)
            {
                string category = string.IsNullOrWhiteSpace(descriptor.Category) ? "General" : descriptor.Category;
                return category;
            }

            string extension = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(extension) && _assetExplorerExtensionCategoryMap.TryGetValue(extension, out var mapped))
                return mapped;

            return "Other";
        }

        private static bool AssetExplorerFiltersNeedDescriptor()
        {
            if (_assetExplorerCategoryFilterActive)
                return true;

            if (!string.IsNullOrWhiteSpace(_assetExplorerSearchTerm)
                && _assetExplorerSearchScope.HasFlag(AssetExplorerSearchScope.Metadata))
                return true;

            return false;
        }

        private static void ClearAssetExplorerTypeCaches()
        {
            _assetExplorerAssetTypeCache.Clear();
            _assetExplorerYamlKeyCache.Clear();
            _assetTypeCacheDirty = true;
            _assetExplorerCategoryFiltersDirty = true;
        }

        private static void DrawAssetExplorerDeleteConfirmation()
        {
            if (_assetExplorerPendingDeletePath is null)
                return;

            bool open = true;
            if (ImGui.BeginPopupModal("Delete Asset?", ref open, ImGuiWindowFlags.AlwaysAutoResize))
            {
                string path = _assetExplorerPendingDeletePath;
                bool isDirectory = _assetExplorerPendingDeleteIsDirectory;

                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name))
                    name = path;

                ImGui.TextUnformatted(isDirectory ? $"Delete folder '{name}'?" : $"Delete asset '{name}'?");
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(path);
                ImGui.PopTextWrapPos();

                if (isDirectory)
                    ImGui.TextDisabled("This will delete the folder and all contents.");

                ImGui.Separator();

                if (ImGui.Button("Delete"))
                {
                    ExecuteAssetExplorerDeletion();
                    ImGui.EndPopup();
                    return;
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    ImGui.CloseCurrentPopup();
                    ClearAssetExplorerDeletionRequest();
                }

                ImGui.EndPopup();
            }

            if (!open)
                ClearAssetExplorerDeletionRequest();
        }

        private static void RequestAssetExplorerDelete(AssetExplorerTabState state, string path, bool isDirectory)
        {
            _assetExplorerPendingDeleteState = state;
            _assetExplorerPendingDeletePath = path;
            _assetExplorerPendingDeleteIsDirectory = isDirectory;
            ImGui.OpenPopup("Delete Asset?");
        }

        private static void ExecuteAssetExplorerDeletion()
        {
            if (_assetExplorerPendingDeleteState is null || string.IsNullOrEmpty(_assetExplorerPendingDeletePath))
                return;

            var state = _assetExplorerPendingDeleteState;
            string path = _assetExplorerPendingDeletePath;
            bool isDirectory = _assetExplorerPendingDeleteIsDirectory;

            try
            {
                if (isDirectory)
                {
                    Directory.Delete(path, true);

                    var keysToRemove = state.PreviewCache.Keys
                        .Where(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var key in keysToRemove)
                        state.PreviewCache.Remove(key);
                }
                else
                {
                    File.Delete(path);
                    state.PreviewCache.Remove(path);
                    if (string.Equals(state.SelectedPath, path, StringComparison.OrdinalIgnoreCase))
                        ClearAssetExplorerSelection(state);
                }

                RemoveAssetExplorerCachedData(path, isDirectory);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to delete '{path}'.");
            }
            finally
            {
                ClearAssetExplorerDeletionRequest();
            }
        }

        private static void ClearAssetExplorerDeletionRequest()
        {
            _assetExplorerPendingDeleteState = null;
            _assetExplorerPendingDeletePath = null;
            _assetExplorerPendingDeleteIsDirectory = false;
        }

        private static void BeginAssetExplorerRename(AssetExplorerTabState state, string path, bool isDirectory)
        {
            if (string.IsNullOrEmpty(path))
                return;

            state.RenamingPath = path;
            state.RenamingIsDirectory = isDirectory;
            state.RenameFocusRequested = true;
            _assetExplorerRenameFocusRequested = true;

            string bufferSource = Path.GetFileName(path);
            if (string.IsNullOrEmpty(bufferSource))
                bufferSource = path;

            PopulateAssetExplorerRenameBuffer(bufferSource);

            if (!isDirectory)
                SetAssetExplorerSelection(state, path, true);
        }

        private static void ApplyAssetExplorerRename(AssetExplorerTabState state)
        {
            if (string.IsNullOrEmpty(state.RenamingPath))
                return;

            string oldPath = state.RenamingPath;
            bool isDirectory = state.RenamingIsDirectory;

            string newName = ExtractAssetExplorerRenameBuffer();
            if (string.IsNullOrWhiteSpace(newName))
            {
                CancelAssetExplorerRename(state);
                return;
            }

            if (ContainsInvalidFileNameChars(newName))
            {
                Debug.LogWarning($"Invalid name '{newName}'.");
                return;
            }

            string? parent = Path.GetDirectoryName(oldPath);
            if (string.IsNullOrEmpty(parent))
            {
                CancelAssetExplorerRename(state);
                return;
            }

            string newPath = Path.Combine(parent, newName);
            newPath = NormalizeAssetExplorerPath(newPath);

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                CancelAssetExplorerRename(state);
                return;
            }

            if (!AssetPathWithinRoot(state, newPath))
            {
                Debug.LogWarning($"Cannot rename '{oldPath}' to '{newPath}'. Target is outside the asset root.");
                return;
            }

            try
            {
                if (isDirectory)
                {
                    if (Directory.Exists(newPath))
                    {
                        Debug.LogWarning($"A folder named '{newName}' already exists.");
                        return;
                    }

                    Directory.Move(oldPath, newPath);
                    TransferAssetExplorerCachedData(oldPath, newPath, true);

                    var affectedKeys = state.PreviewCache.Keys
                        .Where(k => k.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var key in affectedKeys)
                    {
                        if (state.PreviewCache.TryGetValue(key, out var preview))
                        {
                            state.PreviewCache.Remove(key);
                            string updated = newPath + key.Substring(oldPath.Length);
                            preview.UpdatePath(updated);
                            state.PreviewCache[updated] = preview;
                        }
                    }
                }
                else
                {
                    if (File.Exists(newPath))
                    {
                        Debug.LogWarning($"A file named '{newName}' already exists.");
                        return;
                    }

                    File.Move(oldPath, newPath);
                    TransferAssetExplorerPreview(state, oldPath, newPath);
                    TransferAssetExplorerCachedData(oldPath, newPath, false);
                    SetAssetExplorerSelection(state, newPath, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to rename '{oldPath}'.");
            }
            finally
            {
                CancelAssetExplorerRename(state);
            }
        }

        private static void CancelAssetExplorerRename(AssetExplorerTabState state)
        {
            state.RenamingPath = null;
            state.RenamingIsDirectory = false;
            state.RenameFocusRequested = false;
            _assetExplorerRenameFocusRequested = false;
            ClearAssetExplorerRenameBuffer();
        }

        private static bool ContainsInvalidFileNameChars(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            if (name.IndexOf(Path.DirectorySeparatorChar) >= 0 || name.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                return true;

            return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
        }

        private static bool AssetPathWithinRoot(AssetExplorerTabState state, string path)
            => path.StartsWith(state.RootPath, StringComparison.OrdinalIgnoreCase);

        private static void TransferAssetExplorerPreview(AssetExplorerTabState state, string oldPath, string newPath)
        {
            if (!state.PreviewCache.TryGetValue(oldPath, out var entry))
                return;

            state.PreviewCache.Remove(oldPath);
            entry.UpdatePath(newPath);
            state.PreviewCache[newPath] = entry;
        }

        private static void RemoveAssetExplorerCachedData(string path, bool isDirectory)
        {
            string normalized = NormalizeAssetExplorerPath(path);
            if (isDirectory)
            {
                var assetKeys = _assetExplorerAssetTypeCache.Keys
                    .Where(k => k.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var key in assetKeys)
                    _assetExplorerAssetTypeCache.Remove(key);

                var yamlKeys = _assetExplorerYamlKeyCache.Keys
                    .Where(k => k.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var key in yamlKeys)
                    _assetExplorerYamlKeyCache.Remove(key);
            }
            else
            {
                _assetExplorerAssetTypeCache.Remove(normalized);
                _assetExplorerYamlKeyCache.Remove(normalized);
            }
        }

        private static void TransferAssetExplorerCachedData(string oldPath, string newPath, bool isDirectory)
        {
            string oldNormalized = NormalizeAssetExplorerPath(oldPath);
            string newNormalized = NormalizeAssetExplorerPath(newPath);

            if (isDirectory)
            {
                var assetEntries = _assetExplorerAssetTypeCache
                    .Where(kvp => kvp.Key.StartsWith(oldNormalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var (key, value) in assetEntries)
                {
                    _assetExplorerAssetTypeCache.Remove(key);
                    string transferredKey = NormalizeAssetExplorerPath(newNormalized + key.Substring(oldNormalized.Length));
                    _assetExplorerAssetTypeCache[transferredKey] = value;
                }

                var yamlEntries = _assetExplorerYamlKeyCache
                    .Where(kvp => kvp.Key.StartsWith(oldNormalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var (key, value) in yamlEntries)
                {
                    _assetExplorerYamlKeyCache.Remove(key);
                    string transferredKey = NormalizeAssetExplorerPath(newNormalized + key.Substring(oldNormalized.Length));
                    _assetExplorerYamlKeyCache[transferredKey] = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                if (_assetExplorerAssetTypeCache.TryGetValue(oldNormalized, out var descriptor))
                {
                    _assetExplorerAssetTypeCache.Remove(oldNormalized);
                    _assetExplorerAssetTypeCache[newNormalized] = descriptor;
                }

                if (_assetExplorerYamlKeyCache.TryGetValue(oldNormalized, out var keys))
                {
                    _assetExplorerYamlKeyCache.Remove(oldNormalized);
                    _assetExplorerYamlKeyCache[newNormalized] = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private static IReadOnlyList<AssetTypeDescriptor> EnsureAssetTypeCache()
        {
            if (!_assetTypeCacheDirty && _assetTypeDescriptors.Count > 0)
                return _assetTypeDescriptors;

            _assetTypeDescriptors.Clear();
            _assetExplorerCategoryFiltersDirty = true;

            Type baseType = typeof(XRAsset);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (type is null)
                        continue;
                    if (!baseType.IsAssignableFrom(type))
                        continue;
                    if (type.IsAbstract || type.IsInterface)
                        continue;
                    if (type.ContainsGenericParameters)
                        continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    string displayName = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? type.Name;
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = type.Name;

                    string category = type.Namespace ?? "General";

                    HashSet<string> properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(p => p.GetIndexParameters().Length == 0)
                        .Select(p => p.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase) { ".asset" };
                    var thirdParty = type.GetCustomAttribute<XR3rdPartyExtensionsAttribute>();
                    if (thirdParty is not null)
                    {
                        foreach (var (ext, _) in thirdParty.Extensions)
                        {
                            if (string.IsNullOrWhiteSpace(ext))
                                continue;
                            string normalized = ext.StartsWith(".", StringComparison.Ordinal) ? ext : "." + ext;
                            extensions.Add(normalized.ToLowerInvariant());
                        }
                    }

                    string? inspectorTypeName = type.GetCustomAttribute<XRAssetInspectorAttribute>(true)?.InspectorTypeName;
                    XRAssetContextMenuAttribute[] contextMenus = type.GetCustomAttributes<XRAssetContextMenuAttribute>(true).ToArray();

                    _assetTypeDescriptors.Add(new AssetTypeDescriptor(type, displayName, category, properties, extensions, inspectorTypeName, contextMenus));
                }
            }

            _assetTypeDescriptors.Sort((a, b) =>
            {
                int categoryCompare = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (categoryCompare != 0)
                    return categoryCompare;
                int nameCompare = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                    return nameCompare;
                return string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
            });

            _assetTypeCacheDirty = false;
            return _assetTypeDescriptors;
        }

        private sealed class AssetTypeDescriptor
        {
            public AssetTypeDescriptor(
                Type type,
                string displayName,
                string category,
                HashSet<string> propertyNames,
                HashSet<string> supportedExtensions,
                string? inspectorTypeName,
                IReadOnlyList<XRAssetContextMenuAttribute> contextMenuAttributes)
            {
                Type = type;
                DisplayName = displayName;
                Category = category;
                FullName = type.FullName ?? type.Name;
                PropertyNames = propertyNames;
                SupportedExtensions = supportedExtensions;
                InspectorTypeName = inspectorTypeName;
                ContextMenuAttributes = contextMenuAttributes;
            }

            public Type Type { get; }
            public string DisplayName { get; }
            public string Category { get; }
            public string FullName { get; }
            public HashSet<string> PropertyNames { get; }
            public HashSet<string> SupportedExtensions { get; }
            public string? InspectorTypeName { get; }
            public IReadOnlyList<XRAssetContextMenuAttribute> ContextMenuAttributes { get; }
            public int PropertyCount => PropertyNames.Count;

            public bool SupportsExtension(string extension)
                => SupportedExtensions.Contains(extension);

            public int GetMatchScore(ISet<string> keys)
            {
                if (keys.Count == 0 || PropertyNames.Count == 0)
                    return 0;

                int score = 0;
                foreach (var key in keys)
                {
                    if (PropertyNames.Contains(key))
                        score++;
                }
                return score;
            }

            public XRAsset? CreateInstance()
                => Activator.CreateInstance(Type) as XRAsset;
        }

        private static bool AssetExplorerEntryIsTexture(string path)
        {
            string extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && _assetExplorerTextureExtensions.Contains(extension.ToLowerInvariant());
        }

        private static bool DrawAssetTypeContextMenuItems(AssetTypeDescriptor descriptor, string path)
        {
            if (descriptor.ContextMenuAttributes.Count == 0)
                return false;

            XRAsset? assetInstance = null;
            bool assetLoaded = false;
            XRAssetContextMenuContext context = default;
            bool contextReady = false;

            foreach (var attribute in descriptor.ContextMenuAttributes)
            {
                MethodInfo? handler = ResolveAssetContextMenuHandler(attribute.HandlerTypeName, attribute.HandlerMethodName);
                bool enabled = handler is not null;

                if (!ImGui.MenuItem(attribute.Label, null, false, enabled))
                    continue;

                if (handler is null)
                    continue;

                EnsureContext();

                try
                {
                    handler.Invoke(null, new object[] { context });
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Asset context menu action '{attribute.Label}' failed for '{path}'.");
                }

                return true;
            }

            return false;

            void EnsureContext()
            {
                if (contextReady)
                    return;

                if (!assetLoaded)
                {
                    assetInstance = LoadAssetForInspector(descriptor, path);
                    assetLoaded = true;
                }

                context = new XRAssetContextMenuContext(path, assetInstance);
                contextReady = true;
            }
        }

        private static MethodInfo? ResolveAssetContextMenuHandler(string typeName, string methodName)
        {
            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
                return null;

            string cacheKey = string.Concat(typeName, "::", methodName);
            if (_assetContextMenuHandlerCache.TryGetValue(cacheKey, out var cached))
                return cached;

            Type? handlerType = Type.GetType(typeName, false, false);
            if (handlerType is null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    handlerType = assembly.GetType(typeName, false, false);
                    if (handlerType is not null)
                        break;
                }
            }

            if (handlerType is null)
            {
                Debug.LogWarning($"Asset context menu handler type '{typeName}' could not be resolved.");
                _assetContextMenuHandlerCache[cacheKey] = null;
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo? method = handlerType.GetMethod(methodName, flags);
            if (method is null)
            {
                Debug.LogWarning($"Asset context menu handler method '{methodName}' was not found on '{handlerType.FullName}'.");
                _assetContextMenuHandlerCache[cacheKey] = null;
                return null;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(XRAssetContextMenuContext))
            {
                Debug.LogWarning($"Asset context menu handler '{handlerType.FullName}.{methodName}' must accept a single {nameof(XRAssetContextMenuContext)} parameter.");
                _assetContextMenuHandlerCache[cacheKey] = null;
                return null;
            }

            _assetContextMenuHandlerCache[cacheKey] = method;
            return method;
        }

        private static AssetExplorerPreviewCacheEntry GetOrCreatePreviewEntry(AssetExplorerTabState state, string path)
        {
            if (!state.PreviewCache.TryGetValue(path, out var entry))
            {
                entry = new AssetExplorerPreviewCacheEntry(path);
                state.PreviewCache[path] = entry;
            }

            return entry;
        }

        private static void RequestAssetExplorerPreview(AssetExplorerPreviewCacheEntry entry, uint desiredSize)
        {
            if (entry.Texture is not null && entry.RequestedSize >= desiredSize && !entry.RequestInFlight)
                return;

            if (entry.RequestInFlight && entry.RequestedSize >= desiredSize)
                return;

            entry.RequestedSize = Math.Max(entry.RequestedSize, desiredSize);
            entry.RequestInFlight = true;

            XRTexture2D seed = entry.Texture ?? new XRTexture2D();
            seed.FilePath ??= entry.Path;
            if (string.IsNullOrWhiteSpace(seed.Name))
                seed.Name = Path.GetFileNameWithoutExtension(entry.Path);

            XRTexture2D.SchedulePreviewJob(
                entry.Path,
                seed,
                desiredSize,
                onFinished: tex => Engine.InvokeOnMainThread(() =>
                {
                    entry.Texture = tex;
                    entry.RequestInFlight = false;
                }),
                onError: ex => Engine.InvokeOnMainThread(() =>
                {
                    Debug.LogException(ex, $"Texture preview job failed for '{entry.Path}'.");
                    entry.RequestInFlight = false;
                }),
                onCanceled: () => Engine.InvokeOnMainThread(() =>
                {
                    entry.RequestInFlight = false;
                }));
        }

        private static bool TryGetTexturePreviewHandle(XRTexture2D texture, float maxEdge, out nint handle, out Vector2 displaySize)
        {
            handle = nint.Zero;
            displaySize = new Vector2(AssetExplorerPreviewFallbackEdge, AssetExplorerPreviewFallbackEdge);

            if (!Engine.IsRenderThread)
                return false;

            OpenGLRenderer? renderer = TryGetOpenGLRenderer();
            if (renderer is null)
                return false;

            var apiTexture = renderer.GenericToAPI<GLTexture2D>(texture);
            if (apiTexture is null)
                return false;

            uint binding = apiTexture.BindingId;
            if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
                return false;

            handle = (nint)binding;
            Vector2 pixelSize = new(texture.Width, texture.Height);
            displaySize = GetPreviewSizeForEdge(pixelSize, maxEdge);
            return true;
        }

        private static Vector2 GetPreviewSizeForEdge(Vector2 pixelSize, float maxEdge)
        {
            float width = MathF.Max(pixelSize.X, 1f);
            float height = MathF.Max(pixelSize.Y, 1f);

            if (maxEdge <= 0f)
                return new Vector2(AssetExplorerPreviewFallbackEdge, AssetExplorerPreviewFallbackEdge);

            float largest = MathF.Max(width, height);
            if (largest <= maxEdge)
                return new Vector2(width, height);

            float scale = maxEdge / largest;
            return new Vector2(width * scale, height * scale);
        }

        private static OpenGLRenderer? TryGetOpenGLRenderer()
        {
            if (AbstractRenderer.Current is OpenGLRenderer current)
                return current;

            foreach (var window in Engine.Windows)
                if (window.Renderer is OpenGLRenderer renderer)
                    return renderer;

            return null;
        }

        private static IEnumerable<AssetExplorerContextAction> GetAssetExplorerActions(string path)
        {
            string extension = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(extension)
                && _assetExplorerContextActionsByExtension.TryGetValue(extension.ToLowerInvariant(), out var specific))
            {
                foreach (var action in specific)
                {
                    if (action.ShouldDisplay(path))
                        yield return action;
                }
            }

            foreach (var action in _assetExplorerGlobalContextActions)
            {
                if (action.ShouldDisplay(path))
                    yield return action;
            }
        }

        private static void OpenPathInExplorer(string path, bool isDirectory)
        {
            try
            {
                string arguments = isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arguments)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to open explorer for '{path}'.");
            }
        }


        private static partial void EnsureAssetExplorerState(AssetExplorerTabState state, string rootPath)
        {
            string normalizedRoot = NormalizeAssetExplorerPath(rootPath);
            if (string.IsNullOrEmpty(normalizedRoot) || !Directory.Exists(normalizedRoot))
            {
                state.RootPath = string.Empty;
                state.CurrentDirectory = string.Empty;
                ClearAssetExplorerSelection(state);
                return;
            }

            if (!string.Equals(state.RootPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                state.RootPath = normalizedRoot;
                state.CurrentDirectory = normalizedRoot;
                ClearAssetExplorerSelection(state);
            }

            if (string.IsNullOrEmpty(state.CurrentDirectory)
                || !Directory.Exists(state.CurrentDirectory)
                || !state.CurrentDirectory.StartsWith(state.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                state.CurrentDirectory = state.RootPath;
            }
            else
            {
                state.CurrentDirectory = NormalizeAssetExplorerPath(state.CurrentDirectory);
            }

            if (!string.IsNullOrEmpty(state.SelectedPath) && !File.Exists(state.SelectedPath))
            {
                RemoveAssetExplorerCachedData(state.SelectedPath, false);
                ClearAssetExplorerSelection(state);
            }

            if (!string.IsNullOrEmpty(state.RenamingPath))
            {
                bool exists = state.RenamingIsDirectory
                    ? Directory.Exists(state.RenamingPath)
                    : File.Exists(state.RenamingPath);

                if (!exists)
                    CancelAssetExplorerRename(state);
            }
        }

        private static partial string NormalizeAssetExplorerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                string full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        private static partial bool DirectoryHasChildren(string path)
        {
            try
            {
                using var enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
                return enumerator.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        private static partial string FormatFileSize(long size)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (size >= GB)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", size / (double)GB);
            if (size >= MB)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", size / (double)MB);
            if (size >= KB)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} KB", size / (double)KB);
            return string.Format(CultureInfo.InvariantCulture, "{0} B", size);
        }
}
