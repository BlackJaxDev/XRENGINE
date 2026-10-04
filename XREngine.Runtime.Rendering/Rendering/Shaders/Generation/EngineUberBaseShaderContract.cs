using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Exact source generation and dependency proof for prepared canonical Uber base variants.</summary>
public static partial class EngineUberBaseShaderContract
{
    public const string Schema = "xrengine.engine.uber-base.v1";
    public const string Pass = "forward-uber-base";
    public const string OrderPass = "forward-uber-base-order-gate";
    public const string VertexProfile = "position-normal-tangent-uv4-color-v1";
    public const string OrderGateSchema = "xrengine.engine.uber-base-order-gate.v1";

    public static string CookName(Guid materialId, ulong variantHash, string pass)
    {
        if (materialId == Guid.Empty || variantHash == 0) throw new ArgumentException("Uber cooks require material and prepared variant identities.");
        string suffix = pass switch
        {
            Pass => "color", OrderPass => "order", "depth-normal" => "normal", "depth" => "depth",
            "point-shadow-depth" => "point", "spot-shadow-depth" => "spot",
            _ => throw new ArgumentException("Unsupported Uber base pass.", nameof(pass)),
        };
        return $"uber-{materialId:N}-{variantHash:x16}-{suffix}";
    }

    /// <summary>Emits constants only from the prepared canonical request, preserving animated uniform fields.</summary>
    public static string GenerateSource(uint features, string sourceIdentity, ulong variantHash,
        IReadOnlyList<string> staticProperties, IReadOnlyList<string> pipelineMacros, string pass)
    {
        if ((features & ~31u) != 0 || sourceIdentity.Length != 64 || !sourceIdentity.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || variantHash == 0)
            throw new ArgumentException("Uber source generation requires a supported feature set and exact canonical source identity.");
        string source = pass switch
        {
            Pass or OrderPass => "UberBaseForward.slang",
            "depth-normal" or "depth" or "point-shadow-depth" or "spot-shadow-depth" => "UberBaseDepth.slang",
            _ => throw new ArgumentException("Unsupported Uber base pass.", nameof(pass)),
        };
        StringBuilder output = new();
        output.Append("// Canonical Uber source ").Append(sourceIdentity).Append("; prepared variant ")
            .Append(variantHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).Append(".\n");
        output.Append("#define XRE_UBER_BASE_FEATURES ").Append(features).Append("u\n");
        output.Append("#define XRE_UBER_BASE_SPECIALIZED 1\n");
        if (pass == OrderPass) output.Append("#define XRE_UBER_BASE_ORDER_GATE 1\n");
        if (pass == "depth-normal") output.Append("#define XRE_UBER_BASE_DEPTH_NORMAL 1\n");
        if (pass == "depth") output.Append("#define XRE_UBER_BASE_DEPTH_ONLY 1\n");
        if (pass == "point-shadow-depth") output.Append("#define XRE_UBER_BASE_POINT_DEPTH 1\n");
        foreach (string macro in pipelineMacros)
        {
            string? translated = macro switch
            {
                "XRENGINE_UBER_DISABLE_FORWARD_LIGHTING" => "XRE_UBER_BASE_DISABLE_LIGHTING",
                "XRENGINE_UBER_DISABLE_FORWARD_AMBIENT_OCCLUSION" => "XRE_UBER_BASE_DISABLE_AMBIENT_OCCLUSION",
                "XRENGINE_UBER_DISABLE_FORWARD_SHADOWS" => "XRE_UBER_BASE_DISABLE_SHADOWS",
                "XRENGINE_UBER_DISABLE_FORWARD_CONTACT_SHADOWS" => null,
                "XRENGINE_UBER_DISABLE_FORWARD_PBR_RESOURCES" => "XRE_UBER_BASE_DISABLE_PBR_RESOURCES",
                _ => throw new NotSupportedException($"UberBase.PipelineMacroUnsupported: '{macro}' has no admitted base lowering."),
            };
            if (translated is not null) output.Append("#define ").Append(translated).Append(" 1\n");
        }
        output.Append("#include \"").Append(source).Append("\"\n");
        output.Append("UberBaseMaterialParameters uberBaseResolveMaterial()\n{\n    UberBaseMaterialParameters value = uberMaterial;\n");
        ulong assigned = 0;
        Span<byte> bytes = stackalloc byte[16];
        foreach (string property in staticProperties)
        {
            int separator = property.IndexOf('=');
            if (separator <= 0) throw new ArgumentException("Prepared static properties require name=literal.", nameof(staticProperties));
            int index = UberBaseParameterSchema.IndexOf(property[..separator]);
            if (index < 0 || !UberBaseParameterSchema.IsActive(index, features)) continue;
            ShaderAbiMemberContract member = UberBaseParameterSchema.Members[index];
            Span<byte> value = bytes[..checked((int)member.Size)];
            if ((assigned & (1UL << index)) != 0 || !UberBaseStaticLiteral.TryWrite(member.PhysicalType, property[(separator + 1)..], value))
                throw new NotSupportedException($"UberBase.StaticExpressionUnsupported: '{member.ProviderName}' requires one finite canonical literal.");
            assigned |= 1UL << index;
            output.Append("    value.").Append(member.ProviderName).Append(" = ")
                .Append(UberBaseStaticLiteral.FormatSlang(member.PhysicalType, value)).Append(";\n");
        }
        output.Append("    return value;\n}\n");
        return output.ToString();
    }

    public static string NormalizedHash(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ReplaceLineEndings("\n"))));

    /// <summary>Proves the exact prepared source wrapper and pinned shared source closure at installation.</summary>
    public static bool TryValidateSource(ShaderProgramArtifact artifact, string expectedName, string expectedSource,
        string pass, out string reason)
    {
        reason = "UberBase.ProgramSourceMismatch: recook the exact prepared canonical Uber variant and complete source closure.";
        if (artifact.Name != expectedName || artifact.Pass != pass || artifact.SourceLanguage != "Slang" ||
            artifact.SemanticSchemaIdentity != (pass == OrderPass ? OrderGateSchema : Schema) || artifact.Target != ShaderCompileTarget.WebGPUWgsl ||
            artifact.Coordinates != "xrengine.webgpu.coordinates.v1" || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            artifact.VertexEntryPoint != "uberBaseVertex" || artifact.FragmentEntryPoint != "uberBaseFragment" || artifact.ComputeEntryPoint is not null)
            return false;
        using JsonDocument document = JsonDocument.Parse(artifact.DescriptorBytes.AsMemory());
        JsonElement descriptor = document.RootElement;
        if (!descriptor.TryGetProperty("defines", out JsonElement defines) || defines.ValueKind != JsonValueKind.Array || defines.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("includes", out JsonElement includes) || includes.ValueKind != JsonValueKind.Array || includes.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array ||
            !descriptor.TryGetProperty("sourceMap", out JsonElement map) || !map.TryGetProperty("path", out JsonElement pathValue)) return false;
        string? wrapperPath = pathValue.GetString();
        string wrapperHash = NormalizedHash(expectedSource);
        bool wrapperSeen = false;
        ulong seen = 0, seenDesktop = 0;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("path", out JsonElement path) || !dependency.TryGetProperty("sha256", out JsonElement sha)) return false;
            string? name = path.GetString(), hash = sha.GetString();
            if (name == wrapperPath)
            {
                if (wrapperSeen || hash != wrapperHash) return false;
                wrapperSeen = true;
            }
            for (int index = 0; index < RequiredSources.Length; index++)
            {
                EngineLitMaterialShaderSource required = RequiredSources[index];
                if (name != required.Path && name?.EndsWith("/" + required.Path, StringComparison.Ordinal) != true) continue;
                if ((seen & (1UL << index)) != 0 || hash != required.Sha256) return false;
                seen |= 1UL << index;
            }
            for (int index = 0; index < RequiredDesktopSources.Length; index++)
            {
                EngineLitMaterialShaderSource required = RequiredDesktopSources[index];
                string requiredPath = "Desktop/" + required.Path;
                if (name != requiredPath && name?.EndsWith("/" + requiredPath, StringComparison.Ordinal) != true) continue;
                if ((seenDesktop & (1UL << index)) != 0 || hash != required.Sha256) return false;
                seenDesktop |= 1UL << index;
            }
        }
        if (!wrapperSeen || seen != (1UL << RequiredSources.Length) - 1UL ||
            seenDesktop != (1UL << RequiredDesktopSources.Length) - 1UL) return false;
        reason = string.Empty;
        return true;
    }
}
