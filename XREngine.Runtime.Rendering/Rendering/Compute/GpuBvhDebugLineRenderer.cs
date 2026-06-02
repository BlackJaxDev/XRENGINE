using System;
using System.IO;
using System.Numerics;
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

    private XRShader? _computeShader;
    private XRRenderProgram? _computeProgram;
    private XRDataBuffer? _linesBuffer;
    private XRMeshRenderer? _linesRenderer;
    private uint _allocatedLineCapacity;

    public bool Render(
        XRDataBuffer nodeBuffer,
        uint nodeCount,
        Matrix4x4 nodeToWorld,
        XRCamera? camera,
        uint maxNodes,
        float lineWidth,
        Vector4 leafColor,
        Vector4 internalColor,
        uint showFilter)
    {
        if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
            return false;
        if (!RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable || nodeCount == 0)
            return false;

        uint visualizedNodes = Math.Min(nodeCount, Math.Max(maxNodes, 1u));
        uint visualizedLines = visualizedNodes * LinesPerBox;
        if (!EnsureResources(visualizedLines))
            return false;

        try
        {
            _computeProgram!.BindBuffer(nodeBuffer, 0);
            _computeProgram.BindBuffer(_linesBuffer!, 1);
            _computeProgram.Uniform("MaxNodes", visualizedNodes);
            _computeProgram.Uniform("LeafColor", leafColor);
            _computeProgram.Uniform("InternalColor", internalColor);
            _computeProgram.Uniform("NodeToWorld", nodeToWorld);
            _computeProgram.Uniform("ShowFilter", showFilter);

            uint groups = (visualizedNodes + ComputeGroupSize - 1u) / ComputeGroupSize;
            _computeProgram.DispatchCompute(
                groups,
                1u,
                1u,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray | EMemoryBarrierMask.Command);

            var material = _linesRenderer!.Material;
            material?.SetFloat(0, lineWidth);
            material?.SetInt(1, (int)visualizedLines);

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (PushRenderingCamera(camera))
                _linesRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, visualizedLines, forceNoStereo: true);

            return true;
        }
        finally
        {
            ResetStencilState();
        }
    }

    private static IDisposable? PushRenderingCamera(XRCamera? camera)
        => camera is not null
            ? RuntimeEngine.Rendering.State.RenderingPipelineState?.PushRenderingCamera(camera)
            : null;

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
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        return material;
    }

    public void Dispose()
    {
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
