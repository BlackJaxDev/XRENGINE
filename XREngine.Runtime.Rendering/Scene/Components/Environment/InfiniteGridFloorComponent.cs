using System;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Environment;

/// <summary>
/// Renders infinite, anti-aliased reference grids on the principal coordinate planes using
/// clip-space fullscreen triangles and an adaptive multi-scale LOD shader.
/// </summary>
[Serializable]
[Category("Environment")]
[DisplayName("Infinite Reference Grid")]
[Description("Renders independently selectable infinite grids on the XZ, XY, and YZ planes.")]
public sealed class InfiniteGridFloorComponent : XRComponent, IRenderable
{
    private const int XZPlane = 0;
    private const int XYPlane = 1;
    private const int YZPlane = 2;
    private const int PlaneCount = 3;

    private readonly record struct GridBindingState(
        float GridHeight,
        float GridCellSize,
        float GridSubdivisions,
        float GridLineWidth,
        float GridLodTargetPixelSpacing,
        float GridMaxDistance,
        float GridFadeRange,
        float GridAltitudeDistanceScale,
        ColorF4 GridMinorColor,
        ColorF4 GridMajorColor,
        ColorF4 GridXAxisColor,
        ColorF4 GridYAxisColor,
        ColorF4 GridZAxisColor,
        int GridShowAxes,
        int GridPlaneMask,
        float GridBehindPlaneOpacity);

    private sealed class InfiniteGridBindingPublisher(
        InfiniteGridFloorComponent owner,
        int gridPlane) : IRenderBindingPublisher
    {
        private readonly object _sync = new();
        private GridBindingState _lastState;
        private bool _hasLastState;
        private ulong _generation = 1;

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.View;

        public ulong Generation
        {
            get
            {
                GridBindingState state = owner.CaptureBindingState();
                lock (_sync)
                {
                    if (_hasLastState && state == _lastState)
                        return _generation;

                    _lastState = state;
                    _hasLastState = true;
                    unchecked { _generation++; }
                    if (_generation == 0)
                        _generation = 1;
                    return _generation;
                }
            }
        }

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
            => owner.PublishUniforms(materialProgram, gridPlane);
    }

    private static XRShader? s_vertexShader;
    private static XRShader? s_stereoVertexShader;
    private static XRShader? s_fragmentShader;

    private readonly RenderCommandMesh3D[] _renderCommands;
    private readonly RenderInfo3D[] _renderInfos;

    private XRMesh? _mesh;
    private readonly XRMeshRenderer?[] _meshRenderers = new XRMeshRenderer?[PlaneCount];
    private readonly XRMaterial?[] _materials = new XRMaterial?[PlaneCount];

    private bool _enabled = true;
    private bool _showXZPlane = true;
    private bool _showXYPlane;
    private bool _showYZPlane;
    private float _gridHeight;
    private bool _useTransformY = true;
    private float _cellSize = 1.0f;
    private float _majorGridInterval = 10.0f;
    private float _lineWidth = 1.0f;
    private float _lodTargetPixelSpacing = 8.0f;
    private float _maxDistance = 500.0f;
    private float _fadeRange = 150.0f;
    private float _altitudeDistanceScale = 8.0f;
    private ColorF4 _minorLineColor = new(0.45f, 0.45f, 0.48f, 0.35f);
    private ColorF4 _majorLineColor = new(0.70f, 0.70f, 0.75f, 0.60f);
    private ColorF4 _xAxisColor = new(0.90f, 0.25f, 0.25f, 0.85f);
    private ColorF4 _yAxisColor = new(0.30f, 0.80f, 0.35f, 0.85f);
    private ColorF4 _zAxisColor = new(0.25f, 0.45f, 0.95f, 0.85f);
    private bool _showAxes = true;
    private float _behindPlaneOpacity;

    public InfiniteGridFloorComponent()
    {
        _renderCommands =
        [
            CreateRenderCommand("XZ"),
            CreateRenderCommand("XY"),
            CreateRenderCommand("YZ"),
        ];
        _renderInfos =
        [
            RenderInfo3D.New(this, _renderCommands[XZPlane]),
            RenderInfo3D.New(this, _renderCommands[XYPlane]),
            RenderInfo3D.New(this, _renderCommands[YZPlane]),
        ];
        RenderedObjects = _renderInfos;
        RebuildAll();
    }

    private static RenderCommandMesh3D CreateRenderCommand(string planeName)
        => new(EDefaultRenderPass.OpaqueForward)
        {
            GpuProfilingLabel = $"{nameof(InfiniteGridFloorComponent)}.{planeName}",
            ForceCpuRendering = true,
        };

    #region Properties

    [Browsable(false)]
    public RenderInfo[] RenderedObjects { get; }

    [Category("Grid")]
    [DisplayName("Enabled")]
    [Description("Controls whether the infinite grid floor is rendered.")]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetField(ref _enabled, value))
            {
                RefreshRegistration();
                UpdateVisibility();
            }
        }
    }

    [Category("Grid Planes")]
    [DisplayName("XZ Plane")]
    [Description("Renders the horizontal XZ grid at Grid Height.")]
    public bool ShowXZPlane
    {
        get => _showXZPlane;
        set => SetField(ref _showXZPlane, value);
    }

    [Category("Grid Planes")]
    [DisplayName("XY Plane")]
    [Description("Renders the vertical XY grid at world Z = 0.")]
    public bool ShowXYPlane
    {
        get => _showXYPlane;
        set => SetField(ref _showXYPlane, value);
    }

    [Category("Grid Planes")]
    [DisplayName("YZ Plane")]
    [Description("Renders the vertical YZ grid at world X = 0.")]
    public bool ShowYZPlane
    {
        get => _showYZPlane;
        set => SetField(ref _showYZPlane, value);
    }

    [Category("Grid")]
    [DisplayName("Grid Height")]
    [Description("Fixed world-space Y elevation of the grid plane when UseTransformY is disabled.")]
    public float GridHeight
    {
        get => _gridHeight;
        set => SetField(ref _gridHeight, value);
    }

    [Category("Grid")]
    [DisplayName("Use Transform Y")]
    [Description("When enabled, the grid elevation tracks the Node Transform Y position.")]
    public bool UseTransformY
    {
        get => _useTransformY;
        set => SetField(ref _useTransformY, value);
    }

    [Category("Grid")]
    [DisplayName("Cell Size")]
    [Description("Base cell size in world units.")]
    public float CellSize
    {
        get => _cellSize;
        set => SetField(ref _cellSize, MathF.Max(0.001f, value));
    }

    [Category("Grid")]
    [DisplayName("Major Grid Interval")]
    [Description("Subdivision factor for major grid lines (e.g. 10 means every 10th line is major).")]
    public float MajorGridInterval
    {
        get => _majorGridInterval;
        set => SetField(ref _majorGridInterval, MathF.Max(2.0f, value));
    }

    [Category("Grid")]
    [DisplayName("Line Width")]
    [Description("Width of grid lines in screen-space pixels.")]
    public float LineWidth
    {
        get => _lineWidth;
        set => SetField(ref _lineWidth, MathF.Max(0.1f, value));
    }

    [Category("Grid")]
    [DisplayName("LOD Target Spacing")]
    [Description("Minimum target spacing, in screen pixels, before transitioning to the next coarser grid level.")]
    public float LodTargetPixelSpacing
    {
        get => _lodTargetPixelSpacing;
        set => SetField(ref _lodTargetPixelSpacing, MathF.Max(1.0f, value));
    }

    [Category("Grid")]
    [DisplayName("Max Distance")]
    [Description("Maximum distance from the camera at which grid lines are visible.")]
    public float MaxDistance
    {
        get => _maxDistance;
        set => SetField(ref _maxDistance, MathF.Max(0.0f, value));
    }

    [Category("Grid")]
    [DisplayName("Fade Range")]
    [Description("Distance range over which the grid smoothly fades to zero opacity.")]
    public float FadeRange
    {
        get => _fadeRange;
        set => SetField(ref _fadeRange, MathF.Max(1.0f, value));
    }

    [Category("Grid")]
    [DisplayName("Altitude Distance Scale")]
    [Description("Minimum grid radius as a multiple of camera height above the grid. Set to zero to use Max Distance alone.")]
    public float AltitudeDistanceScale
    {
        get => _altitudeDistanceScale;
        set => SetField(ref _altitudeDistanceScale, MathF.Max(0.0f, value));
    }

    [Category("Grid Appearance")]
    [DisplayName("Minor Line Color")]
    [Description("Color and opacity of minor (fine) grid lines.")]
    public ColorF4 MinorLineColor
    {
        get => _minorLineColor;
        set => SetField(ref _minorLineColor, value);
    }

    [Category("Grid Appearance")]
    [DisplayName("Major Line Color")]
    [Description("Color and opacity of major grid lines.")]
    public ColorF4 MajorLineColor
    {
        get => _majorLineColor;
        set => SetField(ref _majorLineColor, value);
    }

    [Category("Grid Appearance")]
    [DisplayName("X-Axis Color")]
    [Description("Color and opacity of the X-axis highlight line (Z=0).")]
    public ColorF4 XAxisColor
    {
        get => _xAxisColor;
        set => SetField(ref _xAxisColor, value);
    }

    [Category("Grid Appearance")]
    [DisplayName("Y-Axis Color")]
    [Description("Color and opacity of Y-axis highlight lines.")]
    public ColorF4 YAxisColor
    {
        get => _yAxisColor;
        set => SetField(ref _yAxisColor, value);
    }

    [Category("Grid Appearance")]
    [DisplayName("Z-Axis Color")]
    [Description("Color and opacity of Z-axis highlight lines.")]
    public ColorF4 ZAxisColor
    {
        get => _zAxisColor;
        set => SetField(ref _zAxisColor, value);
    }

    [Category("Grid Appearance")]
    [DisplayName("Show Axes")]
    [Description("Highlights the principal axes on each enabled grid plane.")]
    public bool ShowAxes
    {
        get => _showAxes;
        set => SetField(ref _showAxes, value);
    }

    [Category("Grid Appearance")]
    [DisplayName("Behind Plane Opacity")]
    [Description("Opacity multiplier for grid fragments occluded by a nearer enabled grid plane.")]
    public float BehindPlaneOpacity
    {
        get => _behindPlaneOpacity;
        set => SetField(ref _behindPlaneOpacity, Math.Clamp(value, 0.0f, 1.0f));
    }

    #endregion

    public float ResolvedGridHeight
        => _useTransformY && Transform is not null ? Transform.RenderTranslation.Y : _gridHeight;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RebuildAll();
        RefreshRegistration();
        UpdateVisibility();
    }

    protected override void OnComponentDeactivated()
    {
        Unregister();
        UpdateVisibility();
        base.OnComponentDeactivated();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(World):
            case nameof(SceneNode):
            case nameof(IsActive):
            case nameof(Enabled):
                RefreshRegistration();
                UpdateVisibility();
                break;
            case nameof(ShowXZPlane):
            case nameof(ShowXYPlane):
            case nameof(ShowYZPlane):
                RefreshRegistration();
                UpdateVisibility();
                break;
            case nameof(CellSize):
            case nameof(MajorGridInterval):
            case nameof(LineWidth):
            case nameof(LodTargetPixelSpacing):
            case nameof(MaxDistance):
            case nameof(FadeRange):
            case nameof(AltitudeDistanceScale):
            case nameof(MinorLineColor):
            case nameof(MajorLineColor):
            case nameof(XAxisColor):
            case nameof(YAxisColor):
            case nameof(ZAxisColor):
            case nameof(ShowAxes):
            case nameof(BehindPlaneOpacity):
            case nameof(GridHeight):
            case nameof(UseTransformY):
                // Handled dynamically via uniform publishing
                break;
        }
    }

    private void RefreshRegistration()
    {
        var world = World.GetRenderRegistrationTarget();
        bool shouldRegister = world is not null && IsActiveInHierarchy && _enabled;

        if (shouldRegister)
        {
            for (int plane = 0; plane < PlaneCount; plane++)
            {
                var registrationTarget = IsPlaneEnabled(plane) ? world : null;
                if (!ReferenceEquals(_renderInfos[plane].WorldInstance, registrationTarget))
                    _renderInfos[plane].WorldInstance = registrationTarget;
            }
        }
        else
        {
            Unregister();
        }
    }

    private void Unregister()
    {
        for (int plane = 0; plane < PlaneCount; plane++)
            _renderInfos[plane].WorldInstance = null;
    }

    private void UpdateVisibility()
    {
        bool componentVisible = _enabled && IsActiveInHierarchy;
        for (int plane = 0; plane < PlaneCount; plane++)
            _renderInfos[plane].IsVisible = componentVisible && IsPlaneEnabled(plane);
    }

    private bool IsPlaneEnabled(int plane)
        => plane switch
        {
            XZPlane => _showXZPlane,
            XYPlane => _showXYPlane,
            YZPlane => _showYZPlane,
            _ => false,
        };

    private int EnabledPlaneMask
        => (_showXZPlane ? 1 << XZPlane : 0) |
           (_showXYPlane ? 1 << XYPlane : 0) |
           (_showYZPlane ? 1 << YZPlane : 0);

    private void RebuildAll()
    {
        RebuildMesh();
        RebuildMaterials();
        UpdateRenderCommands();
    }

    private void RebuildMesh()
    {
        _mesh?.Destroy();

        // Fullscreen oversized triangle covering NDC [-1, 1] clip space
        VertexTriangle triangle = new(
            new Vertex(new Vector3(-1, -1, 0)),
            new Vertex(new Vector3(3, -1, 0)),
            new Vertex(new Vector3(-1, 3, 0)));

        _mesh = XRMesh.Create(triangle);
        _mesh.Name = "InfiniteGrid.FullscreenTriangle";

        for (int plane = 0; plane < PlaneCount; plane++)
        {
            if (_meshRenderers[plane] is not null)
                _meshRenderers[plane]!.Mesh = _mesh;
        }
    }

    private void RebuildMaterials()
    {
        DetachMaterialUniformHandlers();

        XRShader vertexShader = GetVertexShader();
        XRShader stereoVertexShader = GetStereoVertexShader();
        XRShader fragmentShader = GetFragmentShader();

        for (int plane = 0; plane < PlaneCount; plane++)
        {
            RenderingParameters renderParams = new()
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Enabled,
                    UpdateDepth = false,
                    Function = EComparison.Lequal,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ClipSpacePolicy,
                ExcludeFromGpuIndirect = true,
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
            };

            string planeName = GetPlaneName(plane);
            XRMaterial material = new([vertexShader, stereoVertexShader, fragmentShader])
            {
                Name = $"InfiniteGrid.{planeName}.Material",
                RenderPass = (int)EDefaultRenderPass.OpaqueForward,
                RenderOptions = renderParams,
            };
            material.BindingPublishers.Add(new InfiniteGridBindingPublisher(this, plane));
            _materials[plane] = material;

            if (_meshRenderers[plane] is null)
                _meshRenderers[plane] = new XRMeshRenderer(_mesh, material);
            else
                _meshRenderers[plane]!.Material = material;

            _meshRenderers[plane]!.Name = $"InfiniteGrid.{planeName}.Renderer";
        }

        _materials[XZPlane]!.SettingUniforms += OnSettingXZUniforms;
        _materials[XYPlane]!.SettingUniforms += OnSettingXYUniforms;
        _materials[YZPlane]!.SettingUniforms += OnSettingYZUniforms;

        UpdateRenderCommands();
    }

    private static string GetPlaneName(int plane)
        => plane switch
        {
            XZPlane => "XZ",
            XYPlane => "XY",
            YZPlane => "YZ",
            _ => "Unknown",
        };

    private void OnSettingXZUniforms(XRMaterialBase material, XRRenderProgram program)
        => PublishUniforms(program, XZPlane);

    private void OnSettingXYUniforms(XRMaterialBase material, XRRenderProgram program)
        => PublishUniforms(program, XYPlane);

    private void OnSettingYZUniforms(XRMaterialBase material, XRRenderProgram program)
        => PublishUniforms(program, YZPlane);

    private void UpdateRenderCommands()
    {
        for (int plane = 0; plane < PlaneCount; plane++)
        {
            if (_meshRenderers[plane] is null)
                continue;

            _renderCommands[plane].Mesh = _meshRenderers[plane];
            _renderCommands[plane].WorldMatrix = Matrix4x4.Identity;
            _renderCommands[plane].RenderPass = _materials[plane]?.RenderPass ?? (int)EDefaultRenderPass.OpaqueForward;
            _renderCommands[plane].ForceCpuRendering = true;
        }
    }

    private void CleanupResources()
    {
        DetachMaterialUniformHandlers();
        _mesh?.Destroy();
        _mesh = null;
        for (int plane = 0; plane < PlaneCount; plane++)
        {
            _materials[plane] = null;
            _meshRenderers[plane] = null;
        }
    }

    private void DetachMaterialUniformHandlers()
    {
        if (_materials[XZPlane] is not null)
            _materials[XZPlane]!.SettingUniforms -= OnSettingXZUniforms;
        if (_materials[XYPlane] is not null)
            _materials[XYPlane]!.SettingUniforms -= OnSettingXYUniforms;
        if (_materials[YZPlane] is not null)
            _materials[YZPlane]!.SettingUniforms -= OnSettingYZUniforms;
    }

    private GridBindingState CaptureBindingState()
        => new(
            ResolvedGridHeight,
            _cellSize,
            _majorGridInterval,
            _lineWidth,
            _lodTargetPixelSpacing,
            _maxDistance,
            _fadeRange,
            _altitudeDistanceScale,
            _minorLineColor,
            _majorLineColor,
            _xAxisColor,
            _yAxisColor,
            _zAxisColor,
            _showAxes ? 1 : 0,
            EnabledPlaneMask,
            _behindPlaneOpacity);

    public void PublishUniforms(XRRenderProgram program)
        => PublishUniforms(program, XZPlane);

    private void PublishUniforms(XRRenderProgram program, int gridPlane)
    {
        program.Uniform("GridPlane", gridPlane);
        program.Uniform("GridHeight", ResolvedGridHeight);
        program.Uniform("GridCellSize", _cellSize);
        program.Uniform("GridSubdivisions", _majorGridInterval);
        program.Uniform("GridLineWidth", _lineWidth);
        program.Uniform("GridLodTargetPixelSpacing", _lodTargetPixelSpacing);
        program.Uniform("GridMaxDistance", _maxDistance);
        program.Uniform("GridFadeRange", _fadeRange);
        program.Uniform("GridAltitudeDistanceScale", _altitudeDistanceScale);
        program.Uniform("GridMinorColor", (Vector4)_minorLineColor);
        program.Uniform("GridMajorColor", (Vector4)_majorLineColor);
        program.Uniform("GridXAxisColor", (Vector4)_xAxisColor);
        program.Uniform("GridYAxisColor", (Vector4)_yAxisColor);
        program.Uniform("GridZAxisColor", (Vector4)_zAxisColor);
        program.Uniform("GridShowAxes", _showAxes ? 1 : 0);
        program.Uniform("GridPlaneMask", EnabledPlaneMask);
        program.Uniform("GridBehindPlaneOpacity", _behindPlaneOpacity);
    }

    private static XRShader GetVertexShader()
    {
        if (s_vertexShader is not null)
            return s_vertexShader;

        s_vertexShader = RuntimeEngine.Assets.LoadEngineAsset<XRShader>(
            JobPriority.Highest,
            "Shaders", "Scene3D", "InfiniteGrid.vs");

        s_vertexShader ??= new XRShader(EShaderType.Vertex, VertexShaderSource);
        return s_vertexShader;
    }

    private static XRShader GetStereoVertexShader()
    {
        if (s_stereoVertexShader is not null)
            return s_stereoVertexShader;

        s_stereoVertexShader = RuntimeEngine.Assets.LoadEngineAsset<XRShader>(
            JobPriority.Highest,
            "Shaders", "Scene3D", "InfiniteGridStereo.vs");

        s_stereoVertexShader ??= new XRShader(EShaderType.Vertex, StereoVertexShaderSource);
        return s_stereoVertexShader;
    }

    private static XRShader GetFragmentShader()
    {
        if (s_fragmentShader is not null)
            return s_fragmentShader;

        s_fragmentShader = RuntimeEngine.Assets.LoadEngineAsset<XRShader>(
            JobPriority.Highest,
            "Shaders", "Scene3D", "InfiniteGrid.fs");

        s_fragmentShader ??= new XRShader(EShaderType.Fragment, FragmentShaderSource);
        return s_fragmentShader;
    }

    #region Shader Sources

    public const string VertexShaderSource = """
#version 450

layout(location = 0) in vec3 Position;

layout(location = 0) out vec3 NearWorldPos;
layout(location = 1) out vec3 FarWorldPos;
layout(location = 2) out vec2 FragClipXY;

uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform int DepthMode;
uniform int ClipDepthRange;

vec3 Unproject(vec2 clipXY, float clipZ, mat4 invView, mat4 invProj)
{
    vec4 viewPos = invProj * vec4(clipXY, clipZ, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    return (invView * (viewPos * invW)).xyz;
}

float GetNearClipZ()
{
    if (DepthMode == 1) // Reverse-Z
        return 1.0;
    return ClipDepthRange == 1 ? -1.0 : 0.0;
}

float GetFarClipZ()
{
    if (DepthMode == 1) // Reverse-Z
        return ClipDepthRange == 1 ? -1.0 : 0.0;
    return 1.0;
}

void main()
{
    vec2 clipXY = Position.xy;
    FragClipXY = clipXY;

    NearWorldPos = Unproject(clipXY, GetNearClipZ(), InverseViewMatrix, InverseProjMatrix);
    FarWorldPos = Unproject(clipXY, GetFarClipZ(), InverseViewMatrix, InverseProjMatrix);

    gl_Position = vec4(clipXY, GetNearClipZ(), 1.0);
}
""";

    public const string StereoVertexShaderSource = """
#version 450
#extension GL_OVR_multiview2 : require

layout(num_views = 2) in;

layout(location = 0) in vec3 Position;

layout(location = 0) out vec3 NearWorldPos;
layout(location = 1) out vec3 FarWorldPos;
layout(location = 2) out vec2 FragClipXY;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform int DepthMode;
uniform int ClipDepthRange;

mat4 GetInverseViewMatrix()
{
    return gl_ViewID_OVR == 0 ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
}

mat4 GetInverseProjMatrix()
{
    return gl_ViewID_OVR == 0 ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
}

vec3 Unproject(vec2 clipXY, float clipZ)
{
    vec4 viewPos = GetInverseProjMatrix() * vec4(clipXY, clipZ, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    return (GetInverseViewMatrix() * (viewPos * invW)).xyz;
}

float GetNearClipZ()
{
    if (DepthMode == 1) // Reverse-Z
        return 1.0;
    return ClipDepthRange == 1 ? -1.0 : 0.0;
}

float GetFarClipZ()
{
    if (DepthMode == 1) // Reverse-Z
        return ClipDepthRange == 1 ? -1.0 : 0.0;
    return 1.0;
}

void main()
{
    vec2 clipXY = Position.xy;
    FragClipXY = clipXY;

    NearWorldPos = Unproject(clipXY, GetNearClipZ());
    FarWorldPos = Unproject(clipXY, GetFarClipZ());

    gl_Position = vec4(clipXY, GetNearClipZ(), 1.0);
}
""";

    public const string FragmentShaderSource = """
#version 450

layout(location = 0) in vec3 NearWorldPos;
layout(location = 1) in vec3 FarWorldPos;
layout(location = 2) in vec2 FragClipXY;

layout(location = 0) out vec4 OutColor;

uniform mat4 ViewProjectionMatrix;
uniform mat4 InverseViewMatrix;
uniform vec3 CameraPosition;
uniform int DepthMode;
uniform int ClipDepthRange;

// Grid parameters
uniform int GridPlane = 0;
uniform float GridHeight = 0.0;
uniform float GridCellSize = 1.0;
uniform float GridSubdivisions = 10.0;
uniform float GridLineWidth = 1.0;
uniform float GridLodTargetPixelSpacing = 8.0;
uniform float GridMaxDistance = 500.0;
uniform float GridFadeRange = 150.0;
uniform float GridAltitudeDistanceScale = 8.0;
uniform vec4 GridMinorColor = vec4(0.45, 0.45, 0.48, 0.35);
uniform vec4 GridMajorColor = vec4(0.70, 0.70, 0.75, 0.60);
uniform vec4 GridXAxisColor = vec4(0.90, 0.25, 0.25, 0.85);
uniform vec4 GridYAxisColor = vec4(0.30, 0.80, 0.35, 0.85);
uniform vec4 GridZAxisColor = vec4(0.25, 0.45, 0.95, 0.85);
uniform int GridShowAxes = 1;
uniform int GridPlaneMask = 1;
uniform float GridBehindPlaneOpacity = 0.0;

float PristineGrid(vec2 coord, vec2 dxy, float cellSize, float lineWidth)
{
    vec2 grid = abs(fract(coord / cellSize - 0.5) - 0.5) * cellSize;
    vec2 antiAlias = max(dxy, vec2(1e-7));
    vec2 line = clamp((lineWidth * antiAlias * 0.5 - grid) / antiAlias + 0.5, 0.0, 1.0);
    return max(line.x, line.y);
}

float SmootherStep(float value)
{
    value = clamp(value, 0.0, 1.0);
    return value * value * value * (value * (value * 6.0 - 15.0) + 10.0);
}

float IntersectGridPlane(int plane, vec3 rayOrigin, vec3 rayDir)
{
    float denominator;
    float origin;
    float offset;

    if (plane == 1) // XY
    {
        denominator = rayDir.z;
        origin = rayOrigin.z;
        offset = 0.0;
    }
    else if (plane == 2) // YZ
    {
        denominator = rayDir.x;
        origin = rayOrigin.x;
        offset = 0.0;
    }
    else // XZ
    {
        denominator = rayDir.y;
        origin = rayOrigin.y;
        offset = GridHeight;
    }

    if (abs(denominator) < 1e-6)
        return 1e30;

    float intersection = (offset - origin) / denominator;
    return intersection > 0.0 ? intersection : 1e30;
}

float FindNearestOtherGridPlane(int currentPlane, vec3 rayOrigin, vec3 rayDir)
{
    float nearest = 1e30;
    for (int plane = 0; plane < 3; ++plane)
    {
        if (plane == currentPlane || (GridPlaneMask & (1 << plane)) == 0)
            continue;

        nearest = min(nearest, IntersectGridPlane(plane, rayOrigin, rayDir));
    }
    return nearest;
}

void main()
{
    vec3 rayOrigin = NearWorldPos;
    vec3 rayDir = FarWorldPos - NearWorldPos;
    vec3 cameraPosition = InverseViewMatrix[3].xyz;

    float rayPlaneAxis;
    float rayOriginPlaneAxis;
    float planeOffset;
    float cameraHeight;

    if (GridPlane == 1) // XY
    {
        rayPlaneAxis = rayDir.z;
        rayOriginPlaneAxis = rayOrigin.z;
        planeOffset = 0.0;
        cameraHeight = abs(cameraPosition.z);
    }
    else if (GridPlane == 2) // YZ
    {
        rayPlaneAxis = rayDir.x;
        rayOriginPlaneAxis = rayOrigin.x;
        planeOffset = 0.0;
        cameraHeight = abs(cameraPosition.x);
    }
    else // XZ
    {
        rayPlaneAxis = rayDir.y;
        rayOriginPlaneAxis = rayOrigin.y;
        planeOffset = GridHeight;
        cameraHeight = abs(cameraPosition.y - GridHeight);
    }

    if (abs(rayPlaneAxis) < 1e-6)
        discard;

    float t = (planeOffset - rayOriginPlaneAxis) / rayPlaneAxis;
    if (t <= 0.0)
        discard;

    float nearestOtherPlaneT = FindNearestOtherGridPlane(GridPlane, rayOrigin, rayDir);
    vec3 hitPos = rayOrigin + t * rayDir;
    vec2 coord;
    vec2 cameraCoord;
    vec4 firstAxisColor;
    vec4 secondAxisColor;

    if (GridPlane == 1) // XY
    {
        coord = hitPos.xy;
        cameraCoord = cameraPosition.xy;
        firstAxisColor = GridXAxisColor;
        secondAxisColor = GridYAxisColor;
    }
    else if (GridPlane == 2) // YZ
    {
        coord = hitPos.yz;
        cameraCoord = cameraPosition.yz;
        firstAxisColor = GridYAxisColor;
        secondAxisColor = GridZAxisColor;
    }
    else // XZ
    {
        coord = hitPos.xz;
        cameraCoord = cameraPosition.xz;
        firstAxisColor = GridXAxisColor;
        secondAxisColor = GridZAxisColor;
    }

    // Depth computation
    vec4 clipHit = ViewProjectionMatrix * vec4(hitPos, 1.0);
    if (clipHit.w <= 0.0)
        discard;

    float ndcZ = clipHit.z / clipHit.w;
    float depth = ClipDepthRange == 1 ? ndcZ * 0.5 + 0.5 : ndcZ;
    if (depth < 0.0 || depth > 1.0)
        discard;

    gl_FragDepth = DepthMode == 1 ? (1.0 - depth) : depth;

    // Multi-scale grid calculation
    vec2 dxy = fwidth(coord);
    float pixelFootprint = max(dxy.x, dxy.y);

    float baseCell = max(GridCellSize, 1e-4);
    float lodScale = max(GridSubdivisions, 2.0);
    float targetSpacing = max(GridLodTargetPixelSpacing, 1.0);
    float lodLevel = log(max(pixelFootprint * targetSpacing / baseCell, 1.0)) / log(lodScale);
    float lodFloor = floor(lodLevel);
    float lodFraction = SmootherStep(lodLevel - lodFloor);

    float scale0 = baseCell * pow(lodScale, lodFloor);
    float scale1 = scale0 * lodScale;
    float scale2 = scale1 * lodScale;

    float g0 = PristineGrid(coord, dxy, scale0, GridLineWidth);
    float g1 = PristineGrid(coord, dxy, scale1, GridLineWidth);
    float g2 = PristineGrid(coord, dxy, scale2, GridLineWidth);

    float fineAlpha = g0 * (1.0 - lodFraction) * GridMinorColor.a;
    vec4 middleColor = mix(GridMajorColor, GridMinorColor, lodFraction);
    float middleAlpha = g1 * middleColor.a;
    float coarseAlpha = g2 * lodFraction * GridMajorColor.a;

    float alphaSum = fineAlpha + middleAlpha + coarseAlpha;
    vec3 gridRgb = alphaSum > 1e-6
        ? (GridMinorColor.rgb * fineAlpha + middleColor.rgb * middleAlpha + GridMajorColor.rgb * coarseAlpha) / alphaSum
        : GridMajorColor.rgb;
    float gridAlpha = min(alphaSum, 1.0);

    if (GridShowAxes == 1)
    {
        float firstAxisMask = clamp((GridLineWidth * 1.5 * dxy.y * 0.5 - abs(coord.y)) / max(dxy.y, 1e-7) + 0.5, 0.0, 1.0);
        float secondAxisMask = clamp((GridLineWidth * 1.5 * dxy.x * 0.5 - abs(coord.x)) / max(dxy.x, 1e-7) + 0.5, 0.0, 1.0);

        if (firstAxisMask > 0.0)
        {
            gridRgb = mix(gridRgb, firstAxisColor.rgb, firstAxisMask);
            gridAlpha = max(gridAlpha, firstAxisMask * firstAxisColor.a);
        }
        if (secondAxisMask > 0.0)
        {
            gridRgb = mix(gridRgb, secondAxisColor.rgb, secondAxisMask);
            gridAlpha = max(gridAlpha, secondAxisMask * secondAxisColor.a);
        }
    }

    float planeVisibility = 1.0;
    if (nearestOtherPlaneT < t)
    {
        float worldSeparation = (t - nearestOtherPlaneT) * length(rayDir);
        float transitionWidth = max(baseCell, pixelFootprint * 4.0);
        float behindAmount = SmootherStep(worldSeparation / transitionWidth);
        planeVisibility = mix(1.0, clamp(GridBehindPlaneOpacity, 0.0, 1.0), behindAmount);
    }

    // Distance fade within the selected plane.
    float dist = length(coord - cameraCoord);
    float distanceFade = 1.0;
    if (GridMaxDistance > 0.0)
    {
        float effectiveMaxDistance = max(GridMaxDistance, cameraHeight * GridAltitudeDistanceScale);
        float effectiveFadeRange = max(GridFadeRange, effectiveMaxDistance * 0.2);
        float startFade = max(0.0, effectiveMaxDistance - effectiveFadeRange);
        distanceFade = SmootherStep(1.0 - (dist - startFade) / max(effectiveFadeRange, 1e-5));
    }

    float finalAlpha = gridAlpha * distanceFade * planeVisibility;
    if (finalAlpha < 0.001)
        discard;

    OutColor = vec4(gridRgb, finalAlpha);
}
""";

    #endregion
}
