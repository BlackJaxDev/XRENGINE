using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using ImGuiNET;
using XREngine;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static partial void DrawAssetExplorerPanel()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawAssetExplorerPanel");
            var viewport = ImGui.GetMainViewport();
            float reservedLeft = _profilerDockLeftEnabled ? _profilerDockWidth : 0.0f;
            float reservedRight = _inspectorDockRightEnabled ? _inspectorDockWidth : 0.0f;
            bool dockedTop = _assetExplorerDockTopEnabled;
            bool dockedBottom = _assetExplorerDockBottomEnabled;
            bool isDocked = dockedTop || dockedBottom;

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;
            const float minHeight = 220.0f;
            const float reservedVerticalMargin = 110.0f;

            if (dockedTop)
            {
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                float dockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);
                float dockWidth = Math.Max(320.0f, viewport.WorkSize.X - reservedLeft - reservedRight);

                _assetExplorerDockHeight = dockHeight;

                Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y);
                Vector2 size = new(dockWidth, dockHeight);

                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                ImGui.SetNextWindowViewport(viewport.ID);

                windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
            }
            else if (dockedBottom)
            {
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                float dockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);
                float dockWidth = Math.Max(320.0f, viewport.WorkSize.X - reservedLeft - reservedRight);

                _assetExplorerDockHeight = dockHeight;

                Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y + viewport.WorkSize.Y - dockHeight);
                Vector2 size = new(dockWidth, dockHeight);

                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                ImGui.SetNextWindowViewport(viewport.ID);

                windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
            }
            else if (_assetExplorerUndockNextFrame)
            {
                var defaultSize = new Vector2(920.0f, 420.0f);
                var pos = viewport.WorkPos + (viewport.WorkSize - defaultSize) * 0.5f;
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(defaultSize, ImGuiCond.Always);
                _assetExplorerUndockNextFrame = false;
            }

            if (!ImGui.Begin("Assets", windowFlags))
            {
                ImGui.End();
                return;
            }

            bool headerAtBottom = dockedBottom && !dockedTop;

            if (!headerAtBottom)
                DrawAssetExplorerHeader(viewport, false, dockedTop, dockedBottom, isDocked, minHeight, reservedVerticalMargin);

            bool showContent = !_assetExplorerCollapsed;
            if (showContent)
            {
                if (!headerAtBottom)
                    ImGui.Separator();

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
            }

            if (headerAtBottom)
                DrawAssetExplorerHeader(viewport, true, dockedTop, dockedBottom, isDocked, minHeight, reservedVerticalMargin);

            if (_assetExplorerDockTopEnabled || _assetExplorerDockBottomEnabled)
                HandleAssetExplorerDockResize(viewport, reservedLeft, reservedRight, _assetExplorerDockTopEnabled);

            ImGui.End();
        }

        private static partial void DrawAssetExplorerHeader(ImGuiViewportPtr viewport, bool headerAtBottom, bool dockedTop, bool dockedBottom, bool isDocked, float minHeight, float reservedVerticalMargin)
        {
            ImGui.PushID(headerAtBottom ? "AssetExplorerHeaderBottom" : "AssetExplorerHeaderTop");

            if (headerAtBottom)
                ImGui.Separator();

            ImGuiDir arrowDir = _assetExplorerCollapsed ? ImGuiDir.Right : ImGuiDir.Down;
            if (ImGui.ArrowButton("##AssetExplorerCollapse", arrowDir))
                _assetExplorerCollapsed = !_assetExplorerCollapsed;

            ImGui.SameLine(0f, 6f);
            ImGui.TextUnformatted("Assets");

            ImGui.SameLine();
            if (ImGui.Button(isDocked ? "Undock" : "Dock Bottom"))
            {
                if (isDocked)
                {
                    _assetExplorerDockBottomEnabled = false;
                    _assetExplorerDockTopEnabled = false;
                    _assetExplorerUndockNextFrame = true;
                }
                else
                {
                    float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                    _assetExplorerDockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);
                    _assetExplorerDockBottomEnabled = true;
                    _assetExplorerDockTopEnabled = false;
                    _assetExplorerUndockNextFrame = false;
                }
            }

            ImGui.SameLine();
            string dockOrientationLabel = _assetExplorerDockTopEnabled ? "Dock Bottom" : "Dock Top";
            if (ImGui.Button(dockOrientationLabel))
            {
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                _assetExplorerDockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);

                if (_assetExplorerDockTopEnabled)
                {
                    _assetExplorerDockTopEnabled = false;
                    _assetExplorerDockBottomEnabled = true;
                }
                else
                {
                    _assetExplorerDockTopEnabled = true;
                    _assetExplorerDockBottomEnabled = false;
                }

                _assetExplorerUndockNextFrame = false;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(260.0f);
            if (ImGui.InputTextWithHint("##AssetExplorerSearch", "Filter files...", ref _assetExplorerSearchTerm, 256u))
                _assetExplorerSearchTerm = _assetExplorerSearchTerm.Trim();

            ImGui.SameLine();
            ImGui.TextDisabled("Filter applies to the current directory.");

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
                state.SelectedPath = null;
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
                    state.SelectedPath = null;
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
                        state.SelectedPath = null;
                        directoryChanged = true;
                    }
                }
            }

            if (directoryChanged)
                return;

            ImGui.Separator();

            _assetExplorerScratchEntries.Clear();

            try
            {
                foreach (var subdir in Directory.GetDirectories(directory))
                {
                    string name = Path.GetFileName(subdir) ?? subdir;
                    DateTime modifiedUtc;
                    try
                    {
                        modifiedUtc = Directory.GetLastWriteTimeUtc(subdir);
                    }
                    catch
                    {
                        modifiedUtc = DateTime.MinValue;
                    }

                    _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, NormalizeAssetExplorerPath(subdir), true, 0L, modifiedUtc));
                }

                foreach (var file in Directory.GetFiles(directory))
                {
                    string name = Path.GetFileName(file) ?? file;
                    if (!string.IsNullOrWhiteSpace(_assetExplorerSearchTerm)
                        && !name.Contains(_assetExplorerSearchTerm, StringComparison.OrdinalIgnoreCase)
                        && !file.Contains(_assetExplorerSearchTerm, StringComparison.OrdinalIgnoreCase))
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

                    _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, NormalizeAssetExplorerPath(file), false, size, modifiedUtc));
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

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.BordersOuter;

            Vector2 tableSize = ImGui.GetContentRegionAvail();
            if (ImGui.BeginTable($"{state.Id}FileTable", 4, tableFlags, tableSize))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthFixed, 170.0f);
                ImGui.TableHeadersRow();

                bool directoryChangedViaTable = false;

                foreach (var entry in _assetExplorerScratchEntries)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    bool selected = string.Equals(state.SelectedPath, entry.Path, StringComparison.OrdinalIgnoreCase);
                    ImGuiSelectableFlags selectableFlags = ImGuiSelectableFlags.SpanAllColumns;
                    string label = entry.IsDirectory ? $"[DIR] {entry.Name}" : entry.Name;
                    bool activated = ImGui.Selectable(label, selected, selectableFlags);
                    bool hovered = ImGui.IsItemHovered();

                    if (hovered)
                        ImGui.SetTooltip(entry.Path);

                    if (!entry.IsDirectory && ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
                    {
                        ImGuiAssetUtilities.SetPathPayload(entry.Path);
                        ImGui.TextUnformatted(entry.Name);
                        ImGui.EndDragDropSource();
                    }

                    if (entry.IsDirectory)
                    {
                        if (activated || (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)))
                        {
                            state.CurrentDirectory = entry.Path;
                            state.SelectedPath = null;
                            directoryChangedViaTable = true;
                            break;
                        }
                    }
                    else if (activated)
                    {
                        state.SelectedPath = entry.Path;
                    }

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
                }

                ImGui.EndTable();

                if (directoryChangedViaTable)
                    return;
            }

            if (!string.IsNullOrEmpty(state.SelectedPath))
            {
                ImGui.Separator();
                ImGui.TextUnformatted(Path.GetFileName(state.SelectedPath));
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(state.SelectedPath);
                ImGui.PopTextWrapPos();
            }
        }

        private static partial void HandleAssetExplorerDockResize(ImGuiViewportPtr viewport, float reservedLeft, float reservedRight, bool dockedTop)
        {
            const float minHeight = 220.0f;
            const float reservedVerticalMargin = 110.0f;
            const float handleHeight = 12.0f;

            Vector2 originalCursor = ImGui.GetCursorScreenPos();
            Vector2 windowPos = ImGui.GetWindowPos();
            Vector2 windowSize = ImGui.GetWindowSize();
            Vector2 handlePos = dockedTop
                ? new Vector2(windowPos.X, windowPos.Y + windowSize.Y - handleHeight)
                : windowPos;

            ImGui.SetCursorScreenPos(handlePos);
            ImGui.PushID("AssetExplorerDockResize");
            ImGui.InvisibleButton(string.Empty, new Vector2(windowSize.X, handleHeight), ImGuiButtonFlags.MouseButtonLeft);

            bool hovered = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();
            bool activated = ImGui.IsItemActivated();
            bool deactivated = ImGui.IsItemDeactivated();

            if (hovered || active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);

            if (activated)
            {
                _assetExplorerDockDragging = true;
                _assetExplorerDockDragStartHeight = _assetExplorerDockHeight;
                _assetExplorerDockDragStartMouseY = ImGui.GetIO().MousePos.Y;
            }

            if (active && _assetExplorerDockDragging)
            {
                float delta = ImGui.GetIO().MousePos.Y - _assetExplorerDockDragStartMouseY;
                float newHeight = dockedTop
                    ? _assetExplorerDockDragStartHeight + delta
                    : _assetExplorerDockDragStartHeight - delta;
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                newHeight = Math.Clamp(newHeight, minHeight, maxHeight);

                if (MathF.Abs(newHeight - _assetExplorerDockHeight) > float.Epsilon)
                {
                    _assetExplorerDockHeight = newHeight;
                    float dockWidth = Math.Max(320.0f, viewport.WorkSize.X - reservedLeft - reservedRight);
                    Vector2 size = new(dockWidth, _assetExplorerDockHeight);
                    if (dockedTop)
                    {
                        Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y);
                        ImGui.SetWindowPos(pos);
                        ImGui.SetWindowSize(size);
                    }
                    else
                    {
                        Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y + viewport.WorkSize.Y - _assetExplorerDockHeight);
                        ImGui.SetWindowPos(pos);
                        ImGui.SetWindowSize(size);
                    }
                }
            }

            if (deactivated)
                _assetExplorerDockDragging = false;

            var drawList = ImGui.GetWindowDrawList();
            uint color = ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
            Vector2 rectMin = dockedTop
                ? new Vector2(windowPos.X, windowPos.Y + windowSize.Y - handleHeight)
                : new Vector2(windowPos.X, windowPos.Y);
            Vector2 rectMax = dockedTop
                ? new Vector2(windowPos.X + windowSize.X, windowPos.Y + windowSize.Y)
                : new Vector2(windowPos.X + windowSize.X, windowPos.Y + handleHeight);
            drawList.AddRectFilled(rectMin, rectMax, color);

            ImGui.PopID();
            ImGui.SetCursorScreenPos(originalCursor);
        }

        private static partial void EnsureAssetExplorerState(AssetExplorerTabState state, string rootPath)
        {
            string normalizedRoot = NormalizeAssetExplorerPath(rootPath);
            if (string.IsNullOrEmpty(normalizedRoot) || !Directory.Exists(normalizedRoot))
            {
                state.RootPath = string.Empty;
                state.CurrentDirectory = string.Empty;
                state.SelectedPath = null;
                return;
            }

            if (!string.Equals(state.RootPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                state.RootPath = normalizedRoot;
                state.CurrentDirectory = normalizedRoot;
                state.SelectedPath = null;
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
                state.SelectedPath = null;
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
}
