using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private const string ArchiveImportPopupId = "Import Archive";
        private const string ArchiveImportFileDialogId = "ArchiveImportFileDialog";
        private const string ArchiveDestinationFolderDialogId = "ArchiveImportDestinationFolderDialog";

        private static ArchiveImportDialogState? _archiveImportDialog;

        private sealed class ArchiveImportDialogState
        {
            public bool Visible;
            public bool RequestOpen;
            public string ArchivePath = string.Empty;
            public ArchiveTreeResult? Tree;
            public ArchiveEntryNode? Root;
            public HashSet<string> SelectedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string DestinationRelativePath = string.Empty;
            public string? Error;
            public int SelectedFileCount;
            public long SelectedTotalBytes;
        }

        private static void OpenArchiveImportDialog()
        {
            XREngine.Editor.UI.ImGuiFileBrowser.OpenFile(
                ArchiveImportFileDialogId,
                "Select Archive",
                result =>
                {
                    if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                        BeginArchiveImport(result.SelectedPath);
                },
                "Archive Files (*.zip;*.rar;*.7z;*.tar;*.tgz;*.gz;*.unitypackage)|*.zip;*.rar;*.7z;*.tar;*.tgz;*.gz;*.unitypackage|All Files (*.*)|*.*");
        }

        private static void BeginArchiveImport(string archivePath)
        {
            try
            {
                var tree = ArchiveImportUtilities.BuildArchiveTree(archivePath);
                var root = tree.Root;
                var state = new ArchiveImportDialogState
                {
                    Visible = true,
                    RequestOpen = true,
                    ArchivePath = archivePath,
                    Tree = tree,
                    Root = root,
                    DestinationRelativePath = Path.Combine("Imported", Path.GetFileNameWithoutExtension(archivePath))
                        .Replace('\\', '/'),
                };
                PopulateArchiveSelection(state, root);
                _archiveImportDialog = state;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to inspect archive '{archivePath}'.");
            }
        }

        private static void DrawArchiveImportDialog()
        {
            var state = _archiveImportDialog;
            if (state is null || !state.Visible)
                return;

            if (state.RequestOpen)
            {
                ImGui.OpenPopup(ArchiveImportPopupId);
                state.RequestOpen = false;
            }

            bool open = true;
            if (ImGui.BeginPopupModal(ArchiveImportPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            {
                DrawArchiveImportDialogContents(state);
                ImGui.EndPopup();
            }

            if (!open)
                CloseArchiveImportDialog(closePopup: false);
        }

        private static void DrawArchiveImportDialogContents(ArchiveImportDialogState state)
        {
            ImGui.Text("Archive:");
            ImGui.TextWrapped(state.ArchivePath);

            ImGui.Separator();
            ImGui.Text("Destination Folder (relative to Assets)");
            string destination = state.DestinationRelativePath;
            if (ImGui.InputText("##ArchiveDestination", ref destination, 256))
            {
                state.DestinationRelativePath = destination.Replace('\\', '/');
                state.Error = null;
            }
            ImGui.SameLine();
            if (ImGui.Button("Browse"))
                OpenArchiveDestinationBrowser(state);

            var assets = Engine.Assets;
            bool withinRoot = true;
            string resolvedDestination;
            if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
            {
                withinRoot = false;
                resolvedDestination = "No project Assets folder available.";
            }
            else
            {
                resolvedDestination = ResolveArchiveDestination(assets.GameAssetsPath, state.DestinationRelativePath, out withinRoot);
            }

            if (assets is not null && !withinRoot)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
                ImGui.TextWrapped("Destination must stay inside the project's Assets folder.");
                ImGui.PopStyleColor();
            }
            ImGui.TextWrapped($"Importing to: {resolvedDestination}");

            ImGui.Separator();
            ImGui.Text("Select files to import:");
            ImGui.BeginChild("ArchiveTree", new Vector2(640, 360), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
            if (state.Root is null || state.Root.Children.Count == 0)
            {
                ImGui.TextWrapped("Archive contains no importable files.");
            }
            else
            {
                foreach (var child in state.Root.Children)
                    DrawArchiveTreeNode(state, child);
            }
            ImGui.EndChild();

            ImGui.Separator();
            ImGui.Text($"Selected: {state.SelectedFileCount} file(s), {FormatFileSize(state.SelectedTotalBytes)}");

            if (!string.IsNullOrWhiteSpace(state.Error))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
                ImGui.TextWrapped(state.Error);
                ImGui.PopStyleColor();
            }

            if (ImGui.Button("Select All"))
                SelectAllArchiveEntries(state);
            ImGui.SameLine();
            if (ImGui.Button("Clear Selection"))
            {
                state.SelectedFiles.Clear();
                UpdateArchiveSelectionMetrics(state);
            }

            ImGui.Spacing();
            bool canImport = state.SelectedFileCount > 0;
            if (!canImport)
                ImGui.BeginDisabled();
            if (ImGui.Button("Import", new Vector2(120, 0)))
            {
                if (TryStartArchiveImportJob(state))
                    CloseArchiveImportDialog();
            }
            if (!canImport)
                ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
                CloseArchiveImportDialog();
        }

        private static void DrawArchiveTreeNode(ArchiveImportDialogState state, ArchiveEntryNode node)
        {
            bool partiallySelected = false;
            bool selected = DetermineArchiveSelection(state, node, ref partiallySelected);
            bool checkboxState = selected;

            bool toggled = ImGui.Checkbox($"##chk_{node.FullPath}", ref checkboxState);

            ImGui.SameLine();
            if (!node.IsDirectory)
            {
                ImGui.TextUnformatted($"{node.Name} ({FormatFileSize(node.Size)})");
                if (toggled)
                    SetArchiveSelectionForNode(state, node, checkboxState);
                return;
            }

            var nodeFlags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding;
            string label = partiallySelected ? $"[~] {node.Name}" : node.Name;
            bool open = ImGui.TreeNodeEx(node.FullPath, nodeFlags, label);
            if (toggled)
                SetArchiveSelectionForNode(state, node, checkboxState);
            if (open)
            {
                foreach (var child in node.Children)
                    DrawArchiveTreeNode(state, child);
                ImGui.TreePop();
            }
        }

        private static bool DetermineArchiveSelection(ArchiveImportDialogState state, ArchiveEntryNode node, ref bool partiallySelected)
        {
            if (!node.IsDirectory)
                return state.SelectedFiles.Contains(node.FullPath);

            bool any = false;
            bool all = true;
            foreach (var child in node.Children)
            {
                bool childPartial = false;
                bool childSelected = DetermineArchiveSelection(state, child, ref childPartial);
                if (childPartial)
                    partiallySelected = true;
                if (childSelected || childPartial)
                    any = true;
                if (!(childSelected && !childPartial))
                    all = false;
            }

            if (any && !all)
                partiallySelected = true;

            return any && all;
        }

        private static void SetArchiveSelectionForNode(ArchiveImportDialogState state, ArchiveEntryNode node, bool select)
        {
            if (!node.IsDirectory)
            {
                if (select)
                    state.SelectedFiles.Add(node.FullPath);
                else
                    state.SelectedFiles.Remove(node.FullPath);
                UpdateArchiveSelectionMetrics(state);
                state.Error = null;
                return;
            }

            foreach (var file in EnumerateArchiveFiles(node))
            {
                if (select)
                    state.SelectedFiles.Add(file);
                else
                    state.SelectedFiles.Remove(file);
            }

            UpdateArchiveSelectionMetrics(state);
            state.Error = null;
        }

        private static IEnumerable<string> EnumerateArchiveFiles(ArchiveEntryNode node)
        {
            if (!node.IsDirectory)
            {
                yield return node.FullPath;
                yield break;
            }

            foreach (var child in node.Children)
            {
                foreach (var leaf in EnumerateArchiveFiles(child))
                    yield return leaf;
            }
        }

        private static void PopulateArchiveSelection(ArchiveImportDialogState state, ArchiveEntryNode root)
        {
            if (root is null)
            {
                state.SelectedFiles.Clear();
                UpdateArchiveSelectionMetrics(state);
                return;
            }

            state.SelectedFiles.Clear();
            foreach (var path in EnumerateArchiveFiles(root))
                state.SelectedFiles.Add(path);
            UpdateArchiveSelectionMetrics(state);
            state.Error = null;
        }

        private static void SelectAllArchiveEntries(ArchiveImportDialogState state)
        {
            if (state.Root is null)
                return;

            state.SelectedFiles.Clear();
            foreach (var path in EnumerateArchiveFiles(state.Root))
                state.SelectedFiles.Add(path);
            UpdateArchiveSelectionMetrics(state);
            state.Error = null;
        }

        private static void UpdateArchiveSelectionMetrics(ArchiveImportDialogState state)
        {
            if (state.Root is null)
            {
                state.SelectedFileCount = 0;
                state.SelectedTotalBytes = 0;
                return;
            }

            int count = 0;
            long bytes = 0;
            var stack = new Stack<ArchiveEntryNode>();
            stack.Push(state.Root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.IsDirectory)
                {
                    foreach (var child in node.Children)
                        stack.Push(child);
                }
                else if (state.SelectedFiles.Contains(node.FullPath))
                {
                    count++;
                    bytes += node.Size;
                }
            }

            state.SelectedFileCount = count;
            state.SelectedTotalBytes = bytes;
        }

        private static void OpenArchiveDestinationBrowser(ArchiveImportDialogState state)
        {
            var assets = Engine.Assets;
            if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
            {
                Debug.LogWarning("Cannot select destination because the project Assets folder is unavailable.");
                return;
            }

            XREngine.Editor.UI.ImGuiFileBrowser.SelectFolder(
                ArchiveDestinationFolderDialogId,
                "Select Destination Folder",
                result =>
                {
                    if (!result.Success || string.IsNullOrEmpty(result.SelectedPath))
                        return;

                    ApplyArchiveDestination(state, result.SelectedPath);
                });
        }

        private static void ApplyArchiveDestination(ArchiveImportDialogState state, string absolutePath)
        {
            if (!ReferenceEquals(_archiveImportDialog, state))
                return;

            var assets = Engine.Assets;
            if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
                return;

            var assetsRoot = Path.GetFullPath(assets.GameAssetsPath);
            var selectedPath = Path.GetFullPath(absolutePath);
            if (!selectedPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                state.Error = "Destination must be inside the project's Assets folder.";
                return;
            }

            state.Error = null;
            state.DestinationRelativePath = selectedPath.Length == assetsRoot.Length
                ? string.Empty
                : selectedPath[(assetsRoot.Length + 1)..].Replace('\\', '/');
        }

        private static string ResolveArchiveDestination(string assetsRoot, string? relativePath, out bool withinRoot)
        {
            var trimmed = string.IsNullOrWhiteSpace(relativePath)
                ? string.Empty
                : relativePath.Replace('\\', '/').Trim('/');

            var combined = Path.GetFullPath(Path.Combine(assetsRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
            assetsRoot = Path.GetFullPath(assetsRoot);
            withinRoot = combined.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase);
            return combined;
        }

        private static bool TryStartArchiveImportJob(ArchiveImportDialogState state)
        {
            state.Error = null;

            if (state.Tree is null || state.Root is null)
            {
                state.Error = "Archive metadata unavailable. Reopen the dialog and try again.";
                return false;
            }

            if (state.SelectedFileCount == 0)
            {
                state.Error = "Select at least one file to import.";
                return false;
            }

            var assets = Engine.Assets;
            if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
            {
                state.Error = "Project Assets folder not available.";
                return false;
            }

            string destination = ResolveArchiveDestination(assets.GameAssetsPath, state.DestinationRelativePath, out bool withinRoot);
            if (!withinRoot)
            {
                state.Error = "Destination must stay inside the project's Assets folder.";
                return false;
            }

            var selections = state.SelectedFiles.ToArray();
            if (selections.Length == 0)
            {
                state.Error = "Select at least one file to import.";
                return false;
            }

            System.Collections.IEnumerable Routine()
            {
                foreach (var update in ArchiveImportUtilities.ExtractSelectedEntries(state.ArchivePath, selections, destination, state.Tree))
                    yield return new JobProgress(update.Progress, update);
            }

            string label = $"Import {Path.GetFileName(state.ArchivePath)}";
            var job = Engine.Jobs.Schedule(Routine());
            EditorJobTracker.Track(job, label, FormatArchiveImportProgress);
            return true;
        }

        private static void CloseArchiveImportDialog(bool closePopup = true)
        {
            _archiveImportDialog = null;
            if (closePopup)
                ImGui.CloseCurrentPopup();
        }

    private static string? FormatArchiveImportProgress(object? payload)
    {
        if (payload is ArchiveExtractionProgress progress)
            return progress.Message ?? progress.CurrentItem;

        return payload?.ToString();
    }
}
