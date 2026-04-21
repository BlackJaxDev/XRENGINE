using System.Text;
using XREngine.Rendering;
using XREngine.Scene.Components.Landscape.Interfaces;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// Assembles compute and rendering shaders for terrain systems by combining module code.
/// Generates LOD, culling, normal generation, splatmap, and rendering shaders.
/// </summary>
public class TerrainShaderAssembler
{
    #region Shader Templates

    private const string HeightShaderTemplate = @"#version 450

// GPU Terrain System - Height Sampling Functions
// Auto-generated from terrain modules

// --- Module Uniforms ---
{MODULE_UNIFORMS}

// --- Noise Functions ---
vec3 mod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 1.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float snoise(vec2 v) {
    const vec4 C = vec4(0.211324865405187, 0.366025403784439, -0.577350269189626, 0.024390243902439);
    vec2 i = floor(v + dot(v, C.yy));
    vec2 x0 = v - i + dot(i, C.xx);
    vec2 i1 = (x0.x > x0.y) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
    vec4 x12 = x0.xyxy + C.xxzz;
    x12.xy -= i1;
    i = mod289(i.xyxy).xy;
    vec3 p = permute(permute(i.y + vec3(0.0, i1.y, 1.0)) + i.x + vec3(0.0, i1.x, 1.0));
    vec3 m = max(0.5 - vec3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m = m * m;
    m = m * m;
    vec3 x = 2.0 * fract(p * C.www) - 1.0;
    vec3 h = abs(x) - 0.5;
    vec3 ox = floor(x + 0.5);
    vec3 a0 = x - ox;
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
    vec3 g;
    g.x = a0.x * x0.x + h.x * x0.y;
    g.yz = a0.yz * x12.xz + h.yz * x12.yw;
    return 130.0 * dot(m, g);
}

// --- Height Sampling Function ---
float sampleHeight(vec2 uv, vec3 worldPos) {
    float height = 0.0;
    
{MODULE_HEIGHT_CODE}
    
    return height;
}
";

    private const string SplatShaderTemplate = @"#version 450

// GPU Terrain System - Splatmap Generation
// Auto-generated from terrain modules

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// --- Textures ---
layout(binding = 0) uniform sampler2D uHeightmap;
layout(binding = 1) uniform sampler2D uNormalmap;
layout(binding = 2, rgba8) uniform writeonly image2D uSplatmap;

// --- Terrain Parameters ---
layout(std140, binding = 2) uniform TerrainParamsBlock {
    vec3 TerrainWorldPosition;
    float TerrainWorldSize;
    vec2 HeightmapSize;
    float MinHeight;
    float MaxHeight;
    vec3 CameraPosition;
    float LOD0Distance;
    uint ChunkCountX;
    uint ChunkCountZ;
    uint TotalChunks;
    float MorphStartRatio;
    vec4 LayerTilings0;
    vec4 LayerTilings1;
    uint ActiveLayerCount;
    uint EnableTriplanar;
    uint EnableParallax;
    float ParallaxScale;
} uTerrainParams;

// --- Module Uniforms ---
{MODULE_UNIFORMS}

// --- Module Functions ---
{MODULE_FUNCTIONS}

// --- Splat Weight Calculation ---
vec4 calculateSplatWeights(vec2 uv, vec3 worldPos, vec3 normal, float height) {
    vec4 weights = vec4(1.0, 0.0, 0.0, 0.0);
    
{MODULE_SPLAT_CODE}
    
    // Normalize weights
    float totalWeight = weights.x + weights.y + weights.z + weights.w;
    if (totalWeight > 0.0) {
        weights /= totalWeight;
    }
    
    return weights;
}

// --- Main ---
void main() {
    ivec2 texelCoord = ivec2(gl_GlobalInvocationID.xy);
    ivec2 texSize = ivec2(uTerrainParams.HeightmapSize);
    
    if (texelCoord.x >= texSize.x || texelCoord.y >= texSize.y) return;
    
    vec2 uv = (vec2(texelCoord) + 0.5) / vec2(texSize);
    
    // Sample height and normal
    float heightNormalized = texture(uHeightmap, uv).r;
    float height = mix(uTerrainParams.MinHeight, uTerrainParams.MaxHeight, heightNormalized);
    vec3 normal = texture(uNormalmap, uv).xyz * 2.0 - 1.0;
    
    // Calculate world position
    vec3 worldPos = uTerrainParams.TerrainWorldPosition;
    worldPos.x += (uv.x - 0.5) * uTerrainParams.TerrainWorldSize;
    worldPos.y = height;
    worldPos.z += (uv.y - 0.5) * uTerrainParams.TerrainWorldSize;
    
    // Calculate splat weights
    vec4 weights = calculateSplatWeights(uv, worldPos, normal, height);
    
    // Write to splatmap
    imageStore(uSplatmap, texelCoord, weights);
}
";

    private const string TerrainVertexTemplate = @"#version 450

// GPU Terrain System - Vertex Shader
// Auto-generated from terrain modules

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;

out VS_OUT {
    vec3 WorldPosition;
    vec3 Normal;
    vec2 TexCoord;
    vec2 HeightmapUV;
    float MorphFactor;
    flat uint ChunkIndex;
} vs_out;

// --- Chunk Structure ---
struct TerrainChunk {
    vec3 WorldPosition;
    float Size;
    vec2 HeightmapOffset;
    vec2 HeightmapScale;
    uint LODLevel;
    uint ChunkIndex;
    float MinHeight;
    float MaxHeight;
    vec4 NeighborLODs;
    uint Visible;
    float MorphFactor;
    float _pad0;
    float _pad1;
};

// --- Buffers ---
layout(std430, binding = 0) readonly buffer ChunksBuffer { TerrainChunk Chunks[]; };
layout(std430, binding = 1) readonly buffer VisibleChunksBuffer { uint VisibleChunks[]; };

// --- Uniforms ---
uniform mat4 uViewProjection;
uniform sampler2D uHeightmap;
uniform sampler2D uNormalmap;

layout(std140, binding = 2) uniform TerrainParamsBlock {
    vec3 TerrainWorldPosition;
    float TerrainWorldSize;
    vec2 HeightmapSize;
    float MinHeight;
    float MaxHeight;
    vec3 CameraPosition;
    float LOD0Distance;
    uint ChunkCountX;
    uint ChunkCountZ;
    uint TotalChunks;
    float MorphStartRatio;
    vec4 LayerTilings0;
    vec4 LayerTilings1;
    uint ActiveLayerCount;
    uint EnableTriplanar;
    uint EnableParallax;
    float ParallaxScale;
} uTerrainParams;

// --- Module Uniforms ---
{MODULE_UNIFORMS}

// --- Main ---
void main() {
    // Get chunk data
    uint visibleIndex = gl_InstanceID;
    uint chunkIndex = VisibleChunks[visibleIndex];
    TerrainChunk chunk = Chunks[chunkIndex];
    
    // Calculate world position from chunk-local coordinates
    vec3 localPos = aPosition * chunk.Size;
    vec3 worldPos = chunk.WorldPosition + localPos;
    
    // Calculate heightmap UV
    vec2 heightmapUV = chunk.HeightmapOffset + aTexCoord * chunk.HeightmapScale;
    
    // Sample height
    float heightNormalized = texture(uHeightmap, heightmapUV).r;
    float height = mix(uTerrainParams.MinHeight, uTerrainParams.MaxHeight, heightNormalized);
    worldPos.y = height;
    
    // Sample normal
    vec3 normal = texture(uNormalmap, heightmapUV).xyz * 2.0 - 1.0;
    
    // Apply vertex morphing for LOD transitions
    // TODO: Implement morphing based on chunk.MorphFactor and neighbor LODs
    
    // Output
    vs_out.WorldPosition = worldPos;
    vs_out.Normal = normal;
    vs_out.TexCoord = aTexCoord;
    vs_out.HeightmapUV = heightmapUV;
    vs_out.MorphFactor = chunk.MorphFactor;
    vs_out.ChunkIndex = chunkIndex;
    
    gl_Position = uViewProjection * vec4(worldPos, 1.0);
}
";

    private const string TerrainFragmentTemplate = @"#version 450

// GPU Terrain System - Fragment Shader (PBR)
// Auto-generated from terrain modules

in VS_OUT {
    vec3 WorldPosition;
    vec3 Normal;
    vec2 TexCoord;
    vec2 HeightmapUV;
    float MorphFactor;
    flat uint ChunkIndex;
} fs_in;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 FragNormal;

// --- Textures ---
uniform sampler2D uHeightmap;
uniform sampler2D uNormalmap;
uniform sampler2D uSplatmap;

// --- Layer Textures ---
uniform sampler2D uLayerDiffuse0;
uniform sampler2D uLayerDiffuse1;
uniform sampler2D uLayerDiffuse2;
uniform sampler2D uLayerDiffuse3;
uniform sampler2D uLayerNormal0;
uniform sampler2D uLayerNormal1;
uniform sampler2D uLayerNormal2;
uniform sampler2D uLayerNormal3;

// --- Layer Properties ---
uniform vec4 uLayerTint0;
uniform vec4 uLayerTint1;
uniform vec4 uLayerTint2;
uniform vec4 uLayerTint3;
uniform vec2 uLayerTiling0;
uniform vec2 uLayerTiling1;
uniform vec2 uLayerTiling2;
uniform vec2 uLayerTiling3;
uniform float uLayerRoughness0;
uniform float uLayerRoughness1;
uniform float uLayerRoughness2;
uniform float uLayerRoughness3;
uniform int uLayerCount;

// --- Terrain Parameters ---
layout(std140, binding = 2) uniform TerrainParamsBlock {
    vec3 TerrainWorldPosition;
    float TerrainWorldSize;
    vec2 HeightmapSize;
    float MinHeight;
    float MaxHeight;
    vec3 CameraPosition;
    float LOD0Distance;
    uint ChunkCountX;
    uint ChunkCountZ;
    uint TotalChunks;
    float MorphStartRatio;
    vec4 LayerTilings0;
    vec4 LayerTilings1;
    uint ActiveLayerCount;
    uint EnableTriplanar;
    uint EnableParallax;
    float ParallaxScale;
} uTerrainParams;

// --- Module Uniforms ---
{MODULE_UNIFORMS}

// --- Triplanar Sampling ---
vec4 triplanarSample(sampler2D tex, vec3 worldPos, vec3 normal, vec2 tiling) {
    vec3 blending = abs(normal);
    blending = normalize(max(blending, 0.00001));
    float b = blending.x + blending.y + blending.z;
    blending /= b;
    
    vec4 xaxis = texture(tex, worldPos.yz * tiling);
    vec4 yaxis = texture(tex, worldPos.xz * tiling);
    vec4 zaxis = texture(tex, worldPos.xy * tiling);
    
    return xaxis * blending.x + yaxis * blending.y + zaxis * blending.z;
}

// --- Main ---
void main() {
    vec3 worldPos = fs_in.WorldPosition;
    vec3 normal = normalize(fs_in.Normal);
    vec2 uv = fs_in.HeightmapUV;
    
    // Sample splatmap
    vec4 splatWeights = texture(uSplatmap, uv);
    
    // Initialize output
    vec4 albedo = vec4(0.0);
    float roughness = 0.0;
    
    // Sample and blend layers
    bool useTriplanar = uTerrainParams.EnableTriplanar != 0u;
    
    // Layer 0
    if (uLayerCount >= 1 && splatWeights.x > 0.001) {
        vec4 layerColor;
        if (useTriplanar) {
            layerColor = triplanarSample(uLayerDiffuse0, worldPos, normal, uLayerTiling0);
        } else {
            layerColor = texture(uLayerDiffuse0, worldPos.xz * uLayerTiling0);
        }
        albedo += layerColor * uLayerTint0 * splatWeights.x;
        roughness += uLayerRoughness0 * splatWeights.x;
    }
    
    // Layer 1
    if (uLayerCount >= 2 && splatWeights.y > 0.001) {
        vec4 layerColor;
        if (useTriplanar) {
            layerColor = triplanarSample(uLayerDiffuse1, worldPos, normal, uLayerTiling1);
        } else {
            layerColor = texture(uLayerDiffuse1, worldPos.xz * uLayerTiling1);
        }
        albedo += layerColor * uLayerTint1 * splatWeights.y;
        roughness += uLayerRoughness1 * splatWeights.y;
    }
    
    // Layer 2
    if (uLayerCount >= 3 && splatWeights.z > 0.001) {
        vec4 layerColor;
        if (useTriplanar) {
            layerColor = triplanarSample(uLayerDiffuse2, worldPos, normal, uLayerTiling2);
        } else {
            layerColor = texture(uLayerDiffuse2, worldPos.xz * uLayerTiling2);
        }
        albedo += layerColor * uLayerTint2 * splatWeights.z;
        roughness += uLayerRoughness2 * splatWeights.z;
    }
    
    // Layer 3
    if (uLayerCount >= 4 && splatWeights.w > 0.001) {
        vec4 layerColor;
        if (useTriplanar) {
            layerColor = triplanarSample(uLayerDiffuse3, worldPos, normal, uLayerTiling3);
        } else {
            layerColor = texture(uLayerDiffuse3, worldPos.xz * uLayerTiling3);
        }
        albedo += layerColor * uLayerTint3 * splatWeights.w;
        roughness += uLayerRoughness3 * splatWeights.w;
    }
    
    // Fallback if no layers
    if (albedo.a < 0.001) {
        albedo = vec4(0.3, 0.5, 0.2, 1.0); // Default grass color
        roughness = 0.7;
    }
    
    // Output
    FragColor = vec4(albedo.rgb, roughness);
    FragNormal = vec4(normal * 0.5 + 0.5, 1.0);
}
";

    #endregion

    #region Public Methods

    /// <summary>
    /// Generates the splatmap compute shader source code from enabled modules.
    /// </summary>
    public string GenerateSplatShader(IEnumerable<ITerrainModule> modules)
    {
        var enabledModules = modules.Where(m => m.Enabled).OrderBy(m => m.Priority).ToList();

        var uniformDeclarations = new StringBuilder();
        var moduleFunctions = new StringBuilder();
        var moduleSplatCode = new StringBuilder();

        foreach (var module in enabledModules)
        {
            // Add uniform declarations
            string uniforms = module.GetUniformDeclarations();
            if (!string.IsNullOrWhiteSpace(uniforms))
            {
                uniformDeclarations.AppendLine($"// {module.ModuleName} uniforms");
                uniformDeclarations.AppendLine(uniforms);
                uniformDeclarations.AppendLine();
            }

            // Add splat code for splat modules
            if (module is ITerrainSplatModule splatModule)
            {
                string code = splatModule.GetSplatShaderCode();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    moduleSplatCode.AppendLine($"    // {module.ModuleName}");
                    moduleSplatCode.AppendLine(IndentCode(code, 4));
                    moduleSplatCode.AppendLine();
                }
            }
        }

        return SplatShaderTemplate
            .Replace("{MODULE_UNIFORMS}", uniformDeclarations.ToString())
            .Replace("{MODULE_FUNCTIONS}", moduleFunctions.ToString())
            .Replace("{MODULE_SPLAT_CODE}", moduleSplatCode.ToString());
    }

    /// <summary>
    /// Generates the height sampling shader code from enabled modules.
    /// </summary>
    public string GenerateHeightShader(IEnumerable<ITerrainModule> modules)
    {
        var enabledModules = modules.Where(m => m.Enabled).OrderBy(m => m.Priority).ToList();

        var uniformDeclarations = new StringBuilder();
        var moduleHeightCode = new StringBuilder();

        foreach (var module in enabledModules)
        {
            // Add uniform declarations
            string uniforms = module.GetUniformDeclarations();
            if (!string.IsNullOrWhiteSpace(uniforms))
            {
                uniformDeclarations.AppendLine($"// {module.ModuleName} uniforms");
                uniformDeclarations.AppendLine(uniforms);
                uniformDeclarations.AppendLine();
            }

            // Add height code for height modules
            if (module is ITerrainHeightModule heightModule)
            {
                string code = heightModule.GetHeightShaderCode();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    moduleHeightCode.AppendLine($"    // {module.ModuleName}");
                    moduleHeightCode.AppendLine(IndentCode(code, 4));
                    moduleHeightCode.AppendLine();
                }
            }
        }

        return HeightShaderTemplate
            .Replace("{MODULE_UNIFORMS}", uniformDeclarations.ToString())
            .Replace("{MODULE_HEIGHT_CODE}", moduleHeightCode.ToString());
    }

    /// <summary>
    /// Generates the terrain vertex shader source code.
    /// </summary>
    public string GenerateVertexShader(IEnumerable<ITerrainModule> modules)
    {
        var uniformDeclarations = new StringBuilder();

        foreach (var module in modules.Where(m => m.Enabled))
        {
            string uniforms = module.GetUniformDeclarations();
            if (!string.IsNullOrWhiteSpace(uniforms))
            {
                uniformDeclarations.AppendLine($"// {module.ModuleName} uniforms");
                uniformDeclarations.AppendLine(uniforms);
                uniformDeclarations.AppendLine();
            }
        }

        return TerrainVertexTemplate
            .Replace("{MODULE_UNIFORMS}", uniformDeclarations.ToString());
    }

    /// <summary>
    /// Generates the terrain fragment shader source code.
    /// </summary>
    public string GenerateFragmentShader(IEnumerable<ITerrainModule> modules)
    {
        var uniformDeclarations = new StringBuilder();

        foreach (var module in modules.Where(m => m.Enabled))
        {
            string uniforms = module.GetUniformDeclarations();
            if (!string.IsNullOrWhiteSpace(uniforms))
            {
                uniformDeclarations.AppendLine($"// {module.ModuleName} uniforms");
                uniformDeclarations.AppendLine(uniforms);
                uniformDeclarations.AppendLine();
            }
        }

        return TerrainFragmentTemplate
            .Replace("{MODULE_UNIFORMS}", uniformDeclarations.ToString());
    }

    /// <summary>
    /// Sets all module uniforms on the shader program.
    /// </summary>
    public void SetModuleUniforms(XRRenderProgram program, IEnumerable<ITerrainModule> modules)
    {
        foreach (var module in modules.Where(m => m.Enabled))
        {
            module.SetUniforms(program);
        }
    }

    /// <summary>
    /// Computes a hash of the enabled modules for caching compiled shaders.
    /// </summary>
    public string ComputeModuleHash(IEnumerable<ITerrainModule> modules)
    {
        int hash = 17;
        foreach (var module in modules.Where(m => m.Enabled).OrderBy(m => m.Priority))
        {
            hash = hash * 31 + module.ModuleName.GetHashCode();
        }
        return hash.ToString();
    }

    #endregion

    #region Private Methods

    private static string IndentCode(string code, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = code.Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                result.AppendLine(indent + trimmed);
            }
        }

        return result.ToString().TrimEnd();
    }

    #endregion
}
