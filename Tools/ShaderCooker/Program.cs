using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Tools.ShaderCooker;

/// <summary>Packages explicit engine and legacy fixture shader recipes into immutable, content-addressed assets.</summary>
internal static partial class Program
{
    private const int MaxSourceBytes = 1024 * 1024;
    private const int MaxJsonBytes = 64 * 1024;
    private const int MaxArtifacts = 256;
    private const string Coordinates = BrowserShaderAbi.CoordinateConvention;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };
    private static readonly string[] RecipeKeys = ["schemaVersion", "name", "source", "sourceLanguage", "target", "entryPoints", "defines", "includes", "specialization", "requiredFeatures", "requiredLimits", "matrixLayout", "semanticSchemaIdentity", "layout", "pipeline"];
    private static readonly JsonNode Layout = ParseLiteral("""
        {"vertexStride":20,"positionOffset":0,"uvOffset":12,"transformBytes":64,"materialBytes":16,"bindings":[{"group":0,"binding":0,"kind":"uniform","visibility":"vertex","bytes":64,"dynamic":true},{"group":1,"binding":0,"kind":"uniform","visibility":"fragment","bytes":16,"dynamic":false},{"group":1,"binding":1,"kind":"texture-2d-float","visibility":"fragment"},{"group":1,"binding":2,"kind":"filtering-sampler","visibility":"fragment"}]}
        """);
    private static readonly JsonNode Pipeline = ParseLiteral("""
        {"topology":"triangle-list","cullMode":"none","depthFormat":"depth24plus","depthWrite":true,"depthCompare":"less","blend":false,"sampleCount":1}
        """);
    private static readonly Dictionary<string, int> MinimumLimits = new(StringComparer.Ordinal)
    {
        ["maxVertexAttributes"] = 2, ["maxBindGroups"] = 2, ["maxBindingsPerBindGroup"] = 3,
        ["maxUniformBufferBindingSize"] = 64, ["maxDynamicUniformBuffersPerPipelineLayout"] = 1,
    };

    private static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            string repository = FindRepository();
            string sourceRoot = Path.Combine(repository, "XREngine.Runtime.Rendering.WebGPU", "Assets");
            string output = Path.Combine(sourceRoot, "shaders");
            List<string> recipes = [];
            bool explicitRoot = false, explicitOutput = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("ShaderCooker [--recipe <path>]... [--source-root <directory>] [--output <directory>]");
                    return 0;
                }
                if (i + 1 >= args.Length || args[i] is not ("--recipe" or "--source-root" or "--output"))
                    throw new InvalidDataException($"Unknown or incomplete argument '{args[i]}'.");
                string value = args[++i];
                if (args[i - 1] == "--recipe") recipes.Add(value);
                else if (args[i - 1] == "--source-root") { sourceRoot = value; explicitRoot = true; }
                else { output = value; explicitOutput = true; }
            }
            if (recipes.Count == 0)
                recipes.Add(Path.Combine(repository, "XREngine.Runtime.Rendering.WebGPU", "Shaders", "browser-unlit.recipe.json"));
            if (recipes.Count > MaxArtifacts)
                throw new InvalidDataException($"A package supports 1–{MaxArtifacts} recipes.");
            sourceRoot = Path.GetFullPath(sourceRoot);
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Source root does not exist: {sourceRoot}");
            RejectLinks(sourceRoot, sourceRoot);
            if (explicitRoot && !explicitOutput) output = Path.Combine(sourceRoot, "shaders");
            output = Path.GetFullPath(output);
            string dependencyRoot = Path.GetFullPath(Path.Combine(sourceRoot, ".."));
            List<PreparedShader> prepared = [];
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (string path in recipes)
            {
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    PreparedShader result = await PrepareAsync(Path.GetFullPath(path), sourceRoot, dependencyRoot, cancellation.Token);
                    if (!names.Add(result.Name)) throw new InvalidDataException($"Duplicate shader artifact name '{result.Name}'.");
                    prepared.Add(result);
                    for (PreparedShader? companion = result.Companion; companion is not null; companion = companion.Companion)
                    {
                        if (!names.Add(companion.Name)) throw new InvalidDataException($"Duplicate shader artifact name '{companion.Name}'.");
                        prepared.Add(companion);
                    }
                    Require(prepared.Count <= MaxArtifacts, $"A package supports at most {MaxArtifacts} artifacts, including generated companions.");
                }
                catch (ShaderCompilationException) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException)
                {
                    throw new InvalidDataException($"Recipe {path}: {error.Message}", error);
                }
            }
            int distinctSchemas = prepared.Select(item => ParseJson(item.Descriptor)["schemaVersion"]!.GetValue<int>()).Distinct().Count();
            Require(distinctSchemas == 1, "Different artifact schemas require separate cooker invocations.");
            // The manifest is the commit point: prepare and validate every artifact before writing it.
            CheckAncestry(output);
            Directory.CreateDirectory(output);
            CheckAncestry(output);
            JsonArray artifacts = [];
            JsonArray materialVariants = [];
            JsonArray pipelineArtifacts = [];
            JsonArray computeArtifacts = [];
            HashSet<string> variantKeys = new(StringComparer.Ordinal);
            HashSet<string> pipelineBindings = new(StringComparer.Ordinal);
            HashSet<string> computeKernels = new(StringComparer.Ordinal);
            foreach (PreparedShader item in prepared.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                string descriptorHash = Hash(item.Descriptor);
                string descriptorName = descriptorHash + ".shader.json";
                WriteAtomic(Path.Combine(output, Hash(item.Source) + ".wgsl"), item.Source, true);
                WriteAtomic(Path.Combine(output, descriptorName), item.Descriptor, true);
                artifacts.Add(new JsonObject { ["name"] = item.Name, ["descriptor"] = descriptorName, ["sha256"] = descriptorHash });
                if (item.MaterialVariant is { } variant)
                {
                    JsonObject reference = (JsonObject)variant.DeepClone();
                    reference["descriptorIdentity"] = descriptorHash;
                    string key = string.Join('\u001f', reference["semantic"], reference["semanticVersion"], reference["target"],
                        reference["pass"], reference["vertexProfile"], reference["outputProfile"]);
                    Require(variantKeys.Add(key), $"Duplicate material variant key for '{item.Name}'.");
                    materialVariants.Add(reference);
                }
                if (item.PipelineArtifact is { } pipeline)
                {
                    JsonObject reference = (JsonObject)pipeline.DeepClone();
                    reference["descriptorIdentity"] = descriptorHash;
                    string pass = String(reference, "pass");
                    string? scope = reference.ContainsKey("scope") ? String(reference, "scope") : null;
                    string bindingKey = WebPipelineArtifactCatalog.GetBindingKey(scope, pass);
                    Require(pipelineBindings.Add(bindingKey), $"Duplicate pipeline artifact binding '{bindingKey}'.");
                    Require(pipelineBindings.Count <= WebPipelineArtifactCatalog.MaximumEntries,
                        "Pipeline artifact catalog exceeds its limit.");
                    pipelineArtifacts.Add(reference);
                }
                if (item.ComputeArtifact is { } compute)
                {
                    JsonObject reference = (JsonObject)compute.DeepClone();
                    reference["descriptorIdentity"] = descriptorHash;
                    string kernel = String(reference, "kernel");
                    Require(computeKernels.Add(kernel), $"Duplicate compute artifact kernel '{kernel}'.");
                    computeArtifacts.Add(reference);
                }
            }
            int schema = ParseJson(prepared[0].Descriptor)["schemaVersion"]!.GetValue<int>();
            JsonObject manifestDocument = new() { ["schemaVersion"] = schema, ["backend"] = "WebGPU", ["packetVersion"] = 2, ["artifacts"] = artifacts };
            if (materialVariants.Count > 0) manifestDocument["materialVariants"] = materialVariants;
            if (pipelineArtifacts.Count > 0) manifestDocument["pipelineArtifacts"] = pipelineArtifacts;
            if (computeArtifacts.Count > 0) manifestDocument["computeArtifacts"] = computeArtifacts;
            byte[] manifest = Canonical(manifestDocument);
            Require(manifest.Length <= (schema == 3 ? ShaderProgramArtifactCatalog.MaximumManifestBytes : MaxJsonBytes),
                "Manifest exceeds the JSON byte limit.");
            cancellation.Token.ThrowIfCancellationRequested();
            WriteAtomic(Path.Combine(output, "manifest.json"), manifest, false);
            Console.WriteLine($"Packaged {prepared.Count} shader artifact(s).");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Shader cook canceled; no new manifest was published.");
            return 130;
        }
        catch (ShaderCompilationException error)
        {
            string context = error.Data["ShaderCooker.Context"] as string ?? "shader source";
            Console.Error.WriteLine($"Shader cook failed: {context}: {error.Message}");
            foreach (ShaderCompileDiagnostic diagnostic in error.Diagnostics)
            {
                string location = diagnostic.OriginalPath is null ? context :
                    diagnostic.Line is null ? diagnostic.OriginalPath :
                    diagnostic.Column is null ? $"{diagnostic.OriginalPath}:{diagnostic.Line}" :
                    $"{diagnostic.OriginalPath}:{diagnostic.Line}:{diagnostic.Column}";
                Console.Error.WriteLine($"{location}: {diagnostic.Severity ?? "error"}: {diagnostic.Message}");
            }
            return 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"Shader cook failed: {error.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static async Task<SlangWgslOutput> CompileSlangWithContextAsync(string context, string sourceRoot,
        string source, IReadOnlyList<string> includes, IReadOnlyList<string> defines, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> entryPoints, bool preserveResourceParameters = false)
    {
        try
        {
            return await SlangWgslCompiler.CompileAsync(sourceRoot, source, includes, defines, cancellationToken,
                entryPoints, preserveResourceParameters);
        }
        catch (ShaderCompilationException error)
        {
            error.Data["ShaderCooker.Context"] = context;
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException)
        {
            throw new InvalidDataException($"{context}: {error.Message}", error);
        }
    }

    private static T WithMaterialSourceContext<T>(string context, Func<T> plan)
    {
        try { return plan(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            if (error.Message.StartsWith(context + ":", StringComparison.Ordinal)) throw;
            throw new InvalidDataException($"{context}: {error.Message}", error);
        }
    }

    private static async Task<PreparedShader> PrepareAsync(string recipePath, string sourceRoot, string dependencyRoot, CancellationToken cancellationToken)
    {
        RejectLinks(dependencyRoot, recipePath);
        byte[] recipeBytes = ReadBounded(recipePath, MaxJsonBytes);
        JsonObject recipe = Object(ParseJson(recipeBytes), "recipe");
        int schema = Integer(recipe, "schemaVersion");
        Require(schema is 1 or 2 or 3, "Unsupported recipe schemaVersion.");
        HashSet<string> keys = new(RecipeKeys, StringComparer.Ordinal);
        if (schema >= 2) keys.Add("coordinates");
        if (schema == 3) keys.Add("pass");
        if (schema == 3 && recipe.ContainsKey("materialVariant")) keys.Add("materialVariant");
        if (schema == 3 && recipe.ContainsKey("pipelineArtifact")) keys.Add("pipelineArtifact");
        if (schema == 3 && recipe.ContainsKey("computeArtifact")) keys.Add("computeArtifact");
        if (schema == 3 && recipe.ContainsKey("workgroupSize")) keys.Add("workgroupSize");
        Require(recipe.Count == keys.Count && recipe.All(item => keys.Contains(item.Key)), "Recipe properties must match the shader recipe schema.");
        string name = String(recipe, "name");
        Require(Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant), "Name must be a lowercase shader identifier.");
        string stageContext = $"material '{name}' pass '{(schema == 3 ? String(recipe, "pass") : "opaque")}' source '{String(recipe, "source")}' target 'WebGPUWgsl'";
        string language = String(recipe, "sourceLanguage");
        Require(language is "WGSL" or "MaterialRecipe" or "Slang", $"{stageContext}: unsupported sourceLanguage '{language}'.");
        Require(schema >= 2 || language == "WGSL", $"{stageContext}: schema 1 supports explicit WGSL only.");
        Require(String(recipe, "target") == "WebGPUWgsl", $"{stageContext}: target must be WebGPUWgsl.");
        ShaderProgramArtifact? engineLayout = null;
        JsonObject? materialVariant = null;
        JsonObject? pipelineArtifact = null;
        JsonObject? computeArtifact = null;
        if (schema == 3)
        {
            Require(language is "Slang" or "WGSL" or "MaterialRecipe", $"{stageContext}: engine recipes require Slang, WGSL, or an authored engine material recipe.");
            using JsonDocument layoutDocument = JsonDocument.Parse(recipeBytes);
            try { engineLayout = ShaderProgramArtifactReader.ReadLayout(layoutDocument.RootElement, ShaderArtifact.FromWgsl(""), "recipe"); }
            catch (InvalidDataException error) { throw new InvalidDataException($"{stageContext}: {error.Message}", error); }
            if (recipe.TryGetPropertyValue("materialVariant", out JsonNode? variantNode))
            {
                JsonObject variant = Object(variantNode, "materialVariant");
                Require(variant.Count == 4 && variant.ContainsKey("semantic") && variant.ContainsKey("semanticVersion")
                    && variant.ContainsKey("vertexProfile") && variant.ContainsKey("outputProfile"), $"{stageContext}: invalid materialVariant properties.");
                string semantic = String(variant, "semantic");
                int semanticVersion = Integer(variant, "semanticVersion");
                bool knownSemantic = semantic is "Unlit" or "StandardLitColor" or "StandardLitTexture" or "OpaqueShadowDepth" or "DebugPoint" or "DebugLine" or "DebugTriangle" or
                    "UIQuadBatched" or "UIQuadBatchedTexture" or "UITextBatchedBitmap" or "UICanvasSurface" or "UberOutline" or "OpaquePointShadowDepth" or "OpaqueSpotShadowDepth" or
                    "SkyboxGradient" or "SkyboxEquirectangular" or "SkyboxOctahedral" or
                    "SkyboxCubemap" or "SkyboxDynamicProcedural" or "AuthoredLitTextureAlpha" or "AuthoredLitTextured" or "OctahedralImpostor";
                bool knownVersion = semantic == "Unlit" ? semanticVersion is >= 1 and <= 5 :
                    semanticVersion == 1 || semanticVersion == 2 &&
                    semantic is "StandardLitColor" or "UIQuadBatched" or "UIQuadBatchedTexture" or "UITextBatchedBitmap";
                Require(knownSemantic && knownVersion,
                    $"{stageContext}: unsupported engine material semantic.");
                string vertexProfile = String(variant, "vertexProfile"), outputProfile = String(variant, "outputProfile");
                if (semantic == "Unlit")
                {
                    EngineMaterialSemanticIdentity unlitSemantic = new(EngineMaterialSemantic.Unlit, semanticVersion);
                    bool builtIn = language == "MaterialRecipe";
                    EngineMaterialVariantKey expected = builtIn
                        ? EngineUnlitMaterialShaderGenerator.BuiltInKey(unlitSemantic)
                        : EngineUnlitMaterialShaderGenerator.CompanionKey(unlitSemantic, String(recipe, "pass"),
                            String(recipe, "semanticSchemaIdentity") == EngineUnlitMaterialShaderGenerator.OrderGateSchema);
                    Require(vertexProfile == expected.VertexProfile && outputProfile == expected.OutputProfile &&
                        (!builtIn || String(recipe, "name") == EngineUnlitMaterialShaderGenerator.BuiltInName(unlitSemantic) &&
                         String(recipe, "semanticSchemaIdentity") == EngineUnlitMaterialShaderGenerator.SchemaFor(unlitSemantic)) &&
                        engineLayout.VertexEntryPoint == "unlitVertex" && engineLayout.FragmentEntryPoint ==
                        (expected.Pass switch
                        {
                            EngineUnlitMaterialShaderGenerator.Pass => "unlitFragment",
                            "depth-normal" => "unlitNormalFragment",
                            "depth" => "unlitDepthFragment",
                            "point-shadow-depth" => "unlitPointDepthFragment",
                            "spot-shadow-depth" => "unlitSpotDepthFragment",
                            _ => throw new NotSupportedException($"Unsupported unlit pass '{expected.Pass}'."),
                        }), $"{stageContext}: unlit variant has an unsupported pass, entry or profile.");
                }
                if (semantic == "OctahedralImpostor")
                    Require(String(recipe, "pass") == EngineOctahedralImpostorShaderContract.Pass &&
                        vertexProfile is EngineOctahedralImpostorShaderContract.VertexProfile or EngineOctahedralImpostorShaderContract.OrderGateVertexProfile &&
                        outputProfile == EngineOctahedralImpostorShaderContract.OutputProfile,
                        $"{stageContext}: impostors require their exact camera-facing vertex and 26-view RGBA profile.");
                if (semantic == "UberOutline")
                    Require(String(recipe, "pass") == "outline" && vertexProfile == "position-normal-uv4-color-v1" &&
                        outputProfile is "linear-hdr-v1" or "linear-hdr-alpha-mask-v1" or "linear-hdr-dissolve-v1" or "linear-hdr-alpha-mask-dissolve-v1" &&
                        engineLayout.VertexEntryPoint is not null && engineLayout.FragmentEntryPoint is not null,
                        $"{stageContext}: outlines require the exact inverse-hull vertex and authored coverage profile.");
                if (semantic is "SkyboxGradient" or "SkyboxEquirectangular" or "SkyboxOctahedral" or
                    "SkyboxCubemap" or "SkyboxDynamicProcedural")
                    Require(String(recipe, "pass") == "background" && vertexProfile == "fullscreen-sky-v1" &&
                        outputProfile == "linear-hdr-v1" && engineLayout.VertexEntryPoint is not null && engineLayout.FragmentEntryPoint is not null,
                        $"{stageContext}: skybox requires its exact HDR background pass and profiles.");
                if (semantic == "OpaquePointShadowDepth")
                    Require(String(recipe, "pass") == "point-shadow-depth" && vertexProfile == "static-position-v1" &&
                        outputProfile == "radial-r16f-v1" && engineLayout.VertexEntryPoint is not null && engineLayout.FragmentEntryPoint is not null,
                        $"{stageContext}: point shadows require the radial R16F vertex and fragment profile.");
                if (semantic == "OpaqueSpotShadowDepth")
                    Require(String(recipe, "pass") == "spot-shadow-depth" && vertexProfile == "static-position-v1" &&
                        outputProfile == "projected-r16f-v1" && engineLayout.VertexEntryPoint is not null && engineLayout.FragmentEntryPoint is not null,
                        $"{stageContext}: spot shadows require the projected R16F vertex and fragment profile.");
                if (semantic == "OpaqueShadowDepth")
                    Require(String(recipe, "pass") == "depth" && vertexProfile == "static-position-v1" &&
                        outputProfile == "depth-normal-v1" &&
                        CanonicalString(recipe["entryPoints"]) == "{\"vertex\":\"depthVertex\"}\n",
                        $"{stageContext}: opaque shadow depth requires its vertex-only depth recipe.");
                string? debugProfile = semantic switch
                {
                    "DebugPoint" => "instanced-debug-point-v1",
                    "DebugLine" => "instanced-debug-line-v1",
                    "DebugTriangle" => "instanced-debug-triangle-v1",
                    _ => null,
                };
                if (debugProfile is not null)
                    Require(String(recipe, "pass") == "debug-overlay" && vertexProfile == debugProfile &&
                        outputProfile == "display-rgba-v1",
                        $"{stageContext}: debug primitive requires its exact overlay pass and profiles.");
                string? uiProfile = semantic switch
                {
                    "UIQuadBatched" => "instanced-ui-quad-v1",
                    "UIQuadBatchedTexture" => "instanced-ui-quad-texture-v1",
                    "UITextBatchedBitmap" => "instanced-ui-bitmap-text-v1",
                    _ => null,
                };
                if (uiProfile is not null)
                    Require(String(recipe, "pass") == "screen-ui" && vertexProfile == uiProfile &&
                        outputProfile == (semanticVersion == 2 ? "canvas-rgba-v2" : "display-rgba-v1"),
                        $"{stageContext}: screen UI requires its exact versioned output profile and batched pass.");
                if (semantic == "UICanvasSurface")
                    Require(String(recipe, "pass") == "canvas-composite" && vertexProfile == "position-uv-v1" &&
                        outputProfile == "linear-hdr-premultiplied-rgba-v1" &&
                        engineLayout.VertexEntryPoint == "canvasSurfaceVertex" && engineLayout.FragmentEntryPoint == "canvasSurfaceFragment",
                        $"{stageContext}: canvas surfaces require their exact composite pass, profiles, and entry points.");
                Require(Regex.IsMatch(vertexProfile, "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)
                    && Regex.IsMatch(outputProfile, "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant),
                    $"{stageContext}: invalid material variant profile.");
                Require(language != "MaterialRecipe" || semantic == "Unlit",
                    $"{stageContext}: authored material recipes use exact stage companions, not built-in semantic variants.");
                materialVariant = new JsonObject { ["semantic"] = semantic, ["semanticVersion"] = semanticVersion,
                    ["target"] = "WebGPUWgsl", ["pass"] = String(recipe, "pass"),
                    ["vertexProfile"] = vertexProfile, ["outputProfile"] = outputProfile };
            }
            if (recipe.TryGetPropertyValue("pipelineArtifact", out JsonNode? pipelineNode))
            {
                JsonObject pipeline = Object(pipelineNode, "pipelineArtifact");
                string pass = String(pipeline, "pass");
                bool hasScope = pipeline.ContainsKey("scope");
                string? scope = hasScope ? String(pipeline, "scope") : null;
                _ = WebPipelineArtifactCatalog.GetBindingKey(scope, pass);
                JsonObject entries = Object(recipe["entryPoints"], "entryPoints");
                bool raster = entries.Count == 2 && entries.ContainsKey("vertex") && entries.ContainsKey("fragment") &&
                    engineLayout.VertexEntryPoint is not null && engineLayout.FragmentEntryPoint is not null &&
                    engineLayout.ComputeEntryPoint is null;
                bool compute = entries.Count == 1 && entries.ContainsKey("compute") &&
                    engineLayout.ComputeEntryPoint is not null && engineLayout.VertexEntryPoint is null &&
                    engineLayout.FragmentEntryPoint is null && engineLayout.ComputeWorkgroupSize is not null;
                Require(pipeline.Count == (hasScope ? 2 : 1) &&
                    pass == String(recipe, "pass") && (raster ^ compute) && materialVariant is null,
                    $"{stageContext}: the pipeline artifact must explicitly select a complete authored raster or compute program without a material variant.");
                pipelineArtifact = new JsonObject { ["pass"] = pass };
                if (scope is not null)
                    pipelineArtifact["scope"] = scope;
            }
            if (recipe.TryGetPropertyValue("computeArtifact", out JsonNode? computeNode))
            {
                JsonObject compute = Object(computeNode, "computeArtifact");
                JsonObject entries = Object(recipe["entryPoints"], "entryPoints");
                string kernel = String(compute, "kernel");
                bool skinning = kernel == WebComputeArtifactCatalog.PackedSkinningKernel;
                bool reduction = kernel is WebComputeArtifactCatalog.LuminanceReductionKernel or
                    WebComputeArtifactCatalog.LuminanceReduction2DKernel;
                bool mipmap = kernel == WebComputeArtifactCatalog.LuminanceMipmapKernel;
                Require(compute.Count == 1 && (skinning || reduction || mipmap) &&
                    String(recipe, "pass") == (skinning ? "skinning" : kernel) && entries.Count == 1 &&
                    String(entries, "compute") == (skinning ? "skin" : mipmap ? "generate" : "reduce") &&
                    materialVariant is null && pipelineArtifact is null &&
                    String(recipe, "semanticSchemaIdentity") == "xrengine.engine.compute.v1" &&
                    CanonicalString(recipe["workgroupSize"]) == (skinning ? "[64,1,1]\n" : mipmap ? "[16,16,1]\n" : "[256,1,1]\n"),
                    $"{stageContext}: engine compute kernel requires its exact identity, entry, workgroup and semantic schema.");
                computeArtifact = new JsonObject { ["kernel"] = kernel };
            }
        }
        else
        {
            Require(CanonicalString(recipe["entryPoints"]) == "{\"fragment\":\"fragmentMain\",\"vertex\":\"vertexMain\"}\n", $"{stageContext}: entry points must be vertexMain and fragmentMain.");
            Require(String(recipe, "matrixLayout") == "column-major" && String(recipe, "semanticSchemaIdentity") == "xrengine.browser.mesh.v1", $"{stageContext}: incompatible matrix or semantic ABI.");
            Require(CanonicalString(recipe["layout"]) == CanonicalString(Layout) && CanonicalString(recipe["pipeline"]) == CanonicalString(Pipeline), $"{stageContext}: unsupported binding, vertex, material, or pipeline ABI.");
            if (schema == 2) Require(String(recipe, "coordinates") == Coordinates, $"{stageContext}: incompatible WebGPU coordinate convention.");
            JsonArray features = Array(recipe["requiredFeatures"], "requiredFeatures");
            Require(features.Count == 0, $"{stageContext}: this profile does not support optional WebGPU features.");
            JsonObject limits = Object(recipe["requiredLimits"], "requiredLimits");
            Require(limits.Count == MinimumLimits.Count && limits.All(item => MinimumLimits.ContainsKey(item.Key)), $"{stageContext}: unsupported requiredLimits.");
            foreach ((string key, int minimum) in MinimumLimits)
                Require(Integer(limits, key) >= minimum, $"{stageContext}: requiredLimits.{key} must be at least {minimum}.");
        }
        JsonArray defines = Array(recipe["defines"], "defines");
        JsonArray includes = Array(recipe["includes"], "includes");
        JsonObject specialization = Object(recipe["specialization"], "specialization");
        Require(specialization.Count == 0, $"{stageContext}: specialization is unsupported; create separate compiled recipes instead.");
        Require(language == "Slang" || (defines.Count == 0 && includes.Count == 0), $"{stageContext}: includes and defines require a Slang source.");
        Require(defines.Count <= 64 && includes.Count <= 16, $"{stageContext}: too many defines or includes.");
        List<string> defineValues = defines.Select(node => ScalarString(node, "define")).ToList();
        Require(defineValues.All(value => Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*(?:=[A-Za-z0-9_.+-]+)?$", RegexOptions.CultureInvariant)), $"{stageContext}: invalid Slang define.");
        Require(defineValues.Distinct(StringComparer.Ordinal).Count() == defineValues.Count, $"{stageContext}: duplicate Slang define.");
        string passName = schema == 3 ? String(recipe, "pass") : string.Empty;
        bool decalAbsentPass = passName is "shade-native-no-decals" or "shade-native-depth-no-decals" or
            "shade-native-no-decals-msaa" or "shade-native-depth-no-decals-msaa" or
            "shade-surface-exports-no-decals" or "shade-surface-exports-depth-no-decals" or
            "shade-surface-exports-no-decals-msaa" or "shade-surface-exports-depth-no-decals-msaa";
        bool decalAbsentDefine = defineValues.Any(value => value.StartsWith("XR_ADV_NATIVE_DECALS_ABSENT_SCHEMA_VERSION", StringComparison.Ordinal));
        Require(decalAbsentDefine == decalAbsentPass &&
            (decalAbsentPass || String(recipe, "semanticSchemaIdentity") != "xrengine.engine.native-no-decals.v1"),
            $"{stageContext}: the absent-decal define and semantic identity require an exact native pass.");
        if (decalAbsentPass)
        {
            bool multisample = passName.EndsWith("-msaa", StringComparison.Ordinal);
            JsonObject entryPoints = Object(recipe["entryPoints"], "entryPoints");
            JsonObject pipeline = Object(recipe["pipelineArtifact"], "pipelineArtifact");
            Require(defineValues.Contains("XR_ADV_NATIVE_DECALS_ABSENT_SCHEMA_VERSION=1") &&
                !defineValues.Any(value => value.StartsWith("XR_ADV_NATIVE_MODIFIERS_ABSENT_SCHEMA_VERSION", StringComparison.Ordinal) ||
                    value.StartsWith("XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION", StringComparison.Ordinal)) &&
                String(recipe, "semanticSchemaIdentity") == "xrengine.engine.native-no-decals.v1" &&
                String(recipe, "name") == "engine-advanced-" + passName &&
                String(recipe, "source") == (multisample ? "AdvancedShadeNativeMsaa.slang" : "AdvancedShadeNative.slang") &&
                entryPoints.Count == 1 && String(entryPoints, "compute") == (multisample ? "advancedShadeNativeMsaa" : "advancedShadeNative") &&
                String(pipeline, "scope") == "advanced" && String(pipeline, "pass") == passName,
                $"{stageContext}: absent-decal shading requires its exact identity, entry, source, and standalone schema.");
        }
        string sourceRelative = RelativeSource(String(recipe, "source"), language);
        string sourcePath = ResolveInput(sourceRoot, sourceRelative);
        byte[] originalSource = ReadBounded(sourcePath, MaxSourceBytes);
        string decoded = StrictUtf8.GetString(originalSource);
        Require(!string.IsNullOrWhiteSpace(decoded) && !decoded.Contains('\0') && !decoded.StartsWith('\ufeff'), $"{stageContext}: source must be nonempty UTF-8 without BOM or NUL.");
        byte[] normalized = StrictUtf8.GetBytes(NormalizeLines(decoded));
        if (schema == 3 && language == "MaterialRecipe" && Object(ParseJson(normalized), "material").ContainsKey("nativeVertex"))
            return await PrepareNativeVertexAsync(recipe, recipeBytes, recipePath, sourceRoot, dependencyRoot,
                sourcePath, originalSource, engineLayout!, cancellationToken);
        string recipeDependency = DependencyPath(dependencyRoot, recipePath);
        string sourceDependency = DependencyPath(dependencyRoot, sourcePath);
        SortedDictionary<string, string> dependencies = new(StringComparer.Ordinal)
        {
            [recipeDependency] = Hash(recipeBytes), [sourceDependency] = Hash(originalSource),
        };
        byte[] source = normalized;
        string compilerIdentity = "xrengine-wgsl-packager/2";
        int authoredTextureFlags = 0;
        JsonObject sourceMap = new() { ["kind"] = "identity", ["path"] = sourceDependency };
        if (language == "MaterialRecipe")
        {
            Require(originalSource.Length <= MaxJsonBytes, $"{stageContext}: material JSON exceeds the JSON byte limit.");
            if (schema == 2)
            {
                source = GenerateMaterial(normalized, name, stageContext);
                compilerIdentity = "xrengine-material-wgsl/1";
                sourceMap = new() { ["kind"] = "generated", ["path"] = sourceDependency };
            }
            else
            {
                (bool unlit, EngineLitMaterialShaderPlan litPlan, EngineUnlitMaterialShaderPlan unlitPlan) =
                    WithMaterialSourceContext(stageContext, () =>
                    {
                        JsonObject material = Object(ParseJson(normalized), "material");
                        bool isUnlit = String(material, "shadingModel") == "unlit";
                        EngineLitMaterialShaderPlan lit = isUnlit ? default : PlanEngineLitMaterial(normalized, name, stageContext);
                        EngineUnlitMaterialShaderPlan unlitMaterial = isUnlit ? PlanEngineUnlitMaterial(normalized, name, stageContext) : default;
                        return (isUnlit, lit, unlitMaterial);
                    });
                authoredTextureFlags = unlit ? 0 : litPlan.AuthoredTextureFlags;
                Require(String(recipe, "pass") == (unlit ? unlitPlan.Pass : litPlan.Pass) &&
                    String(recipe, "semanticSchemaIdentity") == (unlit ? unlitPlan.SemanticSchemaIdentity : litPlan.SemanticSchemaIdentity),
                    $"{stageContext}: pass or semantic schema does not match the generated material surface.");
                JsonObject entries = Object(recipe["entryPoints"], "entryPoints");
                Require(entries.Count == 2 && String(entries, "vertex") == (unlit ? "unlitVertex" : "standardLitVertex") &&
                    String(entries, "fragment") == (unlit ? "unlitFragment" : "standardLitFragment"),
                    $"{stageContext}: generated material stages require their exact canonical entry points.");
                if (unlit) VerifyEngineUnlitSources(sourceRoot, dependencyRoot, unlitPlan.Semantic, stageContext);
                else VerifyEngineLitSources(sourceRoot, litPlan, stageContext);
                SlangWgslOutput generated = await CompileSlangWithContextAsync(stageContext, sourceRoot,
                    unlit ? unlitPlan.SlangSource : litPlan.SlangSource, [], [], cancellationToken,
                    entries.ToDictionary(pair => pair.Key, pair => ScalarString(pair.Value, "entry point"), StringComparer.Ordinal));
                if (unlit) VerifyEngineUnlitSources(sourceRoot, dependencyRoot, unlitPlan.Semantic, stageContext);
                else VerifyEngineLitSources(sourceRoot, litPlan, stageContext);
                source = StrictUtf8.GetBytes(NormalizeLines(generated.Source));
                compilerIdentity = "xrengine-material-slang/1+" + generated.CompilerIdentity;
                foreach ((string path, string hash) in generated.Dependencies)
                {
                    string checkedPath = RelativePath(path, "engine lit dependency");
                    string localHash = Hash(ReadBounded(ResolveInput(sourceRoot, checkedPath), MaxSourceBytes));
                    Require(localHash == hash, $"{stageContext}: generated lit dependency changed: {path}.");
                }
                // The compiler conservatively watches all staged Slang inputs.
                // Native equivalence uses the generator's closed, pinned frontend
                // graph, not unrelated shaders found beside those inputs.
                foreach (EngineLitMaterialShaderSource canonical in unlit
                    ? EngineUnlitMaterialShaderGenerator.RequiredCanonicalSources(unlitPlan.Semantic)
                    : EngineLitMaterialShaderGenerator.RequiredCanonicalSources(litPlan))
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, canonical.Path))] = canonical.Sha256;
                if (unlit)
                    foreach (EngineLitMaterialShaderSource canonical in EngineUnlitMaterialShaderGenerator.RequiredDesktopSources(unlitPlan.Semantic))
                        dependencies[DependencyPath(dependencyRoot, ResolveInput(dependencyRoot, canonical.Path))] = canonical.Sha256;
                if (!unlit && litPlan.SemanticSchemaIdentity == EngineAuthoredTexturedShaderGenerator.Schema)
                    foreach (EngineLitMaterialShaderSource canonical in EngineAuthoredTexturedShaderGenerator.DesktopSources(litPlan.AuthoredTextureFlags))
                        dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot,
                            EngineAuthoredTexturedShaderGenerator.DesktopStagingDirectory + "/" + canonical.Path))] = canonical.Sha256;
                if (!unlit && litPlan.SemanticSchemaIdentity == EngineTexturedAlphaShaderGenerator.Schema)
                    foreach (EngineLitMaterialShaderSource canonical in EngineTexturedAlphaShaderGenerator.RequiredDesktopSources)
                        dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot,
                            EngineTexturedAlphaShaderGenerator.DesktopStagingDirectory + "/" + canonical.Path))] = canonical.Sha256;
                sourceMap = new() { ["kind"] = "generated", ["path"] = sourceDependency };
            }
        }
        else if (language == "Slang")
        {
            Require(schema >= 2, $"{stageContext}: Slang compilation needs schema 2 or later.");
            bool uberBase = String(recipe, "semanticSchemaIdentity") is EngineUberBaseShaderContract.Schema or EngineUberBaseShaderContract.OrderGateSchema;
            if (uberBase) VerifyUberBaseSources(sourceRoot, stageContext);
            bool impostorCompanion = materialVariant is not null && String(materialVariant, "semantic") == "OctahedralImpostor";
            bool impostorGate = String(recipe, "semanticSchemaIdentity") == EngineOctahedralImpostorShaderContract.OrderGateSchema;
            if (impostorCompanion) VerifyImpostorSources(sourceRoot, dependencyRoot, impostorGate, sourceRelative, stageContext);
            bool authoredTexturedCompanion = materialVariant is not null && String(materialVariant, "semantic") == "AuthoredLitTextured";
            bool authoredTexturedGate = String(recipe, "semanticSchemaIdentity") == EngineAuthoredTexturedShaderGenerator.OrderGateSchema;
            if (authoredTexturedCompanion)
                VerifyAuthoredTexturedCompanionSources(sourceRoot, String(recipe, "pass"), authoredTexturedGate, sourceRelative, stageContext);
            bool texturedAlphaCompanion = materialVariant is not null && String(materialVariant, "semantic") == "AuthoredLitTextureAlpha";
            bool texturedAlphaGate = String(recipe, "semanticSchemaIdentity") == EngineTexturedAlphaShaderGenerator.OrderGateSchema;
            if (texturedAlphaCompanion)
                VerifyTexturedAlphaCompanionSources(sourceRoot, String(recipe, "pass"), texturedAlphaGate, sourceRelative, stageContext);
            bool unlitCompanion = materialVariant is not null && String(materialVariant, "semantic") == "Unlit";
            bool unlitGate = String(recipe, "semanticSchemaIdentity") == EngineUnlitMaterialShaderGenerator.OrderGateSchema;
            EngineMaterialSemanticIdentity unlitSemantic = unlitCompanion
                ? new(EngineMaterialSemantic.Unlit, Integer(Object(recipe["materialVariant"], "materialVariant"), "semanticVersion")) : default;
            if (unlitCompanion)
            {
                Require(String(recipe, "semanticSchemaIdentity") == (unlitGate
                    ? EngineUnlitMaterialShaderGenerator.OrderGateSchema
                    : EngineUnlitMaterialShaderGenerator.SchemaFor(unlitSemantic)),
                    $"{stageContext}: unlit companion semantic schema does not match the exact surface.");
                VerifyEngineUnlitCompanionSources(sourceRoot, dependencyRoot, unlitSemantic,
                    String(recipe, "pass"), unlitGate, sourceRelative, stageContext);
            }
            List<string> includeValues = includes.Select(node => RelativePath(ScalarString(node, "include"), "include directory")).ToList();
            Require(includeValues.Distinct(StringComparer.Ordinal).Count() == includeValues.Count, $"{stageContext}: duplicate include.");
            foreach (string include in includeValues)
            {
                string resolved = ResolveInput(sourceRoot, include);
                Require(Directory.Exists(resolved), $"{stageContext}: include directory does not exist: {include}.");
            }
            SlangWgslOutput result = await CompileSlangWithContextAsync(stageContext, sourceRoot, sourceRelative, includeValues, defineValues, cancellationToken,
                Object(recipe["entryPoints"], "entryPoints").ToDictionary(pair => pair.Key, pair => ScalarString(pair.Value, "entry point"), StringComparer.Ordinal),
                preserveResourceParameters: uberBase || String(recipe, "semanticSchemaIdentity") is
                    "xrengine.engine.uber-raster-consumer.v2" or "xrengine.engine.native-unmodified.v1" or
                    "xrengine.engine.native-no-decals.v1");
            source = StrictUtf8.GetBytes(NormalizeLines(result.Source));
            compilerIdentity = result.CompilerIdentity;
            Require(Regex.IsMatch(compilerIdentity, "^slang/2026\\.8/[0-9a-f]{64}$", RegexOptions.CultureInvariant), $"{stageContext}: incompatible Slang compiler identity.");
            foreach ((string path, string hash) in result.Dependencies)
            {
                string checkedPath = RelativePath(path, "Slang dependency");
                Require(Regex.IsMatch(hash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), $"{stageContext}: invalid include hash '{path}'.");
                string localHash = Hash(ReadBounded(ResolveInput(sourceRoot, checkedPath), MaxSourceBytes));
                Require(localHash == hash, $"{stageContext}: compiler dependency changed or hash mismatched: {path}.");
                if (!texturedAlphaCompanion && !authoredTexturedCompanion && !impostorCompanion && !unlitCompanion)
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, checkedPath))] = hash;
            }
            if (uberBase)
            {
                VerifyUberBaseSources(sourceRoot, stageContext);
                foreach (EngineLitMaterialShaderSource canonical in EngineUberBaseShaderContract.RequiredDesktopSources)
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, "Desktop/" + canonical.Path))] = canonical.Sha256;
            }
            if (texturedAlphaCompanion)
            {
                VerifyTexturedAlphaCompanionSources(sourceRoot, String(recipe, "pass"), texturedAlphaGate, sourceRelative, stageContext);
                foreach (EngineLitMaterialShaderSource canonical in EngineTexturedAlphaShaderGenerator.CompanionSources(String(recipe, "pass"), texturedAlphaGate))
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, canonical.Path))] = canonical.Sha256;
            }
            if (unlitCompanion)
            {
                VerifyEngineUnlitCompanionSources(sourceRoot, dependencyRoot, unlitSemantic,
                    String(recipe, "pass"), unlitGate, sourceRelative, stageContext);
                foreach (EngineLitMaterialShaderSource canonical in EngineUnlitMaterialShaderGenerator.CompanionSources(
                    unlitSemantic, String(recipe, "pass"), unlitGate))
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, canonical.Path))] = canonical.Sha256;
                foreach (EngineLitMaterialShaderSource canonical in EngineUnlitMaterialShaderGenerator.RequiredDesktopSources(unlitSemantic))
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(dependencyRoot, canonical.Path))] = canonical.Sha256;
            }
            if (authoredTexturedCompanion)
            {
                VerifyAuthoredTexturedCompanionSources(sourceRoot, String(recipe, "pass"), authoredTexturedGate, sourceRelative, stageContext);
                foreach (EngineLitMaterialShaderSource canonical in EngineAuthoredTexturedShaderGenerator.CompanionSources(String(recipe, "pass"), authoredTexturedGate))
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, canonical.Path))] = canonical.Sha256;
            }
            if (impostorCompanion)
            {
                VerifyImpostorSources(sourceRoot, dependencyRoot, impostorGate, sourceRelative, stageContext);
                foreach (EngineLitMaterialShaderSource canonical in EngineOctahedralImpostorShaderContract.CompanionSources(impostorGate))
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, canonical.Path))] = canonical.Sha256;
                foreach (EngineLitMaterialShaderSource canonical in EngineOctahedralImpostorShaderContract.DesktopSources)
                    dependencies[DependencyPath(dependencyRoot, ResolveInput(dependencyRoot, canonical.Path))] = canonical.Sha256;
            }
            sourceMap = new() { ["kind"] = "unmapped", ["path"] = sourceDependency };
        }
        Require(source.Length is > 0 and <= MaxSourceBytes, $"{stageContext}: emitted WGSL exceeds the source byte limit or is empty.");
        string emitted = StrictUtf8.GetString(source);
        Require(!emitted.Contains('\0') && !emitted.StartsWith('\ufeff'), $"{stageContext}: emitted WGSL contains BOM or NUL.");
        if (engineLayout is null) WgslAbiVerifier.Validate(emitted, stageContext);
        else new WgslAbiParser(emitted, stageContext, engineLayout).Validate();
        JsonObject descriptor = new();
        foreach (string key in new[] { "schemaVersion", "name", "sourceLanguage", "target", "entryPoints", "defines", "specialization", "requiredLimits", "matrixLayout", "semanticSchemaIdentity", "layout", "pipeline" })
            descriptor[key] = recipe[key]!.DeepClone();
        if (schema == 3) descriptor["pass"] = recipe["pass"]!.DeepClone();
        if (schema == 3 && recipe.ContainsKey("workgroupSize")) descriptor["workgroupSize"] = recipe["workgroupSize"]!.DeepClone();
        if (authoredTextureFlags != 0) descriptor["authoredTextureFlags"] = authoredTextureFlags;
        if (materialVariant is not null) descriptor["materialVariant"] = recipe["materialVariant"]!.DeepClone();
        descriptor["requiredFeatures"] = new JsonArray();
        descriptor["compilerIdentity"] = schema == 1 ? "xrengine-wgsl-packager/1" : compilerIdentity;
        string emittedPath = language == "WGSL" ? (schema == 1 ? sourceRelative : sourceDependency) : name + ".wgsl";
        string sourceHash = Hash(source);
        descriptor["source"] = new JsonObject { ["path"] = emittedPath, ["sha256"] = sourceHash, ["byteLength"] = source.Length, ["url"] = sourceHash + ".wgsl" };
        if (schema == 1)
            descriptor["dependencies"] = new JsonArray(new JsonObject { ["path"] = sourceRelative, ["sha256"] = sourceHash });
        else
        {
            descriptor["includes"] = includes.DeepClone();
            descriptor["coordinates"] = Coordinates;
            descriptor["sourceMap"] = sourceMap;
            JsonArray dependencyArray = [];
            foreach ((string path, string hash) in dependencies)
                dependencyArray.Add(new JsonObject { ["path"] = path, ["sha256"] = hash });
            descriptor["dependencies"] = dependencyArray;
        }
        byte[] encoded = Canonical(descriptor);
        Require(dependencies.Count <= 512, $"{stageContext}: dependency count exceeds 512.");
        Require(encoded.Length <= MaxJsonBytes, $"{stageContext}: descriptor exceeds the JSON byte limit.");
        if (schema == 3)
        {
            ShaderProgramArtifact artifact = ShaderProgramArtifactReader.Read(encoded, source);
            if (language == "MaterialRecipe")
            {
                bool unlit = artifact.SemanticSchemaIdentity is EngineUnlitMaterialShaderGenerator.ColorSchema or
                    EngineUnlitMaterialShaderGenerator.TextureSchema or EngineUnlitMaterialShaderGenerator.OpaqueTextureSchema or
                    EngineUnlitMaterialShaderGenerator.AlphaTextureSchema or EngineUnlitMaterialShaderGenerator.TextureArraySliceSchema;
                string provenanceReason;
                bool admitted = unlit ? EngineUnlitShaderProvenance.TryValidate(artifact, out provenanceReason)
                    : EngineLitMaterialShaderProvenance.TryValidate(artifact, out provenanceReason);
                Require(admitted, $"{stageContext}: {provenanceReason}");
            }
            if (materialVariant is not null && String(materialVariant, "semantic") == "Unlit")
            {
                using JsonDocument declaration = JsonDocument.Parse(Canonical(recipe["materialVariant"]));
                EngineMaterialVariantKey key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration.RootElement, artifact.Pass, artifact.Target);
                bool builtIn = language == "MaterialRecipe";
                bool proven = builtIn
                    ? EngineUnlitShaderProvenance.TryValidateBuiltIn(artifact, key, out string unlitReason)
                    : EngineUnlitShaderProvenance.TryValidateCompanion(artifact, key, out unlitReason);
                Require(proven,
                    $"{stageContext}: {unlitReason}");
            }
            if (materialVariant is not null && String(materialVariant, "semantic") == "OctahedralImpostor")
                Require(EngineOctahedralImpostorShaderProvenance.TryValidate(artifact,
                    String(recipe, "semanticSchemaIdentity") == EngineOctahedralImpostorShaderContract.OrderGateSchema, out string impostorReason),
                    $"{stageContext}: {impostorReason}");
            if (materialVariant is not null && String(materialVariant, "semantic") == "AuthoredLitTextured")
            {
                using JsonDocument declaration = JsonDocument.Parse(Canonical(recipe["materialVariant"]));
                EngineMaterialVariantKey key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration.RootElement, artifact.Pass, artifact.Target);
                Require(EngineAuthoredTexturedShaderProvenance.TryValidateCompanion(artifact, key, out string texturedReason),
                    $"{stageContext}: {texturedReason}");
            }
            if (materialVariant is not null && String(materialVariant, "semantic") == "AuthoredLitTextureAlpha")
            {
                using JsonDocument declaration = JsonDocument.Parse(Canonical(recipe["materialVariant"]));
                EngineMaterialVariantKey key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration.RootElement, artifact.Pass, artifact.Target);
                Require(EngineTexturedAlphaShaderProvenance.TryValidateCompanion(artifact, key, out string alphaReason),
                    $"{stageContext}: {alphaReason}");
            }
            if (pipelineArtifact is not null)
                WebPipelineArtifactCatalog.ValidateProgram(
                    WebPipelineArtifactCatalog.GetBindingKey(
                        pipelineArtifact.ContainsKey("scope") ? String(pipelineArtifact, "scope") : null,
                        String(pipelineArtifact, "pass")), artifact);
            if (computeArtifact is not null)
                WebComputeArtifactCatalog.ValidateKernel(String(computeArtifact, "kernel"), artifact);
        }
        return new PreparedShader(name, encoded, source, materialVariant, pipelineArtifact, computeArtifact);
    }

    private static byte[] GenerateMaterial(byte[] source, string name, string context)
    {
        // The shared generator is linked into this small tool, without loading the engine project graph.
        JsonObject material = Object(ParseJson(source), "material");
        Require(material.Count == 5 && material.ContainsKey("schemaVersion") && material.ContainsKey("name") &&
                material.ContainsKey("shadingModel") && material.ContainsKey("surface") && material.ContainsKey("baseColor"),
                $"{context}: unsupported material properties.");
        Require(Integer(material, "schemaVersion") == 1, $"{context}: unsupported material schemaVersion.");
        Require(String(material, "name") == name, $"{context}: material name must equal recipe name.");
        BrowserMaterialShaderDefinition definition = new(name, String(material, "shadingModel"), String(material, "surface"), String(material, "baseColor"));
        return BrowserMaterialShaderGenerator.Generate(definition, ShaderCompileTarget.WebGPUWgsl).Bytes.ToArray();
    }

    private static EngineLitMaterialShaderPlan PlanEngineLitMaterial(byte[] source, string name, string context)
    {
        JsonObject material = Object(ParseJson(source), "material");
        bool baseKeys = material.ContainsKey("schemaVersion") && material.ContainsKey("name") &&
            material.ContainsKey("shadingModel") && material.ContainsKey("surface") && material.ContainsKey("baseColor");
        Require(baseKeys && (material.Count == 5 || material.Count == 6 && material.ContainsKey("normal") ||
            material.Count == 7 && material.ContainsKey("normal") && material.ContainsKey("textureFlags")),
            $"{context}: unsupported authored material properties.");
        Require(Integer(material, "schemaVersion") == 2 && String(material, "name") == name,
            $"{context}: material schema or name does not match the recipe.");
        if (material.ContainsKey("textureFlags"))
        {
            int flags = Integer(material, "textureFlags");
            Require(String(material, "shadingModel") == "lit" && String(material, "baseColor") == "authored-textured" &&
                String(material, "normal") == ((flags & 1) != 0 ? "texture" : "vertex"), $"{context}: authored textured feature fields disagree.");
            return EngineAuthoredTexturedShaderGenerator.Plan(name, flags, String(material, "surface"), ShaderCompileTarget.WebGPUWgsl);
        }
        return EngineLitMaterialShaderGenerator.Plan(name, String(material, "shadingModel"), String(material, "surface"),
            String(material, "baseColor"), material.ContainsKey("normal") ? String(material, "normal") : "vertex",
            ShaderCompileTarget.WebGPUWgsl);
    }

    private static EngineUnlitMaterialShaderPlan PlanEngineUnlitMaterial(byte[] source, string name, string context)
    {
        JsonObject material = Object(ParseJson(source), "material");
        Require(material.Count == 6 && material.ContainsKey("schemaVersion") && material.ContainsKey("name") &&
            material.ContainsKey("shadingModel") && material.ContainsKey("surface") &&
            material.ContainsKey("baseColor") && material.ContainsKey("semanticVersion"),
            $"{context}: unsupported unlit material properties.");
        Require(Integer(material, "schemaVersion") == 3 && String(material, "name") == name &&
            String(material, "shadingModel") == "unlit", $"{context}: unlit material schema or name does not match the recipe.");
        EngineMaterialSemanticIdentity semantic = new(EngineMaterialSemantic.Unlit, Integer(material, "semanticVersion"));
        Require(String(material, "baseColor") == EngineUnlitMaterialShaderGenerator.BaseColorFor(semantic),
            $"{context}: unlit source and semantic version disagree.");
        return EngineUnlitMaterialShaderGenerator.Plan(name, semantic, String(material, "surface"), ShaderCompileTarget.WebGPUWgsl);
    }

    private static void VerifyEngineUnlitSources(string root, string dependencyRoot,
        EngineMaterialSemanticIdentity semantic, string context)
    {
        foreach (EngineLitMaterialShaderSource source in EngineUnlitMaterialShaderGenerator.RequiredCanonicalSources(semantic))
            Require(Hash(ReadBounded(ResolveInput(root, source.Path), MaxSourceBytes)) == source.Sha256,
                $"{context}: canonical unlit WebGPU source '{source.Path}' is missing or modified.");
        foreach (EngineLitMaterialShaderSource source in EngineUnlitMaterialShaderGenerator.RequiredDesktopSources(semantic))
            Require(EngineTexturedAlphaShaderGenerator.NormalizedHash(
                    StrictUtf8.GetString(ReadBounded(ResolveInput(dependencyRoot, source.Path), MaxSourceBytes))) == source.Sha256,
                $"{context}: canonical unlit authored source '{source.Path}' is missing or modified.");
    }

    private static void VerifyEngineUnlitCompanionSources(string root, string dependencyRoot,
        EngineMaterialSemanticIdentity semantic, string pass, bool orderGate, string sourcePath, string context)
    {
        IReadOnlyList<EngineLitMaterialShaderSource> sources = EngineUnlitMaterialShaderGenerator.CompanionSources(semantic, pass, orderGate);
        Require(sources[0].Path == sourcePath, $"{context}: unlit companion requires its canonical entry source.");
        foreach (EngineLitMaterialShaderSource source in sources)
            Require(Hash(ReadBounded(ResolveInput(root, source.Path), MaxSourceBytes)) == source.Sha256,
                $"{context}: canonical unlit companion '{source.Path}' is missing or modified.");
        foreach (EngineLitMaterialShaderSource source in EngineUnlitMaterialShaderGenerator.RequiredDesktopSources(semantic))
            Require(EngineTexturedAlphaShaderGenerator.NormalizedHash(
                    StrictUtf8.GetString(ReadBounded(ResolveInput(dependencyRoot, source.Path), MaxSourceBytes))) == source.Sha256,
                $"{context}: canonical unlit authored source '{source.Path}' is missing or modified.");
    }

    private static void VerifyUberBaseSources(string root, string context)
    {
        foreach (EngineLitMaterialShaderSource source in EngineUberBaseShaderContract.RequiredSources)
            Require(Hash(ReadBounded(ResolveInput(root, source.Path), MaxSourceBytes)) == source.Sha256,
                $"{context}: canonical Uber WebGPU source '{source.Path}' is missing or modified.");
        foreach (EngineLitMaterialShaderSource source in EngineUberBaseShaderContract.RequiredDesktopSources)
            Require(Hash(ReadBounded(ResolveInput(root, "Desktop/" + source.Path), MaxSourceBytes)) == source.Sha256,
                $"{context}: canonical Uber authored source '{source.Path}' is missing or modified.");
    }

    private static void VerifyEngineLitSources(string root, EngineLitMaterialShaderPlan plan, string context)
    {
        foreach (EngineLitMaterialShaderSource canonical in EngineLitMaterialShaderGenerator.RequiredCanonicalSources(plan))
        {
            string actual = Hash(ReadBounded(ResolveInput(root, canonical.Path), MaxSourceBytes));
            Require(actual == canonical.Sha256,
                $"{context}: engine PBR frontend '{canonical.Path}' differs from its versioned canonical source.");
        }
        if (plan.SemanticSchemaIdentity == EngineAuthoredTexturedShaderGenerator.Schema)
            foreach (EngineLitMaterialShaderSource canonical in EngineAuthoredTexturedShaderGenerator.DesktopSources(plan.AuthoredTextureFlags))
            {
                byte[] bytes = ReadBounded(ResolveInput(root, EngineAuthoredTexturedShaderGenerator.DesktopStagingDirectory + "/" + canonical.Path), MaxSourceBytes);
                Require(EngineAuthoredTexturedShaderGenerator.NormalizedHash(StrictUtf8.GetString(bytes)) == canonical.Sha256,
                    $"{context}: authored desktop source '{canonical.Path}' differs from its versioned canonical graph.");
            }
        if (plan.SemanticSchemaIdentity == EngineTexturedAlphaShaderGenerator.Schema)
            foreach (EngineLitMaterialShaderSource canonical in EngineTexturedAlphaShaderGenerator.RequiredDesktopSources)
            {
                byte[] bytes = ReadBounded(ResolveInput(root, EngineTexturedAlphaShaderGenerator.DesktopStagingDirectory + "/" + canonical.Path), MaxSourceBytes);
                string actual = EngineTexturedAlphaShaderGenerator.NormalizedHash(StrictUtf8.GetString(bytes));
                Require(actual == canonical.Sha256,
                    $"{context}: authored desktop source '{canonical.Path}' differs from its versioned canonical graph.");
            }
    }

    private static void VerifyTexturedAlphaCompanionSources(string root, string pass, bool gate, string source, string context)
    {
        IReadOnlyList<EngineLitMaterialShaderSource> sources = EngineTexturedAlphaShaderGenerator.CompanionSources(pass, gate);
        Require(sources.Count != 0 && sources[0].Path == source, $"{context}: textured-alpha companion requires its canonical entry source.");
        foreach (EngineLitMaterialShaderSource canonical in sources)
            Require(Hash(ReadBounded(ResolveInput(root, canonical.Path), MaxSourceBytes)) == canonical.Sha256,
                $"{context}: textured-alpha frontend '{canonical.Path}' differs from its canonical source.");
    }

    private static void VerifyAuthoredTexturedCompanionSources(string root, string pass, bool gate, string source, string context)
    {
        IReadOnlyList<EngineLitMaterialShaderSource> sources = EngineAuthoredTexturedShaderGenerator.CompanionSources(pass, gate);
        Require(sources.Count != 0 && sources[0].Path == source, $"{context}: authored-textured companion requires its canonical entry source.");
        foreach (EngineLitMaterialShaderSource canonical in sources)
            Require(Hash(ReadBounded(ResolveInput(root, canonical.Path), MaxSourceBytes)) == canonical.Sha256,
                $"{context}: authored-textured frontend '{canonical.Path}' differs from its canonical source.");
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "ShaderCooker", "ShaderCooker.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate the repository from ShaderCooker binary location.");
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException($"{path}: exceeds {limit}-byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static JsonNode ParseJson(byte[] bytes)
    {
        // Utf8JsonReader exposes duplicate keys; JsonNode.Parse alone would silently accept them.
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        CheckUnique(document.RootElement);
        return JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = false }, new JsonDocumentOptions { MaxDepth = 32 })!;
    }

    private static JsonNode ParseLiteral(string json) => ParseJson(StrictUtf8.GetBytes(json));

    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Require(keys.Add(property.Name), $"Duplicate JSON property '{property.Name}'.");
                CheckUnique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement element in value.EnumerateArray()) CheckUnique(element);
    }

    private static byte[] Canonical(JsonNode? value) => StrictUtf8.GetBytes(CanonicalString(value));
    private static string CanonicalString(JsonNode? value)
    {
        StringBuilder builder = new();
        AppendCanonical(builder, value);
        builder.Append('\n');
        return builder.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, JsonNode? value)
    {
        switch (value)
        {
            case JsonObject obj:
                builder.Append('{');
                bool first = true;
                foreach ((string key, JsonNode? item) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(key, JsonOptions)).Append(':');
                    AppendCanonical(builder, item);
                }
                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i != 0) builder.Append(',');
                    AppendCanonical(builder, array[i]);
                }
                builder.Append(']');
                break;
            case null: builder.Append("null"); break;
            default: builder.Append(value.ToJsonString(JsonOptions)); break;
        }
    }

    private static JsonObject Object(JsonNode? value, string name) => value as JsonObject ?? throw new InvalidDataException($"{name} must be a JSON object.");
    private static JsonArray Array(JsonNode? value, string name) => value as JsonArray ?? throw new InvalidDataException($"{name} must be a JSON array.");
    private static string ScalarString(JsonNode? value, string name)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out string? result) || result is null)
            throw new InvalidDataException($"{name} must be a string.");
        return result;
    }
    private static string String(JsonObject value, string key) => ScalarString(value[key], key);
    private static int Integer(JsonObject value, string key)
    {
        if (value[key] is not JsonValue scalar || !scalar.TryGetValue<int>(out int result))
            throw new InvalidDataException($"{key} must be an integer.");
        return result;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string RelativeSource(string value, string language)
    {
        string suffix = language switch { "WGSL" => ".wgsl", "MaterialRecipe" => ".material.json", _ => ".slang" };
        RelativePath(value, "source");
        Require(value.EndsWith(suffix, StringComparison.Ordinal), $"Source must be a relative {suffix} path.");
        return value;
    }

    private static string RelativePath(string value, string label)
    {
        Require(value.Length is > 0 and <= 240 &&
                Regex.IsMatch(value, "^(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) &&
                !value.Split('/').Any(part => part is "." or ".."),
                $"{label} must be a normalized relative path using portable characters.");
        return value;
    }

    private static string ResolveInput(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        RejectLinks(root, path);
        return path;
    }

    private static void RejectLinks(string root, string candidate)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        Require(Path.IsPathFullyQualified(fullCandidate) &&
                (string.Equals(fullCandidate, fullRoot, StringComparison.Ordinal) ||
                 fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
                $"Input escapes its root: {candidate}");
        // Checking every component prevents a symlink/reparse point from bypassing the lexical containment check.
        CheckAncestry(fullRoot);
        string cursor = fullRoot;
        foreach (string part in Path.GetRelativePath(fullRoot, fullCandidate).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            cursor = Path.Combine(cursor, part);
            CheckLink(cursor);
        }
    }

    private static void CheckLink(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        Require((attributes & FileAttributes.ReparsePoint) == 0, $"Reparse point or symlink is not permitted: {path}");
    }

    private static void CheckAncestry(string path)
    {
        DirectoryInfo? ancestor = new(Path.GetFullPath(path));
        while (ancestor is not null)
        {
            if (File.Exists(ancestor.FullName) || Directory.Exists(ancestor.FullName)) CheckLink(ancestor.FullName);
            ancestor = ancestor.Parent;
        }
    }

    private static string DependencyPath(string root, string path)
    {
        RejectLinks(root, path);
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        Require(!relative.StartsWith("../", StringComparison.Ordinal) && relative != "..", "Dependency escapes its root.");
        return relative;
    }

    private static void WriteAtomic(string path, byte[] content, bool immutable)
    {
        if (File.Exists(path))
        {
            CheckLink(path);
            byte[] existing = ReadBounded(path, MaxSourceBytes);
            Require(!immutable || existing.AsSpan().SequenceEqual(content),
                $"{Path.GetFileName(path)}: existing immutable asset differs; refusing overwrite.");
            if (existing.AsSpan().SequenceEqual(content)) return;
        }
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".shader-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: !immutable);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
