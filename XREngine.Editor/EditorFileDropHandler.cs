using System;
using System.IO;
using System.Linq;
using XREngine.Core.Files;
using XREngine.Rendering;

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

        if (!pawn.TryCaptureNativeTextureDrop(window, out XRMaterial material, out Func<bool> admission))
        {
            Debug.LogWarning("The dropped texture has no current authored material target in this window.");
            return;
        }

        if (material.Textures.Count == 0)
        {
            Debug.LogWarning("Material has no texture slots to assign the dropped texture.");
            return;
        }

        try
        {
            ApplyDroppedTexture(assetManager, Path.GetFullPath(sourcePath), material, admission);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"The dropped texture was not applied: {exception.Message}");
        }
    }

    /// <summary>
    /// Imports the dropped image as a texture asset and assigns it to the first material slot in one
    /// Undo action. The texture contains the admitted image pixels. New files go through the asset
    /// creation recovery journal, so Undo moves them back into the journal and Redo publishes them
    /// again. The material's source document becomes dirty on apply, Undo, and Redo.
    /// </summary>
    private static void ApplyDroppedTexture(AssetManager assetManager, string sourcePath, XRMaterial material,
        Func<bool> targetAdmission)
    {
        string assetsRoot = Path.GetFullPath(assetManager.GameAssetsPath!);
        var recovery = assetManager.CreationRecovery;
        bool Admitted() => ReferenceEquals(Engine.Assets, assetManager) &&
            ReferenceEquals(assetManager.CreationRecovery, recovery) && recovery is not null &&
            string.Equals(Path.GetFullPath(assetManager.GameAssetsPath!), assetsRoot, StringComparison.OrdinalIgnoreCase) &&
            targetAdmission();
        void RequireAdmission()
        {
            if (!Admitted())
                throw new InvalidOperationException("The texture drop project or target changed.");
        }

        XRTexture2D texture = NativeTextureFileImport.Read(sourcePath, assetsRoot, Admitted);
        byte[] assetBytes = NativeAssetCreation.SerializeAsset(texture);
        if (assetBytes.LongLength > NativeTextureFileImport.MaximumAssetBytes)
            throw new InvalidDataException("The imported texture exceeds the asset byte limit.");
        RequireAdmission();

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Drop Texture");
        bool initialPublication = true;
        NativeAssetCreation.Create("Drop Texture",
            [new NativeAssetCreationFile(texture.FilePath!, assetBytes, texture.ID)], afterPublish: () =>
            {
                if (!initialPublication) return;
                RequireAdmission();
                initialPublication = false;
            });
        assetManager.NotifyExternalAssetSaved(texture, newAsset: true);
        if (!UI.MaterialTextureSlotAction.ApplyInScope(material, 0, texture, "Drop Texture", Admitted))
            throw new InvalidOperationException("The material target refused the imported texture.");
    }
}
