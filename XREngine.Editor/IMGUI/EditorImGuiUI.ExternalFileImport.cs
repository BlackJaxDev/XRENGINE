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
    private const string ExternalFileImportPopupId = "Import External Assets";
    private const string ExternalFileImportDialogId = "ExternalAssetImportFileDialog";
    private const string ExternalFolderImportDialogId = "ExternalAssetImportFolderDialog";
    private const string ExternalImportDestinationFolderDialogId = "ExternalAssetImportDestinationFolderDialog";

    private const string ExternalImportFileFilter =
        "Common Assets (*.unity;*.prefab;*.mat;*.shader;*.glsl;*.frag;*.vert;*.geom;*.comp;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.tga;*.exr;*.hdr;*.gif;*.fbx;*.obj;*.gltf;*.glb;*.dae;*.wav;*.ogg;*.mp3;*.flac)|*.unity;*.prefab;*.mat;*.shader;*.glsl;*.frag;*.vert;*.geom;*.comp;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.tga;*.exr;*.hdr;*.gif;*.fbx;*.obj;*.gltf;*.glb;*.dae;*.wav;*.ogg;*.mp3;*.flac|" +
        "Unity Assets (*.unity;*.prefab;*.mat;*.shader;*.meta)|*.unity;*.prefab;*.mat;*.shader;*.meta|" +
        "Textures (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.tga;*.exr;*.hdr;*.gif)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.tga;*.exr;*.hdr;*.gif|" +
        "Shaders (*.shader;*.glsl;*.frag;*.vert;*.geom;*.comp)|*.shader;*.glsl;*.frag;*.vert;*.geom;*.comp|" +
        "Models (*.fbx;*.obj;*.gltf;*.glb;*.dae)|*.fbx;*.obj;*.gltf;*.glb;*.dae|" +
        "Audio (*.wav;*.ogg;*.mp3;*.flac)|*.wav;*.ogg;*.mp3;*.flac|" +
        "All Files (*.*)|*.*";

    private static readonly HashSet<string> ExternalImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".unity",
        ".prefab",
        ".mat",
        ".shader",
        ".glsl",
        ".frag",
        ".vert",
        ".geom",
        ".tesc",
        ".tese",
        ".comp",
        ".task",
        ".mesh",
        ".fs",
        ".vs",
        ".gs",
        ".tcs",
        ".tes",
        ".cs",
        ".ts",
        ".ms",
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff",
        ".tga",
        ".exr",
        ".hdr",
        ".gif",
        ".fbx",
        ".obj",
        ".gltf",
        ".glb",
        ".dae",
        ".wav",
        ".ogg",
        ".mp3",
        ".flac",
        ".otf",
        ".ttf",
        ".json",
        ".txt",
    };

    private static ExternalFileImportDialogState? _externalFileImportDialog;
    private static string? _externalImportLastSourceDirectory;

    private sealed class ExternalFileImportDialogState
    {
        public bool Visible;
        public bool RequestOpen;
        public bool SourceIsFolder;
        public string[] SourcePaths = [];
        public string DestinationRelativePath = "Imported";
        public bool CopySidecarMeta = true;
        public bool OverwriteExisting;
        public bool ImportAfterCopy = true;
        public string? Error;
    }

    private readonly record struct ExternalImportEntry(string SourcePath, string RelativePath, bool ImportAfterCopy);

    private sealed record ExternalFileImportProgress(float Progress, string Message);

    private static void OpenExternalFilesImportDialog()
    {
        XREngine.Editor.UI.ImGuiFileBrowser.Open(
            ExternalFileImportDialogId,
            XREngine.Editor.UI.ImGuiFileBrowser.DialogMode.OpenFile,
            "Select External Assets",
            result =>
            {
                if (!result.Success)
                    return;

                string[] paths = result.SelectedPaths is { Length: > 0 }
                    ? result.SelectedPaths
                    : string.IsNullOrWhiteSpace(result.SelectedPath) ? [] : [result.SelectedPath];

                BeginExternalFileImport(paths, sourceIsFolder: false);
            },
            ExternalImportFileFilter,
            _externalImportLastSourceDirectory,
            allowMultiSelect: true);
    }

    private static void OpenExternalFolderImportDialog()
    {
        XREngine.Editor.UI.ImGuiFileBrowser.SelectFolder(
            ExternalFolderImportDialogId,
            "Select External Asset Folder",
            result =>
            {
                if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                    return;

                BeginExternalFileImport([result.SelectedPath], sourceIsFolder: true);
            },
            _externalImportLastSourceDirectory);
    }

    private static void BeginExternalFileImport(string[] sourcePaths, bool sourceIsFolder)
    {
        string[] normalized = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => sourceIsFolder ? Directory.Exists(path) : File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return;

        _externalImportLastSourceDirectory = sourceIsFolder
            ? normalized[0]
            : Path.GetDirectoryName(normalized[0]);

        string destination = sourceIsFolder
            ? Path.Combine("Imported", Path.GetFileName(normalized[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            : "Imported";

        _externalFileImportDialog = new ExternalFileImportDialogState
        {
            Visible = true,
            RequestOpen = true,
            SourceIsFolder = sourceIsFolder,
            SourcePaths = normalized,
            DestinationRelativePath = destination.Replace('\\', '/'),
        };
    }

    private static void DrawExternalFileImportDialog()
    {
        ExternalFileImportDialogState? state = _externalFileImportDialog;
        if (state is null || !state.Visible)
            return;

        if (state.RequestOpen)
        {
            ImGui.OpenPopup(ExternalFileImportPopupId);
            state.RequestOpen = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal(ExternalFileImportPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawExternalFileImportDialogContents(state);
            ImGui.EndPopup();
        }

        if (!open)
            CloseExternalFileImportDialog(closePopup: false);
    }

    private static void DrawExternalFileImportDialogContents(ExternalFileImportDialogState state)
    {
        ImGui.TextUnformatted(state.SourceIsFolder ? "Folder:" : "Files:");
        ImGui.BeginChild("ExternalImportSources", new Vector2(640, state.SourceIsFolder ? 56 : 132), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
        foreach (string source in state.SourcePaths)
            ImGui.TextWrapped(source);
        ImGui.EndChild();

        ImGui.Separator();
        ImGui.TextUnformatted("Destination Folder (relative to Assets)");
        string destination = state.DestinationRelativePath;
        if (ImGui.InputText("##ExternalImportDestination", ref destination, 256))
        {
            state.DestinationRelativePath = destination.Replace('\\', '/');
            state.Error = null;
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse"))
            OpenExternalImportDestinationBrowser(state);

        AssetManager? assets = Engine.Assets;
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

        if (!withinRoot)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
            ImGui.TextWrapped("Destination must stay inside the project's Assets folder.");
            ImGui.PopStyleColor();
        }
        ImGui.TextWrapped($"Importing to: {resolvedDestination}");

        ImGui.Separator();
        ImGui.Checkbox("Import copied files after copy", ref state.ImportAfterCopy);
        ImGui.Checkbox("Overwrite existing files", ref state.OverwriteExisting);
        if (!state.SourceIsFolder)
            ImGui.Checkbox("Copy Unity .meta sidecars when present", ref state.CopySidecarMeta);

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
            ImGui.TextWrapped(state.Error);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        bool canImport = withinRoot && assets is not null && state.SourcePaths.Length > 0;
        if (!canImport)
            ImGui.BeginDisabled();
        if (ImGui.Button("Import", new Vector2(120, 0)))
        {
            if (TryStartExternalFileImportJob(state))
                CloseExternalFileImportDialog();
        }
        if (!canImport)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            CloseExternalFileImportDialog();
    }

    private static void OpenExternalImportDestinationBrowser(ExternalFileImportDialogState state)
    {
        AssetManager? assets = Engine.Assets;
        if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
        {
            Debug.LogWarning("Cannot select destination because the project Assets folder is unavailable.");
            return;
        }

        XREngine.Editor.UI.ImGuiFileBrowser.SelectFolder(
            ExternalImportDestinationFolderDialogId,
            "Select Import Destination",
            result =>
            {
                if (!result.Success || string.IsNullOrEmpty(result.SelectedPath))
                    return;

                ApplyExternalImportDestination(state, result.SelectedPath);
            },
            assets.GameAssetsPath);
    }

    private static void ApplyExternalImportDestination(ExternalFileImportDialogState state, string absolutePath)
    {
        if (!ReferenceEquals(_externalFileImportDialog, state))
            return;

        AssetManager? assets = Engine.Assets;
        if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
            return;

        string assetsRoot = Path.GetFullPath(assets.GameAssetsPath);
        string selectedPath = Path.GetFullPath(absolutePath);
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

    private static bool TryStartExternalFileImportJob(ExternalFileImportDialogState state)
    {
        state.Error = null;

        AssetManager? assets = Engine.Assets;
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

        ExternalImportEntry[] entries;
        try
        {
            entries = BuildExternalImportPlan(state).ToArray();
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            return false;
        }

        if (entries.Length == 0)
        {
            state.Error = "No files were selected for import.";
            return false;
        }

        bool overwrite = state.OverwriteExisting;
        bool importAfterCopy = state.ImportAfterCopy;

        IEnumerable Routine()
        {
            int copied = 0;
            int imported = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                ExternalImportEntry entry = entries[i];
                string targetPath = Path.Combine(destination, entry.RelativePath);
                CopyExternalImportEntry(entry, targetPath, overwrite);
                copied++;

                if (importAfterCopy && entry.ImportAfterCopy && TryImportCopiedExternalAsset(assets, targetPath))
                    imported++;

                float progress = (i + 1) / (float)entries.Length;
                yield return new JobProgress(progress, new ExternalFileImportProgress(
                    progress,
                    $"Imported {copied}/{entries.Length} file(s), generated {imported} asset(s)"));
            }

            InvalidateAssetExplorerSnapshots(_assetExplorerGameState);
            yield return new JobProgress(1f, new ExternalFileImportProgress(
                1f,
                $"Import complete. Copied {copied} file(s), generated {imported} asset(s)."));
        }

        string label = state.SourceIsFolder
            ? $"Import {Path.GetFileName(state.SourcePaths[0])}"
            : $"Import {entries.Length} external file(s)";
        var job = Engine.Jobs.Schedule(Routine());
        EditorJobTracker.Track(job, label, FormatExternalFileImportProgress);
        return true;
    }

    private static IEnumerable<ExternalImportEntry> BuildExternalImportPlan(ExternalFileImportDialogState state)
    {
        if (state.SourceIsFolder)
        {
            string root = Path.GetFullPath(state.SourcePaths[0]);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(root, file);
                if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                    continue;

                yield return new ExternalImportEntry(file, relativePath, ShouldAttemptExternalAssetImport(file));
            }
            yield break;
        }

        HashSet<string> emittedTargets = new(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in state.SourcePaths)
        {
            string fileName = Path.GetFileName(sourcePath);
            if (emittedTargets.Add(fileName))
                yield return new ExternalImportEntry(sourcePath, fileName, ShouldAttemptExternalAssetImport(sourcePath));

            if (!state.CopySidecarMeta || sourcePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            string metaPath = $"{sourcePath}.meta";
            if (!File.Exists(metaPath))
                continue;

            string metaFileName = Path.GetFileName(metaPath);
            if (emittedTargets.Add(metaFileName))
                yield return new ExternalImportEntry(metaPath, metaFileName, ImportAfterCopy: false);
        }
    }

    private static void CopyExternalImportEntry(ExternalImportEntry entry, string targetPath, bool overwrite)
    {
        string normalizedTarget = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(normalizedTarget);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(normalizedTarget) && !overwrite)
            throw new IOException($"'{normalizedTarget}' already exists. Enable overwrite or choose another destination.");

        File.Copy(entry.SourcePath, normalizedTarget, overwrite);
    }

    private static bool TryImportCopiedExternalAsset(AssetManager assets, string targetPath)
    {
        if (!ShouldAttemptExternalAssetImport(targetPath))
            return false;

        try
        {
            return assets.ReimportThirdPartyFile(targetPath);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"External asset import failed for '{targetPath}'.");
            return false;
        }
    }

    private static bool ShouldAttemptExternalAssetImport(string path)
    {
        string extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) &&
               ExternalImportableExtensions.Contains(extension);
    }

    private static void CloseExternalFileImportDialog(bool closePopup = true)
    {
        _externalFileImportDialog = null;
        if (closePopup)
            ImGui.CloseCurrentPopup();
    }

    private static string? FormatExternalFileImportProgress(object? payload)
        => payload is ExternalFileImportProgress progress
            ? progress.Message
            : payload?.ToString();
}
