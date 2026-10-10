using System.Text.Json;
using XREngine.Rendering;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Editor.Publishing;

/// <summary>Stages the exact canonical Uber request as ordinary schema-three Slang recipes for the offline cooker.</summary>
public static class BrowserUberBaseCookExporter
{
    /// <summary>Writes source/provenance only; the ordinary shader cooker owns compilation and artifact publication.</summary>
    public static IReadOnlyList<string> Write(XRMaterial source, string engineShaderRoot, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineShaderRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        UberBaseMaterialProfile profile = UberBaseSourcePreparation.Prepare(source);
        string root = Path.GetFullPath(engineShaderRoot), output = Path.GetFullPath(outputDirectory);
        List<(string Path, string Text)> files = [];
        foreach (EngineLitMaterialShaderSource file in EngineUberBaseShaderContract.RequiredSources)
            Stage(file, "WebGPU", string.Empty);
        foreach (EngineLitMaterialShaderSource file in EngineUberBaseShaderContract.RequiredDesktopSources)
            Stage(file, string.Empty, "Desktop");
        List<string> recipes = [];
        foreach (string pass in new[] { EngineUberBaseShaderContract.Pass, "depth-normal", "depth", "point-shadow-depth", "spot-shadow-depth", EngineUberBaseShaderContract.OrderPass })
        {
            string name = EngineUberBaseShaderContract.CookName(source.ID, profile.Variant.VariantHash, pass);
            string code = EngineUberBaseShaderContract.GenerateSource(profile.Features, profile.SourceIdentity,
                profile.Variant.VariantHash, profile.Variant.StaticProperties, profile.Variant.PipelineMacros, pass);
            files.Add((Path.Combine(output, name + ".slang"), code));
            string recipePath = Path.Combine(output, name + ".recipe.json");
            files.Add((recipePath, EngineUberBaseShaderContract.CreateRecipe(name, name + ".slang", profile.Features, pass)
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            recipes.Add(recipePath);
        }
        // Validate every source and destination before creating any output.
        foreach ((string path, string text) in files)
            if (File.Exists(path) && File.ReadAllText(path).ReplaceLineEndings("\n") != text.ReplaceLineEndings("\n"))
                throw new IOException($"UberBase.CookSourceCollision: '{Path.GetFileName(path)}' contains different source; select a fresh staging directory.");
        foreach ((string path, string text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, text);
        }
        return recipes;

        void Stage(EngineLitMaterialShaderSource file, string inputPrefix, string outputPrefix)
        {
            string text = File.ReadAllText(Path.Combine(root, inputPrefix, file.Path)).ReplaceLineEndings("\n");
            if (EngineUberBaseShaderContract.NormalizedHash(text) != file.Sha256)
                throw new NotSupportedException($"UberBase.CanonicalSourceChanged: '{file.Path}' differs from the admitted source closure.");
            files.Add((Path.Combine(output, outputPrefix, file.Path), text));
        }
    }
}
