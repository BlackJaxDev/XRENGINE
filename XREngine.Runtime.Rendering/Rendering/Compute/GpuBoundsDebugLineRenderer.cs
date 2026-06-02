using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Emits one AABB from a GPU bounds buffer to the engine debug-line instance buffer.
/// </summary>
public sealed class GpuBoundsDebugLineRenderer : IDisposable
{
    private const uint LinesPerBox = 12u;
    private const uint Vec4PerLine = 3u;
    private const uint FloatsPerVec4 = 4u;

    private XRShader? _computeShader;
    private XRRenderProgram? _computeProgram;
    private XRDataBuffer? _linesBuffer;
    private XRMeshRenderer? _linesRenderer;

    public bool Render(
        XRDataBuffer boundsBuffer,
        Matrix4x4 boundsToWorld,
        XRCamera? camera,
        float lineWidth,
        Vector4 color,
        uint boundsVec4Offset = 0u)
    {
        if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
            return false;
        if (!RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable)
            return false;
        if (!EnsureResources())
            return false;

        try
        {
            _computeProgram!.BindBuffer(boundsBuffer, 0);
            _computeProgram.BindBuffer(_linesBuffer!, 1);
            _computeProgram.Uniform("BoundsToWorld", boundsToWorld);
            _computeProgram.Uniform("Color", color);
            _computeProgram.Uniform("BoundsVec4Offset", boundsVec4Offset);
            _computeProgram.DispatchCompute(
                1u,
                1u,
                1u,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray | EMemoryBarrierMask.Command);

            var material = _linesRenderer!.Material;
            material?.SetFloat(0, lineWidth);
            material?.SetInt(1, (int)LinesPerBox);

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (PushRenderingCamera(camera))
                _linesRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, LinesPerBox, forceNoStereo: true);

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

    private bool EnsureResources()
    {
        if (_computeShader is null || _computeProgram is null)
        {
            _computeShader = ShaderHelper.LoadEngineShader(
                Path.Combine("Scene3D", "RenderPipeline", "skinned_bounds_debug_lines.comp"),
                EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, _computeShader);
        }

        if (!_computeProgram.IsLinked)
        {
            _computeProgram.Link();
            if (!_computeProgram.IsLinked)
                return false;
        }

        if (_linesBuffer is null)
        {
            uint requiredVec4 = LinesPerBox * Vec4PerLine;
            _linesBuffer = new XRDataBuffer(
                "SkinnedBoundsDebugLines",
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
            AddOrReplaceLineBuffer();
        }

        if (_linesRenderer is null)
        {
            XRMaterial material = CreateLineMaterial();
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
