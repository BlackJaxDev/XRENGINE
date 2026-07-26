using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const int MaximumGlobalDebugChains = 256;
    private const int MaximumGlobalDebugParticles = 16_384;

    private readonly List<PhysicsChainGpuDebugItem> _gpuDebugItems = [];
    private XRShader? _gpuDebugShader;
    private XRRenderProgram? _gpuDebugProgram;
    private XRDataBuffer<PhysicsChainGpuDebugItem>? _gpuDebugItemBuffer;
    private XRDataBuffer? _gpuDebugPointsBuffer;
    private XRDataBuffer? _gpuDebugLinesBuffer;
    private XRMeshRenderer? _gpuDebugPointsRenderer;
    private XRMeshRenderer? _gpuDebugLinesRenderer;
    private ulong _gpuDebugRenderedFrame = ulong.MaxValue;
    private PhysicsChainGpuDebugDiagnostics _gpuDebugDiagnostics;

    public PhysicsChainGpuDebugDiagnostics GetGpuDebugDiagnosticsSnapshot()
        => _gpuDebugDiagnostics;

    /// <summary>
    /// Generates and draws one bounded compact debug batch for all explicitly
    /// selected GPU chains. Repeated component callbacks in the same render
    /// frame are coalesced by <see cref="RuntimeEngine.Rendering.State.RenderFrameId"/>.
    /// </summary>
    internal void RenderSelectedGpuDebug()
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_gpuDebugRenderedFrame == frameId)
            return;
        _gpuDebugRenderedFrame = frameId;

        IPhysicsChainComputeBackend? backend = ResolveComputeBackend(AbstractRenderer.Current);
        if (backend is null
            || !RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable
            || _particlesBuffer is null
            || _particleStaticBuffer is null)
        {
            _gpuDebugDiagnostics = default;
            return;
        }

        int selectedChainCount = BuildGlobalDebugItems();
        int particleCount = _gpuDebugItems.Count;
        if (particleCount == 0)
        {
            _gpuDebugDiagnostics = default;
            return;
        }

        EnsureGlobalDebugProgram();
        EnsureGlobalDebugResources(checked((uint)particleCount));
        if (_gpuDebugProgram is null
            || _gpuDebugItemBuffer is null
            || _gpuDebugPointsBuffer is null
            || _gpuDebugLinesBuffer is null)
            return;

        uint itemBytes = _gpuDebugItemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuDebugItems));
        PushBufferUpdate(_gpuDebugItemBuffer, fullPush: false, itemBytes);
        RecordCpuUploadBytes(itemBytes, isBatched: true);

        _gpuDebugProgram.Uniform("ParticleCount", particleCount);
        _gpuDebugProgram.Uniform("PointColor", new Vector4(1.0f, 1.0f, 0.0f, 1.0f));
        _gpuDebugProgram.Uniform("LineColor", Vector4.One);
        _gpuDebugProgram.BindBuffer(_particlesBuffer, 0);
        _gpuDebugProgram.BindBuffer(_particleStaticBuffer, 1);
        _gpuDebugProgram.BindBuffer(_gpuDebugPointsBuffer, 2);
        _gpuDebugProgram.BindBuffer(_gpuDebugLinesBuffer, 3);
        _gpuDebugProgram.BindBuffer(_gpuDebugItemBuffer, 4);

        uint groupCount = (checked((uint)particleCount) + 127u) / 128u;
        if (!TryDispatchDirect(
                backend,
                _gpuDebugProgram,
                Math.Max(groupCount, 1u),
                1u,
                1u,
                PhysicsChainComputePassKind.DebugVisualization))
            return;
        if (!TryCompletePass(
                backend,
                new PhysicsChainComputePass(
                    PhysicsChainComputePassKind.DebugVisualization,
                    EMemoryBarrierMask.ShaderStorage)))
            return;

        _gpuDebugPointsRenderer?.Material?.SetInt(1, particleCount);
        _gpuDebugLinesRenderer?.Material?.SetInt(1, particleCount);
        _gpuDebugPointsRenderer?.Render(null, checked((uint)particleCount));
        _gpuDebugLinesRenderer?.Render(null, checked((uint)particleCount));
        _gpuDebugDiagnostics = new PhysicsChainGpuDebugDiagnostics(
            selectedChainCount,
            particleCount,
            selectedChainCount >= MaximumGlobalDebugChains || particleCount >= MaximumGlobalDebugParticles,
            ComputeDispatchCount: 1,
            DrawSubmissionCount: 2,
            UsesCpuReadback: false);
    }

    private int BuildGlobalDebugItems()
    {
        _gpuDebugItems.Clear();
        int selectedChainCount = 0;
        lock (_registeredComponentsSync)
        {
            for (int requestIndex = 0;
                 requestIndex < _registeredComponentSnapshot.Count
                    && selectedChainCount < MaximumGlobalDebugChains
                    && _gpuDebugItems.Count < MaximumGlobalDebugParticles;
                 ++requestIndex)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[requestIndex];
                if (!request.Component.DebugDrawChains || request.ParticleOffset < 0 || request.Particles.Count == 0)
                    continue;

                ++selectedChainCount;
                float interpolationAlpha = request.Component.GetGpuDebugInterpolationAlpha();
                uint interpolationMode = checked((uint)request.Component.GpuDebugInterpolationMode);
                int remaining = MaximumGlobalDebugParticles - _gpuDebugItems.Count;
                int count = Math.Min(request.Particles.Count, remaining);
                for (int particleIndex = 0; particleIndex < count; ++particleIndex)
                {
                    _gpuDebugItems.Add(new PhysicsChainGpuDebugItem(
                        checked((uint)(request.ParticleOffset + particleIndex)),
                        interpolationAlpha,
                        interpolationMode,
                        0u));
                }
            }
        }

        return selectedChainCount;
    }

    private void EnsureGlobalDebugProgram()
    {
        if (_gpuDebugProgram is not null)
            return;

        _gpuDebugShader = ShaderHelper.LoadEngineShader(
            "Compute/PhysicsChain/PhysicsChainDebugDraw.comp",
            EShaderType.Compute);
        _gpuDebugProgram = new XRRenderProgram(true, false, _gpuDebugShader);
    }

    private void EnsureGlobalDebugResources(uint particleCount)
    {
        EnsureBufferCapacity(ref _gpuDebugItemBuffer, "PhysicsChainGlobalDebugItems", particleCount);
        EnsureRawDebugBuffer(ref _gpuDebugPointsBuffer, "PhysicsChainGlobalDebugPoints", particleCount, 8u);
        EnsureRawDebugBuffer(ref _gpuDebugLinesBuffer, "PhysicsChainGlobalDebugLines", particleCount, 12u);

        _gpuDebugPointsRenderer ??= new XRMeshRenderer(
            new XRMesh([new Vertex(Vector3.Zero)]),
            CreateGpuDebugPointMaterial());
        _gpuDebugLinesRenderer ??= new XRMeshRenderer(
            new XRMesh([new Vertex(Vector3.Zero)]),
            CreateGpuDebugLineMaterial());

        ReplaceDebugRendererBuffer(_gpuDebugPointsRenderer, _gpuDebugPointsBuffer);
        ReplaceDebugRendererBuffer(_gpuDebugLinesRenderer, _gpuDebugLinesBuffer);
    }

    /// <summary>
    /// Publishes a resized debug buffer even though its shader attribute name is
    /// stable. Merely checking the key would leave the renderer referencing the
    /// disposed, undersized allocation after the first capacity growth.
    /// </summary>
    private static void ReplaceDebugRendererBuffer(XRMeshRenderer renderer, XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        string name = buffer.AttributeName;
        if (renderer.Buffers.TryGetValue(name, out XRDataBuffer? current)
            && ReferenceEquals(current, buffer))
            return;

        renderer.Buffers[name] = buffer;
    }

    private static void EnsureRawDebugBuffer(
        ref XRDataBuffer? buffer,
        string name,
        uint elementCount,
        uint componentCount)
    {
        if (buffer is not null && buffer.ElementCount >= elementCount)
            return;

        buffer?.Dispose();
        buffer = new XRDataBuffer(
            name,
            EBufferTarget.ShaderStorageBuffer,
            Math.Max(elementCount, 1u),
            EComponentType.Float,
            componentCount,
            false,
            false,
            true)
        {
            BindingIndexOverride = 0,
            Usage = EBufferUsage.StreamDraw,
            DisposeOnPush = false,
        };
        buffer.SetDataRaw(new float[Math.Max(elementCount, 1u) * componentCount]);
        buffer.PushData();
    }

    private void DisposeGlobalDebugResources()
    {
        _gpuDebugItemBuffer?.Dispose();
        _gpuDebugPointsBuffer?.Dispose();
        _gpuDebugLinesBuffer?.Dispose();
        _gpuDebugPointsRenderer?.Destroy();
        _gpuDebugLinesRenderer?.Destroy();
        _gpuDebugProgram?.Destroy();
        _gpuDebugShader?.Destroy();
        _gpuDebugItemBuffer = null;
        _gpuDebugPointsBuffer = null;
        _gpuDebugLinesBuffer = null;
        _gpuDebugPointsRenderer = null;
        _gpuDebugLinesRenderer = null;
        _gpuDebugProgram = null;
        _gpuDebugShader = null;
        _gpuDebugItems.Clear();
        _gpuDebugRenderedFrame = ulong.MaxValue;
        _gpuDebugDiagnostics = default;
    }

    private static XRMaterial CreateGpuDebugPointMaterial()
    {
        XRShader vertexShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"),
            EShaderType.Vertex);
        XRShader geometryShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "gs", "PointInstance.gs"),
            EShaderType.Geometry);
        XRShader fragmentShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitivePoint.fs"),
            EShaderType.Fragment);
        ShaderVar[] variables =
        [
            new ShaderFloat(0.005f, "PointSize"),
            new ShaderInt(0, "TotalPoints"),
        ];
        var material = new XRMaterial(variables, vertexShader, geometryShader, fragmentShader);
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        return material;
    }

    private static XRMaterial CreateGpuDebugLineMaterial()
    {
        XRShader vertexShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"),
            EShaderType.Vertex);
        XRShader geometryShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "gs", "LineInstance.gs"),
            EShaderType.Geometry);
        XRShader fragmentShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitiveLine.fs"),
            EShaderType.Fragment);
        ShaderVar[] variables =
        [
            new ShaderFloat(0.001f, "LineWidth"),
            new ShaderInt(0, "TotalLines"),
        ];
        var material = new XRMaterial(variables, vertexShader, geometryShader, fragmentShader);
        material.RenderOptions.RequiredEngineUniforms =
            EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        return material;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PhysicsChainGpuDebugItem(
        uint ParticleIndex,
        float InterpolationAlpha,
        uint InterpolationMode,
        uint Padding);
}

public readonly record struct PhysicsChainGpuDebugDiagnostics(
    int SelectedChainCount,
    int GeneratedParticleCount,
    bool WasTruncated,
    int ComputeDispatchCount,
    int DrawSubmissionCount,
    bool UsesCpuReadback);
