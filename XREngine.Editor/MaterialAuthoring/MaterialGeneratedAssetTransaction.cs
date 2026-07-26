using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Adds a generated file and its sidecars to a material transaction. Existing
/// bytes are captured before mutation so undo restores overwritten assets and
/// redo deterministically regenerates them.
/// </summary>
public static class MaterialGeneratedAssetTransaction
{
    public static void AddWrite(
        MaterialAuthoringTransaction transaction,
        XRMaterial material,
        string projectAssetRoot,
        string outputPath,
        Func<CancellationToken, Task<IReadOnlyList<string>>> write,
        Action assignImportedAsset,
        Action restorePreviousAssignment,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string validated = MaterialTexturePacker.ValidateOutputPath(projectAssetRoot, outputPath);
        string[] relatedPaths = [validated, $"{validated}.xrepack.json", $"{validated}.xretexture.json"];
        Dictionary<string, byte[]?> before = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in relatedPaths)
            before[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;

        IReadOnlyList<string>? generated = null;
        transaction.AddStructural(
            material,
            $"Generate {Path.GetFileName(validated)}",
            () => File.Exists(validated) && !overwrite
                ? "The output exists and overwrite was not confirmed."
                : null,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                generated = write(cancellationToken).GetAwaiter().GetResult();
                foreach (string generatedPath in generated)
                    MaterialTexturePacker.ValidateOutputPath(projectAssetRoot, generatedPath);
                assignImportedAsset();
            },
            () =>
            {
                restorePreviousAssignment();
                foreach ((string path, byte[]? bytes) in before)
                {
                    if (bytes is null)
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, bytes);
                    }
                }
            },
            invalidatesVariant: true);
    }
}
