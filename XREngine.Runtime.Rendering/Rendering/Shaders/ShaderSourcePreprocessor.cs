using XREngine.Data.Core;
using RuntimeResolverOptions = XREngine.Rendering.ShaderSourceResolverOptions;

namespace XREngine.Rendering.Shaders;

public static class ShaderSourcePreprocessor
{
    public static string ResolveSource(string source, string? sourcePath, bool annotateIncludes = false)
        => ResolveSource(source, sourcePath, out _, annotateIncludes);

    public static string ResolveSource(string source, string? sourcePath, out List<string> resolvedPaths, bool annotateIncludes = false)
        => global::XREngine.Rendering.ShaderSourceResolver.ResolveSource(
            source,
            sourcePath,
            CreateResolverOptions(),
            out resolvedPaths,
            annotateIncludes);

    private static string? GetEngineShaderRoot()
        => string.IsNullOrWhiteSpace(RuntimeEngine.Assets?.EngineAssetsPath)
            ? null
            : Path.Combine(RuntimeEngine.Assets!.EngineAssetsPath, "Shaders");

    private static string? GetGameShaderRoot()
        => string.IsNullOrWhiteSpace(RuntimeEngine.Assets?.GameAssetsPath)
            ? null
            : Path.Combine(RuntimeEngine.Assets!.GameAssetsPath, "Shaders");

    private static RuntimeResolverOptions CreateResolverOptions()
    {
        List<string> additionalRoots = [];
        if (GetEngineShaderRoot() is string engineShaderRoot)
            additionalRoots.Add(engineShaderRoot);
        if (GetGameShaderRoot() is string gameShaderRoot)
            additionalRoots.Add(gameShaderRoot);

        return new RuntimeResolverOptions
        {
            AdditionalShaderRoots = additionalRoots,
            WarningLogger = message => Debug.LogWarning(message),
        };
    }
}
