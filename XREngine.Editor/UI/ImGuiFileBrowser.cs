using ImGuiNET;
using Silk.NET.Maths;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XREngine;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>
/// ImGui-based file and folder browser dialog rendered inside dedicated XRWindow instances.
/// Falls back to the embedded modal dialog only when the window cannot be created.
/// </summary>
public static class ImGuiFileBrowser
{
    private enum DialogRenderMode
    {
        StandaloneWindow,
        ModalFallback
    }

    /// <summary>
    /// The type of dialog to display.
    /// </summary>
    public enum DialogMode
    {
        OpenFile,
        SaveFile,
        SelectFolder
    }

    /// <summary>
    /// Result of a file browser dialog operation.
    /// </summary>
    public class DialogResult
    {
        public bool Success { get; init; }
        public string? SelectedPath { get; init; }
        public string[]? SelectedPaths { get; init; }
    }

    private class DialogState
    {
        public string Id { get; init; } = string.Empty;
        public DialogMode Mode { get; init; }
        public string Title { get; init; } = string.Empty;
        public string CurrentDirectory { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public string[] FilterExtensions { get; set; } = [];
        public int SelectedFilterIndex { get; set; }
        public bool AllowMultiSelect { get; init; }
        public HashSet<string> SelectedItems { get; } = [];
        public Action<DialogResult>? Callback { get; init; }
        public bool IsOpen { get; set; } = true;
        public byte[] FileNameBuffer { get; } = new byte[256];
        public byte[] PathBuffer { get; } = new byte[512];
        public List<DriveInfo> Drives { get; } = [];
        public List<FileSystemEntry> Entries { get; } = [];
        public string? ErrorMessage { get; set; }
        public bool NeedsRefresh { get; set; } = true;
        public bool ShowNewFolderInput { get; set; }
        public byte[] NewFolderBuffer { get; } = new byte[256];
        public Stack<string> BackHistory { get; } = new();
        public Stack<string> ForwardHistory { get; } = new();
        public DialogRenderMode RenderMode { get; set; } = DialogRenderMode.StandaloneWindow;
        public XRWindow? Window { get; set; }
        public XRWorld? World { get; set; }
        public XRViewport? Viewport { get; set; }
        public Action? RenderHandler { get; set; }
        public Action? WindowClosingHandler { get; set; }
        public Action<Silk.NET.Maths.Vector2D<int>>? FramebufferResizeHandler { get; set; }
        public bool IsCompleting { get; set; }
        public bool NeedsFocusOnFirstFrame { get; set; } = true;
        public bool PendingCloseRequested { get; set; }
        public bool PendingCloseSuccess { get; set; }
        public string? PendingClosePrimaryPath { get; set; }
        public string[]? PendingClosePaths { get; set; }
    }

    private class FileSystemEntry
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public DateTime LastModified { get; init; }
    }

    private static readonly Dictionary<string, DialogState> _activeDialogs = new();
    private static readonly object _stateLock = new();
    private static readonly bool _allowStandaloneDialogs =
        !string.Equals(Environment.GetEnvironmentVariable("XR_DISABLE_IMGUI_FILE_DIALOGS"), "1", StringComparison.OrdinalIgnoreCase);
    private static bool _standaloneDisabledNotified;

    /// <summary>
    /// Opens a file browser dialog.
    /// </summary>
    public static void Open(
        string id,
        DialogMode mode,
        string title,
        Action<DialogResult> callback,
        string? filter = null,
        string? initialDirectory = null,
        string? initialFileName = null,
        bool allowMultiSelect = false)
    {
        var state = new DialogState
        {
            Id = id,
            Mode = mode,
            Title = title,
            Callback = callback,
            AllowMultiSelect = allowMultiSelect && mode == DialogMode.OpenFile,
            CurrentDirectory = GetValidDirectory(initialDirectory),
            FileName = initialFileName ?? string.Empty
        };

        ParseFilter(filter, state);
        RefreshDrives(state);
        InitializeBuffers(state);

        DialogState? existing = null;
        lock (_stateLock)
        {
            if (_activeDialogs.TryGetValue(id, out existing))
                _activeDialogs.Remove(id);
            _activeDialogs[id] = state;
        }

        if (existing != null)
            CloseDialog(existing, false, (string?)null);

        if (!ShouldUseStandaloneDialogs())
        {
            state.RenderMode = DialogRenderMode.ModalFallback;
            state.IsOpen = true;
            return;
        }

        Engine.EnqueueMainThreadTask(() => InitializeStandaloneWindow(state));
    }

    /// <summary>
    /// Opens a file selection dialog.
    /// </summary>
    public static void OpenFile(string id, string title, Action<DialogResult> callback, string? filter = null, string? initialDirectory = null)
        => Open(id, DialogMode.OpenFile, title, callback, filter, initialDirectory);

    /// <summary>
    /// Opens a save file dialog.
    /// </summary>
    public static void SaveFile(string id, string title, Action<DialogResult> callback, string? filter = null, string? initialDirectory = null, string? initialFileName = null)
        => Open(id, DialogMode.SaveFile, title, callback, filter, initialDirectory, initialFileName);

    /// <summary>
    /// Opens a folder selection dialog.
    /// </summary>
    public static void SelectFolder(string id, string title, Action<DialogResult> callback, string? initialDirectory = null)
        => Open(id, DialogMode.SelectFolder, title, callback, null, initialDirectory);

    /// <summary>
    /// Draws any dialogs that had to fall back to the in-editor modal implementation.
    /// </summary>
    public static void DrawDialogs()
    {
        List<DialogState> fallbackStates;
        lock (_stateLock)
        {
            fallbackStates = _activeDialogs.Values
                .Where(s => s.RenderMode == DialogRenderMode.ModalFallback)
                .ToList();
        }

        List<string>? toRemove = null;
        foreach (var state in fallbackStates)
        {
            if (!DrawModalDialog(state) && !state.IsOpen)
            {
                toRemove ??= [];
                toRemove.Add(state.Id);
            }
        }

        if (toRemove != null)
        {
            lock (_stateLock)
            {
                foreach (string id in toRemove)
                    _activeDialogs.Remove(id);
            }
        }
    }

    private static void InitializeStandaloneWindow(DialogState state)
    {
        try
        {
            var world = CreateDialogWorld(state);
            var settings = new GameWindowStartupSettings
            {
                WindowTitle = state.Title,
                Width = 900,
                Height = 600,
                X = 200,
                Y = 200,
                WindowState = EWindowState.Windowed,
                LocalPlayers = 0,
                TargetWorld = world,
                TransparentFramebuffer = false,
                VSync = Engine.UserSettings.VSync != EVSyncMode.Off
            };

            XRWindow window = Engine.CreateWindow(settings);
            state.Window = window;
            state.World = world;

            var viewport = CreateDialogViewport(window, state);
            EnsureWorldInstanceIsRunning(window);

            state.RenderHandler = () => RenderStandaloneDialog(state);
            state.WindowClosingHandler = () => OnWindowClosing(state);
            state.FramebufferResizeHandler = (size) => OnFramebufferResize(state, size);
            window.RenderViewportsCallback += state.RenderHandler;
            window.Window.Closing += state.WindowClosingHandler;
            window.Window.FramebufferResize += state.FramebufferResizeHandler;
        }
        catch (Exception ex)
        {
            state.World = null;
            state.ErrorMessage = $"Failed to open file dialog window: {ex.Message}";
            state.RenderMode = DialogRenderMode.ModalFallback;
        }
    }

    private static bool ShouldUseStandaloneDialogs()
    {
        if (_allowStandaloneDialogs)
            return true;

        if (!_standaloneDisabledNotified)
        {
            _standaloneDisabledNotified = true;
            Debug.LogWarning("Standalone ImGui file dialogs are disabled (ImGui multi-context instability). Set XR_ENABLE_IMGUI_FILE_DIALOGS=1 to opt in at your own risk.");
        }

        return false;
    }

    private static XRWorld CreateDialogWorld(DialogState state)
    {
        var scene = new XRScene($"FileBrowserScene_{state.Id}");
        scene.RootNodes.Add(new SceneNode("FileBrowserRoot"));
        return new XRWorld($"FileBrowserWorld_{state.Id}", scene);
    }

    private static XRViewport CreateDialogViewport(XRWindow window, DialogState state)
    {
        var viewport = XRViewport.ForTotalViewportCount(window, window.Viewports.Count);
        viewport.AutomaticallyCollectVisible = false;
        viewport.AutomaticallySwapBuffers = false;
        viewport.AllowUIRender = false;
        if (window.TargetWorldInstance is not null)
            viewport.WorldInstanceOverride = window.TargetWorldInstance;
        
        // Initialize the viewport's region to the current window size
        // This is necessary because ForTotalViewportCount only sets percentages,
        // and Resize is needed to compute actual dimensions
        var size = window.Window.FramebufferSize;
        viewport.Resize((uint)size.X, (uint)size.Y, true);
        
        window.Viewports.Add(viewport);
        state.Viewport = viewport;
        return viewport;
    }

    private static void EnsureWorldInstanceIsRunning(XRWindow window)
    {
        var instance = window.TargetWorldInstance;
        if (instance is null || instance.IsPlaying)
            return;

        try
        {
            instance.BeginPlay().Wait();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"File browser world failed to begin play: {ex.Message}");
        }
    }

    private static void RenderStandaloneDialog(DialogState state)
    {
        // Don't render if dialog is closed or closing
        if (!state.IsOpen || state.IsCompleting || state.Window is null)
            return;

        state.Viewport ??= state.Window.Viewports.FirstOrDefault();
        var viewport = state.Viewport;
        if (viewport is null)
            return;

        var renderer = state.Window.Renderer;
        renderer.TryRenderImGui(viewport, null, null, () => DrawStandaloneFrame(state));
    }

    private static void DrawStandaloneFrame(DialogState state)
    {
        // Double-check we should still be rendering
        if (!state.IsOpen || state.IsCompleting)
            return;
            
        // Handle Escape key to cancel the dialog
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CloseDialog(state, false, (string?)null);
            return;
        }

        RefreshDirectoryIfNeeded(state);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowViewport(viewport.ID);
        
        // Force focus on first frame to ensure interactivity
        if (state.NeedsFocusOnFirstFrame)
        {
            ImGui.SetNextWindowFocus();
            state.NeedsFocusOnFirstFrame = false;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f));

        // Window flags for a fullscreen dialog - removed NoBringToFrontOnFocus to allow proper input handling
        var flags = ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoScrollbar;

        bool visible = ImGui.Begin($"##XRFileBrowser_{state.Id}", flags);
        if (visible)
        {
            DrawTitleBar(state);
            ImGui.Separator();
            DrawDialogContent(state);
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    private static void DrawTitleBar(DialogState state)
    {
        // Draw a custom title bar with title and close button
        float closeButtonWidth = 24f;
        float availableWidth = ImGui.GetContentRegionAvail().X;

        // Title text
        ImGui.TextUnformatted(state.Title);

        // Close button aligned to the right
        ImGui.SameLine(availableWidth - closeButtonWidth + ImGui.GetStyle().WindowPadding.X);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1f));
        if (ImGui.Button("X", new Vector2(closeButtonWidth, 0)))
        {
            CloseDialog(state, false, (string?)null);
        }
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Close (Esc)");
    }

    private static bool DrawModalDialog(DialogState state)
    {
        if (!state.IsOpen)
            return false;

        ImGui.OpenPopup(state.Title);

        var viewport = ImGui.GetMainViewport();
        var dialogSize = new Vector2(800, 550);
        ImGui.SetNextWindowPos(viewport.GetCenter() - dialogSize * 0.5f, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(dialogSize, ImGuiCond.Appearing);

        bool isOpen = state.IsOpen;
        if (!ImGui.BeginPopupModal(state.Title, ref isOpen, ImGuiWindowFlags.NoScrollbar))
        {
            if (!isOpen)
                CloseDialog(state, false, (string?)null);
            return state.IsOpen;
        }

        state.IsOpen = isOpen;
        RefreshDirectoryIfNeeded(state);
        DrawDialogContent(state);

        ImGui.EndPopup();
        return state.IsOpen;
    }

    private static void DrawDialogContent(DialogState state)
    {
        DrawNavigationBar(state);
        ImGui.Separator();

        float sidebarWidth = 150f;
        float contentHeight = ImGui.GetContentRegionAvail().Y - 70f;

        if (ImGui.BeginChild("Sidebar", new Vector2(sidebarWidth, contentHeight), ImGuiChildFlags.Border))
        {
            DrawDrivesSidebar(state);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("FileList", new Vector2(-1, contentHeight), ImGuiChildFlags.Border))
        {
            DrawFileList(state);
        }
        ImGui.EndChild();

        if (!string.IsNullOrEmpty(state.ErrorMessage))
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), state.ErrorMessage);
        }

        ImGui.Separator();

        if (state.Mode != DialogMode.SelectFolder)
        {
            DrawFileNameInput(state);
        }

        DrawActionButtons(state);
    }

    private static void DrawNavigationBar(DialogState state)
    {
        const float buttonSize = 26f;
        const float refreshWidth = 60f;
        const float newFolderWidth = 85f;
        float spacing = ImGui.GetStyle().ItemSpacing.X;

        bool canGoBack = state.BackHistory.Count > 0;
        if (!canGoBack) ImGui.BeginDisabled();
        if (ImGui.Button("<##Back", new Vector2(buttonSize, 0)))
        {
            NavigateBack(state);
        }
        if (ImGui.IsItemHovered() && canGoBack) ImGui.SetTooltip("Back");
        if (!canGoBack) ImGui.EndDisabled();

        ImGui.SameLine();

        bool canGoForward = state.ForwardHistory.Count > 0;
        if (!canGoForward) ImGui.BeginDisabled();
        if (ImGui.Button(">##Forward", new Vector2(buttonSize, 0)))
        {
            NavigateForward(state);
        }
        if (ImGui.IsItemHovered() && canGoForward) ImGui.SetTooltip("Forward");
        if (!canGoForward) ImGui.EndDisabled();

        ImGui.SameLine();

        bool canGoUp = Directory.GetParent(state.CurrentDirectory) != null;
        if (!canGoUp) ImGui.BeginDisabled();
        if (ImGui.Button("^##Up", new Vector2(buttonSize, 0)))
        {
            NavigateToParent(state);
        }
        if (ImGui.IsItemHovered() && canGoUp) ImGui.SetTooltip("Up one level");
        if (!canGoUp) ImGui.EndDisabled();

        ImGui.SameLine();

        float availableWidth = ImGui.GetContentRegionAvail().X;
        float pathInputWidth = availableWidth - refreshWidth - newFolderWidth - spacing * 2f;
        ImGui.SetNextItemWidth(Math.Max(50f, pathInputWidth));
        if (ImGui.InputText("##Path", state.PathBuffer, (uint)state.PathBuffer.Length, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            string newPath = ExtractString(state.PathBuffer);
            if (Directory.Exists(newPath))
                NavigateTo(state, newPath);
            else
                state.ErrorMessage = "Directory not found.";
        }

        ImGui.SameLine();

        if (ImGui.Button("Refresh", new Vector2(refreshWidth, 0)))
        {
            state.NeedsRefresh = true;
        }

        ImGui.SameLine();

        if (ImGui.Button("New Folder", new Vector2(newFolderWidth, 0)))
        {
            state.ShowNewFolderInput = true;
            Array.Clear(state.NewFolderBuffer);
        }

        if (state.ShowNewFolderInput)
        {
            ImGui.OpenPopup("NewFolderPopup");
        }

        if (ImGui.BeginPopup("NewFolderPopup"))
        {
            ImGui.Text("Folder Name:");
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("##NewFolderName", state.NewFolderBuffer, (uint)state.NewFolderBuffer.Length, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                CreateNewFolder(state);
            }
            if (ImGui.Button("Create", new Vector2(80f, 0)))
            {
                CreateNewFolder(state);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##NewFolder", new Vector2(80f, 0)))
            {
                state.ShowNewFolderInput = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private static void DrawFileList(DialogState state)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("FileTable", 3, tableFlags))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableHeadersRow();

        foreach (var entry in state.Entries)
        {
            bool isSelected = state.SelectedItems.Contains(entry.FullPath);
            bool shouldShow = entry.IsDirectory || state.Mode == DialogMode.SelectFolder || MatchesFilter(entry.Name, state);
            if (!shouldShow)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            string icon = entry.IsDirectory ? "[D] " : "    ";
            if (ImGui.Selectable($"{icon}{entry.Name}", isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (entry.IsDirectory)
                    {
                        NavigateTo(state, entry.FullPath);
                    }
                    else if (state.Mode == DialogMode.OpenFile)
                    {
                        CloseDialog(state, true, entry.FullPath);
                    }
                }
                else
                {
                    HandleSelection(state, entry, isSelected);
                }
            }

            ImGui.TableSetColumnIndex(1);
            if (!entry.IsDirectory)
                ImGui.TextUnformatted(FormatFileSize(entry.Size));

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(entry.LastModified.ToString("yyyy-MM-dd HH:mm"));
        }

        ImGui.EndTable();
    }

    private static void HandleSelection(DialogState state, FileSystemEntry entry, bool wasSelected)
    {
        if (state.AllowMultiSelect && ImGui.GetIO().KeyCtrl)
        {
            if (wasSelected)
                state.SelectedItems.Remove(entry.FullPath);
            else
                state.SelectedItems.Add(entry.FullPath);
        }
        else
        {
            state.SelectedItems.Clear();
            state.SelectedItems.Add(entry.FullPath);
            if (!entry.IsDirectory)
            {
                Array.Clear(state.FileNameBuffer);
                var bytes = System.Text.Encoding.UTF8.GetBytes(entry.Name);
                Array.Copy(bytes, state.FileNameBuffer, Math.Min(bytes.Length, state.FileNameBuffer.Length - 1));
            }
        }
    }

    private static void DrawFileNameInput(DialogState state)
    {
        ImGui.Text("File name:");
        ImGui.SameLine();

        float filterWidth = state.FilterExtensions.Length > 0 ? 180f : 0f;
        float availableWidth = ImGui.GetContentRegionAvail().X - filterWidth - ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(Math.Max(50f, availableWidth));
        ImGui.InputText("##FileName", state.FileNameBuffer, (uint)state.FileNameBuffer.Length);

        if (state.FilterExtensions.Length > 0)
        {
            ImGui.SameLine();
            DrawFilterDropdown(state);
        }
    }

    private static void DrawFilterDropdown(DialogState state)
    {
        ImGui.SetNextItemWidth(170f);
        if (ImGui.BeginCombo("##Filter", GetFilterDisplayName(state.Filter, state.SelectedFilterIndex)))
        {
            var filters = ParseFilterOptions(state.Filter);
            for (int i = 0; i < filters.Count; i++)
            {
                if (ImGui.Selectable(filters[i].DisplayName, i == state.SelectedFilterIndex))
                {
                    state.SelectedFilterIndex = i;
                    state.FilterExtensions = filters[i].Extensions;
                    state.NeedsRefresh = true;
                }
            }
            ImGui.EndCombo();
        }
    }

    private static void DrawActionButtons(DialogState state)
    {
        float buttonWidth = 100f;
        float totalWidth = buttonWidth * 2 + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - totalWidth));

        string confirm = state.Mode switch
        {
            DialogMode.OpenFile => "Open",
            DialogMode.SaveFile => "Save",
            DialogMode.SelectFolder => "Select",
            _ => "OK"
        };

        if (ImGui.Button(confirm, new Vector2(buttonWidth, 0)))
        {
            ConfirmSelection(state);
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
        {
            CloseDialog(state, false, (string?)null);
        }
    }

    private static void ConfirmSelection(DialogState state)
    {
        state.ErrorMessage = null;

        if (state.Mode == DialogMode.SelectFolder)
        {
            if (state.SelectedItems.Count > 0)
            {
                string selected = state.SelectedItems.First();
                if (Directory.Exists(selected))
                {
                    CloseDialog(state, true, selected);
                    return;
                }
            }

            CloseDialog(state, true, state.CurrentDirectory);
            return;
        }

        if (state.Mode == DialogMode.OpenFile)
        {
            if (state.AllowMultiSelect && state.SelectedItems.Count > 0)
            {
                var selected = state.SelectedItems.Where(File.Exists).ToArray();
                if (selected.Length > 0)
                {
                    CloseDialog(state, true, selected);
                    return;
                }
            }
            else if (state.SelectedItems.Count > 0)
            {
                string selected = state.SelectedItems.First();
                if (File.Exists(selected))
                {
                    CloseDialog(state, true, selected);
                    return;
                }
            }

            state.ErrorMessage = "Please select a file.";
            return;
        }

        string fileName = ExtractString(state.FileNameBuffer);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            state.ErrorMessage = "Please enter a file name.";
            return;
        }

        string fullPath = Path.Combine(state.CurrentDirectory, fileName);
        CloseDialog(state, true, fullPath);
    }

    private static void CloseDialog(DialogState state, bool success, string? path, bool windowInitiatedClose = false)
    {
        var paths = path is not null ? new[] { path } : null;

        if (!windowInitiatedClose && TryRequestStandaloneClose(state, success, path, paths))
            return;

        CompleteDialog(state, success, path, paths, windowInitiatedClose);
    }

    private static void CloseDialog(DialogState state, bool success, string[] paths, bool windowInitiatedClose = false)
    {
        var primaryPath = paths.FirstOrDefault();

        if (!windowInitiatedClose && TryRequestStandaloneClose(state, success, primaryPath, paths))
            return;

        CompleteDialog(state, success, primaryPath, paths, windowInitiatedClose);
    }

    private static bool TryRequestStandaloneClose(DialogState state, bool success, string? primaryPath, string[]? paths)
    {
        if (state.RenderMode != DialogRenderMode.StandaloneWindow || state.Window is null)
            return false;

        state.PendingCloseRequested = true;
        state.PendingCloseSuccess = success;
        state.PendingClosePrimaryPath = primaryPath;
        state.PendingClosePaths = paths;

        Engine.EnqueueMainThreadTask(() =>
        {
            var xrWindow = state.Window;
            if (xrWindow?.Window is { } silkWindow && !silkWindow.IsClosing)
            {
                try
                {
                    silkWindow.Close();
                }
                catch
                {
                    // ignored
                }
            }
        });

        return true;
    }

    private static void CompleteDialog(DialogState state, bool success, string? primaryPath, string[]? paths, bool windowInitiatedClose)
    {
        if (!state.IsOpen && !state.IsCompleting)
            return;

        state.IsOpen = false;
        state.IsCompleting = true;

        if (state.RenderMode == DialogRenderMode.ModalFallback)
        {
            ImGui.CloseCurrentPopup();
        }

        TearDownStandaloneWindow(state, windowInitiatedClose);

        var result = new DialogResult
        {
            Success = success,
            SelectedPath = primaryPath,
            SelectedPaths = paths
        };

        // Invoke callback safely - don't let exceptions propagate
        try
        {
            state.Callback?.Invoke(result);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "File browser callback threw an exception");
        }

        lock (_stateLock)
        {
            _activeDialogs.Remove(state.Id);
        }
    }

    private static void TearDownStandaloneWindow(DialogState state, bool windowInitiatedClose)
    {
        if (state.Window is null)
            return;

        var window = state.Window;
        
        // Unsubscribe event handlers first
        if (state.RenderHandler is not null)
            window.RenderViewportsCallback -= state.RenderHandler;
        if (state.WindowClosingHandler is not null)
            window.Window.Closing -= state.WindowClosingHandler;
        if (state.FramebufferResizeHandler is not null)
            window.Window.FramebufferResize -= state.FramebufferResizeHandler;

        if (state.Viewport is not null && window.Viewports.Contains(state.Viewport))
        {
            window.Viewports.Remove(state.Viewport);
            state.Viewport = null;
        }

        // Clear state references
        state.RenderHandler = null;
        state.WindowClosingHandler = null;
        state.FramebufferResizeHandler = null;
        state.Window = null;

        // Clean up world instance
        if (state.World is not null && XRWorldInstance.WorldInstances.TryGetValue(state.World, out var instance))
        {
            if (instance.IsPlaying)
            {
                try
                {
                    instance.EndPlay();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
            XRWorldInstance.WorldInstances.Remove(state.World);
        }
        state.World = null;
    }

    private static void OnWindowClosing(DialogState state)
    {
        if (state.IsCompleting)
            return;

        bool success = state.PendingCloseRequested ? state.PendingCloseSuccess : false;
        string? primaryPath = state.PendingCloseRequested ? state.PendingClosePrimaryPath : null;
        string[]? paths = state.PendingCloseRequested ? state.PendingClosePaths : null;

        state.PendingCloseRequested = false;
        state.PendingCloseSuccess = false;
        state.PendingClosePrimaryPath = null;
        state.PendingClosePaths = null;

        CompleteDialog(state, success, primaryPath, paths, true);
    }

    private static void OnFramebufferResize(DialogState state, Vector2D<int> newSize)
    {
        // The viewport is automatically resized by XRWindow.FramebufferResizeCallback
        // which calls vp.Resize() on all viewports. We just need to invalidate
        // any cached sizing info if necessary.
        if (state.Viewport is not null && state.Window is not null)
        {
            // Ensure the viewport matches the new framebuffer size
            state.Viewport.Resize((uint)newSize.X, (uint)newSize.Y, true);
        }
    }

    private static void CreateNewFolder(DialogState state)
    {
        string folderName = ExtractString(state.NewFolderBuffer);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            state.ErrorMessage = "Please enter a folder name.";
            return;
        }

        try
        {
            string newPath = Path.Combine(state.CurrentDirectory, folderName);
            Directory.CreateDirectory(newPath);
            state.NeedsRefresh = true;
            state.ShowNewFolderInput = false;
            state.ErrorMessage = null;
            ImGui.CloseCurrentPopup();
        }
        catch (Exception ex)
        {
            state.ErrorMessage = $"Failed to create folder: {ex.Message}";
        }
    }

    private static void DrawDrivesSidebar(DialogState state)
    {
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Quick Access");
        ImGui.Separator();

        DrawQuickAccessItem(state, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        DrawQuickAccessItem(state, "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        DrawQuickAccessItem(state, "Downloads", GetDownloadsFolder());

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Drives");
        ImGui.Separator();

        foreach (var drive in state.Drives)
        {
            string label = drive.Name;
            if (drive.IsReady)
            {
                try
                {
                    label = $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                }
                catch
                {
                    // ignored
                }
            }

            bool selected = state.CurrentDirectory.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(label, selected) && drive.IsReady)
            {
                NavigateTo(state, drive.Name);
            }
        }
    }

    private static void DrawQuickAccessItem(DialogState state, string label, string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        bool selected = state.CurrentDirectory.Equals(path, StringComparison.OrdinalIgnoreCase);
        if (ImGui.Selectable(label, selected))
        {
            NavigateTo(state, path);
        }
    }

    private static void NavigateTo(DialogState state, string newPath)
    {
        if (state.CurrentDirectory.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            return;

        state.BackHistory.Push(state.CurrentDirectory);
        state.ForwardHistory.Clear();
        state.CurrentDirectory = newPath;
        UpdatePathBuffer(state);
        state.NeedsRefresh = true;
        state.SelectedItems.Clear();
    }

    private static void NavigateBack(DialogState state)
    {
        if (state.BackHistory.Count == 0)
            return;

        state.ForwardHistory.Push(state.CurrentDirectory);
        state.CurrentDirectory = state.BackHistory.Pop();
        UpdatePathBuffer(state);
        state.NeedsRefresh = true;
        state.SelectedItems.Clear();
    }

    private static void NavigateForward(DialogState state)
    {
        if (state.ForwardHistory.Count == 0)
            return;

        state.BackHistory.Push(state.CurrentDirectory);
        state.CurrentDirectory = state.ForwardHistory.Pop();
        UpdatePathBuffer(state);
        state.NeedsRefresh = true;
        state.SelectedItems.Clear();
    }

    private static void NavigateToParent(DialogState state)
    {
        var parent = Directory.GetParent(state.CurrentDirectory);
        if (parent != null)
        {
            NavigateTo(state, parent.FullName);
        }
    }

    private static void RefreshDirectoryIfNeeded(DialogState state)
    {
        if (!state.NeedsRefresh)
            return;

        state.NeedsRefresh = false;
        RefreshDirectoryContents(state);
    }

    private static void RefreshDirectoryContents(DialogState state)
    {
        state.Entries.Clear();
        state.ErrorMessage = null;

        try
        {
            var dirInfo = new DirectoryInfo(state.CurrentDirectory);

            foreach (var dir in dirInfo.EnumerateDirectories().OrderBy(d => d.Name))
            {
                try
                {
                    state.Entries.Add(new FileSystemEntry
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsDirectory = true,
                        LastModified = dir.LastWriteTime
                    });
                }
                catch
                {
                    // ignored
                }
            }

            if (state.Mode != DialogMode.SelectFolder)
            {
                foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name))
                {
                    try
                    {
                        state.Entries.Add(new FileSystemEntry
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            IsDirectory = false,
                            Size = file.Length,
                            LastModified = file.LastWriteTime
                        });
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }
        catch (Exception ex)
        {
            state.ErrorMessage = $"Error reading directory: {ex.Message}";
        }
    }

    private static void RefreshDrives(DialogState state)
    {
        state.Drives.Clear();
        try
        {
            state.Drives.AddRange(DriveInfo.GetDrives());
        }
        catch
        {
            // ignored
        }
    }

    private static void InitializeBuffers(DialogState state)
    {
        if (!string.IsNullOrEmpty(state.FileName))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(state.FileName);
            Array.Copy(bytes, state.FileNameBuffer, Math.Min(bytes.Length, state.FileNameBuffer.Length - 1));
        }

        var pathBytes = System.Text.Encoding.UTF8.GetBytes(state.CurrentDirectory);
        Array.Copy(pathBytes, state.PathBuffer, Math.Min(pathBytes.Length, state.PathBuffer.Length - 1));
    }

    private static void UpdatePathBuffer(DialogState state)
    {
        Array.Clear(state.PathBuffer);
        var bytes = System.Text.Encoding.UTF8.GetBytes(state.CurrentDirectory);
        Array.Copy(bytes, state.PathBuffer, Math.Min(bytes.Length, state.PathBuffer.Length - 1));
    }

    private static string GetValidDirectory(string? path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            return path;

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(docs))
            return docs;

        return Environment.CurrentDirectory;
    }

    private static string? GetDownloadsFolder()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : null;
    }

    private static void ParseFilter(string? filter, DialogState state)
    {
        state.Filter = filter ?? "All Files|*.*";
        var options = ParseFilterOptions(state.Filter);
        state.FilterExtensions = options.Count > 0 ? options[0].Extensions : ["*.*"];
    }

    private static List<(string DisplayName, string[] Extensions)> ParseFilterOptions(string filter)
    {
        var result = new List<(string, string[])>();
        var parts = filter.Split('|');
        for (int i = 0; i < parts.Length - 1; i += 2)
        {
            string displayName = parts[i].Trim();
            string[] extensions = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .ToArray();
            result.Add((displayName, extensions));
        }

        if (result.Count == 0)
        {
            string[] extensions = filter.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .ToArray();
            result.Add(("Files", extensions.Length > 0 ? extensions : ["*.*"]));
        }

        return result;
    }

    private static string GetFilterDisplayName(string filter, int selectedIndex)
    {
        var options = ParseFilterOptions(filter);
        if (selectedIndex >= 0 && selectedIndex < options.Count)
            return options[selectedIndex].DisplayName;
        return "All Files";
    }

    private static bool MatchesFilter(string fileName, DialogState state)
    {
        if (state.FilterExtensions.Length == 0)
            return true;

        string lowerName = fileName.ToLowerInvariant();
        foreach (var ext in state.FilterExtensions)
        {
            if (ext is "*.*" or "*")
                return true;

            if (ext.StartsWith("*."))
            {
                string extension = ext[1..];
                if (lowerName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:0.#} {suffixes[i]}";
    }

    private static string ExtractString(byte[] buffer)
    {
        int nullIndex = Array.IndexOf(buffer, (byte)0);
        int length = nullIndex >= 0 ? nullIndex : buffer.Length;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }
}
