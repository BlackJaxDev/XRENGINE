using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Tools.ShaderCooker;

internal static partial class Program
{
    private static async Task<PreparedShader> PrepareNativeVertexAsync(JsonObject recipe, byte[] recipeBytes, string recipePath,
        string sourceRoot, string dependencyRoot, string materialPath, byte[] materialBytes,
        ShaderProgramArtifact rasterLayout, CancellationToken cancellationToken)
    {
        string name = String(recipe, "name");
        string authoredSource = String(recipe, "source");
        string context = $"material '{name}' pass '{String(recipe, "pass")}' source '{authoredSource}' target 'WebGPUWgsl'";
        string CompilerContext(string pass, string entries, string compilerSource)
            => $"material '{name}' pass '{pass}' entry '{entries}' source '{authoredSource}' compiler source '{compilerSource}' target 'WebGPUWgsl'";
        Require(materialBytes.Length <= MaxJsonBytes, $"{context}: material JSON exceeds the JSON byte limit.");
        Require(Regex.IsMatch(name, "^mat-[0-9a-f]{32}$", RegexOptions.CultureInvariant), $"{context}: the shader name must be its persistent material identity.");
        Require(!recipe.ContainsKey("materialVariant") && !recipe.ContainsKey("pipelineArtifact") && !recipe.ContainsKey("computeArtifact"),
            $"{context}: authored native vertex pairs cannot declare builtin variant or pipeline ownership.");
        (string functionSource, string functionHash, EngineLitMaterialShaderPlan surface) =
            WithMaterialSourceContext(context, () =>
            {
                JsonObject material = Object(ParseJson(materialBytes), "material");
                JsonObject native = Object(material["nativeVertex"], "nativeVertex");
                Require(native.Count == 1 && native.ContainsKey("body"), $"{context}: nativeVertex requires exactly one function body.");
                string generatedFunction = EngineNativeVertexShaderGenerator.GenerateFunction(String(native, "body"));
                string generatedHash = EngineNativeVertexShaderGenerator.FunctionHash(generatedFunction);
                JsonObject ordinary = (JsonObject)material.DeepClone();
                ordinary.Remove("nativeVertex");
                EngineLitMaterialShaderPlan materialSurface = PlanEngineLitMaterial(Canonical(ordinary), name, context);
                return (generatedFunction, generatedHash, materialSurface);
            });
        Require(surface.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.ColorSchema &&
            rasterLayout.SemanticSchemaIdentity == EngineNativeVertexShaderGenerator.RasterSchema &&
            rasterLayout.Pass == "opaque-forward" && rasterLayout.VertexEntryPoint == "standardLitVertex" &&
            rasterLayout.FragmentEntryPoint == "standardLitFragment" && rasterLayout.ComputeEntryPoint is null,
            $"{context}: native vertex functions require opaque color PBR and the canonical raster entries.");
        Require(rasterLayout.Resources.Length == 7, $"{context}: native vertex color PBR requires exactly seven resources without shadow or texture extensions.");
        ValidateNativeRasterInputs(rasterLayout, context);

        SortedDictionary<string, string> dependencies = new(StringComparer.Ordinal)
        {
            [DependencyPath(dependencyRoot, recipePath)] = Hash(recipeBytes),
            [DependencyPath(dependencyRoot, materialPath)] = Hash(materialBytes),
            [EngineNativeVertexShaderGenerator.FunctionPath] = functionHash,
        };
        string staging = Path.Combine(Path.GetTempPath(), "xr-native-vertex-cook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (EngineLitMaterialShaderSource canonical in EngineNativeVertexShaderGenerator.RequiredCanonicalSources)
            {
                string path = ResolveInput(sourceRoot, canonical.Path);
                byte[] bytes = ReadBounded(path, MaxSourceBytes);
                Require(Hash(bytes) == canonical.Sha256, $"{context}: canonical wrapper '{canonical.Path}' differs from its pinned source.");
                dependencies[DependencyPath(dependencyRoot, path)] = canonical.Sha256;
                await File.WriteAllBytesAsync(Path.Combine(staging, canonical.Path), bytes, cancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "NativeVertexFunction.slang"), functionSource, StrictUtf8, cancellationToken);
            SlangWgslOutput raster = await CompileSlangWithContextAsync(
                CompilerContext(String(recipe, "pass"), "vertex:standardLitVertex, fragment:standardLitFragment", EngineNativeVertexShaderGenerator.RasterSource),
                staging, EngineNativeVertexShaderGenerator.RasterSource,
                [], [], cancellationToken, new Dictionary<string, string> { ["vertex"] = "standardLitVertex", ["fragment"] = "standardLitFragment" },
                preserveResourceParameters: true);
            SlangWgslOutput compute = await CompileSlangWithContextAsync(
                CompilerContext("native-material-vertex", "compute:nativeMaterialVertex", EngineNativeVertexShaderGenerator.ComputeSource),
                staging, EngineNativeVertexShaderGenerator.ComputeSource,
                [], [], cancellationToken, new Dictionary<string, string> { ["compute"] = "nativeMaterialVertex" }, preserveResourceParameters: true);
            Require(raster.CompilerIdentity == compute.CompilerIdentity, $"{context}: the compiler installation changed between paired programs.");
            Require(raster.Dependencies.Count == EngineNativeVertexShaderGenerator.RequiredCanonicalSources.Count(source => source.Path.EndsWith(".slang", StringComparison.Ordinal)) + 1 &&
                compute.Dependencies.Count == raster.Dependencies.Count &&
                raster.Dependencies.TryGetValue("NativeVertexFunction.slang", out string? rasterFunction) && rasterFunction == functionHash &&
                compute.Dependencies.TryGetValue("NativeVertexFunction.slang", out string? computeFunction) && computeFunction == functionHash,
                $"{context}: the isolated function closure changed between paired programs.");
            foreach (EngineLitMaterialShaderSource canonical in EngineNativeVertexShaderGenerator.RequiredCanonicalSources)
                Require(Hash(ReadBounded(ResolveInput(sourceRoot, canonical.Path), MaxSourceBytes)) == canonical.Sha256 &&
                    (!canonical.Path.EndsWith(".slang", StringComparison.Ordinal) ||
                    (raster.Dependencies.TryGetValue(canonical.Path, out string? rasterHash) && rasterHash == canonical.Sha256 &&
                    compute.Dependencies.TryGetValue(canonical.Path, out string? computeHash) && computeHash == canonical.Sha256)),
                    $"{context}: canonical wrapper '{canonical.Path}' changed during cooking.");
            Require(Hash(ReadBounded(materialPath, MaxJsonBytes)) == Hash(materialBytes) && Hash(ReadBounded(recipePath, MaxJsonBytes)) == Hash(recipeBytes),
                $"{context}: authored recipe or material changed during cooking.");

            JsonObject computeRecipe = NativeComputeRecipe(name + "-native-vertex");
            using JsonDocument computeLayoutDocument = JsonDocument.Parse(Canonical(computeRecipe));
            ShaderProgramArtifact computeLayout = ShaderProgramArtifactReader.ReadLayout(computeLayoutDocument.RootElement, ShaderArtifact.FromWgsl(""), "recipe");
            string materialDependency = DependencyPath(dependencyRoot, materialPath);
            PreparedShader computePrepared = PrepareNativeProgram(computeRecipe, computeLayout, compute, dependencies, materialDependency,
                functionSource, functionHash, null, null, context);
            string computeIdentity = Hash(computePrepared.Descriptor);
            JsonObject auxiliaryIdentities = new();
            List<PreparedShader> auxiliaries = [];
            foreach (EngineNativeVertexAuxiliaryPass pass in Enum.GetValues<EngineNativeVertexAuxiliaryPass>())
            {
                JsonObject auxiliaryRecipe = NativeAuxiliaryRecipe(staging, name, pass, recipe);
                using JsonDocument auxiliaryLayoutDocument = JsonDocument.Parse(Canonical(auxiliaryRecipe));
                ShaderProgramArtifact auxiliaryLayout = ShaderProgramArtifactReader.ReadLayout(auxiliaryLayoutDocument.RootElement, ShaderArtifact.FromWgsl(""), "recipe");
                Dictionary<string, string> entries = new() { ["vertex"] = auxiliaryLayout.VertexEntryPoint! };
                if (auxiliaryLayout.FragmentEntryPoint is { } fragment) entries["fragment"] = fragment;
                string auxiliarySource = EngineNativeVertexShaderGenerator.AuxiliarySource(pass);
                string auxiliaryEntries = string.Join(", ", entries.Select(pair => $"{pair.Key}:{pair.Value}"));
                SlangWgslOutput auxiliary = await CompileSlangWithContextAsync(
                    CompilerContext(String(auxiliaryRecipe, "pass"), auxiliaryEntries, auxiliarySource), staging, auxiliarySource,
                    [], [], cancellationToken, entries, preserveResourceParameters: true);
                Require(auxiliary.CompilerIdentity == raster.CompilerIdentity && auxiliary.Dependencies.SequenceEqual(raster.Dependencies),
                    $"{context}: the compiler or isolated source closure changed while cooking an auxiliary.");
                PreparedShader prepared = PrepareNativeProgram(auxiliaryRecipe, auxiliaryLayout, auxiliary, dependencies, materialDependency,
                    functionSource, functionHash, computeIdentity, null, context);
                auxiliaries.Add(prepared);
                string property = pass.ToString();
                auxiliaryIdentities[char.ToLowerInvariant(property[0]) + property[1..] + "Identity"] = Hash(prepared.Descriptor);
            }
            foreach (EngineLitMaterialShaderSource canonical in EngineNativeVertexShaderGenerator.RequiredCanonicalSources)
                Require(Hash(ReadBounded(ResolveInput(sourceRoot, canonical.Path), MaxSourceBytes)) == canonical.Sha256,
                    $"{context}: canonical source '{canonical.Path}' changed while cooking the auxiliary bundle.");
            Require(Hash(ReadBounded(materialPath, MaxJsonBytes)) == Hash(materialBytes) && Hash(ReadBounded(recipePath, MaxJsonBytes)) == Hash(recipeBytes),
                $"{context}: authored recipe or material changed while cooking the auxiliary bundle.");
            PreparedShader rasterPrepared = PrepareNativeProgram(recipe, rasterLayout, raster, dependencies, materialDependency,
                functionSource, functionHash, computeIdentity, auxiliaryIdentities, context);
            ShaderProgramArtifact computeArtifact = ShaderProgramArtifactReader.Read(computePrepared.Descriptor, computePrepared.Source);
            ShaderProgramArtifact rasterArtifact = ShaderProgramArtifactReader.Read(rasterPrepared.Descriptor, rasterPrepared.Source);
            ShaderProgramArtifactCatalog catalog = new([computeArtifact, rasterArtifact, .. auxiliaries.Select(item => ShaderProgramArtifactReader.Read(item.Descriptor, item.Source))]);
            Require(EngineNativeVertexShaderProvenance.TryValidate(rasterArtifact, catalog, out _, out string reason), $"{context}: {reason}");
            foreach (EngineNativeVertexAuxiliaryPass pass in Enum.GetValues<EngineNativeVertexAuxiliaryPass>())
                Require(EngineNativeVertexShaderProvenance.TryResolveAuxiliary(rasterArtifact, catalog, pass, out _, out reason), $"{context}: {reason}");
            PreparedShader chain = computePrepared;
            for (int index = auxiliaries.Count - 1; index >= 0; index--) chain = auxiliaries[index] with { Companion = chain };
            return rasterPrepared with { Companion = chain };
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static PreparedShader PrepareNativeProgram(JsonObject recipe, ShaderProgramArtifact layout, SlangWgslOutput compiled,
        SortedDictionary<string, string> dependencies, string materialPath, string functionSource, string functionHash, string? computeIdentity, JsonObject? auxiliaryIdentities, string context)
    {
        byte[] source = StrictUtf8.GetBytes(NormalizeLines(compiled.Source));
        Require(source.Length is > 0 and <= MaxSourceBytes, $"{context}: emitted source exceeds its byte limit.");
        new WgslAbiParser(StrictUtf8.GetString(source), context, layout).Validate();
        JsonObject descriptor = new();
        foreach (string key in new[] { "schemaVersion", "name", "sourceLanguage", "target", "entryPoints", "defines", "includes", "specialization",
            "requiredFeatures", "requiredLimits", "matrixLayout", "semanticSchemaIdentity", "layout", "pipeline", "coordinates", "pass" })
            descriptor[key] = recipe[key]!.DeepClone();
        if (recipe.ContainsKey("workgroupSize")) descriptor["workgroupSize"] = recipe["workgroupSize"]!.DeepClone();
        string hash = Hash(source);
        descriptor["source"] = new JsonObject { ["path"] = String(recipe, "name") + ".wgsl", ["sha256"] = hash, ["byteLength"] = source.Length, ["url"] = hash + ".wgsl" };
        descriptor["compilerIdentity"] = "xrengine-material-slang/1+" + compiled.CompilerIdentity;
        descriptor["sourceMap"] = new JsonObject { ["kind"] = "generated", ["path"] = materialPath };
        JsonArray closure = [];
        foreach ((string path, string dependencyHash) in dependencies)
            closure.Add(new JsonObject { ["path"] = path, ["sha256"] = dependencyHash });
        descriptor["dependencies"] = closure;
        JsonObject native = new()
        {
            ["profile"] = EngineNativeVertexShaderGenerator.Profile, ["functionSource"] = functionSource, ["functionSha256"] = functionHash,
        };
        if (computeIdentity is not null) native["computeIdentity"] = computeIdentity;
        if (auxiliaryIdentities is not null)
            foreach ((string key, JsonNode? value) in auxiliaryIdentities) native[key] = value!.DeepClone();
        descriptor["nativeVertexCompanion"] = native;
        byte[] encoded = Canonical(descriptor);
        Require(encoded.Length <= MaxJsonBytes, $"{context}: paired descriptor exceeds its byte limit.");
        _ = ShaderProgramArtifactReader.Read(encoded, source);
        return new(String(recipe, "name"), encoded, source, null, null, null);
    }

    private static void ValidateNativeRasterInputs(ShaderProgramArtifact artifact, string context)
    {
        ShaderStageResourceLayout? inputs = artifact.Resources.FirstOrDefault(resource => resource.Contract.Set == 1 && resource.Contract.Binding == 1);
        Require(inputs is { BindingType: "uniform", DynamicOffset: true, RuntimeArray: false, Visibility: ShaderStageVisibility.Vertex,
            Contract: { Name: "NativeVertexInputs", PhysicalName: "nativeVertexInputs_0", Kind: ShaderAbiResourceKind.UniformBuffer,
                Owner: ShaderAbiResourceOwner.Material, Frequency: ShaderAbiFrequency.Material, ByteSize: 64, Members.Length: 4 } },
            $"{context}: native inputs require the exact vertex-only 64-byte material uniform at group 1 binding 1.");
        for (int index = 0; index < 4; index++)
        {
            ShaderAbiMemberContract member = inputs!.Contract.Members[index];
            Require(member.PhysicalName == $"input{index}_0" && member.ProviderName == $"NativeVertexInput{index}" &&
                member.Offset == index * 16 && member.Size == 16 && member.PhysicalType == "vec4<f32>" &&
                member.MatrixOrder == ShaderAbiMatrixOrder.None && member.MatrixStride == 0,
                $"{context}: native input {index} has an incompatible member/provider ABI.");
        }
    }

    private static JsonObject NativeAuxiliaryRecipe(string staging, string name, EngineNativeVertexAuxiliaryPass pass, JsonObject rasterRecipe)
    {
        JsonObject recipe = Object(ParseJson(ReadBounded(Path.Combine(staging, EngineNativeVertexShaderGenerator.AuxiliaryRecipe(pass)), MaxJsonBytes)), "auxiliary recipe");
        recipe.Remove("materialVariant");
        recipe.Remove("pipelineArtifact");
        recipe["name"] = name + EngineNativeVertexShaderGenerator.AuxiliarySuffix(pass);
        recipe["sourceLanguage"] = "MaterialRecipe";
        recipe["semanticSchemaIdentity"] = EngineNativeVertexShaderGenerator.AuxiliarySchema(pass);
        JsonObject layout = Object(recipe["layout"], "layout");
        JsonObject canonicalNormalRecipe = Object(ParseJson(ReadBounded(Path.Combine(staging, "engine-depth-normal-prepass.recipe.json"), MaxJsonBytes)), "depth normal recipe");
        layout["vertexBuffers"] = canonicalNormalRecipe["layout"]!["vertexBuffers"]!.DeepClone();
        JsonObject rasterLayout = Object(rasterRecipe["layout"], "raster layout");
        JsonObject native = rasterLayout["bindings"]!.AsArray().Select(binding => binding!.AsObject()).Single(binding => String(binding, "name") == "NativeVertexInputs");
        layout["bindings"]!.AsArray().Add(native.DeepClone());
        JsonObject limits = Object(recipe["requiredLimits"], "limits");
        limits["maxVertexAttributes"] = 2;
        limits["maxVertexBuffers"] = 2;
        limits["maxVertexBufferArrayStride"] = 24;
        limits["maxBindGroups"] = Math.Max(2, limits["maxBindGroups"]!.GetValue<int>());
        limits["maxBindingsPerBindGroup"] = Math.Max(2, limits["maxBindingsPerBindGroup"]!.GetValue<int>());
        limits["maxDynamicUniformBuffersPerPipelineLayout"] = limits["maxDynamicUniformBuffersPerPipelineLayout"]!.GetValue<int>() + 1;
        limits["maxUniformBuffersPerShaderStage"] = limits["maxUniformBuffersPerShaderStage"]!.GetValue<int>() + 1;
        return recipe;
    }

    private static JsonObject NativeComputeRecipe(string name)
    {
        JsonArray members = [];
        for (int index = 0; index < 8; index++)
            members.Add(NativeMember(index < 4 ? $"input{index}_0" : $"previousInput{index - 4}_0",
                index < 4 ? $"NativeVertexInput{index}" : $"PreviousNativeVertexInput{index - 4}", index * 16, "vec4<f32>", 16));
        string[] names = ["sourceCurrentStream", "sourceCurrentOffset", "sourcePreviousStream", "sourcePreviousOffset", "currentOutputOffset", "previousOutputOffset", "vertexCount", "previousValid"];
        for (int index = 0; index < names.Length; index++)
            members.Add(NativeMember(names[index] + "_0", char.ToUpperInvariant(names[index][0]) + names[index][1..], 128 + index * 4, "u32", 4));
        JsonObject parameters = NativeBinding("NativeVertexParameters", "nativeVertexParameters_0", 2, "uniform", "View", 160, true, members);
        JsonObject source = NativeBinding("SourceGeometry", "sourceGeometry_0", 0, "read-only-storage", "Frame", 4, false, []);
        source["runtimeArray"] = true;
        JsonObject output = NativeBinding("OutputGeometry", "outputGeometry_0", 1, "storage", "Frame", 4, false, []);
        output["runtimeArray"] = true;
        return new JsonObject
        {
            ["schemaVersion"] = 3, ["name"] = name, ["sourceLanguage"] = "MaterialRecipe", ["target"] = "WebGPUWgsl",
            ["pass"] = "native-material-vertex", ["entryPoints"] = new JsonObject { ["compute"] = "nativeMaterialVertex" },
            ["workgroupSize"] = new JsonArray(64, 1, 1), ["defines"] = new JsonArray(), ["includes"] = new JsonArray(),
            ["specialization"] = new JsonObject(), ["requiredFeatures"] = new JsonArray(), ["matrixLayout"] = "column-major",
            ["semanticSchemaIdentity"] = EngineNativeVertexShaderGenerator.ComputeSchema, ["coordinates"] = Coordinates,
            ["pipeline"] = new JsonObject(), ["source"] = name + ".material.json",
            ["layout"] = new JsonObject { ["vertexBuffers"] = new JsonArray(), ["bindings"] = new JsonArray(source, output, parameters) },
            ["requiredLimits"] = new JsonObject
            {
                ["maxBindGroups"] = 1, ["maxBindingsPerBindGroup"] = 3, ["maxUniformBufferBindingSize"] = 160,
                ["maxDynamicUniformBuffersPerPipelineLayout"] = 1, ["maxUniformBuffersPerShaderStage"] = 1,
                ["maxStorageBuffersPerShaderStage"] = 2, ["maxStorageBufferBindingSize"] = 4,
                ["maxComputeWorkgroupSizeX"] = 64, ["maxComputeWorkgroupSizeY"] = 1, ["maxComputeWorkgroupSizeZ"] = 1,
                ["maxComputeInvocationsPerWorkgroup"] = 64,
            },
        };
    }

    private static JsonObject NativeMember(string name, string provider, int offset, string type, int bytes)
        => new() { ["name"] = name, ["provider"] = provider, ["offset"] = offset, ["bytes"] = bytes, ["type"] = type };

    private static JsonObject NativeBinding(string name, string physical, int binding, string kind, string frequency, int bytes, bool dynamic, JsonArray members)
        => new() { ["name"] = name, ["physicalName"] = physical, ["group"] = 0, ["binding"] = binding, ["kind"] = kind,
            ["visibility"] = new JsonArray("compute"), ["owner"] = "Engine", ["frequency"] = frequency,
            ["bytes"] = bytes, ["dynamic"] = dynamic, ["members"] = members };
}
