using System;
using System.IO;
using System.Linq;
using XREngine.Rendering;
using XREngine.Rendering.Picking;
using XREngine.Components.Scene.Mesh;

namespace XREngine.Editor;

internal static class EditorFileDropHandler
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        Engine.Windows.PostAnythingAdded += OnWindowAdded;
        foreach (var window in Engine.Windows)
            AttachToWindow(window);
    }

    private static void OnWindowAdded(XRWindow window)
        => AttachToWindow(window);

    private static void AttachToWindow(XRWindow window)
    {
        try
        {
            window.Window.FileDrop += paths => HandleFileDrop(window, paths);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to attach file drop handler: {ex.Message}");
        }
    }

    private static void HandleFileDrop(XRWindow window, string[] paths)
    {
        if (paths is null || paths.Length == 0)
            return;

        Engine.InvokeOnMainThread(() => ProcessFileDrop(window, paths), "Editor: Process file drop", executeNowIfAlreadyMainThread: true);
    }

    private static void ProcessFileDrop(XRWindow window, string[] paths)
    {
        _ = window; // Currently unused; retained for future multi-window routing.

        var assetManager = Engine.Assets;
        if (assetManager is null)
        {
            Debug.LogWarning("Asset manager unavailable; cannot import dropped file.");
            return;
        }

        string? sourcePath = paths.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Debug.LogWarning("Dropped file path is invalid or missing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(assetManager.GameAssetsPath))
        {
            Debug.LogWarning("Game assets path is not set; cannot import dropped file.");
            return;
        }

        string assetsRoot = Path.GetFullPath(assetManager.GameAssetsPath);
        string normalizedSource = Path.GetFullPath(sourcePath);
        string copiedPath = normalizedSource.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase)
            ? normalizedSource
            : CopyIntoAssetsRoot(normalizedSource, assetsRoot);

        if (string.IsNullOrWhiteSpace(copiedPath) || !File.Exists(copiedPath))
        {
            Debug.LogWarning("Failed to copy dropped file into Assets.");
            return;
        }

        if (Engine.State.MainPlayer.ControlledPawn is not EditorFlyingCameraPawnComponent pawn)
        {
            Debug.LogWarning("No editor camera pawn available to apply dropped texture.");
            return;
        }

        if (!pawn.TryGetLastMeshHit(out MeshPickResult meshHit))
        {
            Debug.LogWarning("No mesh under the cursor to apply the dropped texture.");
            return;
        }

        XRMaterial? material = GetMaterialForMesh(meshHit.Mesh);
        if (material is null)
        {
            Debug.LogWarning("Target mesh has no material to receive a texture.");
            return;
        }

        if (material.Textures.Count == 0)
        {
            Debug.LogWarning("Material has no texture slots to assign the dropped texture.");
            return;
        }

        XRTexture2D texture = new();
        texture.Name = Path.GetFileNameWithoutExtension(copiedPath);
        texture.FilePath = copiedPath;

        if (!texture.Load3rdParty(copiedPath))
        {
            Debug.LogWarning($"Failed to import texture from '{copiedPath}'.");
            return;
        }

        string assetPath = AssetManager.VerifyAssetPath(texture, assetsRoot);
        if (File.Exists(assetPath))
            assetPath = AssetManager.GetUniqueAssetPath(assetPath);

        texture.FilePath = assetPath;
        assetManager.Save(texture);

        material.Textures[0] = texture;
    }

    private static XRMaterial? GetMaterialForMesh(RenderableMesh mesh)
    {
        var renderer = mesh.CurrentLODRenderer ?? mesh.LODs.First?.Value.Renderer;
        return renderer?.Material;
    }

    private static string CopyIntoAssetsRoot(string sourcePath, string assetsRoot)
    {
        string targetPath = Path.Combine(assetsRoot, Path.GetFileName(sourcePath));
        targetPath = GetUniqueFilePath(targetPath);
        File.Copy(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int index = 1;
        string candidate;

        do
        {
            candidate = Path.Combine(directory, $"{fileName} ({index++}){extension}");
        }
        while (File.Exists(candidate));

        return candidate;
    }
}
