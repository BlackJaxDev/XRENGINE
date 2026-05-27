using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Core.Files;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

internal static partial class UberShaderVariantBuilder
{
    internal const string GeneratedVariantMarker = "XRENGINE_UBER_GENERATED_VARIANT";
    private const int GeneratedVariantHeaderScanLimit = 2048;

    private static readonly ConcurrentDictionary<UberVariantCacheKey, string> GeneratedSourceCache = new();
    private static readonly ConcurrentDictionary<UberVariantCacheKey, XRShader> GeneratedShaderCache = new();
    private static readonly ConcurrentDictionary<SourceResolveCacheKey, Lazy<ResolvedShaderSource>> ResolvedSourceCache = new();
    private static readonly ConcurrentDictionary<VertexPermutationCacheKey, ulong> VertexPermutationHashCache = new();
    private static readonly ConcurrentDictionary<string, long> LastKnownSourceVersionsByPath = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<ShaderUiManifest, ManifestDerivedData> ManifestDerivedCache = new();
    private static long _resolvedSourceCacheHits;
    private static long _resolvedSourceCacheMisses;
    private static readonly string[] PipelineAxisMacros =
    [
        "XRENGINE_DEPTH_NORMAL_PREPASS",
        "XRENGINE_SHADOW_CASTER_PASS",
        "XRENGINE_POINT_SHADOW_CASTER_PASS",
        "XRENGINE_FORWARD_WEIGHTED_OIT",
        "XRENGINE_FORWARD_PPLL",
        "XRENGINE_FORWARD_DEPTH_PEEL",
    ];

    private readonly record struct UberVariantCacheKey(ulong VariantHash, long SourceVersion, ulong SourcePathHash, string? SourcePath);
    private readonly record struct SourceResolveCacheKey(string SourceText, string? SourcePath, bool EmitIncludeDeadCodeMarkers);
    private readonly record struct VertexPermutationCacheKey(EShaderType Type, string SourceText, string? SourcePath, long SourceVersion);

    private sealed class ResolvedShaderSource
    {
        public required string Source { get; init; }
        public required string? SourcePath { get; init; }
        public required ulong SourcePathHash { get; init; }
        public required long SourceVersion { get; init; }
        public required ShaderSourceFileDependency[] Dependencies { get; init; }
        public required string[] PipelineMacros { get; init; }
    }

    private sealed class MaterialVariantAxes
    {
        public required string[] EnabledFeatures { get; init; }
        public required string[] PipelineMacros { get; init; }
        public required string[] AnimatedProperties { get; init; }
        public required string[] StaticProperties { get; init; }
        public required ulong VertexPermutationHash { get; init; }
    }

    private sealed class ManifestDerivedData
    {
        public required HashSet<string> KnownConditionalMacros { get; init; }
        public required ShaderUiProperty[] AuthorableProperties { get; init; }
        public required ShaderUiProperty[] SamplerProperties { get; init; }
        public required HashSet<string> StaticSupportedPropertyNames { get; init; }
        public ConcurrentDictionary<string, HashSet<string>> DisabledFeatureMacrosByEnabledKey { get; } = new(StringComparer.Ordinal);
    }

    internal readonly record struct CacheStats(long ResolvedSourceHits, long ResolvedSourceMisses);

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
    {
        if (shader is null)
            return false;
        if (shader.IsGeneratedUberVariant)
            return true;

        string? source = shader.Source?.Text;
        if (string.IsNullOrEmpty(source))
            return false;

        int scanLength = Math.Min(source.Length, GeneratedVariantHeaderScanLimit);
        return source.AsSpan(0, scanLength).IndexOf(GeneratedVariantMarker.AsSpan(), StringComparison.Ordinal) >= 0;
    }

    internal static void ClearCachesForTests()
    {
        GeneratedSourceCache.Clear();
        GeneratedShaderCache.Clear();
        ResolvedSourceCache.Clear();
        VertexPermutationHashCache.Clear();
        LastKnownSourceVersionsByPath.Clear();
        Interlocked.Exchange(ref _resolvedSourceCacheHits, 0);
        Interlocked.Exchange(ref _resolvedSourceCacheMisses, 0);
    }

    internal static CacheStats GetCacheStatsForTests()
        => new(
            Interlocked.Read(ref _resolvedSourceCacheHits),
            Interlocked.Read(ref _resolvedSourceCacheMisses));

    internal static PreparedUberVariant PrepareVariant(XRMaterial material, XRShader canonicalShader, ShaderUiManifest manifest, CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ResolvedShaderSource resolvedSource = ResolveCanonicalVariantSource(canonicalShader);
        cancellationToken.ThrowIfCancellationRequested();

        UberMaterialVariantRequest request = BuildRequest(material, canonicalShader, manifest, resolvedSource);
        UberVariantCacheKey cacheKey = new(request.VariantHash, request.SourceVersion, resolvedSource.SourcePathHash, request.SourcePath);
        PruneStaleSourceEntries(request.SourcePath, resolvedSource.SourcePathHash, request.SourceVersion);

        bool hasGeneratedSource = GeneratedSourceCache.TryGetValue(cacheKey, out string? cachedGeneratedSource);
        bool hasGeneratedShader = GeneratedShaderCache.TryGetValue(cacheKey, out XRShader? cachedFragmentShader);
        bool cacheHit = hasGeneratedSource && hasGeneratedShader;

        string generatedSource = cachedGeneratedSource ?? GeneratedSourceCache.GetOrAdd(cacheKey, _ => GenerateVariantSource(resolvedSource.Source, manifest, request));
        cancellationToken.ThrowIfCancellationRequested();

        XRShader fragmentShader = cachedFragmentShader ?? GeneratedShaderCache.GetOrAdd(cacheKey, _ => CreateVariantShader(canonicalShader, generatedSource, request.VariantHash));
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

    private static UberMaterialVariantRequest BuildRequest(XRMaterial material, XRShader canonicalShader, ShaderUiManifest manifest, ResolvedShaderSource resolvedSource)
    {
        MaterialVariantAxes axes = BuildMaterialAxes(material, canonicalShader, manifest, resolvedSource);

        ulong variantHash = ComputeVariantHash(
            axes.EnabledFeatures,
            axes.PipelineMacros,
            axes.AnimatedProperties,
            axes.StaticProperties,
            material.RenderPass,
            resolvedSource.SourceVersion,
            axes.VertexPermutationHash);
        return new UberMaterialVariantRequest
        {
            VariantHash = variantHash,
            VertexPermutationHash = axes.VertexPermutationHash,
            EnabledFeatures = axes.EnabledFeatures,
            PipelineMacros = axes.PipelineMacros,
            AnimatedProperties = axes.AnimatedProperties,
            StaticProperties = axes.StaticProperties,
            RenderPass = material.RenderPass,
            SourceVersion = resolvedSource.SourceVersion,
            SourcePath = resolvedSource.SourcePath,
        };
    }

    private static MaterialVariantAxes BuildMaterialAxes(XRMaterial material, XRShader canonicalShader, ShaderUiManifest manifest, ResolvedShaderSource resolvedSource)
    {
        ManifestDerivedData manifestData = GetManifestDerivedData(manifest);

        List<string> enabledFeatures = new(manifest.Features.Count);
        foreach (ShaderUiFeature feature in manifest.Features)
        {
            if (ResolveFeatureEnabled(material, canonicalShader, feature))
                enabledFeatures.Add(feature.Id);
        }

        enabledFeatures.Sort(StringComparer.Ordinal);
        HashSet<string> enabledFeatureSet = new(enabledFeatures, StringComparer.Ordinal);
        List<string> animatedProperties = [];
        List<string> staticProperties = [];

        foreach (ShaderUiProperty property in manifestData.AuthorableProperties)
        {
            if (property.FeatureId is not null && !enabledFeatureSet.Contains(property.FeatureId))
                continue;

            EShaderUiPropertyMode mode = ResolvePropertyMode(material, property);
            if (property.IsSampler || mode == EShaderUiPropertyMode.Animated || !manifestData.StaticSupportedPropertyNames.Contains(property.Name))
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

        return new MaterialVariantAxes
        {
            EnabledFeatures = [.. enabledFeatures],
            PipelineMacros = resolvedSource.PipelineMacros,
            AnimatedProperties = [.. animatedProperties],
            StaticProperties = [.. staticProperties],
            VertexPermutationHash = ComputeVertexPermutationHash(material),
        };
    }

    private static XRShader CreateVariantShader(XRShader canonicalShader, string generatedSource, ulong variantHash)
    {
        TextFile text = TextFile.FromText(generatedSource);
        text.FilePath = canonicalShader.Source?.FilePath ?? canonicalShader.FilePath;
        text.Name = canonicalShader.Source?.Name ?? canonicalShader.Name;

        return new XRShader(canonicalShader.Type, text)
        {
            Name = canonicalShader.Name,
            GenerateAsync = canonicalShader.GenerateAsync,
            IsGeneratedUberVariant = true,
            GeneratedUberVariantHash = variantHash,
        };
    }

    private static string? ResolveShaderSourcePathOrName(XRShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
            return shader.Source.FilePath;
        if (!string.IsNullOrWhiteSpace(shader.FilePath))
            return shader.FilePath;
        if (!string.IsNullOrWhiteSpace(shader.Source?.Name))
            return shader.Source.Name;
        return shader.Name;
    }

    private static string GenerateVariantSource(string resolvedSource, ShaderUiManifest manifest, UberMaterialVariantRequest request)
    {
        ManifestDerivedData manifestData = GetManifestDerivedData(manifest);
        HashSet<string> disabledFeatureMacros = ResolveDisabledFeatureMacros(manifest, request.EnabledFeatures, manifestData);
        Dictionary<string, string> staticLiterals = ResolveStaticLiteralMap(request.StaticProperties);
        HashSet<string> staticPropertyNames = new(staticLiterals.Keys, StringComparer.Ordinal);

        string strippedSource = StripRecognizedDefines(resolvedSource, manifestData.KnownConditionalMacros, request.PipelineMacros);
        strippedSource = PruneKnownConditionalBlocks(
            strippedSource,
            manifestData.KnownConditionalMacros,
            ResolveDefinedConditionalMacros(disabledFeatureMacros, request.PipelineMacros));
        strippedSource = StripStaticUniformDeclarations(strippedSource, staticPropertyNames);
        strippedSource = InlineStaticPropertyLiterals(strippedSource, staticLiterals);
        strippedSource = GlslSnippetDeadCodeEliminator.Trim(strippedSource);
        strippedSource = GlslSnippetDeadCodeEliminator.StripRegionMarkers(strippedSource);
        strippedSource = CollapseBlankLineRuns(strippedSource);

        HashSet<string> residualMacros = ResolveResidualDefineMacros(strippedSource, disabledFeatureMacros, request.PipelineMacros);
        List<string> defines = new(residualMacros.Count + 2);
        defines.Add($"// {GeneratedVariantMarker}");
        defines.Add($"// variant-hash: 0x{request.VariantHash:x16}");

        foreach (string macro in residualMacros.OrderBy(static x => x, StringComparer.Ordinal))
            defines.Add($"#define {macro} 1");

        return InsertDefinesAfterVersion(strippedSource, defines);
    }

    private static ResolvedShaderSource ResolveCanonicalVariantSource(XRShader canonicalShader)
        => ResolveShaderSourceCached(canonicalShader, emitIncludeDeadCodeMarkers: true);

    private static ResolvedShaderSource ResolveShaderSourceCached(XRShader shader, bool emitIncludeDeadCodeMarkers)
    {
        string sourceText = shader.Source?.Text ?? string.Empty;
        string? sourcePath = ResolveShaderSourcePathOrName(shader);
        SourceResolveCacheKey cacheKey = new(sourceText, NormalizeSourcePathKey(sourcePath), emitIncludeDeadCodeMarkers);

        while (true)
        {
            if (ResolvedSourceCache.TryGetValue(cacheKey, out Lazy<ResolvedShaderSource>? cachedLazy))
            {
                ResolvedShaderSource cached;
                try
                {
                    cached = cachedLazy.Value;
                }
                catch
                {
                    ResolvedSourceCache.TryRemove(cacheKey, out _);
                    throw;
                }

                if (ShaderSourceResolver.AreDependenciesCurrent(cached.Dependencies))
                {
                    Interlocked.Increment(ref _resolvedSourceCacheHits);
                    return cached;
                }

                ResolvedSourceCache.TryRemove(cacheKey, out _);
            }

            Lazy<ResolvedShaderSource> created = new(
                () => ResolveShaderSourceUncached(shader, sourceText, sourcePath, emitIncludeDeadCodeMarkers),
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<ResolvedShaderSource> actual = ResolvedSourceCache.GetOrAdd(cacheKey, created);

            try
            {
                ResolvedShaderSource resolved = actual.Value;
                if (!ShaderSourceResolver.AreDependenciesCurrent(resolved.Dependencies))
                {
                    ResolvedSourceCache.TryRemove(cacheKey, out _);
                    continue;
                }

                if (ReferenceEquals(actual, created))
                    Interlocked.Increment(ref _resolvedSourceCacheMisses);
                else
                    Interlocked.Increment(ref _resolvedSourceCacheHits);
                return resolved;
            }
            catch
            {
                if (ReferenceEquals(actual, created))
                    ResolvedSourceCache.TryRemove(cacheKey, out _);

                throw;
            }
        }
    }

    private static ResolvedShaderSource ResolveShaderSourceUncached(
        XRShader shader,
        string sourceText,
        string? sourcePath,
        bool emitIncludeDeadCodeMarkers)
    {
        string resolvedSource;
        ShaderSourceFileDependency[] dependencies = [];

        if (!string.IsNullOrEmpty(sourceText))
        {
            try
            {
                ShaderSourceResolutionResult result = ShaderSourceResolver.ResolveSourceDetailed(
                    sourceText,
                    sourcePath,
                    new ShaderSourceResolverOptions
                    {
                        EmitIncludeDeadCodeMarkers = emitIncludeDeadCodeMarkers,
                        EnableSnippetDeadCodeElimination = emitIncludeDeadCodeMarkers,
                    });
                resolvedSource = result.Source;
                dependencies = result.FileDependencies;
                return CreateResolvedShaderSource(resolvedSource, sourcePath, dependencies);
            }
            catch
            {
            }
        }

        bool resolved = shader.TryGetResolvedSource(out resolvedSource, annotateIncludes: false, logFailures: true);
        if (resolved)
        {
            // XRShader owns the detailed dependency cache for this fallback path;
            // this local cache entry remains direct-source validated only.
            dependencies = [];
        }

        return CreateResolvedShaderSource(resolvedSource, sourcePath, dependencies);
    }

    private static ResolvedShaderSource CreateResolvedShaderSource(
        string resolvedSource,
        string? sourcePath,
        ShaderSourceFileDependency[] dependencies)
    {
        string? normalizedPath = NormalizeSourcePathKey(sourcePath);
        return new ResolvedShaderSource
        {
            Source = resolvedSource,
            SourcePath = normalizedPath,
            SourcePathHash = ComputeStableHash(normalizedPath ?? string.Empty),
            SourceVersion = unchecked((long)ComputeStableHash(resolvedSource)),
            Dependencies = dependencies,
            PipelineMacros = ResolvePipelineMacros(resolvedSource),
        };
    }

    private static ManifestDerivedData GetManifestDerivedData(ShaderUiManifest manifest)
        => ManifestDerivedCache.GetValue(manifest, CreateManifestDerivedData);

    private static ManifestDerivedData CreateManifestDerivedData(ShaderUiManifest manifest)
    {
        HashSet<string> featureGuardMacros = new(StringComparer.Ordinal);
        foreach (ShaderUiFeature feature in manifest.Features)
        {
            if (!string.IsNullOrWhiteSpace(feature.GuardMacro))
                featureGuardMacros.Add(feature.GuardMacro);
        }

        HashSet<string> knownConditionalMacros = new(StringComparer.Ordinal);
        foreach (string macro in PipelineAxisMacros)
            knownConditionalMacros.Add(macro);
        foreach (string macro in featureGuardMacros)
            knownConditionalMacros.Add(macro);

        List<ShaderUiProperty> authorableProperties = new(manifest.Properties.Count);
        List<ShaderUiProperty> samplerProperties = [];
        HashSet<string> staticSupportedPropertyNames = new(StringComparer.Ordinal);
        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (IsAuthorableUberProperty(property))
                authorableProperties.Add(property);
            if (property.IsSampler)
                samplerProperties.Add(property);
            if (IsStaticPropertySupported(property))
                staticSupportedPropertyNames.Add(property.Name);
        }

        return new ManifestDerivedData
        {
            KnownConditionalMacros = knownConditionalMacros,
            AuthorableProperties = [.. authorableProperties],
            SamplerProperties = [.. samplerProperties],
            StaticSupportedPropertyNames = staticSupportedPropertyNames,
        };
    }

    private static HashSet<string> ResolveDisabledFeatureMacros(
        ShaderUiManifest manifest,
        IReadOnlyCollection<string> enabledFeatures,
        ManifestDerivedData manifestData)
    {
        string enabledKey = string.Join('\u001f', enabledFeatures.OrderBy(static x => x, StringComparer.Ordinal));
        if (manifestData.DisabledFeatureMacrosByEnabledKey.TryGetValue(enabledKey, out HashSet<string>? cachedMacros))
            return cachedMacros;

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

        return manifestData.DisabledFeatureMacrosByEnabledKey.GetOrAdd(enabledKey, macros);
    }

    private static HashSet<string> ResolveDefinedConditionalMacros(IEnumerable<string> disabledFeatureMacros, IEnumerable<string> pipelineMacros)
    {
        HashSet<string> macros = new(StringComparer.Ordinal);
        foreach (string macro in disabledFeatureMacros)
            macros.Add(macro);
        foreach (string macro in pipelineMacros)
            macros.Add(macro);
        return macros;
    }

    private static string[] ResolvePipelineMacros(string source)
    {
        List<string> macros = [];
        foreach (Match match in DefineLineRegex().Matches(source))
        {
            string name = match.Groups["name"].Value;
            for (int i = 0; i < PipelineAxisMacros.Length; i++)
            {
                if (string.Equals(name, PipelineAxisMacros[i], StringComparison.Ordinal))
                {
                    macros.Add(name);
                    break;
                }
            }
        }

        macros.Sort(StringComparer.Ordinal);
        return [.. macros.Distinct(StringComparer.Ordinal)];
    }

    private static string StripRecognizedDefines(string source, IEnumerable<string> featureGuardMacros, IEnumerable<string> pipelineMacros)
    {
        HashSet<string> macrosToStrip = new(StringComparer.Ordinal);
        foreach (string macro in PipelineAxisMacros)
            macrosToStrip.Add(macro);
        foreach (string macro in featureGuardMacros)
            macrosToStrip.Add(macro);
        foreach (string macro in pipelineMacros)
            macrosToStrip.Add(macro);

        if (macrosToStrip.Count == 0)
            return source;

        return DefineLineRegex().Replace(source, match =>
            macrosToStrip.Contains(match.Groups["name"].Value)
                ? string.Empty
                : match.Value);
    }

    private static string StripStaticUniformDeclarations(string source, HashSet<string> staticPropertyNames)
    {
        if (staticPropertyNames.Count == 0)
            return source;

        StringBuilder builder = new(source.Length);
        foreach (string line in SplitLinesPreservingNewlines(source))
        {
            Match match = UberStaticUniformRegex().Match(line);
            if (match.Success)
            {
                string uniformName = match.Groups["name"].Value;
                if (staticPropertyNames.Contains(uniformName))
                    continue;
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static Dictionary<string, string> ResolveStaticLiteralMap(IReadOnlyList<string> staticProperties)
    {
        Dictionary<string, string> literals = new(staticProperties.Count, StringComparer.Ordinal);
        foreach (string property in staticProperties)
        {
            int equalsIndex = property.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            literals[property[..equalsIndex]] = property[(equalsIndex + 1)..];
        }

        return literals;
    }

    private static string InlineStaticPropertyLiterals(string source, IReadOnlyDictionary<string, string> staticLiterals)
    {
        if (staticLiterals.Count == 0 || string.IsNullOrEmpty(source))
            return source;

        StringBuilder builder = new(source.Length);
        bool inBlockComment = false;
        bool pendingStructDeclaration = false;
        int structDeclarationDepth = 0;
        foreach (string line in SplitLinesPreservingNewlines(source))
        {
            int firstNonWhitespace = 0;
            while (firstNonWhitespace < line.Length &&
                   char.IsWhiteSpace(line[firstNonWhitespace]) &&
                   line[firstNonWhitespace] is not '\r' and not '\n')
            {
                firstNonWhitespace++;
            }

            if (firstNonWhitespace < line.Length && line[firstNonWhitespace] == '#')
            {
                builder.Append(line);
                continue;
            }

            if (!inBlockComment &&
                ShouldSkipStaticLiteralInliningForStructLine(line, ref pendingStructDeclaration, ref structDeclarationDepth))
            {
                builder.Append(line);
                continue;
            }

            InlineStaticPropertyLiteralsInLine(line, staticLiterals, builder, ref inBlockComment);
        }

        return builder.ToString();
    }

    private static bool ShouldSkipStaticLiteralInliningForStructLine(
        string line,
        ref bool pendingStructDeclaration,
        ref int structDeclarationDepth)
    {
        bool startsStruct = StartsWithStructKeyword(line);
        bool shouldSkip = pendingStructDeclaration || structDeclarationDepth > 0 || startsStruct;
        if (!shouldSkip)
            return false;

        if (startsStruct)
            pendingStructDeclaration = true;

        bool inLineComment = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inLineComment)
                break;

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '{')
            {
                structDeclarationDepth++;
                pendingStructDeclaration = false;
                continue;
            }

            if (c == '}' && structDeclarationDepth > 0)
            {
                structDeclarationDepth--;
                continue;
            }

            if (c == ';' && structDeclarationDepth == 0)
                pendingStructDeclaration = false;
        }

        return true;
    }

    private static bool StartsWithStructKeyword(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;

        const string keyword = "struct";
        if (line.Length - i < keyword.Length ||
            !line.AsSpan(i, keyword.Length).SequenceEqual(keyword))
        {
            return false;
        }

        int next = i + keyword.Length;
        return next >= line.Length || !IsIdentifierPart(line[next]);
    }

    private static void InlineStaticPropertyLiteralsInLine(
        string line,
        IReadOnlyDictionary<string, string> staticLiterals,
        StringBuilder builder,
        ref bool inBlockComment)
    {
        int i = 0;
        bool inString = false;
        while (i < line.Length)
        {
            char c = line[i];
            if (inBlockComment)
            {
                builder.Append(c);
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    builder.Append('/');
                    i += 2;
                    inBlockComment = false;
                    continue;
                }

                i++;
                continue;
            }

            if (inString)
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < line.Length)
                {
                    builder.Append(line[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"')
                    inString = false;
                i++;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                {
                    builder.Append(line, i, line.Length - i);
                    return;
                }

                if (line[i + 1] == '*')
                {
                    builder.Append("/*");
                    i += 2;
                    inBlockComment = true;
                    continue;
                }
            }

            if (c == '"')
            {
                builder.Append(c);
                inString = true;
                i++;
                continue;
            }

            if (!IsIdentifierStart(c))
            {
                builder.Append(c);
                i++;
                continue;
            }

            int start = i++;
            while (i < line.Length && IsIdentifierPart(line[i]))
                i++;

            string identifier = line[start..i];
            bool isFieldName = start > 0 && line[start - 1] == '.';
            if (!isFieldName && staticLiterals.TryGetValue(identifier, out string? literal))
            {
                bool hasSuffixAccess = i < line.Length && line[i] == '.';
                builder.Append(hasSuffixAccess ? $"({literal})" : literal);
                continue;
            }

            builder.Append(identifier);
        }
    }

    private static HashSet<string> ResolveResidualDefineMacros(
        string source,
        IEnumerable<string> disabledFeatureMacros,
        IEnumerable<string> pipelineMacros)
    {
        HashSet<string> candidates = new(StringComparer.Ordinal);
        foreach (string macro in disabledFeatureMacros)
            candidates.Add(macro);
        foreach (string macro in pipelineMacros)
            candidates.Add(macro);

        HashSet<string> residual = new(StringComparer.Ordinal);
        if (candidates.Count == 0)
            return residual;

        bool inBlockComment = false;
        foreach (string line in SplitLinesPreservingNewlines(source))
            CollectResidualMacroIdentifiers(line, candidates, residual, ref inBlockComment);

        return residual;
    }

    private static void CollectResidualMacroIdentifiers(
        string line,
        IReadOnlySet<string> candidates,
        HashSet<string> residual,
        ref bool inBlockComment)
    {
        int i = 0;
        bool inString = false;
        while (i < line.Length)
        {
            char c = line[i];
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    i += 2;
                    inBlockComment = false;
                    continue;
                }

                i++;
                continue;
            }

            if (inString)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                    inString = false;
                i++;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    return;
                if (line[i + 1] == '*')
                {
                    i += 2;
                    inBlockComment = true;
                    continue;
                }
            }

            if (c == '"')
            {
                inString = true;
                i++;
                continue;
            }

            if (!IsIdentifierStart(c))
            {
                i++;
                continue;
            }

            int start = i++;
            while (i < line.Length && IsIdentifierPart(line[i]))
                i++;

            string identifier = line[start..i];
            if (candidates.Contains(identifier))
                residual.Add(identifier);
        }
    }

    private static string CollapseBlankLineRuns(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        StringBuilder builder = new(source.Length);
        bool previousBlank = false;
        foreach (string line in SplitLinesPreservingNewlines(source))
        {
            bool blank = IsBlankLine(line);
            if (blank && previousBlank)
                continue;

            builder.Append(line);
            previousBlank = blank;
        }

        return builder.ToString();
    }

    private static bool IsBlankLine(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '\r' or '\n')
                continue;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    private static string InsertDefinesAfterVersion(string source, IReadOnlyList<string> defines)
    {
        if (defines.Count == 0)
            return source;

        Match versionMatch = Regex.Match(source, @"^[ \t]*#version.*$", RegexOptions.Multiline);
        int defineBlockLength = 0;
        foreach (string define in defines)
            defineBlockLength += define.Length + Environment.NewLine.Length;

        StringBuilder defineBuilder = new(defineBlockLength);
        foreach (string define in defines)
            defineBuilder.Append(define).Append(Environment.NewLine);
        string defineBlock = defineBuilder.ToString();
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

        return feature.DefaultEnabled;
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

        return FormatShaderParameterStaticLiteral(resolvedParameter);
    }

    private static string FormatShaderParameterStaticLiteral(ShaderVar parameter)
    {
        return parameter switch
        {
            ShaderBool value => value.Value ? "true" : "false",
            ShaderInt value => value.Value.ToString(CultureInfo.InvariantCulture),
            ShaderUInt value => value.Value.ToString(CultureInfo.InvariantCulture) + "u",
            ShaderFloat value => FormatFloatLiteral(value.Value),
            ShaderVector2 value => $"vec2({FormatFloatLiteral(value.Value.X)}, {FormatFloatLiteral(value.Value.Y)})",
            ShaderVector3 value => $"vec3({FormatFloatLiteral(value.Value.X)}, {FormatFloatLiteral(value.Value.Y)}, {FormatFloatLiteral(value.Value.Z)})",
            ShaderVector4 value => $"vec4({FormatFloatLiteral(value.Value.X)}, {FormatFloatLiteral(value.Value.Y)}, {FormatFloatLiteral(value.Value.Z)}, {FormatFloatLiteral(value.Value.W)})",
            _ => throw new InvalidOperationException($"Uber shader parameter '{parameter.Name}' uses unsupported shader parameter type '{parameter.GetType().Name}'."),
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
        ManifestDerivedData manifestData = GetManifestDerivedData(manifest);
        HashSet<string> enabledFeatures = request.EnabledFeatures.ToHashSet(StringComparer.Ordinal);
        int count = 0;
        foreach (ShaderUiProperty property in manifestData.SamplerProperties)
        {
            if (property.FeatureId is not null && !enabledFeatures.Contains(property.FeatureId))
                continue;

            count++;
        }

        return count;
    }

    private static string ResolveStaticLiteral(XRMaterial material, ShaderUiProperty property)
    {
        UberMaterialPropertyState? authored = material.UberAuthoredState.GetProperty(property.Name);
        if (!string.IsNullOrWhiteSpace(authored?.StaticLiteral))
            return authored.StaticLiteral!;

        if (TryFormatMaterialParameterStaticLiteral(material, property, out string literal))
            return literal;

        if (TryResolveBakedStaticLiteral(material.ActiveUberVariant.StaticProperties, property.Name, out literal) ||
            TryResolveBakedStaticLiteral(material.RequestedUberVariant.StaticProperties, property.Name, out literal))
        {
            return literal;
        }

        return FormatStaticLiteral(material, property);
    }

    private static bool TryFormatMaterialParameterStaticLiteral(XRMaterial material, ShaderUiProperty property, out string literal)
    {
        ShaderVar? parameter = material.Parameters?.FirstOrDefault(x => string.Equals(x.Name, property.Name, StringComparison.Ordinal));
        if (parameter is not null)
        {
            literal = FormatShaderParameterStaticLiteral(parameter);
            return true;
        }

        if (string.Equals(property.Name, "_Cutoff", StringComparison.Ordinal))
        {
            literal = FormatFloatLiteral(material.AlphaCutoff);
            return true;
        }

        literal = string.Empty;
        return false;
    }

    private static bool TryResolveBakedStaticLiteral(IReadOnlyList<string> staticProperties, string propertyName, out string literal)
    {
        literal = string.Empty;
        string prefix = propertyName + "=";
        foreach (string property in staticProperties)
        {
            if (!property.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            literal = property[prefix.Length..];
            return true;
        }

        return false;
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
        XxHash64 hash = new();
        AppendInt32(hash, 2); // hash schema: xxHash64 variant request

        foreach (string value in enabledFeatures)
            AppendString(hash, value);
        foreach (string value in pipelineMacros)
            AppendString(hash, value);
        foreach (string value in animatedProperties)
            AppendString(hash, value);
        foreach (string value in staticProperties)
            AppendString(hash, value);

        AppendInt32(hash, renderPass);
        AppendInt64(hash, sourceVersion);
        AppendUInt64(hash, vertexPermutationHash);
        return hash.GetCurrentHashAsUInt64();
    }

    private static ulong ComputeVertexPermutationHash(XRMaterial material)
    {
        XxHash64 hash = new();
        AppendInt32(hash, 2); // hash schema: xxHash64 vertex permutation

        foreach (XRShader shader in material.Shaders
            .Where(static x => x is not null && x.Type != EShaderType.Fragment)
            .OrderBy(static x => x.Type))
        {
            ResolvedShaderSource resolved = ResolveShaderSourceCached(shader, emitIncludeDeadCodeMarkers: false);
            VertexPermutationCacheKey cacheKey = new(
                shader.Type,
                shader.Source?.Text ?? string.Empty,
                resolved.SourcePath,
                resolved.SourceVersion);
            ulong shaderHash = VertexPermutationHashCache.GetOrAdd(cacheKey, static key =>
            {
                XxHash64 shaderHasher = new();
                AppendInt32(shaderHasher, 2);
                AppendString(shaderHasher, key.Type.ToString());
                AppendString(shaderHasher, key.SourcePath ?? string.Empty);
                AppendInt64(shaderHasher, key.SourceVersion);
                return shaderHasher.GetCurrentHashAsUInt64();
            });

            AppendUInt64(hash, shaderHash);
        }

        return hash.GetCurrentHashAsUInt64();
    }

    private static void PruneStaleSourceEntries(string? sourcePath, ulong sourcePathHash, long sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;

        bool versionChanged = false;
        LastKnownSourceVersionsByPath.AddOrUpdate(
            sourcePath,
            _ =>
            {
                versionChanged = true;
                return sourceVersion;
            },
            (_, previousVersion) =>
            {
                versionChanged = previousVersion != sourceVersion;
                return sourceVersion;
            });

        if (!versionChanged)
            return;

        foreach ((UberVariantCacheKey key, _) in GeneratedSourceCache)
        {
            if (key.SourcePathHash != sourcePathHash ||
                !string.Equals(key.SourcePath, sourcePath, StringComparison.Ordinal) ||
                key.SourceVersion == sourceVersion)
            {
                continue;
            }

            GeneratedSourceCache.TryRemove(key, out _);
            GeneratedShaderCache.TryRemove(key, out _);
        }
    }

    private static ulong ComputeStableHash(string source)
    {
        XxHash64 hash = new();
        AppendString(hash, source);
        return hash.GetCurrentHashAsUInt64();
    }

    private static string? NormalizeSourcePathKey(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return sourcePath;

        try
        {
            return File.Exists(sourcePath)
                ? Path.GetFullPath(sourcePath)
                : sourcePath;
        }
        catch
        {
            return sourcePath;
        }
    }

    private static void AppendInt32(XxHash64 hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendInt64(XxHash64 hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendUInt64(XxHash64 hash, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendString(XxHash64 hash, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            AppendInt32(hash, 0);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        AppendInt32(hash, byteCount);
        Span<byte> stackBuffer = byteCount <= 512 ? stackalloc byte[byteCount] : [];
        if (!stackBuffer.IsEmpty)
        {
            Encoding.UTF8.GetBytes(value, stackBuffer);
            hash.Append(stackBuffer);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Span<byte> buffer = rented.AsSpan(0, byteCount);
            Encoding.UTF8.GetBytes(value, buffer);
            hash.Append(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [GeneratedRegex(@"^\s*(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?:(?:lowp|mediump|highp)\s+)?(?<type>\w+)\s+(?<name>\w+)(?:\s*\[[^\]]+\])?\s*;", RegexOptions.Multiline)]
    private static partial Regex UberStaticUniformRegex();

    [GeneratedRegex(@"^[ \t]*#define[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[^\r\n]*)?\r?\n?", RegexOptions.Multiline)]
    private static partial Regex DefineLineRegex();
}
