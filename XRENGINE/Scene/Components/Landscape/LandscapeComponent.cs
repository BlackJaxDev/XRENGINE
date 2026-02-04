using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Extensions;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Components.Landscape.Interfaces;
using XREngine.Scene.Components.Landscape.TerrainModules;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using static XREngine.Engine;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// GPU-driven landscape/terrain component with chunked LOD.
/// Supports virtual texturing, procedural generation, and real-time modification.
/// </summary>
[Category("Environment")]
[DisplayName("GPU Landscape")]
[Description("High-performance GPU-driven terrain system with chunked LOD and modular texturing.")]
[XRComponentEditor("XREngine.Editor.ComponentEditors.GPULandscapeComponentEditor")]
public class LandscapeComponent : XRComponent, IRenderable
{
    #region Constants

    /// <summary>
    /// Number of threads per compute shader workgroup.
    /// </summary>
    public const int ComputeWorkgroupSize = 64;

    /// <summary>
    /// Maximum supported LOD levels.
    /// </summary>
    public const int MaxLODLevels = 6;

    /// <summary>
    /// Maximum supported terrain layers.
    /// </summary>
    public const int MaxLayers = 8;

    /// <summary>
    /// Grid size for highest detail LOD (LOD 0).
    /// </summary>
    public const int LOD0GridSize = 32;

    #endregion

    #region Shader Paths

    /// <summary>
    /// Path to the terrain LOD selection compute shader.
    /// </summary>
    public const string LODShaderPath = "Compute/Terrain/TerrainLOD";

    /// <summary>
    /// Path to the terrain chunk culling compute shader.
    /// </summary>
    public const string CullingShaderPath = "Compute/Terrain/TerrainCulling";

    /// <summary>
    /// Path to the terrain normal generation compute shader.
    /// </summary>
    public const string NormalsShaderPath = "Compute/Terrain/TerrainNormals";

    /// <summary>
    /// Path to the terrain splat generation compute shader.
    /// </summary>
    public const string SplatGenShaderPath = "Compute/Terrain/TerrainSplatGen";

    #endregion

    #region GPU Structures

    /// <summary>
    /// GPU-side chunk data structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct GPUTerrainChunk
    {
        public Vector3 WorldPosition;
        public float Size;
        public Vector2 HeightmapOffset;
        public Vector2 HeightmapScale;
        public uint LODLevel;
        public uint ChunkIndex;
        public float MinHeight;
        public float MaxHeight;
        public Vector4 NeighborLODs;
        public uint Visible;
        public float MorphFactor;
        public float Padding0;
        public float Padding1;

        public const int SizeInBytes = 80;
    }

    /// <summary>
    /// GPU-side terrain parameters uniform block.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct GPUTerrainParams
    {
        public Vector3 TerrainWorldPosition;
        public float TerrainWorldSize;
        public Vector2 HeightmapSize;
        public float MinHeight;
        public float MaxHeight;
        public Vector3 CameraPosition;
        public float LOD0Distance;
        public uint ChunkCountX;
        public uint ChunkCountZ;
        public uint TotalChunks;
        public float MorphStartRatio;
        public Vector4 LayerTilings0;
        public Vector4 LayerTilings1;
        public uint ActiveLayerCount;
        public uint EnableTriplanar;
        public uint EnableParallax;
        public float ParallaxScale;

        public const int SizeInBytes = 128;
    }

    /// <summary>
    /// Indirect draw command structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawElementsIndirectCommand
    {
        public uint IndexCount;
        public uint InstanceCount;
        public uint FirstIndex;
        public int BaseVertex;
        public uint BaseInstance;
    }

    #endregion

    #region Private Fields

    private readonly List<ITerrainModule> _modules = [];
    private readonly List<TerrainLayer> _layers = [];
    private bool _modulesChanged = true;
    private bool _buffersInitialized;
    private string _currentShaderHash = "";

    // Shader assembler
    private readonly TerrainShaderAssembler _shaderAssembler = new();

    // Compute shaders
    private XRShader? _lodShader;
    private XRShader? _cullingShader;
    private XRShader? _normalsShader;
    private XRShader? _splatGenShader;
    private XRShader? _heightGenShader;
    private XRRenderProgram? _lodProgram;
    private XRRenderProgram? _cullingProgram;
    private XRRenderProgram? _normalsProgram;
    private XRRenderProgram? _splatGenProgram;
    private XRRenderProgram? _heightGenProgram;

    // GPU Buffers
    private XRDataBuffer? _chunksBuffer;
    private XRDataBuffer? _visibleChunksBuffer;
    private XRDataBuffer? _chunkCountBuffer;
    private XRDataBuffer? _terrainParamsBuffer;
    private XRDataBuffer? _indirectDrawBuffer;

    // Textures
    private XRTexture2D? _heightmapTexture;
    private XRTexture2D? _normalmapTexture;
    private XRTexture2D? _splatmapTexture;

    // CPU-side data
    private GPUTerrainChunk[]? _chunkData;
    private GPUTerrainParams _terrainParams;
    private float[]? _heightmapData;
    private uint _visibleChunkCount;

    // Rendering
    private RenderInfo3D? _renderInfo;
    private XRMeshRenderer? _terrainRenderer;
    private XRMesh?[] _lodMeshes = new XRMesh?[MaxLODLevels];

    // LOD distances
    private readonly float[] _lodDistances = new float[MaxLODLevels];

    #endregion

    #region Public Properties

    [Category("Terrain")]
    [Description("Total terrain size in world units.")]
    public float TerrainSize { get; set; } = 1000.0f;

    [Category("Terrain")]
    [Description("Minimum terrain height.")]
    public float MinHeight { get; set; } = 0.0f;

    [Category("Terrain")]
    [Description("Maximum terrain height.")]
    public float MaxHeight { get; set; } = 100.0f;

    [Category("Terrain")]
    [Description("Number of chunks per axis.")]
    public uint ChunkCount { get; set; } = 16;

    [Category("Terrain")]
    [Description("Resolution of the heightmap texture.")]
    public uint HeightmapResolution { get; set; } = 1024;

    [Category("Terrain")]
    [Description("External heightmap texture (optional).")]
    public XRTexture2D? ExternalHeightmap { get; set; }

    [Category("LOD")]
    [Description("Distance for LOD level 0.")]
    public float LOD0Distance { get; set; } = 50.0f;

    [Category("LOD")]
    [Description("Distance multiplier for each subsequent LOD level.")]
    public float LODDistanceMultiplier { get; set; } = 2.0f;

    [Category("LOD")]
    [Description("Ratio of LOD distance at which morphing starts.")]
    public float MorphStartRatio { get; set; } = 0.7f;

    [Category("LOD")]
    [Description("Enable smooth LOD transitions via vertex morphing.")]
    public bool EnableMorphing { get; set; } = true;

    [Category("Rendering")]
    [Description("Enable triplanar texturing.")]
    public bool EnableTriplanar { get; set; } = true;

    [Category("Rendering")]
    [Description("Enable parallax/displacement mapping.")]
    public bool EnableParallax { get; set; } = false;

    [Category("Rendering")]
    [Description("Parallax mapping scale.")]
    public float ParallaxScale { get; set; } = 0.05f;

    [Category("Rendering")]
    [Description("Custom terrain material (optional).")]
    public XRMaterial? Material { get; set; }

    [Category("Rendering")]
    [Description("Whether terrain casts shadows.")]
    public bool CastShadows { get; set; } = true;

    [Category("Rendering")]
    [Description("Whether terrain receives shadows.")]
    public bool ReceiveShadows { get; set; } = true;

    [Category("Rendering")]
    [Description("Wireframe debug rendering.")]
    public bool WireframeMode { get; set; } = false;

    /// <summary>
    /// Total number of chunks in the terrain.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public uint TotalChunks => ChunkCount * ChunkCount;

    /// <summary>
    /// Current number of visible chunks.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public uint VisibleChunkCount => _visibleChunkCount;

    /// <summary>
    /// Size of each chunk in world units.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public float ChunkSize => TerrainSize / ChunkCount;

    [YamlIgnore]
    [Browsable(false)]
    public RenderInfo[] RenderedObjects { get; private set; } = [];

    /// <summary>
    /// Read-only access to terrain layers.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public IReadOnlyList<TerrainLayer> Layers => _layers;

    /// <summary>
    /// Read-only access to registered modules.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public IReadOnlyList<ITerrainModule> Modules => _modules;

    #endregion

    #region Layer Management

    /// <summary>
    /// Adds a terrain layer.
    /// </summary>
    public void AddLayer(TerrainLayer layer)
    {
        if (_layers.Count >= MaxLayers)
            throw new InvalidOperationException($"Maximum of {MaxLayers} layers supported.");

        _layers.Add(layer);
        _modulesChanged = true;
        UpdateLayerTextures();
    }

    /// <summary>
    /// Removes a terrain layer.
    /// </summary>
    public bool RemoveLayer(TerrainLayer layer)
    {
        bool removed = _layers.Remove(layer);
        if (removed)
        {
            _modulesChanged = true;
            UpdateLayerTextures();
        }
        return removed;
    }

    /// <summary>
    /// Clears all terrain layers.
    /// </summary>
    public void ClearLayers()
    {
        _layers.Clear();
        _modulesChanged = true;
        UpdateLayerTextures();
    }

    /// <summary>
    /// Moves a layer up in the list (decreases its index).
    /// </summary>
    public bool MoveLayerUp(int index)
    {
        if (index <= 0 || index >= _layers.Count)
            return false;

        (_layers[index], _layers[index - 1]) = (_layers[index - 1], _layers[index]);
        _modulesChanged = true;
        return true;
    }

    /// <summary>
    /// Moves a layer down in the list (increases its index).
    /// </summary>
    public bool MoveLayerDown(int index)
    {
        if (index < 0 || index >= _layers.Count - 1)
            return false;

        (_layers[index], _layers[index + 1]) = (_layers[index + 1], _layers[index]);
        _modulesChanged = true;
        return true;
    }

    #endregion

    #region Module Management

    /// <summary>
    /// Adds a terrain module.
    /// </summary>
    public void AddModule(ITerrainModule module)
    {
        _modules.Add(module);
        _modules.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        _modulesChanged = true;
    }

    /// <summary>
    /// Removes a terrain module.
    /// </summary>
    public bool RemoveModule(ITerrainModule module)
    {
        bool removed = _modules.Remove(module);
        if (removed)
            _modulesChanged = true;
        return removed;
    }

    /// <summary>
    /// Gets a module by type.
    /// </summary>
    public T? GetModule<T>() where T : class, ITerrainModule
        => _modules.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Gets all modules of a specific type.
    /// </summary>
    public IEnumerable<T> GetModules<T>() where T : class, ITerrainModule
        => _modules.OfType<T>();

    /// <summary>
    /// Clears all terrain modules.
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

    public LandscapeComponent()
    {
        // Add default modules
        AddModule(new HeightmapModule());
        AddModule(new SlopeSplatModule());

        // Add default layers
        AddLayer(new TerrainLayer { Name = "Grass", Tint = new ColorF4(0.3f, 0.5f, 0.2f, 1.0f), Roughness = 0.7f });
        AddLayer(new TerrainLayer { Name = "Rock", Tint = new ColorF4(0.5f, 0.5f, 0.5f, 1.0f), Roughness = 0.8f });
        AddLayer(new TerrainLayer { Name = "Cliff", Tint = new ColorF4(0.4f, 0.35f, 0.3f, 1.0f), Roughness = 0.9f });
    }

    #endregion

    #region Component Lifecycle

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();

        // Calculate LOD distances
        CalculateLODDistances();

        // Load compute shaders
        LoadComputeShaders();

        // Initialize textures
        InitializeTextures();

        // Initialize GPU buffers
        InitializeBuffers();

        // Create LOD meshes
        CreateLODMeshes();

        // Initialize rendering
        InitializeRendering();

        // Register update tick
        RegisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();

        UnregisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);

        // Cleanup GPU resources
        CleanupBuffers();
        CleanupShaders();
        CleanupTextures();
        CleanupRendering();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        switch (propName)
        {
            case nameof(TerrainSize):
            case nameof(ChunkCount):
            case nameof(HeightmapResolution):
                if (_buffersInitialized)
                {
                    RebuildTerrain();
                }
                break;
            case nameof(LOD0Distance):
            case nameof(LODDistanceMultiplier):
                CalculateLODDistances();
                break;
            case nameof(ExternalHeightmap):
                if (_buffersInitialized)
                {
                    UpdateHeightmap();
                }
                break;
        }
    }

    #endregion

    #region Shader Management

    private void LoadComputeShaders()
    {
        // Load static utility shaders
        LoadStaticShaders();
        
        // Build module-based shaders
        RebuildShadersFromModules();
    }

    private void LoadStaticShaders()
    {
        try
        {
            _lodShader = ShaderHelper.LoadEngineShader(LODShaderPath, EShaderType.Compute);
            _cullingShader = ShaderHelper.LoadEngineShader(CullingShaderPath, EShaderType.Compute);
            _normalsShader = ShaderHelper.LoadEngineShader(NormalsShaderPath, EShaderType.Compute);

            _lodProgram = new XRRenderProgram(true, false, _lodShader);
            _cullingProgram = new XRRenderProgram(true, false, _cullingShader);
            _normalsProgram = new XRRenderProgram(true, false, _normalsShader);
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning($"Failed to load static terrain compute shaders: {ex.Message}");
        }
    }

    private void RebuildShadersFromModules()
    {
        // Compute hash of current module configuration
        string newHash = _shaderAssembler.ComputeModuleHash(_modules);
        
        // Skip rebuild if nothing changed
        if (newHash == _currentShaderHash && _splatGenProgram is not null && _heightGenProgram is not null)
            return;

        try
        {
            // Cleanup old module-generated shaders
            _splatGenProgram?.Destroy();
            _heightGenProgram?.Destroy();
            _splatGenShader = null;
            _heightGenShader = null;

            // Generate shader source from modules
            string splatSource = _shaderAssembler.GenerateSplatShader(_modules);
            string heightSource = _shaderAssembler.GenerateHeightShader(_modules);

            // Compile shaders from generated source
            _splatGenShader = new XRShader(EShaderType.Compute, splatSource);
            _heightGenShader = new XRShader(EShaderType.Compute, heightSource);

            _splatGenProgram = new XRRenderProgram(true, false, _splatGenShader);
            _heightGenProgram = new XRRenderProgram(true, false, _heightGenShader);

            _currentShaderHash = newHash;
            _modulesChanged = false;

            Debug.Log(ELogCategory.Rendering, "Terrain shaders rebuilt with {0} enabled modules", _modules.Count(m => m.Enabled));
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning($"Failed to compile terrain shaders from modules: {ex.Message}");
            // Fallback to loading static shader
            LoadFallbackSplatShader();
        }
    }

    private void LoadFallbackSplatShader()
    {
        try
        {
            _splatGenShader = ShaderHelper.LoadEngineShader(SplatGenShaderPath, EShaderType.Compute);
            _splatGenProgram = new XRRenderProgram(true, false, _splatGenShader);
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning($"Failed to load fallback terrain splat shader: {ex.Message}");
        }
    }

    private void CleanupShaders()
    {
        _lodProgram?.Destroy();
        _cullingProgram?.Destroy();
        _normalsProgram?.Destroy();
        _splatGenProgram?.Destroy();
        _heightGenProgram?.Destroy();

        _lodProgram = null;
        _cullingProgram = null;
        _normalsProgram = null;
        _splatGenProgram = null;
        _heightGenProgram = null;

        _lodShader = null;
        _cullingShader = null;
        _normalsShader = null;
        _splatGenShader = null;
        _heightGenShader = null;
    }

    #endregion

    #region Texture Management

    private void InitializeTextures()
    {
        uint res = HeightmapResolution;

        // Create heightmap texture (R32F for precision)
        _heightmapTexture = XRTexture2D.CreateFrameBufferTexture(
            res, res,
            EPixelInternalFormat.R32f,
            EPixelFormat.Red,
            EPixelType.Float,
            EFrameBufferAttachment.ColorAttachment0);
        _heightmapTexture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        _heightmapTexture.MagFilter = ETexMagFilter.Linear;
        _heightmapTexture.UWrap = ETexWrapMode.ClampToEdge;
        _heightmapTexture.VWrap = ETexWrapMode.ClampToEdge;
        _heightmapTexture.Name = "TerrainHeightmap";
        _heightmapTexture.SamplerName = "uHeightmap";

        // Create normalmap texture (RGB16F for normals)
        _normalmapTexture = XRTexture2D.CreateFrameBufferTexture(
            res, res,
            EPixelInternalFormat.Rgb16f,
            EPixelFormat.Rgb,
            EPixelType.Float,
            EFrameBufferAttachment.ColorAttachment0);
        _normalmapTexture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        _normalmapTexture.MagFilter = ETexMagFilter.Linear;
        _normalmapTexture.UWrap = ETexWrapMode.ClampToEdge;
        _normalmapTexture.VWrap = ETexWrapMode.ClampToEdge;
        _normalmapTexture.Name = "TerrainNormalmap";
        _normalmapTexture.SamplerName = "uNormalmap";

        // Create splatmap texture (RGBA8 for up to 4 layers per channel)
        _splatmapTexture = XRTexture2D.CreateFrameBufferTexture(
            res, res,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        _splatmapTexture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        _splatmapTexture.MagFilter = ETexMagFilter.Linear;
        _splatmapTexture.UWrap = ETexWrapMode.ClampToEdge;
        _splatmapTexture.VWrap = ETexWrapMode.ClampToEdge;
        _splatmapTexture.Name = "TerrainSplatmap";
        _splatmapTexture.SamplerName = "uSplatmap";

        // Initialize heightmap data
        _heightmapData = new float[res * res];

        // Load external heightmap or generate default
        UpdateHeightmap();
    }

    private void UpdateHeightmap()
    {
        if (ExternalHeightmap is not null)
        {
            // Copy from external heightmap
            // In a real implementation, this would sample the external texture
            // For now, we'll leave it flat
        }
        else
        {
            // Generate procedural heightmap using modules
            GenerateProceduralHeightmap();
        }

        // Upload heightmap data to GPU
        UploadHeightmapData();

        // Regenerate normals
        GenerateNormals();

        // Regenerate splatmap
        GenerateSplatmap();

        // Update chunk bounds
        UpdateChunkBounds();
    }

    private void GenerateProceduralHeightmap()
    {
        if (_heightmapData is null)
            return;

        // Rebuild shaders if modules changed
        if (_modulesChanged)
            RebuildShadersFromModules();

        // Try GPU-based generation first
        if (_heightGenProgram is not null && _heightmapTexture is not null)
        {
            GenerateHeightmapGPU();
            return;
        }

        // Fallback to CPU generation
        GenerateHeightmapCPU();
    }

    private void GenerateHeightmapGPU()
    {
        if (_heightGenProgram is null || _heightmapTexture is null)
            return;

        // Set terrain params
        _heightGenProgram.Uniform("uTerrainSize", TerrainSize);
        _heightGenProgram.Uniform("uMinHeight", MinHeight);
        _heightGenProgram.Uniform("uMaxHeight", MaxHeight);
        _heightGenProgram.Uniform("uResolution", (int)HeightmapResolution);
        _heightGenProgram.Uniform("uTerrainOffset", Transform.WorldTranslation);

        // Set module uniforms via interface
        foreach (var module in _modules.OfType<ITerrainHeightModule>().Where(m => m.Enabled))
        {
            module.SetUniforms(_heightGenProgram);
        }

        // Bind heightmap texture as image for writing
        _heightGenProgram.BindImageTexture(0, _heightmapTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32F);

        // Dispatch compute shader
        uint groups = (HeightmapResolution + 7) / 8;
        _heightGenProgram.DispatchCompute(groups, groups, 1, EMemoryBarrierMask.TextureFetch);
    }

    private void GenerateHeightmapCPU()
    {
        if (_heightmapData is null)
            return;

        uint res = HeightmapResolution;
        float heightRange = MaxHeight - MinHeight;

        // Get height modules
        var heightModules = _modules.OfType<ITerrainHeightModule>().Where(m => m.Enabled).ToList();

        for (uint y = 0; y < res; y++)
        {
            for (uint x = 0; x < res; x++)
            {
                float u = x / (float)(res - 1);
                float v = y / (float)(res - 1);

                // Calculate world position
                float worldX = Transform.WorldTranslation.X + u * TerrainSize - TerrainSize * 0.5f;
                float worldZ = Transform.WorldTranslation.Z + v * TerrainSize - TerrainSize * 0.5f;

                // Accumulate height from all modules
                float height = 0.0f;

                if (heightModules.Count == 0)
                {
                    // Default: simple perlin-like noise simulation
                    height = GenerateDefaultHeight(u, v);
                }
                else
                {
                    // Use modules (simplified - real implementation would compile shader)
                    foreach (var module in heightModules)
                    {
                        if (module is ProceduralNoiseModule noiseModule)
                        {
                            height += GenerateNoiseHeight(u, v, noiseModule);
                        }
                        else
                        {
                            // Heightmap module - height comes from texture
                            height = 0.5f; // Default mid-height
                        }
                    }
                }

                // Normalize to 0-1 range
                height = Math.Clamp(height, 0.0f, 1.0f);

                _heightmapData[y * res + x] = height;
            }
        }
    }

    private float GenerateDefaultHeight(float u, float v)
    {
        // Simple multi-octave noise simulation
        float height = 0.0f;
        float amplitude = 0.5f;
        float frequency = 2.0f;

        for (int i = 0; i < 4; i++)
        {
            float nx = u * frequency;
            float ny = v * frequency;

            // Simple hash-based pseudo-noise
            float n = MathF.Sin(nx * 12.9898f + ny * 78.233f) * 43758.5453f;
            n = n - MathF.Floor(n);

            height += n * amplitude;
            amplitude *= 0.5f;
            frequency *= 2.0f;
        }

        return height;
    }

    private float GenerateNoiseHeight(float u, float v, ProceduralNoiseModule module)
    {
        float height = 0.0f;
        float amplitude = module.Amplitude;
        float frequency = module.Frequency;

        for (int i = 0; i < module.Octaves; i++)
        {
            float nx = (u + module.Offset.X) * frequency;
            float ny = (v + module.Offset.Y) * frequency;

            // Simple hash-based pseudo-noise
            float n = MathF.Sin(nx * 12.9898f + ny * 78.233f + module.Seed) * 43758.5453f;
            n = n - MathF.Floor(n);

            height += n * amplitude;
            amplitude *= module.Persistence;
            frequency *= module.Lacunarity;
        }

        return (height + 1.0f) * 0.5f; // Normalize to 0-1
    }

    private void UploadHeightmapData()
    {
        if (_heightmapTexture is null || _heightmapData is null)
            return;

        // Upload heightmap data to texture
        // In a real implementation, this would use texture upload APIs
        _heightmapTexture.AutoGenerateMipmaps = true;
    }

    private void GenerateNormals()
    {
        if (_normalsProgram is null || _heightmapTexture is null || _normalmapTexture is null)
            return;

        // Dispatch normals compute shader
        _normalsProgram.Uniform("uTerrainSize", TerrainSize);
        _normalsProgram.Uniform("uHeightScale", MaxHeight - MinHeight);
        _normalsProgram.Uniform("uResolution", (int)HeightmapResolution);

        // Bind normalmap texture as image for writing
        _normalsProgram.BindImageTexture(0, _normalmapTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGB16F);

        // Bind textures
        _normalsProgram.Sampler("uHeightmap", _heightmapTexture, 0);

        // Dispatch
        uint groups = (HeightmapResolution + 7) / 8;
        _normalsProgram.DispatchCompute(groups, groups, 1, EMemoryBarrierMask.TextureFetch);
    }

    private void GenerateSplatmap()
    {
        if (_splatGenProgram is null || _heightmapTexture is null || _splatmapTexture is null)
            return;

        // Rebuild shaders if modules changed
        if (_modulesChanged)
            RebuildShadersFromModules();

        // Set module uniforms via interface
        foreach (var module in _modules.OfType<ITerrainSplatModule>().Where(m => m.Enabled))
        {
            module.SetUniforms(_splatGenProgram);
        }

        // Bind splatmap texture as image for writing
        _splatGenProgram.BindImageTexture(0, _splatmapTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA8);

        // Bind textures
        _splatGenProgram.Sampler("uHeightmap", _heightmapTexture, 0);
        if (_normalmapTexture != null)
            _splatGenProgram.Sampler("uNormalmap", _normalmapTexture, 1);

        // Dispatch
        uint groups = (HeightmapResolution + 7) / 8;
        _splatGenProgram.DispatchCompute(groups, groups, 1, EMemoryBarrierMask.TextureFetch);
    }

    private void UpdateLayerTextures()
    {
        if (_terrainRenderer?.Material is null)
            return;

        // Update layer textures in material
        for (int i = 0; i < MaxLayers; i++)
        {
            if (i < _layers.Count)
            {
                var layer = _layers[i];
                SetTexture(_terrainRenderer.Material, 3 + i * 2, layer.DiffuseTexture);
                SetTexture(_terrainRenderer.Material, 3 + i * 2 + 1, layer.NormalTexture);
                
                SetUniform<ShaderFloat, float>(_terrainRenderer.Material, $"uLayerRoughness{i}", layer.Roughness);
                SetUniform<ShaderVector4, Vector4>(_terrainRenderer.Material, $"uLayerTint{i}",
                    new Vector4(layer.Tint.R, layer.Tint.G, layer.Tint.B, layer.Tint.A));
                SetUniform<ShaderVector2, Vector2>(_terrainRenderer.Material, $"uLayerTiling{i}", layer.Tiling);
            }
        }

        SetUniform<ShaderInt, int>(_terrainRenderer.Material, "uLayerCount", _layers.Count);
    }

    private void CleanupTextures()
    {
        _heightmapTexture?.Destroy();
        _normalmapTexture?.Destroy();
        _splatmapTexture?.Destroy();

        _heightmapTexture = null;
        _normalmapTexture = null;
        _splatmapTexture = null;
        _heightmapData = null;
    }

    #endregion

    #region Buffer Management

    private void InitializeBuffers()
    {
        if (_buffersInitialized)
            return;

        uint totalChunks = TotalChunks;

        // Initialize chunk data
        _chunkData = new GPUTerrainChunk[totalChunks];
        InitializeChunkData();

        // Create chunks buffer
        _chunksBuffer = new XRDataBuffer(
            "ChunksBuffer",
            EBufferTarget.ShaderStorageBuffer,
            totalChunks,
            EComponentType.Float,
            GPUTerrainChunk.SizeInBytes / sizeof(float),
            false,
            false);
        _chunksBuffer.SetBlockIndex(0);

        // Create visible chunks buffer (indices)
        _visibleChunksBuffer = new XRDataBuffer(
            "VisibleChunksBuffer",
            EBufferTarget.ShaderStorageBuffer,
            totalChunks,
            EComponentType.UInt,
            1,
            false,
            false);
        _visibleChunksBuffer.SetBlockIndex(1);

        // Create terrain params buffer
        _terrainParamsBuffer = new XRDataBuffer(
            "TerrainParamsBlock",
            EBufferTarget.UniformBuffer,
            1,
            EComponentType.Float,
            GPUTerrainParams.SizeInBytes / sizeof(float),
            false,
            false);
        _terrainParamsBuffer.SetBlockIndex(2);

        // Create chunk count buffer (atomic counter)
        _chunkCountBuffer = new XRDataBuffer(
            "ChunkCountBuffer",
            EBufferTarget.ShaderStorageBuffer,
            1,
            EComponentType.UInt,
            1,
            false,
            false);
        _chunkCountBuffer.SetBlockIndex(3);

        // Create indirect draw buffer
        _indirectDrawBuffer = new XRDataBuffer(
            "IndirectDrawBuffer",
            EBufferTarget.DrawIndirectBuffer,
            MaxLODLevels,
            EComponentType.UInt,
            5,
            false,
            false);
        _indirectDrawBuffer.SetBlockIndex(4);

        // Upload initial data
        UploadChunkData();

        _buffersInitialized = true;
    }

    private void InitializeChunkData()
    {
        if (_chunkData is null)
            return;

        float chunkSize = ChunkSize;
        float halfTerrain = TerrainSize * 0.5f;
        Vector3 terrainOrigin = Transform.WorldTranslation;

        for (uint z = 0; z < ChunkCount; z++)
        {
            for (uint x = 0; x < ChunkCount; x++)
            {
                uint index = z * ChunkCount + x;

                float worldX = terrainOrigin.X - halfTerrain + (x + 0.5f) * chunkSize;
                float worldZ = terrainOrigin.Z - halfTerrain + (z + 0.5f) * chunkSize;

                // Heightmap UV coordinates for this chunk
                float u0 = x / (float)ChunkCount;
                float v0 = z / (float)ChunkCount;
                float uScale = 1.0f / ChunkCount;
                float vScale = 1.0f / ChunkCount;

                _chunkData[index] = new GPUTerrainChunk
                {
                    WorldPosition = new Vector3(worldX, (MinHeight + MaxHeight) * 0.5f, worldZ),
                    Size = chunkSize,
                    HeightmapOffset = new Vector2(u0, v0),
                    HeightmapScale = new Vector2(uScale, vScale),
                    LODLevel = 0,
                    ChunkIndex = index,
                    MinHeight = MinHeight,
                    MaxHeight = MaxHeight,
                    NeighborLODs = Vector4.Zero,
                    Visible = 1,
                    MorphFactor = 0.0f,
                    Padding0 = 0,
                    Padding1 = 0
                };
            }
        }
    }

    private void UpdateChunkBounds()
    {
        if (_chunkData is null || _heightmapData is null)
            return;

        uint res = HeightmapResolution;
        uint chunkRes = res / ChunkCount;

        for (uint cz = 0; cz < ChunkCount; cz++)
        {
            for (uint cx = 0; cx < ChunkCount; cx++)
            {
                uint chunkIndex = cz * ChunkCount + cx;

                float minH = float.MaxValue;
                float maxH = float.MinValue;

                // Sample heightmap for this chunk
                uint startX = cx * chunkRes;
                uint startY = cz * chunkRes;

                for (uint y = 0; y <= chunkRes && startY + y < res; y++)
                {
                    for (uint x = 0; x <= chunkRes && startX + x < res; x++)
                    {
                        float h = _heightmapData[(startY + y) * res + (startX + x)];
                        float worldH = MinHeight + h * (MaxHeight - MinHeight);
                        minH = MathF.Min(minH, worldH);
                        maxH = MathF.Max(maxH, worldH);
                    }
                }

                _chunkData[chunkIndex].MinHeight = minH;
                _chunkData[chunkIndex].MaxHeight = maxH;
                _chunkData[chunkIndex].WorldPosition = new Vector3(
                    _chunkData[chunkIndex].WorldPosition.X,
                    (minH + maxH) * 0.5f,
                    _chunkData[chunkIndex].WorldPosition.Z);
            }
        }

        UploadChunkData();
    }

    private void UploadChunkData()
    {
        _chunksBuffer?.SetDataRaw(_chunkData);
    }

    private void CleanupBuffers()
    {
        _chunksBuffer?.Destroy();
        _visibleChunksBuffer?.Destroy();
        _chunkCountBuffer?.Destroy();
        _terrainParamsBuffer?.Destroy();
        _indirectDrawBuffer?.Destroy();

        _chunksBuffer = null;
        _visibleChunksBuffer = null;
        _chunkCountBuffer = null;
        _terrainParamsBuffer = null;
        _indirectDrawBuffer = null;

        _chunkData = null;

        _buffersInitialized = false;
    }

    #endregion

    #region LOD Mesh Generation

    private void CalculateLODDistances()
    {
        float distance = LOD0Distance;
        for (int i = 0; i < MaxLODLevels; i++)
        {
            _lodDistances[i] = distance;
            distance *= LODDistanceMultiplier;
        }
    }

    private void CreateLODMeshes()
    {
        // Create mesh for each LOD level
        // LOD 0: 32x32, LOD 1: 16x16, etc.
        int gridSize = LOD0GridSize;

        for (int lod = 0; lod < MaxLODLevels; lod++)
        {
            _lodMeshes[lod] = CreateChunkMesh(gridSize);
            gridSize = Math.Max(gridSize / 2, 1);
        }
    }

    private XRMesh CreateChunkMesh(int gridSize)
    {
        int vertexCount = (gridSize + 1) * (gridSize + 1);
        int triangleCount = gridSize * gridSize * 2;
        int indexCount = triangleCount * 3;

        var vertices = new Vertex[vertexCount];
        var indices = new List<ushort>(indexCount);

        // Generate vertices
        for (int z = 0; z <= gridSize; z++)
        {
            for (int x = 0; x <= gridSize; x++)
            {
                int i = z * (gridSize + 1) + x;

                float u = x / (float)gridSize;
                float v = z / (float)gridSize;

                // Position in local chunk space (0 to 1)
                // Actual world position and height applied in vertex shader
                var pos = new Vector3(u - 0.5f, 0, v - 0.5f);
                var vertex = new Vertex(pos);
                vertex.TextureCoordinateSets = [new Vector2(u, v)];
                vertex.Normal = Vector3.UnitY;
                
                vertices[i] = vertex;
            }
        }

        // Generate indices
        for (int z = 0; z < gridSize; z++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                int bl = z * (gridSize + 1) + x;
                int br = bl + 1;
                int tl = bl + (gridSize + 1);
                int tr = tl + 1;

                // First triangle
                indices.Add((ushort)bl);
                indices.Add((ushort)tl);
                indices.Add((ushort)br);

                // Second triangle
                indices.Add((ushort)br);
                indices.Add((ushort)tl);
                indices.Add((ushort)tr);
            }
        }

        return new XRMesh(vertices, indices);
    }

    private void CleanupMeshes()
    {
        for (int i = 0; i < MaxLODLevels; i++)
        {
            _lodMeshes[i] = null;
        }
    }

    #endregion

    #region Rendering Setup

    private void InitializeRendering()
    {
        // Create or use provided material
        var material = Material ?? CreateDefaultTerrainMaterial();

        // Create mesh renderer with LOD 0 mesh
        if (_lodMeshes[0] is not null)
        {
            _terrainRenderer = new XRMeshRenderer(_lodMeshes[0], material);

            // Add terrain textures
            if (_heightmapTexture is not null)
                SetTexture(material, 0, _heightmapTexture);
            if (_normalmapTexture is not null)
                SetTexture(material, 1, _normalmapTexture);
            if (_splatmapTexture is not null)
                SetTexture(material, 2, _splatmapTexture);

            // Add buffers
            if (_chunksBuffer is not null)
                _terrainRenderer.Buffers[_chunksBuffer.AttributeName] = _chunksBuffer;
            if (_visibleChunksBuffer is not null)
                _terrainRenderer.Buffers[_visibleChunksBuffer.AttributeName] = _visibleChunksBuffer;
        }

        // Setup render info
        _renderInfo = RenderInfo3D.New(
            this,
            (int)EDefaultRenderPass.OpaqueForward,
            RenderTerrain);

        // Calculate local bounds
        float halfSize = TerrainSize * 0.5f;
        _renderInfo.LocalCullingVolume = new AABB(
            new Vector3(-halfSize, MinHeight, -halfSize),
            new Vector3(halfSize, MaxHeight, halfSize));
        _renderInfo.CullingOffsetMatrix = Transform.WorldMatrix;
        _renderInfo.CastsShadows = CastShadows;

        RenderedObjects = [_renderInfo];

        // Update layer textures
        UpdateLayerTextures();
    }

    private XRMaterial CreateDefaultTerrainMaterial()
    {
        // Create a terrain material with vertex displacement
        var vertShader = XRShader.EngineShader(
            Path.Combine("Common", "TerrainVertex.vs"),
            EShaderType.Vertex);
        var fragShader = XRShader.EngineShader(
            Path.Combine("Common", "TerrainPBR.fs"),
            EShaderType.Fragment);

        var material = new XRMaterial(vertShader, fragShader)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward
        };

        material.RenderOptions.CullMode = ECullMode.Back;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
        material.RenderOptions.DepthTest.UpdateDepth = true;

        return material;
    }

    private void CleanupRendering()
    {
        _renderInfo = null;
        _terrainRenderer?.Destroy();
        _terrainRenderer = null;
        RenderedObjects = [];
        CleanupMeshes();
    }

    private void RenderTerrain()
    {
        if (_terrainRenderer is null || _visibleChunkCount == 0)
            return;

        // Update material uniforms
        var material = _terrainRenderer.Material;
        if (material is not null)
        {SetUniform<ShaderFloat, float>(material, "uTerrainSize", TerrainSize);
            SetUniform<ShaderFloat, float>(material, "uMinHeight", MinHeight);
            SetUniform<ShaderFloat, float>(material, "uMaxHeight", MaxHeight);
            SetUniform<ShaderInt, int>(material, "uChunkCount", (int)ChunkCount);
            SetUniform<ShaderBool, bool>(material, "uEnableTriplanar", EnableTriplanar);
            SetUniform<ShaderBool, bool>(material, "uEnableParallax", EnableParallax);
            SetUniform<ShaderFloat, float>(material, "uParallaxScale", ParallaxScale);
            SetUniform<ShaderBool, bool>(material, "uEnableMorphing", EnableMorphing);

            /*
            if (WireframeMode)
            {
                material.RenderOptions.PolygonMode = EPolygonMode.Line;
            }
            else
            {
                material.RenderOptions.PolygonMode = EPolygonMode.Fill;
            }
            */
        }

        // Render visible chunks
        _terrainRenderer.Render(null, _visibleChunkCount);
    }

    #endregion

    #region Simulation Update

    private void Update()
    {
        if (!_buffersInitialized)
            return;

        // Get current camera position
        Vector3 cameraPosition = GetCameraPosition();

        // Update terrain parameters
        UpdateTerrainParams(cameraPosition);

        // Dispatch LOD selection
        DispatchLODSelection(cameraPosition);

        // Dispatch culling
        DispatchCulling(cameraPosition);

        // Update render info
        if (_renderInfo is not null)
            _renderInfo.CullingOffsetMatrix = Transform.WorldMatrix;
    }

    private Vector3 GetCameraPosition()
    {
        // Get the active camera's world position
        // In a real implementation, this would query the rendering system
        if (World?.RootNodes != null)
        {
            foreach (var node in World.RootNodes)
            {
                var camera = node.GetComponent<CameraComponent>();
                if (camera != null)
                    return camera.Transform.WorldTranslation;
            }
        }
        return Vector3.Zero;
    }

    private void UpdateTerrainParams(Vector3 cameraPosition)
    {
        _terrainParams = new GPUTerrainParams
        {
            TerrainWorldPosition = Transform.WorldTranslation,
            TerrainWorldSize = TerrainSize,
            HeightmapSize = new Vector2(HeightmapResolution, HeightmapResolution),
            MinHeight = MinHeight,
            MaxHeight = MaxHeight,
            CameraPosition = cameraPosition,
            LOD0Distance = LOD0Distance,
            ChunkCountX = ChunkCount,
            ChunkCountZ = ChunkCount,
            TotalChunks = TotalChunks,
            MorphStartRatio = MorphStartRatio,
            LayerTilings0 = GetLayerTilings(0),
            LayerTilings1 = GetLayerTilings(4),
            ActiveLayerCount = (uint)_layers.Count,
            EnableTriplanar = EnableTriplanar ? 1u : 0u,
            EnableParallax = EnableParallax ? 1u : 0u,
            ParallaxScale = ParallaxScale
        };

        _terrainParamsBuffer?.SetDataRaw(new[] { _terrainParams });
    }

    private Vector4 GetLayerTilings(int startIndex)
    {
        return new Vector4(
            startIndex < _layers.Count ? _layers[startIndex].Tiling.X : 1.0f,
            startIndex + 1 < _layers.Count ? _layers[startIndex + 1].Tiling.X : 1.0f,
            startIndex + 2 < _layers.Count ? _layers[startIndex + 2].Tiling.X : 1.0f,
            startIndex + 3 < _layers.Count ? _layers[startIndex + 3].Tiling.X : 1.0f);
    }

    private void DispatchLODSelection(Vector3 cameraPosition)
    {
        if (_lodProgram is null || _chunksBuffer is null || _terrainParamsBuffer is null)
            return;

        // Set uniforms
        _lodProgram.Uniform("uCameraPosition", cameraPosition);
        for (int i = 0; i < MaxLODLevels; i++)
        {
            _lodProgram.Uniform($"uLODDistances[{i}]", _lodDistances[i]);
        }

        // Bind buffers
        _lodProgram.BindBuffer(_chunksBuffer, 0);
        _lodProgram.BindBuffer(_terrainParamsBuffer, 2);

        // Dispatch
        uint workgroupCount = (TotalChunks + ComputeWorkgroupSize - 1) / (uint)ComputeWorkgroupSize;
        _lodProgram.DispatchCompute(workgroupCount, 1, 1, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchCulling(Vector3 cameraPosition)
    {
        if (_cullingProgram is null || _chunksBuffer is null || _visibleChunksBuffer is null ||
            _terrainParamsBuffer is null || _chunkCountBuffer is null || _indirectDrawBuffer is null)
            return;

        // Reset visible chunk count
        _chunkCountBuffer.SetDataRaw(new uint[] { 0 });

        // Get frustum planes from camera
        var frustumPlanes = GetFrustumPlanes();

        // Set uniforms
        for (int i = 0; i < 6; i++)
        {
            _cullingProgram.Uniform($"uFrustumPlanes[{i}]", frustumPlanes[i]);
        }

        // Bind buffers
        _cullingProgram.BindBuffer(_chunksBuffer, 0);
        _cullingProgram.BindBuffer(_visibleChunksBuffer, 1);
        _cullingProgram.BindBuffer(_terrainParamsBuffer, 2);
        _cullingProgram.BindBuffer(_chunkCountBuffer, 3);
        _cullingProgram.BindBuffer(_indirectDrawBuffer, 4);

        // Dispatch
        uint workgroupCount = (TotalChunks + ComputeWorkgroupSize - 1) / (uint)ComputeWorkgroupSize;
        _cullingProgram.DispatchCompute(workgroupCount, 1, 1, EMemoryBarrierMask.ShaderStorage);

        // Read back visible chunk count (simplified - real impl would use fence)
        // For now, assume all chunks visible
        _visibleChunkCount = TotalChunks;
    }

    private Vector4[] GetFrustumPlanes()
    {
        // Get frustum from active camera
        if (World?.RootNodes != null)
        {
            foreach (var node in World.RootNodes)
            {
                var camera = node.GetComponent<CameraComponent>();
                if (camera != null && camera.Camera != null)
                {
                    var frustum = camera.Camera.WorldFrustum();
                    return
                    [
                        PlaneToVector4(frustum.Left),
                        PlaneToVector4(frustum.Right),
                        PlaneToVector4(frustum.Bottom),
                        PlaneToVector4(frustum.Top),
                        PlaneToVector4(frustum.Near),
                        PlaneToVector4(frustum.Far)
                    ];
                }
            }
        }

        // Default frustum (no culling)
        return
        [
            new Vector4(1, 0, 0, float.MaxValue),
            new Vector4(-1, 0, 0, float.MaxValue),
            new Vector4(0, 1, 0, float.MaxValue),
            new Vector4(0, -1, 0, float.MaxValue),
            new Vector4(0, 0, 1, float.MaxValue),
            new Vector4(0, 0, -1, float.MaxValue)
        ];
    }

    private static Vector4 PlaneToVector4(Plane plane)
    {
        return new Vector4(plane.Normal, plane.D);
    }

    #endregion

    #region Height Queries

    /// <summary>
    /// Gets the terrain height at the given world XZ position.
    /// </summary>
    /// <param name="worldX">World X position.</param>
    /// <param name="worldZ">World Z position.</param>
    /// <returns>Height at the position, or MinHeight if outside terrain bounds.</returns>
    public float GetHeightAt(float worldX, float worldZ)
    {
        if (_heightmapData is null)
            return MinHeight;

        Vector3 terrainOrigin = Transform.WorldTranslation;
        float halfSize = TerrainSize * 0.5f;

        // Convert to local terrain space (0-1)
        float u = (worldX - terrainOrigin.X + halfSize) / TerrainSize;
        float v = (worldZ - terrainOrigin.Z + halfSize) / TerrainSize;

        // Check bounds
        if (u < 0 || u > 1 || v < 0 || v > 1)
            return MinHeight;

        // Sample heightmap with bilinear interpolation
        float fx = u * (HeightmapResolution - 1);
        float fz = v * (HeightmapResolution - 1);

        int x0 = (int)MathF.Floor(fx);
        int z0 = (int)MathF.Floor(fz);
        int x1 = Math.Min(x0 + 1, (int)HeightmapResolution - 1);
        int z1 = Math.Min(z0 + 1, (int)HeightmapResolution - 1);

        float tx = fx - x0;
        float tz = fz - z0;

        // Bilinear interpolation
        float h00 = _heightmapData[z0 * HeightmapResolution + x0];
        float h10 = _heightmapData[z0 * HeightmapResolution + x1];
        float h01 = _heightmapData[z1 * HeightmapResolution + x0];
        float h11 = _heightmapData[z1 * HeightmapResolution + x1];

        float h0 = h00 + (h10 - h00) * tx;
        float h1 = h01 + (h11 - h01) * tx;
        float h = h0 + (h1 - h0) * tz;

        return MinHeight + h * (MaxHeight - MinHeight);
    }

    /// <summary>
    /// Gets the terrain normal at the given world XZ position.
    /// </summary>
    public Vector3 GetNormalAt(float worldX, float worldZ)
    {
        // Calculate normal from height derivatives
        float delta = TerrainSize / HeightmapResolution;

        float hL = GetHeightAt(worldX - delta, worldZ);
        float hR = GetHeightAt(worldX + delta, worldZ);
        float hD = GetHeightAt(worldX, worldZ - delta);
        float hU = GetHeightAt(worldX, worldZ + delta);

        Vector3 normal = new Vector3(hL - hR, 2.0f * delta, hD - hU);
        return Vector3.Normalize(normal);
    }

    /// <summary>
    /// Performs a raycast against the terrain.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, out float hitDistance, out Vector3 hitNormal)
    {
        hitDistance = 0;
        hitNormal = Vector3.UnitY;

        if (_heightmapData is null)
            return false;

        // Normalize direction
        direction = Vector3.Normalize(direction);

        // Step along ray and test against terrain height
        float maxDistance = TerrainSize * 2.0f;
        float step = TerrainSize / HeightmapResolution * 0.5f;

        Vector3 pos = origin;
        float totalDistance = 0;

        while (totalDistance < maxDistance)
        {
            float terrainHeight = GetHeightAt(pos.X, pos.Z);

            if (pos.Y <= terrainHeight)
            {
                // Hit - refine with binary search
                Vector3 prevPos = pos - direction * step;
                float prevHeight = GetHeightAt(prevPos.X, prevPos.Z);

                // Binary search for exact hit point
                for (int i = 0; i < 8; i++)
                {
                    Vector3 midPos = (prevPos + pos) * 0.5f;
                    float midHeight = GetHeightAt(midPos.X, midPos.Z);

                    if (midPos.Y <= midHeight)
                    {
                        pos = midPos;
                    }
                    else
                    {
                        prevPos = midPos;
                    }
                }

                hitDistance = Vector3.Distance(origin, pos);
                hitNormal = GetNormalAt(pos.X, pos.Z);
                return true;
            }

            pos += direction * step;
            totalDistance += step;
        }

        return false;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Forces a complete rebuild of the terrain.
    /// </summary>
    public void RebuildTerrain()
    {
        if (!_buffersInitialized)
            return;

        CleanupBuffers();
        CleanupTextures();
        CleanupMeshes();

        CalculateLODDistances();
        InitializeTextures();
        InitializeBuffers();
        CreateLODMeshes();

        if (_terrainRenderer is not null && _lodMeshes[0] is not null)
        {
            // Update renderer with new mesh
            _terrainRenderer.Mesh = _lodMeshes[0];
        }

        UpdateLayerTextures();
    }

    /// <summary>
    /// Modifies the heightmap at a specific location.
    /// </summary>
    /// <param name="worldX">World X position.</param>
    /// <param name="worldZ">World Z position.</param>
    /// <param name="radius">Brush radius in world units.</param>
    /// <param name="strength">Modification strength (-1 to 1).</param>
    public void ModifyHeight(float worldX, float worldZ, float radius, float strength)
    {
        if (_heightmapData is null)
            return;

        Vector3 terrainOrigin = Transform.WorldTranslation;
        float halfSize = TerrainSize * 0.5f;

        // Convert to heightmap coordinates
        float centerU = (worldX - terrainOrigin.X + halfSize) / TerrainSize;
        float centerV = (worldZ - terrainOrigin.Z + halfSize) / TerrainSize;

        int centerX = (int)(centerU * HeightmapResolution);
        int centerZ = (int)(centerV * HeightmapResolution);
        int radiusPixels = (int)(radius / TerrainSize * HeightmapResolution);

        // Modify pixels in radius
        for (int z = -radiusPixels; z <= radiusPixels; z++)
        {
            for (int x = -radiusPixels; x <= radiusPixels; x++)
            {
                int px = centerX + x;
                int pz = centerZ + z;

                if (px < 0 || px >= HeightmapResolution || pz < 0 || pz >= HeightmapResolution)
                    continue;

                float dist = MathF.Sqrt(x * x + z * z) / radiusPixels;
                if (dist > 1.0f)
                    continue;

                // Smooth falloff
                float falloff = 1.0f - dist * dist;
                float heightDelta = strength * falloff * 0.1f;

                int index = (int)(pz * HeightmapResolution + px);
                _heightmapData[index] = Math.Clamp(_heightmapData[index] + heightDelta, 0.0f, 1.0f);
            }
        }

        // Re-upload heightmap
        UploadHeightmapData();
        GenerateNormals();
        UpdateChunkBounds();
    }

    /// <summary>
    /// Sets the heightmap from external data.
    /// </summary>
    /// <param name="data">Height values normalized to 0-1 range.</param>
    /// <param name="width">Width of the height data.</param>
    /// <param name="height">Height of the height data.</param>
    public void SetHeightmapData(float[] data, uint width, uint height)
    {
        if (_heightmapData is null || width != HeightmapResolution || height != HeightmapResolution)
        {
            Debug.RenderingWarning($"Heightmap data dimensions ({width}x{height}) don't match terrain resolution ({HeightmapResolution})");
            return;
        }

        Array.Copy(data, _heightmapData, Math.Min(data.Length, _heightmapData.Length));

        UploadHeightmapData();
        GenerateNormals();
        GenerateSplatmap();
        UpdateChunkBounds();
    }

    /// <summary>
    /// Gets a copy of the current heightmap data.
    /// </summary>
    public float[]? GetHeightmapData()
    {
        if (_heightmapData is null)
            return null;

        var copy = new float[_heightmapData.Length];
        Array.Copy(_heightmapData, copy, _heightmapData.Length);
        return copy;
    }

    #endregion

    #region Transform Updates

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

        if (_renderInfo is not null)
            _renderInfo.CullingOffsetMatrix = renderMatrix;

        // Update chunk positions when terrain moves
        if (_buffersInitialized)
        {
            InitializeChunkData();
            UploadChunkData();
        }
    }

    #endregion

    private void SetTexture(XRMaterial material, int index, XRTexture? texture)
    {
        while (material.Textures.Count <= index)
            material.Textures.Add(null);
        
        material.Textures[index] = texture;
    }

    private void SetUniform<T, TVal>(XRMaterial material, string name, TVal value) where T : ShaderVar<TVal> where TVal : struct
    {
        var param = material.Parameter<T>(name);
        if (param == null)
        {
            // Create new parameter based on type
            if (typeof(T) == typeof(ShaderFloat))
                param = (T)(object)new ShaderFloat((float)(object)value, name, null);
            else if (typeof(T) == typeof(ShaderInt))
                param = (T)(object)new ShaderInt((int)(object)value, name, null);
            else if (typeof(T) == typeof(ShaderVector3))
                param = (T)(object)new ShaderVector3((Vector3)(object)value, name, null);
            else if (typeof(T) == typeof(ShaderVector4))
                param = (T)(object)new ShaderVector4((Vector4)(object)value, name, null);
            else
                throw new NotImplementedException($"Shader type {typeof(T).Name} not supported in SetUniform auto-creation yet.");

            material.Parameters = [.. material.Parameters, param];
        }
        else
        {
            param.Value = value;
        }
    }
}
