using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static readonly ConcurrentDictionary<(string ShaderPath, string Property), bool> s_colorBlendMaskProperties = new();

    /// <summary>
    /// Converts a source global-mask blend value to the engine mask blend mode
    /// (0 replace, 1 subtract, 2 multiply, 3 divide, 4 minimum, 5 maximum, 6 average, 7 add).
    /// </summary>
    /// <remarks>
    /// Newer source shaders blend global masks with their 20-entry color blend list (normal,
    /// darken, multiply, ..., subtract 18, divide 19), while older ones use the engine order
    /// directly. The shader property declaration shows which list a material uses. Color blend
    /// modes without a mask equivalent return -1, so the caller refuses the option.
    /// </remarks>
    /// <param name="shaderPath">The source shader file of the material, or null when it is unknown.</param>
    /// <param name="blendProperty">The blend type property, for example <c>_MatcapMaskGlobalMaskBlendType</c>.</param>
    /// <param name="sourceBlend">The authored blend value.</param>
    private static int ResolveSourceMaskBlend(string? shaderPath, string blendProperty, int sourceBlend)
    {
        if (!UsesSourceColorBlendList(shaderPath, blendProperty))
            return sourceBlend;

        return sourceBlend switch
        {
            0 => 0,
            1 => 4,
            2 => 2,
            5 => 5,
            8 => 7,
            18 => 1,
            19 => 3,
            _ => -1,
        };
    }

    /// <summary>
    /// True when the shader declares <paramref name="blendProperty"/> with the 20-entry color
    /// blend list. The result is cached for each shader file and property.
    /// </summary>
    private static bool UsesSourceColorBlendList(string? shaderPath, string blendProperty)
    {
        if (string.IsNullOrWhiteSpace(shaderPath) ||
            !shaderPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            return false;

        return s_colorBlendMaskProperties.GetOrAdd((shaderPath, blendProperty), static key =>
        {
            try
            {
                string pattern = $@"Subtract,\s*18\b.*\]\s*{Regex.Escape(key.Property)}\s*\(";
                foreach (string line in File.ReadLines(key.ShaderPath))
                {
                    if (line.Contains(key.Property, StringComparison.Ordinal) &&
                        Regex.IsMatch(line, pattern, RegexOptions.CultureInvariant))
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        });
    }
}
