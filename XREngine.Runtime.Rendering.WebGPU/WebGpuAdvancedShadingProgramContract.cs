using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Checks fixed bank shape, storage access, uniform sizes, and GPU tile workgroup contracts before use.</summary>
internal static class WebGpuAdvancedShadingProgramContract
{
    internal static readonly string[] ModifierAbsentBindings =
    [
        "advanced::shade-native-no-modifiers", "advanced::shade-surface-exports-no-modifiers",
        "advanced::shade-native-no-modifiers-msaa", "advanced::shade-surface-exports-no-modifiers-msaa",
    ];

    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        if (pass == "shade-msaa-resolve") { WebGpuAdvancedMsaaProgramContract.Validate(artifact, pass); return; }
        string requestedPass = pass;
        bool modifiersAbsent = pass is "shade-native-no-modifiers" or "shade-surface-exports-no-modifiers" or
            "shade-native-no-modifiers-msaa" or "shade-surface-exports-no-modifiers-msaa";
        if (modifiersAbsent) pass = pass switch
        {
            "shade-native-no-modifiers" => "shade-native",
            "shade-surface-exports-no-modifiers" => "shade-surface-exports",
            "shade-native-no-modifiers-msaa" => "shade-native-msaa",
            "shade-surface-exports-no-modifiers-msaa" => "shade-surface-exports-msaa",
            _ => throw Invalid(),
        };
        bool uberRaster = pass.StartsWith("shade-uber-", StringComparison.Ordinal);
        if (uberRaster) pass = pass switch
        {
            "shade-uber-native" => "shade-native", "shade-uber-native-depth" => "shade-native-depth",
            "shade-uber-native-msaa" => "shade-native-msaa", "shade-uber-native-depth-msaa" => "shade-native-depth-msaa",
            "shade-uber-surface-exports" => "shade-surface-exports", "shade-uber-surface-exports-depth" => "shade-surface-exports-depth",
            "shade-uber-surface-exports-msaa" => "shade-surface-exports-msaa", "shade-uber-surface-exports-depth-msaa" => "shade-surface-exports-depth-msaa",
            _ => throw Invalid(),
        };
        bool multisample = pass.EndsWith("-msaa", StringComparison.Ordinal);
        bool depthBank = pass is "shade-native-depth" or "shade-surface-exports-depth" or "shade-native-depth-msaa" or "shade-surface-exports-depth-msaa";
        bool native = depthBank || pass is "shade-native" or "shade-surface-exports" or "shade-native-msaa" or "shade-surface-exports-msaa";
        bool exports = pass is "shade-surface-exports" or "shade-background-exports" or "shade-surface-exports-msaa" or "shade-background-exports-msaa" or "shade-surface-exports-depth" or "shade-surface-exports-depth-msaa";
        bool classify = pass is "shade-classify" or "shade-classify-msaa", finalize = pass == "shade-finalize";
        bool background = pass is "shade-background" or "shade-background-exports" or "shade-background-exports-msaa";
        if (!native && !classify && !finalize && !background) throw Invalid();
        int count = native ? (multisample && !exports ? 40 : 41) : classify ? 7 : finalize ? 3 : exports ? 5 : 6;
        if (uberRaster) count--;
        if (uberRaster && artifact.SemanticSchemaIdentity != "xrengine.engine.uber-raster-consumer.v2") throw Invalid();
        if (modifiersAbsent && (artifact.SemanticSchemaIdentity != "xrengine.engine.native-unmodified.v1" ||
            artifact.ComputeEntryPoint != (multisample ? "advancedShadeNativeMsaa" : "advancedShadeNative"))) throw Invalid();
        if (artifact.Pass != requestedPass || artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Resources.Length != count ||
            artifact.ComputeEntryPoint is null || artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeWorkgroupSize != (finalize ? new ShaderComputeWorkgroupSize(64, 1, 1) : new ShaderComputeWorkgroupSize(16, 16, 1)))
            throw Invalid();
        if (native)
        {
            if (!HasNativeSchemas(artifact, exports, depthBank, uberRaster, modifiersAbsent))
                throw new NotSupportedException("WebGPU.Advanced.NativeSchemaMismatch: recook native shading and exports with engine-surface schema 5, Uber-base schema 1, the 36-table scene directory, authored-basis/decal/shadow/AO schema 1, and the selected texture-bank contract.");
            for (uint binding = 0; binding < 7; binding++) Require(artifact, 0, binding, "read-only-storage", 4);
            Require(artifact, 0, 7, "uniform", 944, "FrozenView");
            Require(artifact, 0, 8, "uniform", 160, "Parameters");
            Require(artifact, 1, 0, multisample ? "texture-multisampled-2d-uint" : "texture-2d-uint", name: multisample ? "RawVisibilityIdentity" : "VisibilityIdentity");
            Require(artifact, 1, 1, multisample ? "texture-multisampled-2d-uint" : "texture-2d-uint", name: multisample ? "RawVisibilityMetadataSelection" : "VisibilityMetadata");
            Require(artifact, 1, 2, multisample ? "texture-depth-multisampled-2d" : "texture-depth-2d", name: multisample ? "RawVisibilityDepth" : "VisibilityDepth");
            Require(artifact, 1, 3, "texture-2d-unfilterable-float", name: "AmbientOcclusion");
            for (uint slot = 0; slot < 12; slot++)
            {
                if (uberRaster && slot == 8) { Require(artifact, 1, 20, "texture-2d-array-unfilterable-float", name: "UberRasterSurface"); continue; }
                Require(artifact, 1, 4 + slot * 2, depthBank && slot == 9 ? "texture-depth-2d" : slot < 10 ? "texture-2d-float" : slot == 10 ? "texture-cube-float" : "texture-2d-array-float");
                Require(artifact, 1, 5 + slot * 2, depthBank && slot == 9 ? "comparison-sampler" : "filtering-sampler");
            }
            if (multisample && !exports)
            {
                Require(artifact, 2, 0, "storage-texture-2d-array-write-rgba32float", name: "SampleRadianceReactive");
                Require(artifact, 2, 1, "storage-texture-2d-write-rgba16float", name: "Velocity");
                Require(artifact, 2, 2, "storage-texture-2d-write-r32uint", name: "ShadingDiagnostics");
            }
            else
                for (uint binding = 0; binding < 4; binding++) Require(artifact, 2, binding, Output(binding, exports));
        }
        else if (classify)
        {
            Require(artifact, 0, 0, "read-only-storage", 4); Require(artifact, 0, 1, "read-only-storage", 4);
            Require(artifact, 0, 2, "storage", 4); Require(artifact, 0, 3, "storage", 4);
            Require(artifact, 0, 4, "uniform", 32, "Parameters");
            Require(artifact, 0, 5, multisample ? "texture-multisampled-2d-uint" : "texture-2d-uint", name: multisample ? "RawVisibilityIdentity" : "VisibilityIdentity");
            Require(artifact, 0, 6, multisample ? "texture-multisampled-2d-uint" : "texture-2d-uint", name: multisample ? "RawVisibilityMetadataSelection" : "VisibilityMetadata");
        }
        else if (finalize)
        {
            Require(artifact, 0, 0, "read-only-storage", 4); Require(artifact, 0, 1, "storage", 4);
            Require(artifact, 0, 2, "uniform", 16, "Parameters");
        }
        else
        {
            if (!exports) Require(artifact, 0, 0, "texture-2d-uint", name: "VisibilityIdentity");
            for (uint binding = 0; binding < 4; binding++) Require(artifact, 0, binding + (exports ? 0u : 1u), Output(binding, exports));
            Require(artifact, 0, exports ? 4u : 5u, "uniform", exports ? 16u : 32u, "Parameters");
        }
    }

    private static bool HasNativeSchemas(ShaderProgramArtifact artifact, bool exports, bool depthBank, bool uberRaster, bool modifiersAbsent)
    {
        if (artifact.SourceLanguage != "Slang" || artifact.DescriptorBytes.IsDefaultOrEmpty) return false;
        // Program validation also runs while recording. Inspect the retained,
        // hash-verified descriptor without allocating a JSON document per frame.
        Utf8JsonReader reader = new(artifact.DescriptorBytes.AsSpan());
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            if (!reader.ValueTextEquals("defines"u8))
            {
                if (!reader.Read()) return false;
                reader.Skip();
                continue;
            }
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;
            bool schemaSeen = false, exportsSeen = false, shadowSeen = false, depthSeen = false, ambientOcclusionSeen = false, decalsSeen = false, basisSeen = false, uberSeen = false, directorySeen = false, rasterSeen = false, modifiersAbsentSeen = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    return modifiersAbsentSeen == modifiersAbsent && rasterSeen == uberRaster && schemaSeen && basisSeen && uberSeen && directorySeen && shadowSeen && ambientOcclusionSeen && decalsSeen && exportsSeen == exports && depthSeen == depthBank;
                if (reader.TokenType != JsonTokenType.String) return false;
                if (reader.ValueTextEquals("XR_ADV_ENGINE_SURFACE_SCHEMA_VERSION=6"u8))
                {
                    if (schemaSeen) return false;
                    schemaSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_NATIVE_MODIFIERS_ABSENT_SCHEMA_VERSION=1"u8))
                {
                    if (!modifiersAbsent || modifiersAbsentSeen) return false;
                    modifiersAbsentSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION=2"u8))
                {
                    if (!uberRaster || rasterSeen) return false;
                    rasterSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_UBER_BASE_SCHEMA_VERSION=1"u8))
                {
                    if (uberSeen) return false;
                    uberSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_SCENE_DIRECTORY_TABLE_COUNT=36"u8))
                {
                    if (directorySeen) return false;
                    directorySeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_AUTHORED_BASIS_SCHEMA_VERSION=1"u8))
                {
                    if (basisSeen) return false;
                    basisSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_SURFACE_EXPORTS"u8))
                {
                    if (!exports || exportsSeen) return false;
                    exportsSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_STANDALONE_SHADOW_SCHEMA_VERSION=1"u8))
                {
                    if (shadowSeen) return false;
                    shadowSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_DEPTH_COMPARISON_BANK=1"u8))
                {
                    if (!depthBank || depthSeen) return false;
                    depthSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_AMBIENT_OCCLUSION_SCHEMA_VERSION=1"u8))
                {
                    if (ambientOcclusionSeen) return false;
                    ambientOcclusionSeen = true;
                }
                else if (reader.ValueTextEquals("XR_ADV_AUTHORED_DECAL_SCHEMA_VERSION=1"u8))
                {
                    if (decalsSeen) return false;
                    decalsSeen = true;
                }
                else return false;
            }
            return false;
        }
        return false;
    }

    private static string Output(uint binding, bool exports) => exports || binding < 2
        ? "storage-texture-2d-write-rgba16float" : binding == 2 ? "storage-texture-2d-write-r32float" : "storage-texture-2d-write-r32uint";
    private static void Require(ShaderProgramArtifact artifact, uint group, uint binding, string type, uint bytes = 0, string? name = null)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
        {
            if (resource.Contract.Set != group || resource.Contract.Binding != binding) continue;
            bool buffer = type is "read-only-storage" or "storage";
            if (resource.BindingType != type || resource.Visibility != ShaderStageVisibility.Compute ||
                resource.Contract.ByteSize != bytes || resource.DynamicOffset != (type == "uniform") ||
                buffer && (!resource.RuntimeArray || !resource.Contract.Members.IsEmpty) || name is not null && resource.Contract.Name != name)
                throw Invalid();
            return;
        }
        throw Invalid();
    }
    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.ShadingAbiMismatch: the cooked program does not implement the exact native cohort and texture-bank contract.");
}
