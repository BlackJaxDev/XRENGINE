using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Builds one isolated local vertex function for the canonical raster and packed native wrappers.</summary>
public static class EngineNativeVertexShaderGenerator
{
    public const string RasterSchema = "xrengine.engine.authored-lit-color-native-vertex.v1";
    public const string ComputeSchema = "xrengine.engine.native-material-vertex.v1";
    public const string Profile = "xrengine.native-material-vertex.position-normal.v1";
    public const string FunctionPath = "generated/NativeVertexFunction.slang";
    public const string RasterSource = "StandardLitColorNativeVertex.slang";
    public const string ComputeSource = "NativeMaterialVertex.slang";
    private const string Prefix = "// Generated isolated local vertex function; no stage or resource scope.\npublic void applyNativeVertex(inout float3 position, inout float3 normal, float4 input0, float4 input1, float4 input2, float4 input3)\n{\n";
    private const string Suffix = "\n}\n";
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredCanonicalSources { get; } =
    [
        new("StandardLitColor.slang", "50074cecbde9666073ff50275511151c5b01096e155f92115b5ef4c591a59e9c"),
        new("StandardLitColorNativeVertex.slang", "a9fdca7bc751ac7541a23f8a04cffef44e39b8d3d4bfe6385a0515376564dbcd"),
        new("NativeVertexNormal.slang", "0cad5a2666673505b0304b91b260e0cc8c98fe918c0e0bc47f6d886919af298b"),
        new("NativeMaterialVertex.slang", "22630cd14f664de7cb8400de07680d805b1cc13a6ea34dd755813365c12f0a22"),
        new("NativeVertexRasterInputs.slang", "0aa50d4d7af4f4cef4bdcd5c40328f3cf4ce8e1df51d86bc28bddb9222ad865d"),
        new("DepthNormalPrePass.slang", "223ba368faaf07b65ae0c776c552bb3e2cdb028f6eb5ea413aa6b351ed10faf7"),
        new("Depth.vert.slang", "0a2917f0dbd836de1689df13e295bae48cf4ca6649d2042ecf5dd4ad06fba2f6"),
        new("PointShadowDepth.slang", "09e1624675b2940c2ae2d42ece0b5b500e4d0e474caaf9094f8b5c0db94ec8d9"),
        new("SpotShadowDepth.slang", "a242df865c257f3ffcaf9d0f8b96bd1acf074de9477f843221a25e4d1d86fbeb"),
        new("StandardLitColorDirectionalShadow.slang", "eb50a3041d0ebfc17f8ed75d3e15b3196926df32d5cf5d4bf0e81d040c9b07fe"),
        new("StandardLitColorLocalShadows.slang", "07a11ad4fd6d3f762239038d97a50ca610688a61c7c88621c0a2e8bbc24c1052"),
        new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc"),
        new("NativeVertexDepthNormal.slang", "f3fb851577d03fcceef9a74cda58551c2f97d509fe35777400bec20d8d4f0550"),
        new("NativeVertexDirectionalShadow.slang", "ee2b9cd070e59309045d8a463898f18d84d0c19d6403b811f5d0e38df659b9a9"),
        new("NativeVertexPointShadow.slang", "f1948d2dfc77cdbdf3d165cf5cbc0d2216aeb80e76df6bc57c6dce2119dab29d"),
        new("NativeVertexSpotShadow.slang", "3f0a522d24aae4bf732f41fdf1072b3221a60a69b84d927d501144ce232e6cd9"),
        new("StandardLitColorNativeVertexDirectionalShadow.slang", "5603b6353cfe14f863c67aebd0877cd1f27ef00ddf8262fb160abedc6e4319d8"),
        new("StandardLitColorNativeVertexLocalShadows.slang", "72859baa42e8980037ead9cf75fb7078ff12f5f520f804f7ab3c2ea1b780f044"),
        new("engine-depth-normal-prepass.recipe.json", "3ff2b0c45fb2fa13646568cb6ee14fb402373e2c87cb76a18596a7574da39e37"),
        new("engine-shadow-depth.recipe.json", "ba1ac462092678168548c93d75a1598ca609f342dbaac7c8067a1f8316ca11ab"),
        new("engine-point-shadow-depth.recipe.json", "384ee7b106a30baafd129d91d04aa253bd024d7e6fcb6c78d091e726fabd037d"),
        new("engine-spot-shadow-depth.recipe.json", "c7ddf97bcc5fcebc7892b4e9ff2ab50434ea86df9ffdb07e86872d4bebbf4639"),
        new("engine-standard-lit-color-directional-shadow.recipe.json", "d5d9fa2d04f4938249acb84192095d63b403fa344ee409ab680fec6ecbc1491c"),
        new("engine-standard-lit-color-local-shadows.recipe.json", "54d475cf6c4dd49ac97151da0f4d70e3e66610f72704f4a19f10e36751c1a08a"),
    ];

    public static string AuxiliarySuffix(EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => "-native-depth-normal",
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => "-native-directional-shadow",
        EngineNativeVertexAuxiliaryPass.PointShadow => "-native-point-shadow",
        EngineNativeVertexAuxiliaryPass.SpotShadow => "-native-spot-shadow",
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => "-native-directional-receiver",
        EngineNativeVertexAuxiliaryPass.LocalReceiver => "-native-local-receiver",
        _ => throw new ArgumentOutOfRangeException(nameof(pass)),
    };

    public static string AuxiliarySchema(EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => "xrengine.engine.authored-lit-color-native-depth-normal.v1",
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => "xrengine.engine.authored-lit-color-native-directional-shadow.v1",
        EngineNativeVertexAuxiliaryPass.PointShadow => "xrengine.engine.authored-lit-color-native-point-shadow.v1",
        EngineNativeVertexAuxiliaryPass.SpotShadow => "xrengine.engine.authored-lit-color-native-spot-shadow.v1",
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => "xrengine.engine.authored-lit-color-native-directional-receiver.v1",
        EngineNativeVertexAuxiliaryPass.LocalReceiver => "xrengine.engine.authored-lit-color-native-local-receiver.v1",
        _ => throw new ArgumentOutOfRangeException(nameof(pass)),
    };

    // Hashes of the canonical auxiliary layouts, including all resource members and both vertex inputs.
    public static string AuxiliaryLayoutHash(EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => "ccf29f7922b70f4ac08f50f661da439f31b1f6310190fb148fbff2bba842b9a3",
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => "25d45e2b97a8fc1ef5c0888453132039d15da1a6a76b5fb4cf9aed3e470ab99c",
        EngineNativeVertexAuxiliaryPass.PointShadow => "f060fa116be7c05a465d0fa18d191abaf5d783e1152a302fafb3b3314f642b25",
        EngineNativeVertexAuxiliaryPass.SpotShadow => "25d45e2b97a8fc1ef5c0888453132039d15da1a6a76b5fb4cf9aed3e470ab99c",
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => "66c114f282b4b3fde7af8a14efd330f4ba84069536e93299259387c3e59ed4a8",
        EngineNativeVertexAuxiliaryPass.LocalReceiver => "c8df11a565057560a5721ddaff74a8b3eaa1f7875a5b52871e985ab721425af5",
        _ => throw new ArgumentOutOfRangeException(nameof(pass)),
    };

    public static string AuxiliarySource(EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => "NativeVertexDepthNormal.slang",
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => "NativeVertexDirectionalShadow.slang",
        EngineNativeVertexAuxiliaryPass.PointShadow => "NativeVertexPointShadow.slang",
        EngineNativeVertexAuxiliaryPass.SpotShadow => "NativeVertexSpotShadow.slang",
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => "StandardLitColorNativeVertexDirectionalShadow.slang",
        EngineNativeVertexAuxiliaryPass.LocalReceiver => "StandardLitColorNativeVertexLocalShadows.slang",
        _ => throw new ArgumentOutOfRangeException(nameof(pass)),
    };

    public static string AuxiliaryRecipe(EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => "engine-depth-normal-prepass.recipe.json",
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => "engine-shadow-depth.recipe.json",
        EngineNativeVertexAuxiliaryPass.PointShadow => "engine-point-shadow-depth.recipe.json",
        EngineNativeVertexAuxiliaryPass.SpotShadow => "engine-spot-shadow-depth.recipe.json",
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => "engine-standard-lit-color-directional-shadow.recipe.json",
        EngineNativeVertexAuxiliaryPass.LocalReceiver => "engine-standard-lit-color-local-shadows.recipe.json",
        _ => throw new ArgumentOutOfRangeException(nameof(pass)),
    };

    /// <summary>The body may use local arithmetic and control flow, with no imports, declarations outside the function, or stage capabilities.</summary>
    public static string GenerateFunction(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        body = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(body) || body.Length > 12000 || body.IndexOfAny(['\0', '\ufeff', '#', '"', '\'']) >= 0)
            throw new NotSupportedException("The native vertex body must be bounded Slang function statements without preprocessor directives or string literals.");
        int braces = 0;
        for (int index = 0; index < body.Length; index++)
        {
            char value = body[index];
            if (value == '/' && index + 1 < body.Length && body[index + 1] == '/')
            {
                while (index < body.Length && body[index] != '\n') index++;
                continue;
            }
            if (value == '/' && index + 1 < body.Length && body[index + 1] == '*')
            {
                int end = body.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0) throw new NotSupportedException("The native vertex body has an unterminated comment.");
                index = end + 1;
                continue;
            }
            if (value == '{') braces++;
            if (value == '}' && --braces < 0)
                throw new NotSupportedException("The native vertex body cannot escape its isolated function scope.");
            if (char.IsAsciiLetter(value) || value == '_')
            {
                int start = index++;
                while (index < body.Length && (char.IsAsciiLetterOrDigit(body[index]) || body[index] == '_')) index++;
                string token = body[start..index--];
                if (token is "import" or "include" or "module" or "namespace" or "export" or "extern" or "public" or "static" or "groupshared" or "uniform" or "discard" or "asm" ||
                    token.StartsWith("__", StringComparison.Ordinal) || token.StartsWith("SV_", StringComparison.Ordinal) ||
                    token.StartsWith("Wave", StringComparison.Ordinal) || token.StartsWith("Quad", StringComparison.Ordinal) ||
                    token.StartsWith("Interlocked", StringComparison.Ordinal) || token.StartsWith("ddx", StringComparison.Ordinal) || token.StartsWith("ddy", StringComparison.Ordinal) ||
                    token.Contains("Texture", StringComparison.Ordinal) || token.Contains("Buffer", StringComparison.Ordinal) || token.Contains("Sampler", StringComparison.Ordinal) ||
                    token.Contains("Barrier", StringComparison.Ordinal) || token.Contains("Ray", StringComparison.Ordinal))
                    throw new NotSupportedException($"Native vertex function token '{token}' requires an unavailable module, resource, or stage capability.");
            }
        }
        if (braces != 0) throw new NotSupportedException("The native vertex body has unbalanced local scopes.");
        return Prefix + body + Suffix;
    }

    public static bool IsExactFunction(string source)
    {
        if (!source.StartsWith(Prefix, StringComparison.Ordinal) || !source.EndsWith(Suffix, StringComparison.Ordinal)) return false;
        try { return GenerateFunction(source[Prefix.Length..^Suffix.Length]) == source; }
        catch (NotSupportedException) { return false; }
    }

    public static string FunctionHash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
