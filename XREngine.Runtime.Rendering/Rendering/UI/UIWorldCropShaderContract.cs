using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Only these engine fragment shaders have the canvas-local clip ABI.</summary>
internal static class UIWorldCropShaderContract
{
    internal const string MinMaxUniform = "UiWorldCropMinMax";
    internal const string MatrixUniform = "UiWorldModelToCanvas";
    private static readonly string[] CanonicalNames =
    [
        "UnlitColoredForward.fs", "UiTexturedForward.fs", "Text.fs",
        "TextMsdf.fs", "TextMtsdfScreen.fs"
    ];
    private static readonly Lazy<string[]> CanonicalSources = new(() =>
    {
        string[] sources = new string[CanonicalNames.Length];
        for (int i = 0; i < sources.Length; i++)
            sources[i] = XRShader.EngineShader(
                Path.Combine("Common", CanonicalNames[i]),
                EShaderType.Fragment).GetResolvedSource();
        return sources;
    });
    private static readonly string[] CanonicalVertexNames =
    [
        "Text.vs", "TextRotatable.vs", "TextStereo.vs",
        "TextRotatableStereo.vs"
    ];
    private static readonly Lazy<string[]> CanonicalVertexSources = new(() =>
    {
        string[] sources = new string[CanonicalVertexNames.Length];
        for (int i = 0; i < sources.Length; i++)
            sources[i] = XRShader.EngineShader(
                Path.Combine("Common", CanonicalVertexNames[i]),
                EShaderType.Vertex).GetResolvedSource();
        return sources;
    });
    private static readonly Lazy<string[]> CanonicalClippedSources = new(() =>
    {
        string[] sources = new string[CanonicalSources.Value.Length];
        for (int i = 0; i < sources.Length; i++)
            sources[i] = TryInjectCrop(CanonicalSources.Value[i],
                out string clipped) ? clipped : throw new InvalidOperationException(
                    "A canonical UI shader has no supported crop insertion point.");
        return sources;
    });

    private const string CropDeclarations = """
        layout(location = 20) in vec3 FragPosLocal;
        uniform vec4 UiWorldCropMinMax;
        uniform mat4 UiWorldModelToCanvas;

        void XRENGINE_ApplyUiWorldCrop()
        {
            vec4 canvas = UiWorldModelToCanvas * vec4(FragPosLocal, 1.0);
            if (any(isnan(canvas)) || any(isinf(canvas)) || canvas.w == 0.0)
                discard;
            vec2 point = canvas.xy / canvas.w;
            if (any(isnan(point)) || any(isinf(point)) ||
                any(lessThan(point, UiWorldCropMinMax.xy)) ||
                any(greaterThanEqual(point, UiWorldCropMinMax.zw)))
                discard;
        }
        """;

    internal static bool IsCanonicalClippedFragment(XRShader shader)
    {
        if (shader.Type != EShaderType.Fragment ||
            shader.SourceLanguage != ShaderSourceLanguage.Glsl)
            return false;
        string? source = shader.Source.Text;
        foreach (string expected in CanonicalClippedSources.Value)
            if (StringComparer.Ordinal.Equals(source, expected))
                return true;
        return false;
    }

    internal static bool IsCanonicalVertex(XRShader shader)
    {
        if (shader.Type != EShaderType.Vertex ||
            shader.SourceLanguage != ShaderSourceLanguage.Glsl)
            return false;
        string? source = shader.Source.Text;
        return StringComparer.Ordinal.Equals(source,
                   CanonicalVertexSources.Value[0]) ||
               StringComparer.Ordinal.Equals(source,
                   CanonicalVertexSources.Value[1]);
    }

    internal static bool TrySelectFragment(XRMaterial material, out int fragmentIndex)
    {
        fragmentIndex = -1;
        if (material.Shaders.Count is not (1 or 3))
            return false;
        int fragmentKind = -1;
        int vertexMask = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            string resolved = shader.GetResolvedSource();
            if (shader.Type == EShaderType.Fragment &&
                shader.SourceLanguage == ShaderSourceLanguage.Glsl)
            {
                if (fragmentIndex >= 0)
                    return false;
                for (int kind = 0; kind < CanonicalSources.Value.Length; kind++)
                    if (StringComparer.Ordinal.Equals(resolved,
                            CanonicalSources.Value[kind]))
                    {
                        fragmentKind = kind;
                        break;
                    }
                if (fragmentKind < 0)
                    return false;
                fragmentIndex = index;
            }
            else
            {
                if (shader.Type != EShaderType.Vertex ||
                    shader.SourceLanguage != ShaderSourceLanguage.Glsl)
                    return false;
                int kind = -1;
                for (int vertex = 0; vertex < CanonicalVertexSources.Value.Length;
                    vertex++)
                    if (StringComparer.Ordinal.Equals(resolved,
                            CanonicalVertexSources.Value[vertex]))
                    {
                        kind = vertex;
                        break;
                    }
                if (kind < 0 || (vertexMask & (1 << kind)) != 0)
                    return false;
                vertexMask |= 1 << kind;
            }
        }
        if (fragmentIndex < 0)
            return false;
        if (material.Shaders.Count == 1)
            return (fragmentKind == 0 || fragmentKind == 1) &&
                vertexMask == 0;
        return fragmentKind >= 2 && fragmentKind <= 4 &&
            (vertexMask == 5 || vertexMask == 10);
    }

    internal static bool TryCapture(BoundingRectangle region,
        in Matrix4x4 modelToCanvas, out UIWorldCropCapture capture)
    {
        capture = default;
        long left = region.X, bottom = region.Y;
        long right = left + region.Width, top = bottom + region.Height;
        const long exactFloatIntegerLimit = 1L << 24;
        if (region.Width < 0 || region.Height < 0 ||
            Math.Abs(left) > exactFloatIntegerLimit ||
            Math.Abs(bottom) > exactFloatIntegerLimit ||
            Math.Abs(right) > exactFloatIntegerLimit ||
            Math.Abs(top) > exactFloatIntegerLimit ||
            !IsFinite(modelToCanvas) ||
            !Matrix4x4.Invert(modelToCanvas, out Matrix4x4 inverse) ||
            !IsFinite(inverse))
            return false;
        capture = new(region, new((float)left, (float)bottom,
            (float)right, (float)top), modelToCanvas);
        return true;
    }

    internal static bool TryCreateFragmentVariant(XRShader source,
        out XRShader? variant)
    {
        variant = null;
        if (source.Type != EShaderType.Fragment ||
            source.SourceLanguage != ShaderSourceLanguage.Glsl)
            return false;
        string resolved = source.GetResolvedSource();
        bool canonical = false;
        foreach (string expected in CanonicalSources.Value)
            if (StringComparer.Ordinal.Equals(resolved, expected))
            {
                canonical = true;
                break;
            }
        if (!canonical)
            return false;
        if (!TryInjectCrop(resolved, out string clipped))
            return false;
        variant = new XRShader(EShaderType.Fragment, clipped)
        {
            SourceLanguage = ShaderSourceLanguage.Glsl,
        };
        return true;
    }

    private static bool TryInjectCrop(string source, out string clipped)
    {
        clipped = string.Empty;
        int versionEnd = source.IndexOf('\n');
        int mainStart = source.IndexOf("void main()", StringComparison.Ordinal);
        int bodyStart = mainStart < 0 ? -1 : source.IndexOf('{', mainStart);
        if (!source.StartsWith("#version ", StringComparison.Ordinal) ||
            versionEnd < 0 || bodyStart <= versionEnd)
            return false;
        clipped = source[..(versionEnd + 1)] + CropDeclarations + "\n" +
            source[(versionEnd + 1)..(bodyStart + 1)] +
            "\n    XRENGINE_ApplyUiWorldCrop();\n" +
            source[(bodyStart + 1)..];
        return true;
    }

    private static bool IsFinite(in Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
            float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
            float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
            float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
            float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
            float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
            float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
            float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
