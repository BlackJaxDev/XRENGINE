using System.Text;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Scene.Components.Particles;

/// <summary>
/// Assembles compute shaders for particle systems by combining module code.
/// Generates spawn and update shaders with module-specific uniforms and logic.
/// </summary>
public class ParticleShaderAssembler
{
    #region Shader Templates

    private const string SpawnShaderTemplate = @"#version 450

// GPU Particle System - Spawn Compute Shader
// Auto-generated from particle modules

layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

// --- Particle Structure ---
struct Particle {
    vec3 Position;
    float Life;
    vec3 Velocity;
    float MaxLife;
    vec4 Color;
    vec3 Scale;
    float Rotation;
    vec3 AngularVelocity;
    uint Flags;
    vec4 CustomData0;
    vec4 CustomData1;
};

// --- Buffers ---
layout(std430, binding = 0) buffer ParticlesBuffer { Particle Particles[]; };
layout(std430, binding = 1) buffer DeadListBuffer { uint DeadList[]; };
layout(std430, binding = 2) buffer AliveListBuffer { uint AliveList[]; };
layout(std430, binding = 3) buffer CountersBuffer {
    uint DeadCount;
    uint AliveCount;
    uint EmitCount;
    uint Padding;
};

// --- Emitter Parameters ---
layout(std140, binding = 4) uniform EmitterParamsBlock {
    vec3 EmitterPosition;
    float DeltaTime;
    vec3 EmitterForward;
    float TotalTime;
    vec3 EmitterUp;
    uint MaxParticles;
    vec3 EmitterRight;
    uint ActiveParticles;
    vec3 Gravity;
    float EmissionRate;
    vec4 InitialColor;
    vec3 InitialVelocityMin;
    float InitialLifeMin;
    vec3 InitialVelocityMax;
    float InitialLifeMax;
    vec3 InitialScaleMin;
    float Padding0;
    vec3 InitialScaleMax;
    float Padding1;
} uEmitterParams;

// --- Common Uniforms ---
uniform uint uSpawnCount;
uniform uint uRandomSeed;

// --- Module Uniforms ---
{MODULE_UNIFORMS}

// --- Random Functions ---
uint hash(uint x) {
    x += (x << 10u);
    x ^= (x >> 6u);
    x += (x << 3u);
    x ^= (x >> 11u);
    x += (x << 15u);
    return x;
}

float randomFloat(inout uint seed) {
    seed = hash(seed);
    return float(seed) / float(0xFFFFFFFFu);
}

vec3 randomVec3(inout uint seed) {
    return vec3(randomFloat(seed), randomFloat(seed), randomFloat(seed));
}

vec3 randomDirection(inout uint seed) {
    float theta = randomFloat(seed) * 6.28318530718;
    float phi = acos(2.0 * randomFloat(seed) - 1.0);
    return vec3(sin(phi) * cos(theta), sin(phi) * sin(theta), cos(phi));
}

// --- Module Functions ---
{MODULE_FUNCTIONS}

// --- Main ---
void main() {
    uint spawnIndex = gl_GlobalInvocationID.x;
    if (spawnIndex >= uSpawnCount) return;
    
    // Get particle index from dead list
    uint deadIndex = atomicAdd(DeadCount, -1u) - 1u;
    if (deadIndex >= uEmitterParams.MaxParticles) {
        atomicAdd(DeadCount, 1u);
        return;
    }
    
    uint particleIndex = DeadList[deadIndex];
    
    // Initialize random seed for this particle
    uint seed = uRandomSeed + spawnIndex * 1664525u + particleIndex * 1013904223u;
    
    // Initialize particle with defaults
    Particle particle;
    particle.Position = uEmitterParams.EmitterPosition;
    particle.Life = mix(uEmitterParams.InitialLifeMin, uEmitterParams.InitialLifeMax, randomFloat(seed));
    particle.MaxLife = particle.Life;
    particle.Velocity = mix(uEmitterParams.InitialVelocityMin, uEmitterParams.InitialVelocityMax, randomVec3(seed));
    particle.Color = uEmitterParams.InitialColor;
    particle.Scale = mix(uEmitterParams.InitialScaleMin, uEmitterParams.InitialScaleMax, randomVec3(seed));
    particle.Rotation = randomFloat(seed) * 6.28318530718;
    particle.AngularVelocity = vec3(0.0);
    particle.Flags = 1u; // Alive flag
    particle.CustomData0 = vec4(0.0);
    particle.CustomData1 = vec4(0.0);
    
    // Apply spawn modules
{MODULE_SPAWN_CODE}
    
    // Write particle
    Particles[particleIndex] = particle;
    
    // Add to alive list
    uint aliveIndex = atomicAdd(AliveCount, 1u);
    AliveList[aliveIndex] = particleIndex;
}
";

    private const string UpdateShaderTemplate = @"#version 450

// GPU Particle System - Update Compute Shader
// Auto-generated from particle modules

layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

// --- Particle Structure ---
struct Particle {
    vec3 Position;
    float Life;
    vec3 Velocity;
    float MaxLife;
    vec4 Color;
    vec3 Scale;
    float Rotation;
    vec3 AngularVelocity;
    uint Flags;
    vec4 CustomData0;
    vec4 CustomData1;
};

// --- Buffers ---
layout(std430, binding = 0) buffer ParticlesBuffer { Particle Particles[]; };
layout(std430, binding = 1) buffer DeadListBuffer { uint DeadList[]; };
layout(std430, binding = 2) buffer AliveListBuffer { uint AliveList[]; };
layout(std430, binding = 3) buffer CountersBuffer {
    uint DeadCount;
    uint AliveCount;
    uint EmitCount;
    uint Padding;
};

// --- Emitter Parameters ---
layout(std140, binding = 4) uniform EmitterParamsBlock {
    vec3 EmitterPosition;
    float DeltaTime;
    vec3 EmitterForward;
    float TotalTime;
    vec3 EmitterUp;
    uint MaxParticles;
    vec3 EmitterRight;
    uint ActiveParticles;
    vec3 Gravity;
    float EmissionRate;
    vec4 InitialColor;
    vec3 InitialVelocityMin;
    float InitialLifeMin;
    vec3 InitialVelocityMax;
    float InitialLifeMax;
    vec3 InitialScaleMin;
    float Padding0;
    vec3 InitialScaleMax;
    float Padding1;
} uEmitterParams;

// --- Module Uniforms ---
{MODULE_UNIFORMS}

// --- Module Functions ---
{MODULE_FUNCTIONS}

// --- Main ---
void main() {
    uint particleIndex = gl_GlobalInvocationID.x;
    if (particleIndex >= uEmitterParams.MaxParticles) return;
    
    Particle particle = Particles[particleIndex];
    
    // Skip dead particles
    if ((particle.Flags & 1u) == 0u) return;
    
    float deltaTime = uEmitterParams.DeltaTime;
    float lifeRatio = 1.0 - (particle.Life / particle.MaxLife);
    
    // Apply update modules
{MODULE_UPDATE_CODE}
    
    // Apply gravity
    particle.Velocity += uEmitterParams.Gravity * deltaTime;
    
    // Update position
    particle.Position += particle.Velocity * deltaTime;
    
    // Update rotation
    particle.Rotation += particle.AngularVelocity.z * deltaTime;
    
    // Update lifetime
    particle.Life -= deltaTime;
    
    // Check for death
    if (particle.Life <= 0.0) {
        particle.Flags = 0u; // Dead flag
        
        // Add to dead list
        uint deadIndex = atomicAdd(DeadCount, 1u);
        DeadList[deadIndex] = particleIndex;
        
        // Remove from alive count
        atomicAdd(AliveCount, -1u);
    }
    
    // Write particle
    Particles[particleIndex] = particle;
}
";

    #endregion

    #region Public Methods

    /// <summary>
    /// Generates the spawn shader source code from enabled modules.
    /// </summary>
    public string GenerateSpawnShader(IEnumerable<IParticleModule> modules)
    {
        var enabledModules = modules.Where(m => m.Enabled).OrderBy(m => m.Priority).ToList();

        var uniformDeclarations = new StringBuilder();
        var moduleFunctions = new StringBuilder();
        var moduleSpawnCode = new StringBuilder();

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

            // Add spawn code for spawn modules
            if (module is IParticleSpawnModule spawnModule)
            {
                string code = spawnModule.GetSpawnShaderCode();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    moduleSpawnCode.AppendLine($"    // {module.ModuleName}");
                    moduleSpawnCode.AppendLine(IndentCode(code, 4));
                    moduleSpawnCode.AppendLine();
                }
            }
        }

        return SpawnShaderTemplate
            .Replace("{MODULE_UNIFORMS}", uniformDeclarations.ToString())
            .Replace("{MODULE_FUNCTIONS}", moduleFunctions.ToString())
            .Replace("{MODULE_SPAWN_CODE}", moduleSpawnCode.ToString());
    }

    /// <summary>
    /// Generates the update shader source code from enabled modules.
    /// </summary>
    public string GenerateUpdateShader(IEnumerable<IParticleModule> modules)
    {
        var enabledModules = modules.Where(m => m.Enabled).OrderBy(m => m.Priority).ToList();

        var uniformDeclarations = new StringBuilder();
        var moduleFunctions = new StringBuilder();
        var moduleUpdateCode = new StringBuilder();

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

            // Add update code for update modules
            if (module is IParticleUpdateModule updateModule)
            {
                string code = updateModule.GetUpdateShaderCode();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    moduleUpdateCode.AppendLine($"    // {module.ModuleName}");
                    moduleUpdateCode.AppendLine(IndentCode(code, 4));
                    moduleUpdateCode.AppendLine();
                }
            }
        }

        return UpdateShaderTemplate
            .Replace("{MODULE_UNIFORMS}", uniformDeclarations.ToString())
            .Replace("{MODULE_FUNCTIONS}", moduleFunctions.ToString())
            .Replace("{MODULE_UPDATE_CODE}", moduleUpdateCode.ToString());
    }

    /// <summary>
    /// Sets all module uniforms on the shader program.
    /// </summary>
    public void SetModuleUniforms(XRRenderProgram program, IEnumerable<IParticleModule> modules)
    {
        foreach (var module in modules.Where(m => m.Enabled))
        {
            module.SetUniforms(program);
        }
    }

    /// <summary>
    /// Computes a hash of the enabled modules for caching compiled shaders.
    /// </summary>
    public string ComputeModuleHash(IEnumerable<IParticleModule> modules)
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
