using System.Numerics;
using XREngine.Data.Core;
using static XREngine.Rendering.XRShader;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Creates owned raster companions and publishes output-space uniforms through the shared pipeline.</summary>
public static class WebPipelineRasterProgram
{
    public static bool IsActive => OperatingSystem.IsBrowser() || AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;

    /// <summary>Uses the raster camera's captured projection and depth convention for fullscreen consumers.</summary>
    public static RenderFrameViewSelection CaptureView(XRCamera camera, Vector2 viewportSize)
        => RenderFrameViewSetCapture.SelectForDraw(
            RuntimeRenderingHostServices.FrameTiming.ActiveRenderCommandExecutionState, camera,
            RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection == true,
            viewportSize, RuntimeRenderingHostServices.FrameTiming.ElapsedTime, requireCapturedView: false);

    public static XRShader[] CreateShaders(RenderPipeline pipeline, string bindingKey)
    {
        ShaderProgramArtifact artifact = pipeline.GetRequiredWebPipelineArtifact(bindingKey);
        if (!WebPipelineArtifactCatalog.IsCompleteRasterProgram(artifact))
            throw new NotSupportedException($"WebGPU.Pipeline.ProgramShape: '{bindingKey}' requires a complete raster program.");
        ValidateDepthConvention(artifact);
        XRShader vertex = new(EShaderType.Vertex) { CookedArtifact = artifact };
        try { return [vertex, new XRShader(EShaderType.Fragment) { CookedArtifact = artifact }]; }
        catch { vertex.Destroy(true); throw; }
    }

    /// <summary>Rejects stale built-in depth consumers before their unchanged-size uniform buffers are bound.</summary>
    public static void ValidateDepthConvention(ShaderProgramArtifact artifact)
    {
        if (artifact.Name != "engine-advanced-depth-of-field") return;
        if (artifact.SemanticSchemaIdentity == "xrengine.engine.depth-of-field.v2")
            foreach (ShaderStageResourceLayout resource in artifact.Resources)
                if (resource.Contract.Set == 0 && resource.Contract.Binding == 0 &&
                    resource.Contract.Name == "OutputUniforms" && resource.BindingType == "uniform" &&
                    resource.Contract.ByteSize == 80 && resource.DynamicOffset &&
                    resource.Visibility == ShaderStageVisibility.Fragment)
                    foreach (ShaderAbiMemberContract member in resource.Contract.Members)
                        if (member.ProviderName == "DepthMode" && member.Offset == 76 && member.Size == 4 &&
                            member.PhysicalType == "i32" && member.ArrayCount == 0)
                            return;
        throw new NotSupportedException("WebGPU.Pipeline.DepthOfFieldContractMismatch: recook depth of field with the captured camera depth convention.");
    }

    /// <summary>Captures the current privately created material and stages, independently of later quad assignments.</summary>
    public static void OwnMaterial(XRQuadFrameBuffer quad)
    {
        XRMaterial material = quad.Material
            ?? throw new InvalidOperationException("WebGPU.Pipeline.MaterialMissing: owned raster output requires its material.");
        _ = new MaterialOwnership(quad, material);
    }

    private sealed class MaterialOwnership
    {
        private readonly XRQuadFrameBuffer _quad;
        private readonly XRMaterial _material;
        private readonly XRShader[] _shaders;

        internal MaterialOwnership(XRQuadFrameBuffer quad, XRMaterial material)
        {
            _quad = quad;
            _material = material;
            _shaders = [.. material.Shaders];
            quad.Destroyed += Destroy;
        }

        private void Destroy(XRObjectBase value)
        {
            _quad.Destroyed -= Destroy;
            _material.Destroy(true);
            foreach (XRShader shader in _shaders) shader.Destroy(true);
        }
    }

    public static void PublishRenderArea(XRRenderProgram program)
    {
        XRRenderPipelineInstance instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline
            ?? throw new InvalidOperationException("WebGPU.Pipeline.RenderAreaMissing: output uniforms require an active pipeline.");
        var area = instance.RenderState.CurrentRenderRegion;
        program.Uniform("ScreenWidth", (float)Math.Max(1, area.Width));
        program.Uniform("ScreenHeight", (float)Math.Max(1, area.Height));
        program.Uniform("ScreenOrigin", new Vector2(area.X, area.Y));
    }
}
