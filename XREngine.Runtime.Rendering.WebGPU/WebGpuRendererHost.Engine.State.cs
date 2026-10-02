using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private WebGpuRasterState _rasterState = WebGpuRasterState.Default;
    private WebGpuFrameBuffer? _boundEngineFrameBuffer;
    internal WebGpuRasterState RasterState => _rasterState;

    /// <summary>Preserves authored face coverage while an auxiliary shader owns the remaining raster state.</summary>
    internal void ApplyMeshFaceCoverage(RenderingParameters parameters)
        => SetField(ref _rasterState, _rasterState with
        {
            CullMode = parameters.CullMode,
            Winding = parameters.Winding,
        }, publishNotifications: false);

    internal WebGpuFrameBuffer? GetBoundEngineFrameBuffer()
    {
        _boundEngineFrameBuffer?.EnsureCurrent();
        return _boundEngineFrameBuffer;
    }

    public override void EnableDepthTest(bool enable)
        => SetField(ref _rasterState, _rasterState with { DepthEnabled = enable }, publishNotifications: false);
    public override void AllowDepthWrite(bool allow)
        => SetField(ref _rasterState, _rasterState with { DepthWrite = allow }, publishNotifications: false);
    public override void DepthFunc(EComparison function)
        => SetField(ref _rasterState, _rasterState with { DepthComparison = function }, publishNotifications: false);
    public override void EnableBlend(bool enable)
        => SetField(ref _rasterState, _rasterState with { BlendEnabled = enable }, publishNotifications: false);
    public override void BlendFunc(EBlendingFactor source, EBlendingFactor destination)
        => BlendFuncSeparate(source, destination, source, destination);
    public override void BlendFuncSeparate(EBlendingFactor srcRGB, EBlendingFactor dstRGB, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha)
        => SetField(ref _rasterState, _rasterState with
        {
            SourceRgb = srcRGB, DestinationRgb = dstRGB,
            SourceAlpha = srcAlpha, DestinationAlpha = dstAlpha,
        }, publishNotifications: false);
    public override void BlendEquation(EBlendEquationMode mode) => BlendEquationSeparate(mode, mode);
    public override void BlendEquationSeparate(EBlendEquationMode modeRGB, EBlendEquationMode modeAlpha)
        => SetField(ref _rasterState, _rasterState with { RgbEquation = modeRGB, AlphaEquation = modeAlpha }, publishNotifications: false);
    public override void ColorMask(bool red, bool green, bool blue, bool alpha)
        => SetField(ref _rasterState, _rasterState with
        {
            ColorWriteMask = (red ? 1 : 0) | (green ? 2 : 0) | (blue ? 4 : 0) | (alpha ? 8 : 0),
        }, publishNotifications: false);

    public override void ApplyRenderParameters(RenderingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.StencilTest.IsEnabled || parameters.AlphaToCoverage == ERenderParamUsage.Enabled ||
            parameters.BlendModesPerDrawBuffer is { Count: > 0 })
            throw UnsupportedEngineOperation(nameof(ApplyRenderParameters), "stencil, multisample coverage and per-target blending are not admitted by the canvas profile");
        WebGpuRasterState state = _rasterState with
        {
            CullMode = parameters.CullMode,
            Winding = parameters.Winding,
            ColorWriteMask = (parameters.WriteRed ? 1 : 0) | (parameters.WriteGreen ? 2 : 0) |
                (parameters.WriteBlue ? 4 : 0) | (parameters.WriteAlpha ? 8 : 0),
        };
        if (!parameters.DepthTest.IsUnchanged)
            state = state with
            {
                DepthEnabled = parameters.DepthTest.IsEnabled,
                DepthWrite = parameters.DepthTest.UpdateDepth,
                DepthComparison = parameters.DepthTest.Function,
            };
        if (parameters.BlendModeAllDrawBuffers is { IsUnchanged: false } blend)
            state = state with
            {
                BlendEnabled = blend.IsEnabled, SourceRgb = blend.RgbSrcFactor,
                DestinationRgb = blend.RgbDstFactor, SourceAlpha = blend.AlphaSrcFactor,
                DestinationAlpha = blend.AlphaDstFactor, RgbEquation = blend.RgbEquation,
                AlphaEquation = blend.AlphaEquation,
            };
        SetField(ref _rasterState, state, publishNotifications: false);
    }

    public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        api.SetMatrix("ViewProjection", camera.ViewProjectionMatrix);
        api.SetVector4("CameraPosition", new Vector4(camera.Transform.RenderMatrix.Translation, 1));
        api.SetMatrix("InverseViewMatrix", camera.Transform.RenderMatrix);
        api.SetMatrix("InverseProjMatrix", camera.InverseProjectionMatrix);
    }

    public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (material.Textures.Count > 32)
            throw UnsupportedEngineOperation(nameof(SetMaterialUniforms), "the material exceeds the bounded 32-texture publication profile");
        for (int i = 0; i < material.Textures.Count; i++)
            material.Textures[i]?.SampleIn(program, i);
        for (int i = 0; i < material.Parameters.Length; i++)
            material.Parameters[i]?.SetUniform(program, forceUpdate: true);
        material.OnSettingUniforms(program);
    }
}
