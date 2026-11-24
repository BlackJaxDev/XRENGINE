using System.Collections.Concurrent;
using XREngine.ShaderTranslation.Compilation;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.Scene.Importers;

/// <summary>
/// Translates Unity <c>.shader</c> files for the material importer and keeps the results.
/// </summary>
/// <remarks>
/// The translation is opt-in through <see cref="XREngineEnvironmentVariables.UnityShaderTranslation"/>. Many materials use the same shader
/// with the same keywords, so each result is kept by shader path, file write time, and keyword set.
/// The external tools (<c>slangc</c> and <c>spirv-cross</c>) are found one time. When a tool is missing,
/// <see cref="TryTranslate"/> returns false with a reason, and the importer uses its generic conversion.
/// </remarks>
public static class UnityShaderTranslationCache
{
    private static readonly Lazy<ShaderTranslator?> Translator = new(CreateTranslator, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, Lazy<ShaderTranslationResult>> Results = new(StringComparer.Ordinal);

    /// <summary>Returns true when <c>XRE_UNITY_SHADER_TRANSLATION</c> is set to <c>1</c>.</summary>
    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnityShaderTranslation), "1", StringComparison.Ordinal);

    /// <summary>Gets the folder of the Unity compatibility includes in the engine assets.</summary>
    public static string CompatibilityIncludeDirectory
        => Path.Combine(Engine.Assets.EngineAssetsPath, "Shaders", "UnityCompat");

    /// <summary>
    /// Translates a shader for one keyword set, or returns the kept result of an earlier call.
    /// </summary>
    /// <param name="shaderPath">The full path of the <c>.shader</c> file.</param>
    /// <param name="keywords">The keywords that the material enables.</param>
    /// <param name="projectRoot">The Unity project root. It is an include search directory.</param>
    /// <param name="result">The translation result. It can contain errors.</param>
    /// <param name="failure">The reason when no translation was possible, for example a missing tool.</param>
    /// <returns>True when a translation ran. Check <see cref="ShaderTranslationResult.Succeeded"/> for errors.</returns>
    public static bool TryTranslate(
        string shaderPath,
        IReadOnlySet<string> keywords,
        string projectRoot,
        out ShaderTranslationResult? result,
        out string? failure)
    {
        result = null;
        failure = null;
        ShaderTranslator? translator = Translator.Value;
        if (translator is null)
        {
            failure = $"Unity shader translation needs slangc and spirv-cross. Put them on PATH or set {SlangSpirvCompiler.OverrideVariable} and {SpirvCrossGlslEmitter.OverrideVariable}.";
            return false;
        }

        FileInfo file = new(shaderPath);
        if (!file.Exists)
        {
            failure = $"The Unity shader '{shaderPath}' does not exist.";
            return false;
        }

        string[] sortedKeywords = [.. keywords];
        Array.Sort(sortedKeywords, StringComparer.Ordinal);
        string key = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{string.Join(' ', sortedKeywords)}";
        ShaderTranslationRequest request = new()
        {
            MaterialKeywords = new HashSet<string>(keywords, StringComparer.Ordinal),
            IncludeDirectories = [CompatibilityIncludeDirectory, projectRoot],
            WorkDirectory = Path.Combine(Path.GetTempPath(), "XREngine", "UnityShaderTranslation"),
        };

        // Lazy makes parallel imports of the same variant wait for one translation.
        // The translator uses ConfigureAwait(false), so the blocking wait cannot deadlock.
        Lazy<ShaderTranslationResult> entry = Results.GetOrAdd(
            key,
            _ => new Lazy<ShaderTranslationResult>(
                () => translator.TranslateFileAsync(file.FullName, request).GetAwaiter().GetResult(),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            result = entry.Value;
            return true;
        }
        catch (Exception ex) when (ex is ShaderCompileException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Results.TryRemove(key, out _);
            failure = $"Unity shader translation failed for '{shaderPath}'. {ex.Message}";
            return false;
        }
    }

    private static ShaderTranslator? CreateTranslator()
    {
        SlangSpirvCompiler? compiler = SlangSpirvCompiler.FindDefault();
        SpirvCrossGlslEmitter? crossCompiler = SpirvCrossGlslEmitter.FindDefault();
        return compiler is null || crossCompiler is null ? null : new ShaderTranslator(compiler, crossCompiler);
    }
}
