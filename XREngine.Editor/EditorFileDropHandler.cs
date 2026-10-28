using System;
using System.IO;
using System.Linq;
using XREngine.Core.Files;
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
        RuntimeEngine.Windows.PostAnythingAdded += OnWindowAdded;
        foreach (var window in RuntimeEngine.Windows)
            AttachToWindow(window);
    }

    private static void OnWindowAdded(XRWindow window)
        => AttachToWindow(window);

    private static void AttachToWindow(XRWindow window)
    {
        try
        {
            window.FileDropped += HandleFileDrop;
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

        Engine.InvokeOnAppThread(() => ProcessFileDrop(window, paths), "Editor: Process file drop", executeNowIfAlreadyAppThread: true);
    }

    private static void ProcessFileDrop(XRWindow window, string[] paths)
    {
        _ = window; // Currently unused; retained for future multi-window routing.

        if (ImGuiExternalPathDrop.TryHandleExternalDrop(paths))
            return;

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

        // Resolve the target before any file is created, so a refused drop leaves no files.
        if (Engine.State.MainPlayer?.ControlledPawnComponent is not EditorFlyingCameraPawnComponent pawn)
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

        try
        {
            ApplyDroppedTexture(assetManager, Path.GetFullPath(sourcePath), material);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"The dropped texture was not applied: {exception.Message}");
        }
    }

    /// <summary>
    /// Imports the dropped image as a texture asset and assigns it to the first material slot in one
    /// Undo action. A source outside the assets root is copied in. New files go through the asset
    /// creation recovery journal, so Undo moves them back into the journal and Redo publishes them
    /// again. The material's source document becomes dirty on apply, Undo, and Redo.
    /// </summary>
    private static void ApplyDroppedTexture(AssetManager assetManager, string sourcePath, XRMaterial material)
    {
        string assetsRoot = Path.GetFullPath(assetManager.GameAssetsPath!);
        bool insideAssets = IsUnderRoot(assetsRoot, sourcePath);
        string imagePath = insideAssets
            ? sourcePath
            : GetUniqueFilePath(Path.Combine(assetsRoot, Path.GetFileName(sourcePath)));

        XRTexture2D texture = new() { Name = Path.GetFileNameWithoutExtension(imagePath) };
        if (!texture.Load3rdParty(sourcePath))
            throw new InvalidDataException($"Failed to import texture from '{sourcePath}'.");
        string assetPath = AssetManager.VerifyAssetPath(texture, Path.GetDirectoryName(imagePath)!);
        if (File.Exists(assetPath))
            assetPath = AssetManager.GetUniqueAssetPath(assetPath);
        texture.FilePath = assetPath;
        byte[] assetBytes = NativeAssetCreation.SerializeAsset(texture);

        var files = new List<NativeAssetCreationFile>(2);
        if (!insideAssets)
            files.Add(new NativeAssetCreationFile(imagePath, File.ReadAllBytes(sourcePath), Guid.NewGuid()));
        files.Add(new NativeAssetCreationFile(assetPath, assetBytes, texture.ID));

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Drop Texture");
        NativeAssetCreation.Create("Drop Texture", files);
        assetManager.NotifyExternalAssetSaved(texture, newAsset: true);
        UI.MaterialTextureSlotAction.ApplyInScope(material, 0, texture, "Drop Texture");
    }

    private static bool IsUnderRoot(string root, string path)
    {
        try
        {
            NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot: false);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static XRMaterial? GetMaterialForMesh(RenderableMesh mesh)
    {
        var renderer = mesh.CurrentLODRenderer ?? mesh.LODs.First?.Value.Renderer;
        return renderer?.Material;
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
