using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Renders a GPU-driven wireframe debug visualization of the scene-level GPU BVH.
///
/// Designed to pair with <see cref="VPRC_BuildAccelerationStructure"/> and the
/// zero-readback GPU rendering path: a compute shader walks the BVH node SSBO and
/// writes 12 line segments (one per AABB edge) per node directly into a debug-line
/// SSBO that is then drawn via the engine's instanced debug-line geometry shader.
///
/// No CPU-side traversal or readback is involved; the only host-side state is the
/// node count published by <see cref="VPRC_BuildAccelerationStructure"/> so we know
/// how many instances to draw.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_RenderDebugGpuBvh : ViewportRenderCommand
{
    /// <summary>
    /// Script-authored fallback enable flag. The active scene camera's
    /// <see cref="GpuBvhDebugSettings"/> post-process stage takes precedence
    /// when present so the visualization is toggleable from the editor.
    /// </summary>
    public bool Enabled { get; set; } = false;

    public string ReadyVariableName { get; set; } = "AccelerationStructureReady";
    public string NodeCountVariableName { get; set; } = "AccelerationStructureNodeCount";
    public string NodeBufferVariableName { get; set; } = "AccelerationStructureNodes";

    /// <summary>Hard cap on the number of nodes whose AABBs are emitted per frame.</summary>
    public uint MaxNodes { get; set; } = 16384u;

    public float LineWidth { get; set; } = 0.0015f;
    public Vector4 LeafColor { get; set; } = new Vector4(0.20f, 1.00f, 0.40f, 1.00f);
    public Vector4 InternalColor { get; set; } = new Vector4(1.00f, 0.65f, 0.10f, 0.55f);

    /// <summary>0 = all nodes, 1 = leaves only, 2 = internal nodes only.</summary>
    public uint ShowFilter { get; set; } = 0u;

    private const uint ComputeGroupSize = 64u;
    private const uint LinesPerBox = 12u;
    private const uint Vec4PerLine = 3u;       // start, end, color (LineInstance.gs layout)
    private const uint FloatsPerVec4 = 4u;

    private XRShader? _computeShader;
    private XRRenderProgram? _computeProgram;

    private XRDataBuffer? _linesBuffer;
    private XRMeshRenderer? _linesRenderer;
    private uint _allocatedLineCapacity;

    protected override void Execute()
    {
        if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
            return;

        try
        {
            if (!RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable)
                return;

            // Resolve effective settings: the post-processing stage on the active
            // scene camera takes precedence over the script-authored properties so
            // a user can toggle the visualization from the ImGui editor at runtime.
            bool enabled = Enabled;
            uint maxNodes = MaxNodes;
            float lineWidth = LineWidth;
            Vector4 leafColor = LeafColor;
            Vector4 internalColor = InternalColor;
            uint showFilter = ShowFilter;

            var activeInstance = ActivePipelineInstance;
            var camera = activeInstance.RenderState.SceneCamera
                ?? activeInstance.RenderState.RenderingCamera
                ?? activeInstance.LastSceneCamera
                ?? activeInstance.LastRenderingCamera;
            if (GpuBvhDebugSettings.TryResolve(camera, out GpuBvhDebugSettings? settings) && settings is not null)
            {
                enabled = settings.Enabled;
                maxNodes = (uint)Math.Max(1, settings.MaxNodes);
                lineWidth = settings.LineWidth;
                leafColor = settings.LeafColor;
                internalColor = settings.InternalColor;
                showFilter = (uint)settings.Filter;
            }

            if (!enabled)
                return;

            var variables = ActivePipelineInstance.Variables;
            if (!variables.TryGet(ReadyVariableName, out bool ready) || !ready)
                return;
            if (!variables.TryGet(NodeCountVariableName, out uint nodeCount) || nodeCount == 0)
                return;
            if (!variables.BufferVariables.TryGetValue(NodeBufferVariableName, out var nodeBuffer) || nodeBuffer is null)
                return;

            uint visualizedNodes = Math.Min(nodeCount, Math.Max(maxNodes, 1u));
            uint visualizedLines = visualizedNodes * LinesPerBox;

            if (!EnsureResources(visualizedLines))
                return;

            // Compute pass: write per-node AABB edges into the debug line SSBO.
            _computeProgram!.BindBuffer(nodeBuffer, 0);
            _computeProgram.BindBuffer(_linesBuffer!, 1);
            _computeProgram.Uniform("MaxNodes", visualizedNodes);
            _computeProgram.Uniform("LeafColor", leafColor);
            _computeProgram.Uniform("InternalColor", internalColor);
            _computeProgram.Uniform("NodeToWorld", Matrix4x4.Identity);
            _computeProgram.Uniform("ShowFilter", showFilter);

            uint groups = (visualizedNodes + ComputeGroupSize - 1u) / ComputeGroupSize;
            _computeProgram.DispatchCompute(
                groups,
                1u,
                1u,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray | EMemoryBarrierMask.Command);

            // Render pass: draw `visualizedLines` instances of a single point;
            // LineInstance.gs expands each into a screen-space line quad sourced
            // from the SSBO populated above. No CPU readback occurred.
            var material = _linesRenderer!.Material;
            material?.SetFloat(0, lineWidth);
            material?.SetInt(1, (int)visualizedLines);

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
            {
                _linesRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, visualizedLines, forceNoStereo: true);
            }
        }
        finally
        {
            ResetStencilState();
        }
    }

    private static void ResetStencilState()
    {
        RuntimeEngine.Rendering.State.EnableStencilTest(false);
        RuntimeEngine.Rendering.State.StencilMask(0xFF);
        RuntimeEngine.Rendering.State.StencilFunc(EComparison.Always, 0, 0xFF);
        RuntimeEngine.Rendering.State.StencilOp(EStencilOp.Keep, EStencilOp.Keep, EStencilOp.Keep);
    }

    private bool EnsureResources(uint requiredLineCapacity)
    {
        if (_computeShader is null || _computeProgram is null)
        {
            _computeShader = ShaderHelper.LoadEngineShader(
                Path.Combine("Scene3D", "RenderPipeline", "bvh_debug_lines.comp"),
                EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, _computeShader);
        }

        // Expanded layout: 3 vec4 per line (matches LineInstance.gs/SSBO binding 0).
        uint requiredVec4 = Math.Max(requiredLineCapacity, 1u) * Vec4PerLine;

        if (_linesBuffer is null || _allocatedLineCapacity < requiredLineCapacity)
        {
            if (_linesBuffer is not null && _linesRenderer?.Buffers.ContainsKey(_linesBuffer.AttributeName) == true)
                _linesRenderer.Buffers.Remove(_linesBuffer.AttributeName);
            _linesBuffer?.Dispose();
            _linesBuffer = new XRDataBuffer(
                "BvhDebugLines",
                EBufferTarget.ShaderStorageBuffer,
                requiredVec4,
                EComponentType.Float,
                FloatsPerVec4,
                false,
                false,
                true)
            {
                BindingIndexOverride = 0,
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
            };

            // Allocate GPU storage. The contents are overwritten by the compute
            // shader every frame, so the initial zeros are fine.
            _linesBuffer.SetDataRaw(new float[requiredVec4 * FloatsPerVec4]);
            _linesBuffer.PushData();

            _allocatedLineCapacity = requiredLineCapacity;

            // Re-link the buffer to an existing renderer when we resize.
            AddOrReplaceLineBuffer();
        }

        if (_linesRenderer is null)
        {
            var material = CreateLineMaterial();
            if (material is null)
                return false;

            _linesRenderer = new XRMeshRenderer(new XRMesh([new Vertex(Vector3.Zero)]), material);
            _linesRenderer.GenerateAsync = false;
            _linesRenderer.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
            AddOrReplaceLineBuffer();

            // The debug line material expands points in a geometry shader, so it
            // must stay on the default vertex variant instead of generated stereo variants.
            _linesRenderer.GetDefaultVersion().AllowShaderPipelines = false;
        }

        return _computeProgram is not null && _linesBuffer is not null && _linesRenderer is not null;
    }

    private void AddOrReplaceLineBuffer()
    {
        if (_linesRenderer is null || _linesBuffer is null)
            return;

        if (_linesRenderer.Buffers.ContainsKey(_linesBuffer.AttributeName))
            _linesRenderer.Buffers.Remove(_linesBuffer.AttributeName);
        _linesRenderer.Buffers.Add(_linesBuffer.AttributeName, _linesBuffer);
    }

    private XRMaterial? CreateLineMaterial()
    {
        XRShader vert = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
        XRShader geom = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "gs", "LineInstance.gs"), EShaderType.Geometry);
        XRShader frag = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitiveLine.fs"), EShaderType.Fragment);

        ShaderVar[] vars =
        [
            new ShaderFloat(LineWidth, "LineWidth"),
            new ShaderInt(0, "TotalLines"),
        ];

        var mat = new XRMaterial(vars, [vert, geom, frag]);
        mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        mat.RenderOptions.CullMode = ECullMode.None;
        mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        mat.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(mat);
        return mat;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_RenderDebugGpuBvh), ERenderGraphPassStage.Compute);
        builder.ReadBuffer(NodeBufferVariableName);
    }
}
