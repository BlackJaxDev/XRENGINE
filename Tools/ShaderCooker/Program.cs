using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Tools.ShaderCooker;

/// <summary>Packages explicit engine and legacy fixture shader recipes into immutable, content-addressed assets.</summary>
internal static class Program
{
    private const int MaxSourceBytes = 1024 * 1024;
    private const int MaxJsonBytes = 64 * 1024;
    private const int MaxArtifacts = 16;
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
                throw new InvalidDataException("A package supports 1–16 recipes.");
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
                }
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
            HashSet<string> variantKeys = new(StringComparer.Ordinal);
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
            }
            int schema = ParseJson(prepared[0].Descriptor)["schemaVersion"]!.GetValue<int>();
            JsonObject manifestDocument = new() { ["schemaVersion"] = schema, ["backend"] = "WebGPU", ["packetVersion"] = 2, ["artifacts"] = artifacts };
            if (materialVariants.Count > 0) manifestDocument["materialVariants"] = materialVariants;
            byte[] manifest = Canonical(manifestDocument);
            Require(manifest.Length <= MaxJsonBytes, "Manifest exceeds the JSON byte limit.");
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
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"Shader cook failed: {error.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
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
        if (schema == 3)
        {
            Require(language is "Slang" or "WGSL", $"{stageContext}: engine recipes require authored Slang or explicit WGSL; the frozen browser material generator is not an engine frontend.");
            using JsonDocument layoutDocument = JsonDocument.Parse(recipeBytes);
            try { engineLayout = ShaderProgramArtifactReader.ReadLayout(layoutDocument.RootElement, ShaderArtifact.FromWgsl(""), "recipe"); }
            catch (InvalidDataException error) { throw new InvalidDataException($"{stageContext}: {error.Message}", error); }
            if (recipe.TryGetPropertyValue("materialVariant", out JsonNode? variantNode))
            {
                JsonObject variant = Object(variantNode, "materialVariant");
                Require(variant.Count == 4 && variant.ContainsKey("semantic") && variant.ContainsKey("semanticVersion")
                    && variant.ContainsKey("vertexProfile") && variant.ContainsKey("outputProfile"), $"{stageContext}: invalid materialVariant properties.");
                string semantic = String(variant, "semantic");
                Require(semantic is "StandardLitColor" or "OpaqueShadowDepth" && Integer(variant, "semanticVersion") == 1,
                    $"{stageContext}: unsupported engine material semantic.");
                string vertexProfile = String(variant, "vertexProfile"), outputProfile = String(variant, "outputProfile");
                if (semantic == "OpaqueShadowDepth")
                    Require(String(recipe, "pass") == "depth" && vertexProfile == "static-position-v1" &&
                        outputProfile == "depth-normal-v1" &&
                        CanonicalString(recipe["entryPoints"]) == "{\"vertex\":\"depthVertex\"}\n",
                        $"{stageContext}: opaque shadow depth requires its vertex-only depth recipe.");
                Require(Regex.IsMatch(vertexProfile, "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)
                    && Regex.IsMatch(outputProfile, "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant),
                    $"{stageContext}: invalid material variant profile.");
                materialVariant = new JsonObject { ["semantic"] = semantic, ["semanticVersion"] = 1,
                    ["target"] = "WebGPUWgsl", ["pass"] = String(recipe, "pass"),
                    ["vertexProfile"] = vertexProfile, ["outputProfile"] = outputProfile };
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
        string sourceRelative = RelativeSource(String(recipe, "source"), language);
        string sourcePath = ResolveInput(sourceRoot, sourceRelative);
        byte[] originalSource = ReadBounded(sourcePath, MaxSourceBytes);
        string decoded = StrictUtf8.GetString(originalSource);
        Require(!string.IsNullOrWhiteSpace(decoded) && !decoded.Contains('\0') && !decoded.StartsWith('\ufeff'), $"{stageContext}: source must be nonempty UTF-8 without BOM or NUL.");
        byte[] normalized = StrictUtf8.GetBytes(NormalizeLines(decoded));
        string recipeDependency = DependencyPath(dependencyRoot, recipePath);
        string sourceDependency = DependencyPath(dependencyRoot, sourcePath);
        SortedDictionary<string, string> dependencies = new(StringComparer.Ordinal)
        {
            [recipeDependency] = Hash(recipeBytes), [sourceDependency] = Hash(originalSource),
        };
        byte[] source = normalized;
        string compilerIdentity = "xrengine-wgsl-packager/2";
        JsonObject sourceMap = new() { ["kind"] = "identity", ["path"] = sourceDependency };
        if (language == "MaterialRecipe")
        {
            Require(schema == 2, $"{stageContext}: material generation needs schema 2.");
            Require(originalSource.Length <= MaxJsonBytes, $"{stageContext}: material JSON exceeds the JSON byte limit.");
            source = GenerateMaterial(normalized, name, stageContext);
            compilerIdentity = "xrengine-material-wgsl/1";
            sourceMap = new() { ["kind"] = "generated", ["path"] = sourceDependency };
        }
        else if (language == "Slang")
        {
            Require(schema >= 2, $"{stageContext}: Slang compilation needs schema 2 or later.");
            List<string> includeValues = includes.Select(node => RelativePath(ScalarString(node, "include"), "include directory")).ToList();
            Require(includeValues.Distinct(StringComparer.Ordinal).Count() == includeValues.Count, $"{stageContext}: duplicate include.");
            foreach (string include in includeValues)
            {
                string resolved = ResolveInput(sourceRoot, include);
                Require(Directory.Exists(resolved), $"{stageContext}: include directory does not exist: {include}.");
            }
            SlangWgslOutput result = await SlangWgslCompiler.CompileAsync(sourceRoot, sourceRelative, includeValues, defineValues, cancellationToken,
                Object(recipe["entryPoints"], "entryPoints").ToDictionary(pair => pair.Key, pair => ScalarString(pair.Value, "entry point"), StringComparer.Ordinal));
            source = StrictUtf8.GetBytes(NormalizeLines(result.Source));
            compilerIdentity = result.CompilerIdentity;
            Require(Regex.IsMatch(compilerIdentity, "^slang/2026\\.8/[0-9a-f]{64}$", RegexOptions.CultureInvariant), $"{stageContext}: incompatible Slang compiler identity.");
            foreach ((string path, string hash) in result.Dependencies)
            {
                string checkedPath = RelativePath(path, "Slang dependency");
                Require(Regex.IsMatch(hash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), $"{stageContext}: invalid include hash '{path}'.");
                string localHash = Hash(ReadBounded(ResolveInput(sourceRoot, checkedPath), MaxSourceBytes));
                Require(localHash == hash, $"{stageContext}: compiler dependency changed or hash mismatched: {path}.");
                dependencies[DependencyPath(dependencyRoot, ResolveInput(sourceRoot, checkedPath))] = hash;
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
        if (schema == 3) _ = ShaderProgramArtifactReader.Read(encoded, source);
        return new PreparedShader(name, encoded, source, materialVariant);
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
        return BrowserMaterialShaderGenerator.Generate(definition, ShaderCompileTarget.WebGPUWgsl).Bytes;
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
