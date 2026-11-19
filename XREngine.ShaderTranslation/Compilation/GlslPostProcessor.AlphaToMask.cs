using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Compilation;

public static partial class GlslPostProcessor
{
    private const string TranslatedFragmentMain = "_xrTranslatedFragmentMain";

    /// <summary>
    /// Adds a discard for fragments whose output alpha is below one half, for a pass with <c>AlphaToMask On</c>.
    /// </summary>
    /// <remarks>
    /// Alpha to coverage has no effect on a target with one sample in OpenGL. On such a target, Direct3D and
    /// Unity keep a fragment only when its alpha is at least one half. The discard gives that result on every
    /// target. On a multisampled target, the hardware coverage then applies only to fragments that pass.
    /// The method renames <c>main</c> and adds a new <c>main</c> that calls it, then tests the alpha of the
    /// <c>vec4</c> output at location 0.
    /// </remarks>
    /// <param name="glsl">The fragment stage in engine binding form.</param>
    /// <param name="result">The changed source, or <paramref name="glsl"/> when the method returns false.</param>
    /// <returns>False when the source has no single <c>main</c> function or no <c>vec4</c> output at location 0.</returns>
    public static bool TryAddAlphaToMaskDiscard(string glsl, out string result)
    {
        ArgumentNullException.ThrowIfNull(glsl);
        result = glsl;

        Match output = FragmentColorOutputPattern().Match(glsl);
        MatchCollection mains = MainDefinitionPattern().Matches(glsl);
        if (!output.Success || mains.Count != 1)
            return false;

        string outputName = output.Groups["name"].Value;
        Match main = mains[0];
        string renamed = string.Concat(glsl.AsSpan(0, main.Index), $"void {TranslatedFragmentMain}()", glsl.AsSpan(main.Index + main.Length));
        result = $$"""
            {{renamed.TrimEnd()}}

            void main()
            {
                {{TranslatedFragmentMain}}();
                if ({{outputName}}.a < 0.5)
                    discard;
            }

            """;
        return true;
    }

    [GeneratedRegex(@"^layout\(location = 0\) out vec4 (?<name>\w+);$", RegexOptions.Multiline)]
    private static partial Regex FragmentColorOutputPattern();

    [GeneratedRegex(@"^void main\(\)$", RegexOptions.Multiline)]
    private static partial Regex MainDefinitionPattern();
}
