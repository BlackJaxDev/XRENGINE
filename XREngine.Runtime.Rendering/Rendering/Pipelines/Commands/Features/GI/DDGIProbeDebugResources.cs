using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>GPU resources owned by one pipeline instance's DDGI probe debug pass.</summary>
internal sealed class DDGIProbeDebugResources
{
    private const uint Vec4PerDebugProbe = 2u;
    private const uint FloatsPerVec4 = 4u;

    private XRShader? _computeShader;
    private XRRenderProgram? _computeProgram;
    private XRDataBuffer? _debugProbeBuffer;
    private XRMeshRenderer? _probeRenderer;
    private uint _allocatedProbeCapacity;

    public DDGIProbeDebugResources(XRRenderPipelineInstance owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.CacheClearing += ReleaseResources;
    }

    public bool EnsureResources(uint requiredProbeCapacity)
    {
        if (_computeShader is null || _computeProgram is null)
        {
            _computeShader = ShaderHelper.LoadEngineShader(Path.Combine("Scene3D", "DDGIProbeDebug.comp"), EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, _computeShader) { Name = "DDGIProbeDebug" };
        }
        if (!_computeProgram.IsLinked)
        {
            _computeProgram.Link();
            if (!_computeProgram.IsLinked)
                return false;
        }

        if (_debugProbeBuffer is null || _allocatedProbeCapacity < requiredProbeCapacity)
        {
            if (_debugProbeBuffer is not null && _probeRenderer?.Buffers.ContainsKey(_debugProbeBuffer.AttributeName) == true)
                _probeRenderer.Buffers.Remove(_debugProbeBuffer.AttributeName);
            _debugProbeBuffer?.Dispose();

            uint vec4Count = Math.Max(requiredProbeCapacity, 1u) * Vec4PerDebugProbe;
            _debugProbeBuffer = new XRDataBuffer("DDGIProbeDebugPoints", EBufferTarget.ShaderStorageBuffer, vec4Count,
                EComponentType.Float, FloatsPerVec4, false, false, true)
            {
                BindingIndexOverride = 0,
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
            };
            _debugProbeBuffer.SetDataRaw(new float[vec4Count * FloatsPerVec4]);
            _debugProbeBuffer.PushData();
            _allocatedProbeCapacity = requiredProbeCapacity;
            AddOrReplaceDebugBuffer();
        }

        if (_probeRenderer is null)
        {
            _probeRenderer = new XRMeshRenderer(new XRMesh([new Vertex(Vector3.Zero)]), CreateProbeMaterial())
            {
                GenerateAsync = false,
                GenerationPriority = EMeshGenerationPriority.RenderPipeline,
            };
            _probeRenderer.GetDefaultVersion().AllowShaderPipelines = false;
            AddOrReplaceDebugBuffer();
        }
        return true;
    }

    public bool TryPrepareForRendering()
        => _probeRenderer?.TryPrepareForRendering(forceNoStereo: true) == true;

    public void Dispatch(XRDataBuffer probeBuffer, DDGIVolumeRuntimeState state, uint probeCount, uint computeGroupSize)
    {
        _computeProgram!.BindBuffer(probeBuffer, 0);
        _computeProgram.BindBuffer(_debugProbeBuffer!, 1);
        _computeProgram.Uniform("uProbeCount", probeCount);
        DDGIVolumeRuntimeState.UploadCascadeUniforms(_computeProgram, state);
        _computeProgram.DispatchCompute(
            (probeCount + computeGroupSize - 1u) / computeGroupSize,
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray);
    }

    public void SetProbeSize(float probeSize)
        => _probeRenderer?.Material?.SetFloat(0, probeSize);

    public void Render(uint probeCount)
        => _probeRenderer!.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, probeCount, forceNoStereo: true);

    private void AddOrReplaceDebugBuffer()
    {
        if (_probeRenderer is null || _debugProbeBuffer is null)
            return;
        if (_probeRenderer.Buffers.ContainsKey(_debugProbeBuffer.AttributeName))
            _probeRenderer.Buffers.Remove(_debugProbeBuffer.AttributeName);
        _probeRenderer.Buffers.Add(_debugProbeBuffer.AttributeName, _debugProbeBuffer);
    }

    private static XRMaterial CreateProbeMaterial()
    {
        XRShader vert = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
        XRShader geom = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "gs", "PointInstance.gs"), EShaderType.Geometry);
        XRShader frag = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitivePoint.fs"), EShaderType.Fragment);
        var material = new XRMaterial([new ShaderFloat(0.0125f, "PointSize")], [vert, geom, frag]);
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        return material;
    }

    private void ReleaseResources()
    {
        _computeProgram?.Destroy();
        _computeShader?.Destroy();
        _probeRenderer?.Destroy();
        _debugProbeBuffer?.Dispose();
        _computeProgram = null;
        _computeShader = null;
        _probeRenderer = null;
        _debugProbeBuffer = null;
        _allocatedProbeCapacity = 0;
    }
}
