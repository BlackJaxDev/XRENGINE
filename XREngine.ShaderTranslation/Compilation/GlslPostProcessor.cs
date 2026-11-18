using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Rewrites SPIRV-Cross GLSL into the form that the engine GLSL path binds.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Uniform structs become loose uniforms, so the engine binds material and engine values by name.</item>
/// <item>Combined samplers take the texture property name, for example <c>_MainTex</c>.</item>
/// <item>Sampler uniforms lose explicit bindings; the engine assigns texture units and descriptors.</item>
/// <item>Vertex inputs move to the engine attribute locations.</item>
/// </list>
/// The input must be SPIRV-Cross output with plain uniform structs. The rewrites depend on its stable layout.
/// </remarks>
public static partial class GlslPostProcessor
{
    private const string CombinedPrefix = "SPIRV_Cross_Combined";
    private const string DummySampler = "SPIRV_Cross_DummySampler";

    /// <summary>Applies all rewrites to one stage.</summary>
    /// <param name="glsl">The SPIRV-Cross output.</param>
    /// <param name="reflection">The Slang reflection of the same stage.</param>
    /// <param name="isVertexStage">True to remap vertex inputs.</param>
    public static GlslPostProcessResult Process(string glsl, SlangStageReflection reflection, bool isVertexStage)
    {
        ArgumentNullException.ThrowIfNull(glsl);
        ArgumentNullException.ThrowIfNull(reflection);

        List<string> notes = [];
        List<string> unsupported = [];
        glsl = glsl.Replace("\r\n", "\n", StringComparison.Ordinal);
        glsl = SamplerBindingPattern().Replace(glsl, "uniform sampler");
        glsl = RenameCombinedSamplers(glsl, reflection, notes);
        glsl = ExpandUniformStructs(glsl);
        glsl = RewriteStorageBuffers(glsl);
        if (isVertexStage)
            glsl = RemapVertexInputs(glsl, reflection, unsupported);

        return new GlslPostProcessResult(glsl, unsupported, notes);
    }

    private static string RenameCombinedSamplers(string glsl, SlangStageReflection reflection, List<string> notes)
    {
        List<(string Combined, string Texture, string Sampler)> pairs = [];
        foreach (Match match in CombinedDeclarationPattern().Matches(glsl))
        {
            string combined = match.Groups["name"].Value;
            if (TrySplitCombinedName(combined[CombinedPrefix.Length..], reflection, out string texture, out string sampler))
                pairs.Add((combined, texture, sampler));
            else
                notes.Add($"Combined sampler '{combined}' could not be matched to a texture and sampler.");
        }

        if (pairs.Count == 0)
            return glsl;

        Dictionary<string, string> renames = new(StringComparer.Ordinal);
        HashSet<string> removedDeclarations = new(StringComparer.Ordinal);
        foreach (IGrouping<string, (string Combined, string Texture, string Sampler)> group in pairs.GroupBy(pair => pair.Texture))
        {
            string? primary = null;
            foreach ((string combined, string texture, string sampler) in group)
            {
                if (sampler == DummySampler)
                    continue;
                if (primary is null)
                {
                    primary = combined;
                    renames[combined] = texture;
                    continue;
                }

                string split = texture + "__" + sampler;
                renames[combined] = split;
                notes.Add($"Texture '{texture}' is sampled with more than one sampler. '{split}' must bind the same texture.");
            }

            foreach ((string combined, string texture, string sampler) in group)
            {
                if (sampler != DummySampler)
                    continue;

                // A texel fetch ignores sampler state, so it can use the texture's primary combined sampler.
                renames[combined] = texture;
                if (primary is not null)
                    removedDeclarations.Add(combined);
            }
        }

        if (removedDeclarations.Count > 0)
        {
            glsl = CombinedDeclarationPattern().Replace(glsl, match =>
                removedDeclarations.Contains(match.Groups["name"].Value) ? string.Empty : match.Value);
        }

        // Longer names first, so a name never matches inside a longer generated name.
        foreach (KeyValuePair<string, string> rename in renames.OrderByDescending(entry => entry.Key.Length))
            glsl = Regex.Replace(glsl, $@"\b{Regex.Escape(rename.Key)}\b", rename.Value);
        return glsl;
    }

    private static bool TrySplitCombinedName(string rest, SlangStageReflection reflection, out string texture, out string sampler)
    {
        foreach (string candidateTexture in reflection.Textures.OrderByDescending(name => name.Length))
        {
            if (!rest.StartsWith(candidateTexture, StringComparison.Ordinal))
                continue;

            string remainder = rest[candidateTexture.Length..];
            if (remainder == DummySampler || reflection.Samplers.Contains(remainder))
            {
                texture = candidateTexture;
                sampler = remainder;
                return true;
            }
        }

        texture = string.Empty;
        sampler = string.Empty;
        return false;
    }

    private static string ExpandUniformStructs(string glsl)
    {
        Dictionary<string, (int Start, int Length, string[] Members)> structs = new(StringComparer.Ordinal);
        foreach (Match match in StructPattern().Matches(glsl))
        {
            string[] members = match.Groups["body"].Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            structs[match.Groups["name"].Value] = (match.Index, match.Length, members);
        }

        List<(string Type, string Instance)> instances = [];
        foreach (Match match in UniformInstancePattern().Matches(glsl))
        {
            string type = match.Groups["type"].Value;
            if (structs.ContainsKey(type))
                instances.Add((type, match.Groups["instance"].Value));
        }

        foreach ((string type, string instance) in instances)
        {
            StringBuilder loose = new();
            foreach (string member in structs[type].Members)
                loose.Append("uniform ").Append(member).Append('\n');

            glsl = Regex.Replace(glsl, $@"^uniform {Regex.Escape(type)} {Regex.Escape(instance)};\n", loose.ToString(), RegexOptions.Multiline);
            glsl = Regex.Replace(glsl, $@"\b{Regex.Escape(instance)}\.", string.Empty);

            // Remove the struct definition when nothing else uses the type.
            if (Regex.Matches(glsl, $@"\b{Regex.Escape(type)}\b").Count == 1)
                glsl = Regex.Replace(glsl, $@"struct {Regex.Escape(type)}\n\{{\n[^}}]*\}};\n\n?", string.Empty);
        }

        return glsl;
    }

    private static string RewriteStorageBuffers(string glsl)
    {
        bool rewritten = false;
        glsl = StorageBufferPattern().Replace(glsl, match =>
        {
            rewritten = true;
            string instance = match.Groups["instance"].Value;
            string binding = match.Groups["binding"].Value;
            bool isEngineBuffer = EngineStorageBlockNames.TryGetValue(instance, out string? known);
            string blockName = isEngineBuffer ? known! : instance + "Buffer";
            string setQualifier = isEngineBuffer ? "XRENGINE_FORWARD_DESCRIPTOR_SET " : string.Empty;
            return $"layout(std430, {setQualifier}binding = {binding}) {match.Groups["access"].Value}buffer {blockName}\n{{\n    {match.Groups["type"].Value} {instance}[];\n}};";
        });
        if (!rewritten)
            return glsl;

        glsl = StorageMemberPattern().Replace(glsl, "${instance}");

        // The engine places pass-owned forward resources in descriptor set 3 on Vulkan. OpenGL ignores sets.
        const string setDefine = "#ifdef XRENGINE_VULKAN\n#define XRENGINE_FORWARD_DESCRIPTOR_SET set = 3,\n#else\n#define XRENGINE_FORWARD_DESCRIPTOR_SET\n#endif\n";
        if (glsl.Contains("XRENGINE_FORWARD_DESCRIPTOR_SET", StringComparison.Ordinal))
        {
            int versionEnd = glsl.IndexOf('\n', StringComparison.Ordinal) + 1;
            glsl = glsl[..versionEnd] + setDefine + glsl[versionEnd..];
        }

        return glsl;
    }

    /// <summary>
    /// Engine forward-pass storage blocks by instance name. A translated program that reads one of these
    /// buffers uses the engine block name and the engine forward descriptor set.
    /// </summary>
    private static readonly Dictionary<string, string> EngineStorageBlockNames = new(StringComparer.Ordinal)
    {
        ["DirectionalLights"] = "ForwardDirectionalLightsBuffer",
        ["PointLights"] = "ForwardPointLightsBuffer",
        ["SpotLights"] = "ForwardSpotLightsBuffer",
    };

    private static string RemapVertexInputs(string glsl, SlangStageReflection reflection, List<string> unsupported)
    {
        Dictionary<int, int> locations = [];
        foreach (VertexInputReflection input in reflection.VertexInputs)
        {
            int engineLocation = EngineVertexAttributes.GetLocation(input.Semantic, input.SemanticIndex);
            if (engineLocation < 0)
            {
                unsupported.Add(input.FullSemantic);
                continue;
            }

            locations[input.Location] = engineLocation;
        }

        return VertexInputPattern().Replace(glsl, match =>
        {
            int location = int.Parse(match.Groups["location"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return locations.TryGetValue(location, out int engineLocation)
                ? $"layout(location = {engineLocation}) in "
                : match.Value;
        });
    }

    [GeneratedRegex(@"layout\(binding = \d+\) uniform sampler")]
    private static partial Regex SamplerBindingPattern();

    [GeneratedRegex(@"^uniform \w+ (?<name>SPIRV_Cross_Combined\w+);\n", RegexOptions.Multiline)]
    private static partial Regex CombinedDeclarationPattern();

    [GeneratedRegex(@"^struct (?<name>\w+)\n\{\n(?<body>[^}]*)\};\n", RegexOptions.Multiline)]
    private static partial Regex StructPattern();

    [GeneratedRegex(@"^uniform (?<type>\w+) (?<instance>\w+);$", RegexOptions.Multiline)]
    private static partial Regex UniformInstancePattern();

    [GeneratedRegex(@"layout\((?:set = \d+, )?binding = (?<binding>\d+), std430\) (?<access>(?:readonly |writeonly |restrict )*)buffer \w+\n\{\n    (?<type>\w+) _member0\[\];\n\} (?<instance>\w+);")]
    private static partial Regex StorageBufferPattern();

    [GeneratedRegex(@"\b(?<instance>\w+)\._member0\b")]
    private static partial Regex StorageMemberPattern();

    [GeneratedRegex(@"layout\(location = (?<location>\d+)\) in ")]
    private static partial Regex VertexInputPattern();
}
