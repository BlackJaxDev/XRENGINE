using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

public enum GpuBvhDebugNodeRenderMode
{
    AllNodesSame,
    HighlightLeafNodes,
    LeafNodesOnly,
    InternalNodesOnly,
}

/// <summary>
/// Emits BVH node AABBs to a GPU line buffer and draws them without CPU traversal.
/// </summary>
public sealed class GpuBvhDebugLineRenderer : IDisposable
{
    private const uint ComputeGroupSize = 64u;
    private const uint LinesPerBox = 12u;
    private const uint Vec4PerLine = 3u;
    private const uint FloatsPerVec4 = 4u;

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance.RenderingState, GpuBvhDebugOverlayQueue> BaseQueues = new();
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance.RenderingState, GpuBvhDebugOverlayQueue> HighlightQueues = new();

    private XRShader? _computeShader;
    private XRRenderProgram? _computeProgram;
    private XRDataBuffer? _linesBuffer;
    private XRMeshRenderer? _linesRenderer;
    private uint _allocatedLineCapacity;
    private bool _disposed;

    /// <summary>
    /// Queues a BVH overlay for the pipeline's next late-debug pass.
    /// </summary>
    public bool Queue(
        XRDataBuffer nodeBuffer,
        uint nodeCount,
        Matrix4x4 nodeToWorld,
        uint maxNodes,
        float lineWidth,
        Vector4 leafColor,
        Vector4 internalColor,
        uint showFilter,
        GpuBvhDebugNodeClassOptions? nodeClasses = null,
        GpuBvhDebugOverlayLayer overlayLayer = GpuBvhDebugOverlayLayer.Base)
    {
        if (_disposed || nodeBuffer.IsDestroyed || nodeCount == 0)
            return false;
        if (RuntimeEngine.Rendering.State.RenderingPipelineState is not { } pipelineState)
            return false;

        var request = new GpuBvhDebugRenderRequest(
            this,
            nodeBuffer,
            nodeCount,
            nodeToWorld,
            maxNodes,
            lineWidth,
            leafColor,
            internalColor,
            showFilter,
            nodeClasses);
        GetQueues(overlayLayer)
            .GetValue(pipelineState, static _ => new GpuBvhDebugOverlayQueue())
            .Enqueue(in request);
        return true;
    }

    /// <summary>
    /// Renders one overlay layer queued for a pipeline inside its active late-debug pass.
    /// </summary>
    internal static void RenderQueued(
        XRRenderPipelineInstance.RenderingState pipelineState,
        GpuBvhDebugOverlayLayer overlayLayer)
    {
        if (!GetQueues(overlayLayer).TryGetValue(pipelineState, out GpuBvhDebugOverlayQueue? queue))
            return;

        List<GpuBvhDebugRenderRequest> batch = queue.TakeBatch();
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                GpuBvhDebugRenderRequest request = batch[i];
                request.Renderer.RenderImmediate(in request);
            }
        }
        finally
        {
            GpuBvhDebugOverlayQueue.CompleteBatch(batch);
        }
    }

    private static ConditionalWeakTable<XRRenderPipelineInstance.RenderingState, GpuBvhDebugOverlayQueue> GetQueues(
        GpuBvhDebugOverlayLayer overlayLayer)
        => overlayLayer == GpuBvhDebugOverlayLayer.Highlight ? HighlightQueues : BaseQueues;

    private bool RenderImmediate(in GpuBvhDebugRenderRequest request)
    {
        if (_disposed || request.NodeBuffer.IsDestroyed || request.NodeCount == 0)
            return false;

        uint visualizedNodes = Math.Min(request.NodeCount, Math.Max(request.MaxNodes, 1u));
        uint visualizedLines = visualizedNodes * LinesPerBox;
        if (!EnsureResources(visualizedLines))
            return false;
        if (!_linesRenderer!.TryPrepareForRendering(forceNoStereo: true))
            return false;

        try
        {
            _computeProgram!.BindBuffer(request.NodeBuffer, 0);
            _computeProgram.BindBuffer(_linesBuffer!, 1);
            GpuBvhDebugNodeClassOptions? nodeClasses = request.NodeClasses;
            GpuBvhDebugNodeClassOptions nodeClassOptions = nodeClasses.GetValueOrDefault();
            bool useNodeClasses = nodeClasses.HasValue && !nodeClassOptions.Buffer.IsDestroyed;
            _computeProgram.BindBuffer(useNodeClasses ? nodeClassOptions.Buffer : request.NodeBuffer, 2);
            _computeProgram.Uniform("MaxNodes", visualizedNodes);
            _computeProgram.Uniform("LeafColor", request.LeafColor);
            _computeProgram.Uniform("InternalColor", request.InternalColor);
            _computeProgram.Uniform("NodeToWorld", request.NodeToWorld);
            _computeProgram.Uniform("ShowFilter", request.ShowFilter);
            _computeProgram.Uniform(
                "NodeClassMode",
                useNodeClasses ? (uint)nodeClassOptions.Mode : (uint)GpuBvhDebugNodeClassMode.Ignore);
            _computeProgram.Uniform(
                "ClassOneColor",
                useNodeClasses ? nodeClassOptions.ClassOneColor : Vector4.Zero);
            _computeProgram.Uniform(
                "ClassTwoColor",
                useNodeClasses ? nodeClassOptions.ClassTwoColor : Vector4.Zero);
            _computeProgram.Uniform(
                "VisibleClassMask",
                useNodeClasses ? nodeClassOptions.VisibleClassMask : uint.MaxValue);

            uint groups = (visualizedNodes + ComputeGroupSize - 1u) / ComputeGroupSize;
            _computeProgram.DispatchCompute(
                groups,
                1u,
                1u,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray | EMemoryBarrierMask.Command);

            var material = _linesRenderer!.Material;
            material?.SetFloat(0, request.LineWidth);
            material?.SetInt(1, (int)visualizedLines);

            _linesRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, visualizedLines, forceNoStereo: true);

            return true;
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
            _computeProgram = new XRRenderProgram(true, false, _computeShader)
            {
                Name = "GpuBvhDebugLines"
            };
        }

        if (!_computeProgram.IsLinked)
        {
            _computeProgram.Link();
            if (!_computeProgram.IsLinked)
                return false;
        }

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

            _linesBuffer.SetDataRaw(new float[requiredVec4 * FloatsPerVec4]);
            _linesBuffer.PushData();
            _allocatedLineCapacity = requiredLineCapacity;
            AddOrReplaceLineBuffer();
        }

        if (_linesRenderer is null)
        {
            XRMaterial? material = CreateLineMaterial();
            if (material is null)
                return false;

            _linesRenderer = new XRMeshRenderer(new XRMesh([new Vertex(Vector3.Zero)]), material)
            {
                GenerateAsync = false,
                GenerationPriority = EMeshGenerationPriority.RenderPipeline
            };
            AddOrReplaceLineBuffer();
            _linesRenderer.GetDefaultVersion().AllowShaderPipelines = false;
        }

        return _linesBuffer is not null && _linesRenderer is not null;
    }

    private void AddOrReplaceLineBuffer()
    {
        if (_linesRenderer is null || _linesBuffer is null)
            return;

        if (_linesRenderer.Buffers.ContainsKey(_linesBuffer.AttributeName))
            _linesRenderer.Buffers.Remove(_linesBuffer.AttributeName);
        _linesRenderer.Buffers.Add(_linesBuffer.AttributeName, _linesBuffer);
    }

    private static XRMaterial CreateLineMaterial()
    {
        XRShader vert = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
        XRShader geom = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "gs", "LineInstance.gs"), EShaderType.Geometry);
        XRShader frag = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitiveLine.fs"), EShaderType.Fragment);

        ShaderVar[] vars =
        [
            new ShaderFloat(0.0015f, "LineWidth"),
            new ShaderInt(0, "TotalLines"),
        ];

        var material = new XRMaterial(vars, [vert, geom, frag]);
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        return material;
    }

    public void Dispose()
    {
        _disposed = true;
        _computeProgram?.Destroy();
        _computeShader?.Destroy();
        _linesRenderer?.Destroy();
        _linesBuffer?.Dispose();
        _computeProgram = null;
        _computeShader = null;
        _linesRenderer = null;
        _linesBuffer = null;
    }
}
