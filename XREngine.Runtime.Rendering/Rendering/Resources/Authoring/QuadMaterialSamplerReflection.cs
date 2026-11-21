using System.Collections.Concurrent;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>
/// Lists the samplers of a fragment shader, so tools can compare them with the inputs of an authored quad material.
/// Results are cached by shader path, because reflection parses the shader source.
/// </summary>
public static class QuadMaterialSamplerReflection
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the sampler names of an engine fragment shader.</summary>
    /// <param name="fragmentShaderPath">The engine-relative shader path.</param>
    /// <param name="samplers">The sampler names, or an empty list on failure.</param>
    /// <param name="error">The failure reason, or null.</param>
    public static bool TryGetSamplers(string fragmentShaderPath, out IReadOnlyList<string> samplers, out string? error)
    {
        if (_cache.TryGetValue(fragmentShaderPath, out IReadOnlyList<string>? cached))
        {
            samplers = cached;
            error = null;
            return true;
        }

        try
        {
            XRShader shader = XRShader.EngineShader(fragmentShaderPath, EShaderType.Fragment);
            XRRenderProgram program = new(false, true, shader);
            program.RefreshShaderInterfaceMetadata();
            string[] names = [.. program.TextureBindings.Keys];
            Array.Sort(names, StringComparer.Ordinal);
            samplers = names;
            _cache[fragmentShaderPath] = names;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            samplers = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Forgets cached results, for example after a shader file changed.</summary>
    public static void ClearCache()
        => _cache.Clear();
}
