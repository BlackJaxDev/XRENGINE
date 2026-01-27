using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Extensions;
using XREngine.Components;
using XREngine.Components.ParticleModules;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Components.Particles.Enums;
using XREngine.Scene.Components.Particles.Interfaces;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using static XREngine.Engine;

namespace XREngine.Scene.Components.Particles;

/// <summary>
/// GPU-accelerated particle emitter component with a modular architecture.
/// Uses compute shaders for particle simulation and indirect rendering.
/// </summary>
[Category("Effects")]
[DisplayName("GPU Particle Emitter")]
[Description("High-performance GPU-driven particle system with modular effects.")]
[XRComponentEditor("XREngine.Editor.ComponentEditors.GPUParticleEmitterComponentEditor")]
public class ParticleEmitterComponent : XRComponent, IRenderable
{
    #region Constants

    /// <summary>
    /// Number of threads per compute shader workgroup.
    /// </summary>
    public const int ComputeWorkgroupSize = 256;

    /// <summary>
    /// Path to the particle spawn compute shader.
    /// </summary>
    public const string SpawnShaderPath = "Compute/Particles/ParticleSpawn";

    /// <summary>
    /// Path to the particle update compute shader.
    /// </summary>
    public const string UpdateShaderPath = "Compute/Particles/ParticleUpdate";

    #endregion

    #region GPU Counter Structure

    /// <summary>
    /// Counter buffer structure for atomic operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleCounters
    {
        public uint DeadCount;
        public uint AliveCount;
        public uint EmitCount;
        public uint Padding;
    }

    #endregion

    #region Private Fields

    private readonly List<IParticleModule> _modules = [];
    private bool _modulesChanged = true;
    private bool _buffersInitialized;
    private float _emitAccumulator;
    private float _totalTime;
    private uint _randomSeed;
    private string _currentShaderHash = "";

    // Shader assembler
    private readonly ParticleShaderAssembler _shaderAssembler = new();

    // Compute shaders
    private XRShader? _spawnShader;
    private XRShader? _updateShader;
    private XRRenderProgram? _spawnProgram;
    private XRRenderProgram? _updateProgram;

    // GPU Buffers
    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _deadListBuffer;
    private XRDataBuffer? _aliveListBuffer;
    private XRDataBuffer? _countersBuffer;
    private XRDataBuffer? _emitterParamsBuffer;
    private XRDataBuffer? _indirectDrawBuffer;

    // CPU-side data for initialization
    private GPUParticle[]? _particleData;
    private uint[]? _deadListData;
    private uint[]? _aliveListData;
    private ParticleCounters _counters;
    private GPUEmitterParams _emitterParams;

    // Rendering
    private RenderInfo3D? _renderInfo;
    private XRMeshRenderer? _particleRenderer;
    private XRMesh? _particleMesh;

    #endregion

    #region Public Properties

    [Category("Emitter")]
    [Description("Maximum number of particles that can exist at once.")]
    public uint MaxParticles { get; set; } = 10000;

    [Category("Emitter")]
    [Description("Number of particles emitted per second.")]
    public float EmissionRate { get; set; } = 100.0f;

    [Category("Emitter")]
    [Description("Whether the emitter is currently emitting new particles.")]
    public bool IsEmitting { get; set; } = true;

    [Category("Emitter")]
    [Description("Whether the simulation is running.")]
    public bool IsSimulating { get; set; } = true;

    [Category("Particle Defaults")]
    [Description("Minimum initial lifetime in seconds.")]
    public float LifetimeMin { get; set; } = 1.0f;

    [Category("Particle Defaults")]
    [Description("Maximum initial lifetime in seconds.")]
    public float LifetimeMax { get; set; } = 3.0f;

    [Category("Particle Defaults")]
    [Description("Initial color of particles.")]
    public ColorF4 InitialColor { get; set; } = ColorF4.White;

    [Category("Particle Defaults")]
    [Description("Minimum initial scale.")]
    public Vector3 ScaleMin { get; set; } = new(0.1f);

    [Category("Particle Defaults")]
    [Description("Maximum initial scale.")]
    public Vector3 ScaleMax { get; set; } = new(0.2f);

    [Category("Particle Defaults")]
    [Description("Minimum initial velocity.")]
    public Vector3 VelocityMin { get; set; } = new(-1f, 2f, -1f);

    [Category("Particle Defaults")]
    [Description("Maximum initial velocity.")]
    public Vector3 VelocityMax { get; set; } = new(1f, 5f, 1f);

    [Category("Physics")]
    [Description("Gravity vector applied to particles.")]
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);

    [Category("Rendering")]
    [Description("Material used to render particles.")]
    public XRMaterial? Material { get; set; }

    [Category("Rendering")]
    [Description("Billboard mode for particle rendering.")]
    public EParticleBillboardMode BillboardMode { get; set; } = EParticleBillboardMode.ViewFacing;

    [Category("Rendering")]
    [Description("Blend mode for particle rendering.")]
    public EParticleBlendMode BlendMode { get; set; } = EParticleBlendMode.AlphaBlend;

    [Category("Rendering")]
    [Description("Local bounding box for culling.")]
    public AABB LocalBounds { get; set; } = new AABB(new Vector3(-10), new Vector3(10));

    [Category("Rendering")]
    [Description("Base size of particle quads.")]
    public float ParticleSize { get; set; } = 1.0f;

    /// <summary>
    /// Current number of alive particles.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public uint AliveParticleCount => _counters.AliveCount;

    /// <summary>
    /// Current number of dead particles available for spawning.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public uint DeadParticleCount => _counters.DeadCount;

    [YamlIgnore]
    [Browsable(false)]
    public RenderInfo[] RenderedObjects { get; private set; } = [];

    /// <summary>
    /// Read-only access to registered modules.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public IReadOnlyList<IParticleModule> Modules => _modules;

    #endregion

    #region Module Management

    /// <summary>
    /// Adds a particle module to this emitter.
    /// </summary>
    public void AddModule(IParticleModule module)
    {
        _modules.Add(module);
        _modules.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        _modulesChanged = true;
    }

    /// <summary>
    /// Removes a particle module from this emitter.
    /// </summary>
    public bool RemoveModule(IParticleModule module)
    {
        bool removed = _modules.Remove(module);
        if (removed)
            _modulesChanged = true;
        return removed;
    }

    /// <summary>
    /// Gets a module by type.
    /// </summary>
    public T? GetModule<T>() where T : class, IParticleModule
        => _modules.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Gets all modules of a specific type.
    /// </summary>
    public IEnumerable<T> GetModules<T>() where T : class, IParticleModule
        => _modules.OfType<T>();

    /// <summary>
    /// Clears all modules.
    /// </summary>
    public void ClearModules()
    {
        _modules.Clear();
        _modulesChanged = true;
    }

    /// <summary>
    /// Moves a module up in the list.
    /// </summary>
    public bool MoveModuleUp(int index)
    {
        if (index <= 0 || index >= _modules.Count)
            return false;

        (_modules[index], _modules[index - 1]) = (_modules[index - 1], _modules[index]);
        _modulesChanged = true;
        return true;
    }

    /// <summary>
    /// Moves a module down in the list.
    /// </summary>
    public bool MoveModuleDown(int index)
    {
        if (index < 0 || index >= _modules.Count - 1)
            return false;

        (_modules[index], _modules[index + 1]) = (_modules[index + 1], _modules[index]);
        _modulesChanged = true;
        return true;
    }

    #endregion

    #region Constructor

    public ParticleEmitterComponent()
    {
        // Add default modules
        AddModule(new PointSpawnModule());
        AddModule(new GravityModule());
    }

    #endregion

    #region Component Lifecycle

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();

        // Initialize random seed
        _randomSeed = (uint)System.Random.Shared.Next();

        // Load compute shaders
        LoadComputeShaders();

        // Initialize GPU buffers
        InitializeBuffers();

        // Initialize rendering
        InitializeRendering();

        // Register update tick
        if (IsSimulating)
            RegisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();

        UnregisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);

        // Cleanup GPU resources
        CleanupBuffers();
        CleanupShaders();
        CleanupRendering();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        switch (propName)
        {
            case nameof(MaxParticles):
                // Reinitialize buffers if max particles changed
                if (_buffersInitialized)
                {
                    CleanupBuffers();
                    InitializeBuffers();
                }
                break;
            case nameof(LocalBounds):
                if (_renderInfo is not null)
                    _renderInfo.LocalCullingVolume = LocalBounds;
                break;
        }
    }

    #endregion

    #region Shader Management

    private void LoadComputeShaders()
    {
        RebuildShadersFromModules();
    }

    private void RebuildShadersFromModules()
    {
        // Compute hash of current module configuration
        string newHash = _shaderAssembler.ComputeModuleHash(_modules);
        
        // Skip rebuild if nothing changed
        if (newHash == _currentShaderHash && _spawnProgram is not null && _updateProgram is not null)
            return;

        try
        {
            // Cleanup old shaders
            CleanupShaders();

            // Generate shader source from modules
            string spawnSource = _shaderAssembler.GenerateSpawnShader(_modules);
            string updateSource = _shaderAssembler.GenerateUpdateShader(_modules);

            // Compile shaders from generated source
            _spawnShader = new XRShader(EShaderType.Compute, spawnSource);
            _updateShader = new XRShader(EShaderType.Compute, updateSource);

            _spawnProgram = new XRRenderProgram(true, false, _spawnShader);
            _updateProgram = new XRRenderProgram(true, false, _updateShader);

            _currentShaderHash = newHash;
            _modulesChanged = false;

            Debug.Log(ELogCategory.Rendering, "Particle shaders rebuilt with {0} enabled modules", _modules.Count(m => m.Enabled));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to compile particle compute shaders: {ex.Message}");
            // Fallback to loading static shaders
            LoadStaticShaders();
        }
    }

    private void LoadStaticShaders()
    {
        try
        {
            _spawnShader = ShaderHelper.LoadEngineShader(SpawnShaderPath, EShaderType.Compute);
            _updateShader = ShaderHelper.LoadEngineShader(UpdateShaderPath, EShaderType.Compute);

            _spawnProgram = new XRRenderProgram(true, false, _spawnShader);
            _updateProgram = new XRRenderProgram(true, false, _updateShader);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load static particle compute shaders: {ex.Message}");
        }
    }

    private void CleanupShaders()
    {
        _spawnProgram?.Destroy();
        _updateProgram?.Destroy();
        _spawnProgram = null;
        _updateProgram = null;
        _spawnShader = null;
        _updateShader = null;
    }

    #endregion

    #region Buffer Management

    private void InitializeBuffers()
    {
        if (_buffersInitialized)
            return;

        uint maxParticles = MaxParticles;

        // Initialize CPU-side arrays
        _particleData = new GPUParticle[maxParticles];
        _deadListData = new uint[maxParticles];
        _aliveListData = new uint[maxParticles];

        // Initialize dead list with all particle indices
        for (uint i = 0; i < maxParticles; i++)
        {
            _deadListData[i] = i;
            _particleData[i] = new GPUParticle { Flags = 0 }; // All dead initially
        }

        // Initialize counters
        _counters = new ParticleCounters
        {
            DeadCount = maxParticles,
            AliveCount = 0,
            EmitCount = 0,
            Padding = 0
        };

        // Create GPU buffers
        // Particles buffer: GPUParticle struct (24 floats = 96 bytes per particle)
        _particlesBuffer = new XRDataBuffer(
            "ParticlesBuffer",
            EBufferTarget.ShaderStorageBuffer,
            maxParticles,
            EComponentType.Float,
            GPUParticle.SizeInFloats,
            false,
            false);
        _particlesBuffer.SetBlockIndex(0);

        // Dead list buffer: uint indices
        _deadListBuffer = new XRDataBuffer(
            "DeadListBuffer",
            EBufferTarget.ShaderStorageBuffer,
            maxParticles,
            EComponentType.UInt,
            1,
            false,
            false);
        _deadListBuffer.SetBlockIndex(1);

        // Alive list buffer: uint indices
        _aliveListBuffer = new XRDataBuffer(
            "AliveListBuffer",
            EBufferTarget.ShaderStorageBuffer,
            maxParticles,
            EComponentType.UInt,
            1,
            false,
            false);
        _aliveListBuffer.SetBlockIndex(2);

        // Counters buffer: 4 uints
        _countersBuffer = new XRDataBuffer(
            "CountersBuffer",
            EBufferTarget.ShaderStorageBuffer,
            1,
            EComponentType.UInt,
            4,
            false,
            false);
        _countersBuffer.SetBlockIndex(3);

        // Emitter params uniform buffer: GPUEmitterParams struct
        _emitterParamsBuffer = new XRDataBuffer(
            "EmitterParamsBlock",
            EBufferTarget.UniformBuffer,
            1,
            EComponentType.Float,
            GPUEmitterParams.SizeInBytes / sizeof(float),
            false,
            false);
        _emitterParamsBuffer.SetBlockIndex(4);

        // Indirect draw buffer for instanced rendering
        _indirectDrawBuffer = new XRDataBuffer(
            "IndirectDrawBuffer",
            EBufferTarget.DrawIndirectBuffer,
            1,
            EComponentType.UInt,
            4,
            false,
            false);

        // Upload initial data
        UploadInitialData();

        _buffersInitialized = true;
    }

    private void UploadInitialData()
    {
        if (_particleData != null) _particlesBuffer?.SetDataRaw(_particleData);
        if (_deadListData != null) _deadListBuffer?.SetDataRaw(_deadListData);
        if (_aliveListData != null) _aliveListBuffer?.SetDataRaw(_aliveListData);
        _countersBuffer?.SetDataRaw(new[] { _counters });
    }

    private void CleanupBuffers()
    {
        _particlesBuffer?.Dispose();
        _deadListBuffer?.Dispose();
        _aliveListBuffer?.Dispose();
        _countersBuffer?.Dispose();
        _emitterParamsBuffer?.Dispose();
        _indirectDrawBuffer?.Dispose();

        _particlesBuffer = null;
        _deadListBuffer = null;
        _aliveListBuffer = null;
        _countersBuffer = null;
        _emitterParamsBuffer = null;
        _indirectDrawBuffer = null;

        _particleData = null;
        _deadListData = null;
        _aliveListData = null;

        _buffersInitialized = false;
    }

    #endregion

    #region Rendering Setup

    private void InitializeRendering()
    {
        // Create particle billboard mesh (single quad)
        _particleMesh = CreateParticleQuadMesh();

        // Create or use provided material
        var material = Material ?? CreateDefaultParticleMaterial();

        // Create mesh renderer
        _particleRenderer = new XRMeshRenderer(_particleMesh, material);

        // Add particle buffer to renderer
        if (_particlesBuffer is not null)
            _particleRenderer.Buffers[_particlesBuffer.AttributeName] = _particlesBuffer;
        if (_aliveListBuffer is not null)
            _particleRenderer.Buffers[_aliveListBuffer.AttributeName] = _aliveListBuffer;

        // Setup render info
        _renderInfo = RenderInfo3D.New(
            this,
            (int)EDefaultRenderPass.TransparentForward,
            RenderParticles);

        _renderInfo.LocalCullingVolume = LocalBounds;
        _renderInfo.CullingOffsetMatrix = Transform.WorldMatrix;
        _renderInfo.CastsShadows = false;

        RenderedObjects = [_renderInfo];
    }

    private XRMesh CreateParticleQuadMesh()
    {
        // Create a simple quad mesh for particle billboards
        float halfSize = 0.5f;

        var v0 = new Vertex(new Vector3(-halfSize, -halfSize, 0))
        {
            TextureCoordinateSets = [new Vector2(0, 0)]
        };

        var v1 = new Vertex(new Vector3(halfSize, -halfSize, 0))
        {
            TextureCoordinateSets = [new Vector2(1, 0)]
        };

        var v2 = new Vertex(new Vector3(halfSize, halfSize, 0))
        {
            TextureCoordinateSets = [new Vector2(1, 1)]
        };

        var v3 = new Vertex(new Vector3(-halfSize, halfSize, 0))
        {
            TextureCoordinateSets = [new Vector2(0, 1)]
        };

        var vertices = new[] { v0, v1, v2, v3 };
        var indices = new List<ushort> { 0, 1, 2, 0, 2, 3 };

        return new XRMesh(vertices, indices);
    }

    private XRMaterial CreateDefaultParticleMaterial()
    {
        // Create a simple unlit particle material
        var vertShader = XRShader.EngineShader(
            Path.Combine("Common", "ParticleBillboard.vs"),
            EShaderType.Vertex);
        var fragShader = XRShader.EngineShader(
            Path.Combine("Common", "ParticleUnlit.fs"),
            EShaderType.Fragment);

        var material = new XRMaterial(vertShader, fragShader)
        {
            RenderPass = (int)EDefaultRenderPass.TransparentForward
        };

        // Set blend mode based on property
        material.RenderOptions.BlendModeAllDrawBuffers = BlendMode switch
        {
            EParticleBlendMode.AlphaBlend => Rendering.Models.Materials.BlendMode.EnabledTransparent(),
            EParticleBlendMode.Additive => new Rendering.Models.Materials.BlendMode
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbSrcFactor = EBlendingFactor.SrcAlpha,
                RgbDstFactor = EBlendingFactor.One,
                AlphaSrcFactor = EBlendingFactor.SrcAlpha,
                AlphaDstFactor = EBlendingFactor.One
            },
            EParticleBlendMode.SoftAdditive => new Rendering.Models.Materials.BlendMode
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbSrcFactor = EBlendingFactor.OneMinusDstColor,
                RgbDstFactor = EBlendingFactor.One,
                AlphaSrcFactor = EBlendingFactor.OneMinusDstColor,
                AlphaDstFactor = EBlendingFactor.One
            },
            EParticleBlendMode.Multiply => new Rendering.Models.Materials.BlendMode
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbSrcFactor = EBlendingFactor.DstColor,
                RgbDstFactor = EBlendingFactor.Zero,
                AlphaSrcFactor = EBlendingFactor.DstAlpha,
                AlphaDstFactor = EBlendingFactor.Zero
            },
            EParticleBlendMode.Premultiplied => Rendering.Models.Materials.BlendMode.EnabledTransparent(),
            _ => Rendering.Models.Materials.BlendMode.EnabledTransparent()
        };

        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
        material.RenderOptions.DepthTest.UpdateDepth = false;

        return material;
    }

    private void CleanupRendering()
    {
        _renderInfo = null;
        _particleRenderer?.Destroy();
        _particleRenderer = null;
        _particleMesh = null;
        RenderedObjects = [];
    }

    private void RenderParticles()
    {
        if (_particleRenderer is null || _counters.AliveCount == 0)
            return;

        // Update uniforms
        _particleRenderer.Material?.SetFloat("uParticleSize", ParticleSize);
        _particleRenderer.Material?.SetInt("uBillboardMode", (int)BillboardMode);

        // Render instanced particles
        _particleRenderer.Render(null, _counters.AliveCount);
    }

    #endregion

    #region Simulation Update

    private void Update()
    {
        if (!IsSimulating || !_buffersInitialized)
            return;

        float deltaTime = Delta;
        _totalTime += deltaTime;

        // Update emitter parameters
        UpdateEmitterParams(deltaTime);

        // Calculate spawn count for this frame
        uint spawnCount = 0;
        if (IsEmitting && _counters.DeadCount > 0)
        {
            _emitAccumulator += EmissionRate * deltaTime;
            spawnCount = (uint)MathF.Floor(_emitAccumulator);
            _emitAccumulator -= spawnCount;

            // Clamp to available dead particles
            spawnCount = Math.Min(spawnCount, _counters.DeadCount);
        }

        // Dispatch spawn compute shader
        if (spawnCount > 0)
            DispatchSpawnShader(spawnCount);

        // Dispatch update compute shader
        DispatchUpdateShader();

        // Read back counters for CPU-side access
        ReadBackCounters();

        // Update render info
        if (_renderInfo is not null)
            _renderInfo.CullingOffsetMatrix = Transform.WorldMatrix;
    }

    private void UpdateEmitterParams(float deltaTime)
    {
        var worldMatrix = Transform.WorldMatrix;

        _emitterParams = new GPUEmitterParams
        {
            EmitterPosition = Transform.WorldTranslation,
            DeltaTime = deltaTime,
            EmitterForward = Vector3.TransformNormal(Vector3.UnitZ, worldMatrix),
            TotalTime = _totalTime,
            EmitterUp = Vector3.TransformNormal(Vector3.UnitY, worldMatrix),
            MaxParticles = MaxParticles,
            EmitterRight = Vector3.TransformNormal(Vector3.UnitX, worldMatrix),
            ActiveParticles = _counters.AliveCount,
            Gravity = Gravity,
            EmissionRate = EmissionRate,
            InitialColor = new Vector4(InitialColor.R, InitialColor.G, InitialColor.B, InitialColor.A),
            InitialVelocityMin = VelocityMin,
            InitialLifeMin = LifetimeMin,
            InitialVelocityMax = VelocityMax,
            InitialLifeMax = LifetimeMax,
            InitialScaleMin = ScaleMin,
            InitialScaleMax = ScaleMax
        };

        // Upload to GPU
        _emitterParamsBuffer?.SetDataRaw(new[] { _emitterParams });
    }

    private void DispatchSpawnShader(uint spawnCount)
    {
        if (_spawnProgram is null || _particlesBuffer is null ||
            _deadListBuffer is null || _aliveListBuffer is null ||
            _countersBuffer is null || _emitterParamsBuffer is null)
            return;

        // Update random seed
        _randomSeed = (_randomSeed * 1664525u + 1013904223u);

        // Set uniforms
        _spawnProgram.Uniform("uSpawnCount", spawnCount);
        _spawnProgram.Uniform("uRandomSeed", _randomSeed);

        // Set module-specific uniforms
        SetSpawnModuleUniforms(_spawnProgram);

        // Bind buffers
        _spawnProgram.BindBuffer(_particlesBuffer, 0);
        _spawnProgram.BindBuffer(_deadListBuffer, 1);
        _spawnProgram.BindBuffer(_aliveListBuffer, 2);
        _spawnProgram.BindBuffer(_countersBuffer, 3);
        _spawnProgram.BindBuffer(_emitterParamsBuffer, 4);

        // Dispatch
        uint workgroupCount = (spawnCount + ComputeWorkgroupSize - 1) / ComputeWorkgroupSize;
        _spawnProgram.DispatchCompute(workgroupCount, 1, 1, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchUpdateShader()
    {
        if (_updateProgram is null || _particlesBuffer is null ||
            _deadListBuffer is null || _aliveListBuffer is null ||
            _countersBuffer is null || _emitterParamsBuffer is null)
            return;

        // Set module-specific uniforms
        SetUpdateModuleUniforms(_updateProgram);

        // Bind buffers
        _updateProgram.BindBuffer(_particlesBuffer, 0);
        _updateProgram.BindBuffer(_deadListBuffer, 1);
        _updateProgram.BindBuffer(_aliveListBuffer, 2);
        _updateProgram.BindBuffer(_countersBuffer, 3);
        _updateProgram.BindBuffer(_emitterParamsBuffer, 4);

        // Dispatch for all particles
        uint workgroupCount = (MaxParticles + ComputeWorkgroupSize - 1) / ComputeWorkgroupSize;
        _updateProgram.DispatchCompute(workgroupCount, 1, 1, EMemoryBarrierMask.ShaderStorage);
    }

    private void SetSpawnModuleUniforms(XRRenderProgram program)
    {
        // Rebuild shaders if modules changed
        if (_modulesChanged)
            RebuildShadersFromModules();

        // Set uniforms for all enabled spawn modules via interface
        foreach (var module in _modules.OfType<IParticleSpawnModule>())
        {
            if (module.Enabled)
                module.SetUniforms(program);
        }
    }

    private void SetUpdateModuleUniforms(XRRenderProgram program)
    {
        // Rebuild shaders if modules changed
        if (_modulesChanged)
            RebuildShadersFromModules();

        // Set uniforms for all enabled update modules via interface
        foreach (var module in _modules.OfType<IParticleUpdateModule>())
        {
            if (module.Enabled)
                module.SetUniforms(program);
        }
    }

    private void ReadBackCounters()
    {
        // In a real implementation, this would read from the GPU buffer
        // For now, we track counters approximately on CPU
        // True GPU readback would require fence synchronization

        // This is a simplified version - real implementation would use
        // persistent mapped buffers or compute shader atomic readback
    }

    #endregion

    #region Public API

    /// <summary>
    /// Emits a burst of particles immediately.
    /// </summary>
    /// <param name="count">Number of particles to emit.</param>
    public void EmitBurst(uint count)
    {
        if (!_buffersInitialized || _spawnProgram is null)
            return;

        count = Math.Min(count, _counters.DeadCount);
        if (count == 0)
            return;

        UpdateEmitterParams(0);
        DispatchSpawnShader(count);
    }

    /// <summary>
    /// Clears all particles immediately.
    /// </summary>
    public void Clear()
    {
        if (!_buffersInitialized)
            return;

        // Reset all particles to dead state
        for (uint i = 0; i < MaxParticles; i++)
        {
            _deadListData![i] = i;
            _particleData![i] = new GPUParticle { Flags = 0 };
        }

        _counters = new ParticleCounters
        {
            DeadCount = MaxParticles,
            AliveCount = 0,
            EmitCount = 0,
            Padding = 0
        };

        UploadInitialData();
    }

    /// <summary>
    /// Pauses the particle simulation.
    /// </summary>
    public void Pause()
    {
        IsSimulating = false;
    }

    /// <summary>
    /// Resumes the particle simulation.
    /// </summary>
    public void Resume()
    {
        if (!IsSimulating)
        {
            IsSimulating = true;
            RegisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);
        }
    }

    /// <summary>
    /// Restarts the particle system from scratch.
    /// </summary>
    public void Restart()
    {
        Clear();
        _totalTime = 0;
        _emitAccumulator = 0;
        Resume();
    }

    #endregion

    #region Transform Updates

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

        if (_renderInfo is not null)
            _renderInfo.CullingOffsetMatrix = renderMatrix;
    }

    #endregion
}
