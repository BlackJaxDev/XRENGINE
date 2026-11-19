using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;
using XREngine.ShaderTranslation.Diagnostics;
using XREngine.ShaderTranslation.ShaderLab;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private const int MaxTranslationErrorLength = 800;
    private static readonly ConcurrentDictionary<string, XRTexture2D> TranslatedDefaultTextures = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a material from the translated Unity shader of the material, when the translation is enabled and succeeds.
    /// </summary>
    /// <remarks>
    /// The material uses the first forward base pass, or the first unlit pass. The supplied vertex stage
    /// replaces the engine vertex shader, so compute skinning and the engine stereo paths do not apply.
    /// Shader parameters take the material values, then the property defaults. Uniforms that are not
    /// material properties stay engine uniforms. A failure adds a warning and returns false, so the
    /// caller can use the generic conversion.
    /// </remarks>
    private static bool TryConvertTranslatedUnityShaderMaterial(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings,
        [NotNullWhen(true)] out XRMaterial? material)
    {
        material = null;
        if (!UnityShaderTranslationCache.IsEnabled)
            return false;

        string? shaderPath = resolver.Resolve(document.Shader.Guid);
        if (string.IsNullOrWhiteSpace(shaderPath) ||
            !shaderPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(shaderPath))
        {
            return false;
        }

        if (!UnityShaderTranslationCache.TryTranslate(shaderPath, document.ValidKeywords, resolver.ProjectRoot,
            out ShaderTranslationResult? result, out string? failure))
        {
            warnings.Add(failure ?? $"Unity shader translation failed for '{shaderPath}'.");
            return false;
        }

        if (!result!.Succeeded || result.Document is null)
        {
            warnings.Add($"Unity shader '{shaderPath}' did not translate. {FirstTranslationError(result)}");
            return false;
        }

        TranslatedPass? pass = SelectTranslatedColorPass(result.Passes);
        if (pass is null)
        {
            warnings.Add($"Unity shader '{shaderPath}' has no translated forward base or unlit pass with vertex and fragment stages.");
            return false;
        }

        ShaderLabDocument shader = result.Document;
        List<XRShader> stages = new(pass.Stages.Count);
        foreach (TranslatedStage stage in pass.Stages)
            stages.Add(new XRShader(MapShaderType(stage.Stage), stage.Glsl) { Name = $"{shader.Name} {pass.Label} {stage.Stage}" });

        XRTexture?[] textures = ResolveTranslatedTextures(document, resolver, warnings, shader, pass);
        material = new XRMaterial(textures, stages) { Name = document.Name };
        ShaderLabRenderStateMapper.Apply(material, pass, shader, document, warnings);
        BindTranslatedParameters(material, document, shader, resolver.ProjectRoot, warnings);
        return true;
    }

    private static TranslatedPass? SelectTranslatedColorPass(IReadOnlyList<TranslatedPass> passes)
    {
        TranslatedPass? unlit = null;
        foreach (TranslatedPass pass in passes)
        {
            if (!HasStage(pass, ShaderProgramStage.Vertex) || !HasStage(pass, ShaderProgramStage.Fragment))
                continue;
            if (pass.Kind == EnginePassKind.ForwardBase)
                return pass;
            if (pass.Kind == EnginePassKind.Unlit)
                unlit ??= pass;
        }

        return unlit;
    }

    private static bool HasStage(TranslatedPass pass, ShaderProgramStage stage)
    {
        foreach (TranslatedStage translated in pass.Stages)
            if (translated.Stage == stage)
                return true;

        return false;
    }

    private static EShaderType MapShaderType(ShaderProgramStage stage)
        => stage switch
        {
            ShaderProgramStage.Vertex => EShaderType.Vertex,
            ShaderProgramStage.Hull => EShaderType.TessControl,
            ShaderProgramStage.Domain => EShaderType.TessEvaluation,
            ShaderProgramStage.Geometry => EShaderType.Geometry,
            _ => EShaderType.Fragment,
        };

    private static string FirstTranslationError(ShaderTranslationResult result)
    {
        foreach (ShaderTranslationDiagnostic diagnostic in result.Diagnostics)
            if (diagnostic.Severity == ShaderTranslationSeverity.Error)
            {
                // Compiler output can be long. The import report needs only the start.
                string text = diagnostic.ToString();
                return text.Length <= MaxTranslationErrorLength ? text : string.Concat(text.AsSpan(0, MaxTranslationErrorLength), "...");
            }

        return string.Empty;
    }

    /// <summary>
    /// Returns one texture for each 2D sampler uniform of the pass. The sampler name of each texture is the
    /// uniform name, which the engine uses to bind it.
    /// </summary>
    /// <remarks>
    /// A material texture uses its asset, then the Unity default of its property. A sampler that is not a
    /// material texture, for example a global texture that a Unity script sets, gets a black texture.
    /// </remarks>
    private static XRTexture?[] ResolveTranslatedTextures(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings,
        ShaderLabDocument shader,
        TranslatedPass pass)
    {
        List<XRTexture?> textures = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (TranslatedStage stage in pass.Stages)
        {
            foreach (Match match in GlslSamplerUniformPattern().Matches(stage.Glsl))
            {
                string type = match.Groups["type"].Value;
                string name = match.Groups["name"].Value;
                if (!seen.Add(name))
                    continue;

                if (!string.Equals(type, "sampler2D", StringComparison.Ordinal))
                {
                    warnings.Add($"Shader '{shader.Name}' texture '{name}' is a {type}. Only 2D textures are bound now.");
                    continue;
                }

                ShaderLabProperty? property = shader.FindProperty(name);
                if (property is null && !document.Textures.ContainsKey(name))
                {
                    // A Unity global texture, such as the AudioLink _AudioTexture. The backend fallbacks are
                    // not the same on OpenGL and Vulkan, so the importer binds black, which means no data.
                    warnings.Add($"Shader '{shader.Name}' samples '{name}', which is not a material texture. A black texture is bound.");
                    textures.Add(GetTranslatedDefaultTexture(name, "black"));
                    continue;
                }

                textures.Add(ResolveGenericTexture(document, resolver, warnings, name)
                    ?? GetTranslatedDefaultTexture(name, property?.DefaultTexture));
            }
        }

        return [.. textures];
    }

    /// <summary>Returns a 1x1 texture with the Unity color of a default texture name, such as <c>white</c> or <c>bump</c>.</summary>
    private static XRTexture2D GetTranslatedDefaultTexture(string samplerName, string? defaultName)
    {
        string normalized = string.IsNullOrWhiteSpace(defaultName) ? "gray" : defaultName.Trim().ToLowerInvariant();
        return TranslatedDefaultTextures.GetOrAdd($"{samplerName}|{normalized}", _ => CreateDefaultUberSamplerTexture(samplerName, normalized switch
        {
            "white" => new ColorF4(1.0f, 1.0f, 1.0f, 1.0f),
            "black" => new ColorF4(0.0f, 0.0f, 0.0f, 1.0f),
            "clear" => new ColorF4(0.0f, 0.0f, 0.0f, 0.0f),
            "red" => new ColorF4(1.0f, 0.0f, 0.0f, 1.0f),
            "bump" => new ColorF4(0.5f, 0.5f, 1.0f, 1.0f),
            "lineargray" => new ColorF4(0.5f, 0.5f, 0.5f, 1.0f),
            _ => new ColorF4(0.5f, 0.5f, 0.5f, 1.0f),
        }));
    }

    /// <summary>
    /// Sets the material parameters that the engine found in the translated stages.
    /// </summary>
    /// <remarks>
    /// The material creates one parameter for each uniform that is not an engine uniform. A name
    /// <c>_X_ST</c> takes the scale and offset of texture <c>_X</c>. Other names take the material
    /// value, then the property default. In a linear Unity project, color properties change from
    /// sRGB to linear with the same rules as the Poiyomi conversion.
    /// </remarks>
    private static void BindTranslatedParameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        ShaderLabDocument shader,
        string projectRoot,
        List<string> warnings)
    {
        bool linearColorSpace = true;
        try
        {
            linearColorSpace = ReadSourceLinearColorSpace(projectRoot);
        }
        catch (SourceVisualImportException ex)
        {
            warnings.Add($"{ex.Message} Linear color space is used for translated shader colors.");
        }

        foreach (ShaderVar parameter in material.Parameters)
        {
            if (parameter is null || string.IsNullOrEmpty(parameter.Name))
                continue;
            if (!TryGetTranslatedParameterValue(document, shader, parameter.Name, linearColorSpace, out Vector4 value))
                continue;

            switch (parameter)
            {
                case ShaderVar<float> scalar:
                    scalar.SetValue(value.X);
                    break;
                case ShaderVar<int> integer:
                    integer.SetValue((int)MathF.Round(value.X));
                    break;
                case ShaderVar<uint> unsigned:
                    unsigned.SetValue((uint)Math.Max(0.0f, MathF.Round(value.X)));
                    break;
                case ShaderVar<bool> toggle:
                    toggle.SetValue(value.X > 0.5f);
                    break;
                case ShaderVar<Vector2> vector2:
                    vector2.SetValue(new Vector2(value.X, value.Y));
                    break;
                case ShaderVar<Vector3> vector3:
                    vector3.SetValue(new Vector3(value.X, value.Y, value.Z));
                    break;
                case ShaderVar<Vector4> vector4:
                    vector4.SetValue(value);
                    break;
            }
        }
    }

    private static bool TryGetTranslatedParameterValue(
        SerializedMaterialDocument document,
        ShaderLabDocument shader,
        string name,
        bool linearColorSpace,
        out Vector4 value)
    {
        if (name.EndsWith("_ST", StringComparison.Ordinal))
        {
            string textureName = name[..^3];
            if (document.Textures.TryGetValue(textureName, out SerializedTexturePropertyDocument? texture) && texture is not null)
            {
                value = new Vector4(texture.Scale.X, texture.Scale.Y, texture.Offset.X, texture.Offset.Y);
                return true;
            }

            if (shader.FindProperty(textureName) is { DefaultTexture: not null })
            {
                value = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
                return true;
            }
        }

        ShaderLabProperty? property = shader.FindProperty(name);
        // Unity converts ordinary colors and HDR colors with Gamma. HDR colors and ordinary colors with Gamma stay as authored.
        bool isColor = property?.Kind == ShaderLabPropertyKind.Color && property.HasAttribute("HDR") == property.HasAttribute("Gamma");
        if (document.TryGetVector(name, out value))
            return ConvertColor(ref value, isColor && linearColorSpace);

        if (document.TryGetFloat(name, out float scalar))
        {
            value = new Vector4(scalar, 0.0f, 0.0f, 0.0f);
            return true;
        }

        if (property is null || property.DefaultValue.Length == 0)
            return false;

        float[] defaults = property.DefaultValue;
        value = new Vector4(
            defaults[0],
            defaults.Length > 1 ? defaults[1] : 0.0f,
            defaults.Length > 2 ? defaults[2] : 0.0f,
            defaults.Length > 3 ? defaults[3] : 0.0f);
        return ConvertColor(ref value, isColor && linearColorSpace);
    }

    private static bool ConvertColor(ref Vector4 value, bool toLinear)
    {
        if (toLinear)
            value = new Vector4(
                SourceToonColorSpaceNormalizer.GammaToLinear(value.X),
                SourceToonColorSpaceNormalizer.GammaToLinear(value.Y),
                SourceToonColorSpaceNormalizer.GammaToLinear(value.Z),
                value.W);
        return true;
    }

    [GeneratedRegex(@"\buniform\s+(?<type>sampler\w+)\s+(?<name>\w+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex GlslSamplerUniformPattern();
}
