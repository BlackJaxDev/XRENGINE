using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Renders surfel debug visualization (circles or grid heatmap) using the buffers from VPRC_SurfelGIPass.
/// </summary>
public class VPRC_SurfelDebugVisualization : ViewportRenderCommand
{
    private const uint CulledCommandFloats = 48u;

    public enum EVisualizationMode
    {
        /// <summary>
        /// Draw surfels as colored circles on the scene geometry.
        /// </summary>
        SurfelCircles,

        /// <summary>
        /// Draw grid cell occupancy as a heatmap.
        /// </summary>
        GridHeatmap,
    }

    private XRRenderProgram? _debugProgram;

    public EVisualizationMode Mode { get; set; } = EVisualizationMode.SurfelCircles;

    public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
    public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
    public string AlbedoTextureName { get; set; } = DefaultRenderPipeline.AlbedoOpacityTextureName;
    public string TransformIdTextureName { get; set; } = DefaultRenderPipeline.TransformIdTextureName;
    public string HDRSceneTextureName { get; set; } = DefaultRenderPipeline.HDRSceneTextureName;
    public string OutputTextureName { get; set; } = "SurfelDebugOutput";

    protected override void Execute()
    {
        // Find the SurfelGI pass to get its buffers
        VPRC_SurfelGIPass? surfelPass = FindSurfelPass();
        if (surfelPass is null)
        {
            // No surfel pass found - clear output and return
            var output = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);
            output?.Clear(ColorF4.Magenta); // Magenta indicates missing surfel pass
            return;
        }

        // Check buffers are available
        if (surfelPass.SurfelBuffer is null ||
            surfelPass.GridCountsBuffer is null ||
            surfelPass.GridIndicesBuffer is null)
        {
            var output = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);
            output?.Clear(ColorF4.Yellow); // Yellow indicates buffers not ready
            return;
        }

        var camera = ActivePipelineInstance.RenderState.SceneCamera;
        if (camera is null)
            return;

        var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
        if (region.Width <= 0 || region.Height <= 0)
            return;

        uint width = (uint)region.Width;
        uint height = (uint)region.Height;

        // Get textures
        if (ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName) is not XRTexture2D depthTex ||
            ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName) is not XRTexture2D normalTex ||
            ActivePipelineInstance.GetTexture<XRTexture>(AlbedoTextureName) is not XRTexture2D albedoTex ||
            ActivePipelineInstance.GetTexture<XRTexture>(TransformIdTextureName) is not XRTexture2D transformIdTex ||
            ActivePipelineInstance.GetTexture<XRTexture>(HDRSceneTextureName) is not XRTexture2D hdrTex ||
            ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName) is not XRTexture2D outputTex)
        {
            return;
        }

        if (!EnsureProgram())
            return;

        Matrix4x4 proj = camera.ProjectionMatrix;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        Matrix4x4 cameraToWorld = camera.Transform.RenderMatrix;

        DispatchDebugVisualization(
            width, height,
            invProj, cameraToWorld,
            depthTex, normalTex, albedoTex, transformIdTex, hdrTex,
            outputTex,
            surfelPass);
    }

    private VPRC_SurfelGIPass? FindSurfelPass()
    {
        // Search the command chain for the SurfelGI pass
        var pipeline = ActivePipelineInstance.Pipeline;
        if (pipeline?.CommandChain is null)
            return null;

        return FindInContainer(pipeline.CommandChain);
    }

    private VPRC_SurfelGIPass? FindInContainer(ViewportRenderCommandContainer container)
    {
        foreach (var cmd in container)
        {
            if (cmd is VPRC_SurfelGIPass surfelPass)
                return surfelPass;

            if (cmd is VPRC_IfElse ifElse)
            {
                if (ifElse.TrueCommands is not null)
                {
                    var found = FindInContainer(ifElse.TrueCommands);
                    if (found is not null) return found;
                }
                if (ifElse.FalseCommands is not null)
                {
                    var found = FindInContainer(ifElse.FalseCommands);
                    if (found is not null) return found;
                }
            }

            if (cmd is VPRC_Switch switchCmd)
            {
                if (switchCmd.Cases is not null)
                {
                    foreach (var caseContainer in switchCmd.Cases.Values)
                    {
                        var found = FindInContainer(caseContainer);
                        if (found is not null) return found;
                    }
                }
                if (switchCmd.DefaultCase is not null)
                {
                    var found = FindInContainer(switchCmd.DefaultCase);
                    if (found is not null) return found;
                }
            }
        }

        return null;
    }

    private bool EnsureProgram()
    {
        if (_debugProgram is null)
        {
            string shaderPath = Mode switch
            {
                EVisualizationMode.SurfelCircles => "Compute/SurfelGI/DebugCircles.comp",
                EVisualizationMode.GridHeatmap => "Compute/SurfelGI/DebugGrid.comp",
                _ => "Compute/SurfelGI/DebugCircles.comp"
            };

            var shader = XRShader.EngineShader(shaderPath, EShaderType.Compute);
            _debugProgram = new XRRenderProgram(true, false, shader);
        }

        return _debugProgram is not null;
    }

    private void DispatchDebugVisualization(
        uint width, uint height,
        Matrix4x4 invProj, Matrix4x4 cameraToWorld,
        XRTexture2D depthTex, XRTexture2D normalTex, XRTexture2D albedoTex, XRTexture2D transformIdTex, XRTexture2D hdrTex,
        XRTexture2D outputTex,
        VPRC_SurfelGIPass surfelPass)
    {
        if (_debugProgram is null)
            return;

        // Bind textures
        _debugProgram.Sampler("gDepth", depthTex, 0);
        _debugProgram.Sampler("gNormal", normalTex, 1);
        _debugProgram.Sampler("gAlbedo", albedoTex, 2);
        _debugProgram.Sampler("gTransformId", transformIdTex, 3);
        _debugProgram.Sampler("gHDRScene", hdrTex, 4);

        // Bind output image
        _debugProgram.BindImageTexture(0u, outputTex, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA8);

        // Bind surfel buffers from the pass
        surfelPass.SurfelBuffer?.BindTo(_debugProgram, 0u);
        surfelPass.CounterBuffer?.BindTo(_debugProgram, 1u);
        surfelPass.GridCountsBuffer?.BindTo(_debugProgram, 3u);
        surfelPass.GridIndicesBuffer?.BindTo(_debugProgram, 4u);

        // Bind command buffer (optional) for world-matrix reconstruction in debug shaders
        BindCulledCommandsIfAvailable(_debugProgram);

        // Set uniforms
        _debugProgram.Uniform("resolution", new Data.Vectors.IVector2((int)width, (int)height));
        _debugProgram.Uniform("invProjMatrix", invProj);
        _debugProgram.Uniform("cameraToWorldMatrix", cameraToWorld);

        _debugProgram.Uniform("gridOrigin", surfelPass.CurrentGridOrigin);
        _debugProgram.Uniform("cellSize", surfelPass.CurrentCellSize);
        _debugProgram.Uniform("gridDim", new Data.Vectors.UVector3(
            VPRC_SurfelGIPass.GridDimXConst,
            VPRC_SurfelGIPass.GridDimYConst,
            VPRC_SurfelGIPass.GridDimZConst));
        _debugProgram.Uniform("maxPerCell", VPRC_SurfelGIPass.GridMaxPerCellConst);
        _debugProgram.Uniform("maxSurfels", VPRC_SurfelGIPass.MaxSurfelsConst);

        uint groupsX = (width + 15u) / 16u;
        uint groupsY = (height + 15u) / 16u;
        _debugProgram.DispatchCompute(groupsX, groupsY, 1u, EMemoryBarrierMask.ShaderImageAccess);
    }

    private void BindCulledCommandsIfAvailable(XRRenderProgram program)
    {
        var scene = ActivePipelineInstance.RenderState.Scene;
        var gpuScene = scene?.GPUCommands;
        XRDataBuffer? commands = gpuScene is null ? null : gpuScene.AllLoadedCommandsBuffer;
        if (commands is null)
        {
            program.Uniform("hasCulledCommands", false);
            program.Uniform("culledFloatCount", 0u);
            program.Uniform("culledCommandFloats", CulledCommandFloats);
            return;
        }

        commands.BindTo(program, 5u);
        program.Uniform("hasCulledCommands", true);
        program.Uniform("culledFloatCount", commands.ElementCount * CulledCommandFloats);
        program.Uniform("culledCommandFloats", CulledCommandFloats);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(Mode))
        {
            // Reset program so it recompiles with the new shader
            _debugProgram?.Destroy();
            _debugProgram = null;
        }
    }
}
