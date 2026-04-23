using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Core.Files;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

internal static partial class UberShaderVariantBuilder
{
    internal const string GeneratedVariantMarker = "XRENGINE_UBER_GENERATED_VARIANT";

    private static readonly ConcurrentDictionary<UberVariantCacheKey, string> GeneratedSourceCache = new();
    private static readonly ConcurrentDictionary<UberVariantCacheKey, XRShader> GeneratedShaderCache = new();
    private static readonly string[] PipelineAxisMacros =
    [
        "XRENGINE_UBER_IMPORT_MATERIAL",
        "XRENGINE_DEPTH_NORMAL_PREPASS",
        "XRENGINE_SHADOW_CASTER_PASS",
        "XRENGINE_FORWARD_WEIGHTED_OIT",
        "XRENGINE_FORWARD_PPLL",
        "XRENGINE_FORWARD_DEPTH_PEEL",
    ];

    private readonly record struct UberVariantCacheKey(ulong VariantHash, long SourceVersion, string? SourcePath);

    internal sealed class PreparedUberVariant
    {
        public required UberMaterialVariantRequest Request { get; init; }
        public required UberMaterialVariantBindingState BindingState { get; init; }
        public required XRShader FragmentShader { get; init; }
        public required double PreparationMilliseconds { get; init; }
        public required bool CacheHit { get; init; }
        public required int UniformCount { get; init; }
        public required int SamplerCount { get; init; }
        public required int GeneratedSourceLength { get; init; }
    }

    internal static bool IsGeneratedVariant(XRShader? shader)
        => shader?.Source?.Text?.Contains(GeneratedVariantMarker, StringComparison.Ordinal) == true;

    internal static PreparedUberVariant PrepareVariant(XRMaterial material, XRShader canonicalShader, ShaderUiManifest manifest, CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string resolvedSource = canonicalShader.GetResolvedSource();
        cancellationToken.ThrowIfCancellationRequested();

        UberMaterialVariantRequest request = BuildRequest(material, canonicalShader, manifest, resolvedSource);
        UberVariantCacheKey cacheKey = new(request.VariantHash, request.SourceVersion, request.SourcePath);
        PruneStaleSourceEntries(request.SourcePath, request.SourceVersion);

        bool hasGeneratedSource = GeneratedSourceCache.TryGetValue(cacheKey, out string? cachedGeneratedSource);
        bool hasGeneratedShader = GeneratedShaderCache.TryGetValue(cacheKey, out XRShader? cachedFragmentShader);
        bool cacheHit = hasGeneratedSource && hasGeneratedShader;

        string generatedSource = cachedGeneratedSource ?? GeneratedSourceCache.GetOrAdd(cacheKey, _ => GenerateVariantSource(resolvedSource, manifest, request));
        cancellationToken.ThrowIfCancellationRequested();

        XRShader fragmentShader = cachedFragmentShader ?? GeneratedShaderCache.GetOrAdd(cacheKey, _ => CreateVariantShader(canonicalShader, generatedSource));
        stopwatch.Stop();

        return new PreparedUberVariant
        {
            Request = request,
            BindingState = new UberMaterialVariantBindingState
            {
                VariantHash = request.VariantHash,
                VertexPermutationHash = request.VertexPermutationHash,
                EnabledFeatures = request.EnabledFeatures,
                PipelineMacros = request.PipelineMacros,
                AnimatedProperties = request.AnimatedProperties,
                StaticProperties = request.StaticProperties,
                SourceVersion = request.SourceVersion,
                SourcePath = request.SourcePath,
            },
            FragmentShader = fragmentShader,
            PreparationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            CacheHit = cacheHit,
            UniformCount = request.AnimatedProperties.Length,
            SamplerCount = CountReferencedSamplers(manifest, request),
            GeneratedSourceLength = generatedSource.Length,
        };
    }

    private static UberMaterialVariantRequest BuildRequest(XRMaterial material, XRShader canonicalShader, ShaderUiManifest manifest, string resolvedSource)
    {
        string? sourcePath = canonicalShader.Source?.FilePath ?? canonicalShader.FilePath;
        long sourceVersion = unchecked((long)ComputeStableHash(resolvedSource));
        ulong vertexPermutationHash = ComputeVertexPermutationHash(material);

        string[] enabledFeatures = manifest.Features
            .Select(feature => new { feature.Id, Enabled = ResolveFeatureEnabled(material, canonicalShader, feature) })
            .Where(static x => x.Enabled)
            .Select(static x => x.Id)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        string[] pipelineMacros = ResolvePipelineMacros(canonicalShader)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        List<string> animatedProperties = [];
        List<string> staticProperties = [];

        foreach (ShaderUiProperty property in manifest.Properties.Where(IsAuthorableUberProperty))
        {
            EShaderUiPropertyMode mode = ResolvePropertyMode(material, property);
            if (property.IsSampler || mode == EShaderUiPropertyMode.Animated || !IsStaticPropertySupported(property))
            {
                if (!property.IsSampler)
                    animatedProperties.Add(property.Name);
                continue;
            }

            string literal = ResolveStaticLiteral(material, property);
            staticProperties.Add($"{property.Name}={literal}");
        }

        animatedProperties.Sort(StringComparer.Ordinal);
        staticProperties.Sort(StringComparer.Ordinal);

        ulong variantHash = ComputeVariantHash(enabledFeatures, pipelineMacros, animatedProperties, staticProperties, material.RenderPass, sourceVersion, vertexPermutationHash);
        return new UberMaterialVariantRequest
        {
            VariantHash = variantHash,
            VertexPermutationHash = vertexPermutationHash,
            EnabledFeatures = enabledFeatures,
            PipelineMacros = pipelineMacros,
            AnimatedProperties = [.. animatedProperties],
            StaticProperties = [.. staticProperties],
            RenderPass = material.RenderPass,
            SourceVersion = sourceVersion,
            SourcePath = sourcePath,
        };
    }

    private static XRShader CreateVariantShader(XRShader canonicalShader, string generatedSource)
    {
        TextFile text = TextFile.FromText(generatedSource);
        text.FilePath = canonicalShader.Source?.FilePath;
        text.Name = canonicalShader.Source?.Name;

        return new XRShader(canonicalShader.Type, text)
        {
            Name = canonicalShader.Name,
            GenerateAsync = canonicalShader.GenerateAsync,
        };
    }

    private static string GenerateVariantSource(string resolvedSource, ShaderUiManifest manifest, UberMaterialVariantRequest request)
    {
        HashSet<string> disabledFeatureMacros = ResolveDisabledFeatureMacros(manifest, request.EnabledFeatures);
        HashSet<string> staticPropertyNames = request.StaticProperties
            .Select(static x => x.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        string strippedSource = StripRecognizedDefines(resolvedSource, disabledFeatureMacros, request.PipelineMacros);
        strippedSource = StripStaticUniformDeclarations(strippedSource, staticPropertyNames);

        List<string> defines = [];
        defines.Add($"// {GeneratedVariantMarker}");
        defines.Add($"// variant-hash: 0x{request.VariantHash:X16}");

        foreach (string macro in request.PipelineMacros.OrderBy(static x => x, StringComparer.Ordinal))
            defines.Add($"#define {macro} 1");

        foreach (string macro in disabledFeatureMacros.OrderBy(static x => x, StringComparer.Ordinal))
            defines.Add($"#define {macro} 1");

        foreach ((string name, string literal) in request.StaticProperties
            .Select(static x =>
            {
                string[] parts = x.Split('=', 2);
                return (Name: parts[0], Literal: parts.Length > 1 ? parts[1] : string.Empty);
            })
            .OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            defines.Add($"#define {name} {literal}");
        }

        return InsertDefinesAfterVersion(strippedSource, defines);
    }

    private static HashSet<string> ResolveDisabledFeatureMacros(ShaderUiManifest manifest, IReadOnlyCollection<string> enabledFeatures)
    {
        HashSet<string> enabled = enabledFeatures.ToHashSet(StringComparer.Ordinal);
        HashSet<string> macros = new(StringComparer.Ordinal);
        foreach (ShaderUiFeature feature in manifest.Features)
        {
            if (string.IsNullOrWhiteSpace(feature.GuardMacro))
                continue;

            bool featureEnabled = enabled.Contains(feature.Id);
            bool shouldDefineMacro = feature.GuardDefinedEnablesFeature ? featureEnabled : !featureEnabled;
            if (shouldDefineMacro)
                macros.Add(feature.GuardMacro);
        }

        return macros;
    }

    private static string[] ResolvePipelineMacros(XRShader canonicalShader)
    {
        string sourceText = canonicalShader.Source?.Text ?? string.Empty;
        return PipelineAxisMacros
            .Where(macro => Regex.IsMatch(sourceText, $@"^[ \t]*#define[ \t]+{Regex.Escape(macro)}(?:\s+.*)?$", RegexOptions.Multiline))
            .ToArray();
    }

    private static string StripRecognizedDefines(string source, IEnumerable<string> disabledFeatureMacros, IEnumerable<string> pipelineMacros)
    {
        HashSet<string> macrosToStrip = new(StringComparer.Ordinal);
        foreach (string macro in PipelineAxisMacros)
            macrosToStrip.Add(macro);
        foreach (string macro in disabledFeatureMacros)
            macrosToStrip.Add(macro);
        foreach (string macro in pipelineMacros)
            macrosToStrip.Add(macro);

        string stripped = source;
        foreach (string macro in macrosToStrip)
            stripped = Regex.Replace(stripped, $@"^[ \t]*#define[ \t]+{Regex.Escape(macro)}(?:\s+.*)?$\r?\n?", string.Empty, RegexOptions.Multiline);

        return stripped;
    }

    private static string StripStaticUniformDeclarations(string source, HashSet<string> staticPropertyNames)
    {
        if (staticPropertyNames.Count == 0)
            return source;

        StringBuilder builder = new(source.Length);
        foreach (string line in source.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            Match match = UberStaticUniformRegex().Match(line);
            if (match.Success)
            {
                string uniformName = match.Groups["name"].Value;
                if (staticPropertyNames.Contains(uniformName))
                    continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string InsertDefinesAfterVersion(string source, IReadOnlyList<string> defines)
    {
        if (defines.Count == 0)
            return source;

        Match versionMatch = Regex.Match(source, @"^[ \t]*#version.*$", RegexOptions.Multiline);
        string defineBlock = string.Join(Environment.NewLine, defines) + Environment.NewLine;
        if (!versionMatch.Success)
            return defineBlock + source;

        int insertIndex = versionMatch.Index + versionMatch.Length;
        string prefix = source[..insertIndex];
        string suffix = insertIndex < source.Length ? source[insertIndex..].TrimStart('\r', '\n') : string.Empty;
        return string.IsNullOrEmpty(suffix)
            ? prefix + Environment.NewLine + defineBlock
            : prefix + Environment.NewLine + defineBlock + Environment.NewLine + suffix;
    }

    private static bool ResolveFeatureEnabled(XRMaterial material, XRShader canonicalShader, ShaderUiFeature feature)
    {
        UberMaterialFeatureState? authored = material.UberAuthoredState.GetFeature(feature.Id);
        if (authored is not null)
            return authored.Enabled;

        if (string.IsNullOrWhiteSpace(feature.GuardMacro))
            return feature.DefaultEnabled;

        bool isDefined = Regex.IsMatch(canonicalShader.Source?.Text ?? string.Empty, $@"^[ \t]*#define[ \t]+{Regex.Escape(feature.GuardMacro)}(?:\s+.*)?$", RegexOptions.Multiline);
        return feature.GuardDefinedEnablesFeature ? isDefined : !isDefined;
    }

    internal static EShaderUiPropertyMode ResolvePropertyMode(XRMaterial material, ShaderUiProperty property)
    {
        UberMaterialPropertyState? authored = material.UberAuthoredState.GetProperty(property.Name);
        if (authored is not null)
            return authored.Mode;

        if (property.IsSampler)
            return EShaderUiPropertyMode.Animated;

        return property.DefaultMode == EShaderUiPropertyMode.Unspecified
            ? EShaderUiPropertyMode.Static
            : property.DefaultMode;
    }

    private static bool IsStaticPropertySupported(ShaderUiProperty property)
        => property.ArraySize <= 0 &&
           !property.IsSampler &&
           ShaderVar.GlslTypeMap.TryGetValue(property.GlslType, out EShaderVarType type) &&
           type is EShaderVarType._bool or
               EShaderVarType._int or
               EShaderVarType._uint or
               EShaderVarType._float or
               EShaderVarType._vec2 or
               EShaderVarType._vec3 or
               EShaderVarType._vec4;

    private static bool IsAuthorableUberProperty(ShaderUiProperty property)
        => property.Name.StartsWith("_", StringComparison.Ordinal) ||
           string.Equals(property.Name, "AlphaCutoff", StringComparison.Ordinal);

    internal static string FormatStaticLiteral(XRMaterial material, ShaderUiProperty property)
    {
        ShaderVar? parameter = material.Parameters?.FirstOrDefault(x => string.Equals(x.Name, property.Name, StringComparison.Ordinal));
        if (parameter is null && string.Equals(property.Name, "_Cutoff", StringComparison.Ordinal))
            return FormatFloatLiteral(material.AlphaCutoff);

        // When the property has no backing material parameter (e.g. a feature
        // was disabled at import time so its parameters were never populated),
        // prefer the manifest-authored default literal. Without this, a freshly
        // enabled feature would bake as zeros — e.g. rim lighting with
        // _RimLightColor = vec4(0,0,0,0) and _RimBlendStrength = 0 produces
        // black rim regardless of the enabled flag.
        if (parameter is null && !string.IsNullOrWhiteSpace(property.DefaultLiteral))
            return property.DefaultLiteral!;

        if (parameter is null)
        {
            if (!ShaderVar.GlslTypeMap.TryGetValue(property.GlslType, out EShaderVarType type))
                throw new InvalidOperationException($"Uber property '{property.Name}' uses unsupported GLSL type '{property.GlslType}'.");

            parameter = ShaderVar.CreateForType(type, property.Name);
        }

        ShaderVar resolvedParameter = parameter
            ?? throw new InvalidOperationException($"Uber property '{property.Name}' could not resolve a backing shader parameter.");

        return resolvedParameter switch
        {
            ShaderBool value => value.Value ? "true" : "false",
            ShaderInt value => value.Value.ToString(CultureInfo.InvariantCulture),
            ShaderUInt value => value.Value.ToString(CultureInfo.InvariantCulture) + "u",
            ShaderFloat value => FormatFloatLiteral(value.Value),
            ShaderVector2 value => $"vec2({FormatFloatLiteral(value.Value.X)}, {FormatFloatLiteral(value.Value.Y)})",
            ShaderVector3 value => $"vec3({FormatFloatLiteral(value.Value.X)}, {FormatFloatLiteral(value.Value.Y)}, {FormatFloatLiteral(value.Value.Z)})",
            ShaderVector4 value => $"vec4({FormatFloatLiteral(value.Value.X)}, {FormatFloatLiteral(value.Value.Y)}, {FormatFloatLiteral(value.Value.Z)}, {FormatFloatLiteral(value.Value.W)})",
            _ => throw new InvalidOperationException($"Uber property '{property.Name}' uses unsupported shader parameter type '{resolvedParameter.GetType().Name}'."),
        };
    }

    internal static bool TryFormatStaticLiteral(XRMaterial material, ShaderUiProperty property, out string literal)
    {
        try
        {
            literal = FormatStaticLiteral(material, property);
            return true;
        }
        catch
        {
            literal = string.Empty;
            return false;
        }
    }

    private static string FormatFloatLiteral(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            throw new InvalidOperationException($"Uber static literal '{value}' is not a finite float.");

        if (value == 0.0f)
            return "0.0";

        string text = value.ToString("0.0################", CultureInfo.InvariantCulture);
        return text.StartsWith("-0.0", StringComparison.Ordinal) ? "0.0" : text;
    }

    private static int CountReferencedSamplers(ShaderUiManifest manifest, UberMaterialVariantRequest request)
    {
        HashSet<string> enabledFeatures = request.EnabledFeatures.ToHashSet(StringComparer.Ordinal);
        int count = 0;
        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (!property.IsSampler)
                continue;

            if (property.FeatureId is not null && !enabledFeatures.Contains(property.FeatureId))
                continue;

            count++;
        }

        return count;
    }

    private static string ResolveStaticLiteral(XRMaterial material, ShaderUiProperty property)
    {
        if (TryFormatStaticLiteral(material, property, out string literal))
            return literal;

        UberMaterialPropertyState? authored = material.UberAuthoredState.GetProperty(property.Name);
        if (!string.IsNullOrWhiteSpace(authored?.StaticLiteral))
            return authored.StaticLiteral!;

        return FormatStaticLiteral(material, property);
    }

    private static ulong ComputeVariantHash(
        IReadOnlyList<string> enabledFeatures,
        IReadOnlyList<string> pipelineMacros,
        IReadOnlyList<string> animatedProperties,
        IReadOnlyList<string> staticProperties,
        int renderPass,
        long sourceVersion,
        ulong vertexPermutationHash)
    {
        const ulong fnvOffset = 14695981039346656037ul;
        const ulong fnvPrime = 1099511628211ul;

        ulong hash = fnvOffset;
        void HashString(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= fnvPrime;
            }

            hash ^= 0xff;
            hash *= fnvPrime;
        }

        foreach (string value in enabledFeatures)
            HashString(value);
        foreach (string value in pipelineMacros)
            HashString(value);
        foreach (string value in animatedProperties)
            HashString(value);
        foreach (string value in staticProperties)
            HashString(value);

        HashString(renderPass.ToString(CultureInfo.InvariantCulture));
        HashString(sourceVersion.ToString(CultureInfo.InvariantCulture));
        HashString(vertexPermutationHash.ToString(CultureInfo.InvariantCulture));
        return hash;
    }

    private static ulong ComputeVertexPermutationHash(XRMaterial material)
    {
        const ulong fnvOffset = 14695981039346656037ul;
        const ulong fnvPrime = 1099511628211ul;

        ulong hash = fnvOffset;

        void HashString(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= fnvPrime;
            }

            hash ^= 0xff;
            hash *= fnvPrime;
        }

        foreach (XRShader shader in material.Shaders
            .Where(static x => x is not null && x.Type != EShaderType.Fragment)
            .OrderBy(static x => x.Type))
        {
            HashString(shader.Type.ToString());
            HashString(shader.Source?.FilePath ?? shader.FilePath ?? string.Empty);
            HashString(ComputeStableHash(shader.GetResolvedSource()).ToString(CultureInfo.InvariantCulture));
        }

        return hash;
    }

    private static void PruneStaleSourceEntries(string? sourcePath, long sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;

        foreach ((UberVariantCacheKey key, _) in GeneratedSourceCache)
        {
            if (!string.Equals(key.SourcePath, sourcePath, StringComparison.Ordinal) || key.SourceVersion == sourceVersion)
                continue;

            GeneratedSourceCache.TryRemove(key, out _);
            GeneratedShaderCache.TryRemove(key, out _);
        }
    }

    private static ulong ComputeStableHash(string source)
    {
        const ulong fnvOffset = 14695981039346656037ul;
        const ulong fnvPrime = 1099511628211ul;

        ulong hash = fnvOffset;
        for (int i = 0; i < source.Length; i++)
        {
            hash ^= source[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    [GeneratedRegex(@"^\s*(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?:(?:lowp|mediump|highp)\s+)?(?<type>\w+)\s+(?<name>\w+)(?:\s*\[[^\]]+\])?\s*;", RegexOptions.Multiline)]
    private static partial Regex UberStaticUniformRegex();
}