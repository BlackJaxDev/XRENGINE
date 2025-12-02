using System;
using System.IO;
using XREngine.Core.Files;
using XREngine.Diagnostics;
using XREngine.Rendering;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Provides context menu actions for <see cref="XRShader"/> assets.
/// </summary>
public static class ShaderAssetMenuActions
{
    public static void OpenInShaderLockingTool(XRAssetContextMenuContext context)
        => WithShaderPath(context, path =>
        {
            ShaderLockingWindow.Instance.Open();
            ShaderLockingWindow.Instance.LoadShaderFromPath(path);
        }, "Shader Locking Tool");

    public static void OpenInShaderAnalyzer(XRAssetContextMenuContext context)
        => WithShaderPath(context, path =>
        {
            ShaderAnalyzerWindow.Instance.Open();
            ShaderAnalyzerWindow.Instance.LoadShaderFromPath(path);
        }, "Shader Analyzer");

    private static void WithShaderPath(XRAssetContextMenuContext context, Action<string> action, string actionName)
    {
        string? path = ResolveShaderFilePath(context);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogWarning($"Unable to locate shader source for '{actionName}'.");
            return;
        }

        action(path);
    }

    private static string? ResolveShaderFilePath(XRAssetContextMenuContext context)
    {
        if (context.Asset is XRShader shader)
        {
            string? sourcePath = shader.Source?.FilePath;
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                return sourcePath;

            if (!string.IsNullOrEmpty(shader.Source?.Text))
                return WriteTempShader(shader, context.AssetPath);
        }

        if (!string.IsNullOrWhiteSpace(context.AssetPath))
        {
            string extension = Path.GetExtension(context.AssetPath);
            if (!string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase) && File.Exists(context.AssetPath))
                return context.AssetPath;
        }

        return null;
    }

    private static string WriteTempShader(XRShader shader, string assetPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(assetPath);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = shader.Name ?? "Shader";

        string fileName = $"{baseName}_{Guid.NewGuid():N}.glsl";
        string fullPath = Path.Combine(Path.GetTempPath(), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, shader.Source?.Text ?? string.Empty);
        return fullPath;
    }
}
