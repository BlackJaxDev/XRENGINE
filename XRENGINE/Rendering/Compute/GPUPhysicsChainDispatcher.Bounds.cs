using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;
using XREngine.Components;

using XREngine.Rendering.Models.Materials;
namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const uint BoundsWorkgroupSize = 256u;
    private const uint BoundsCopyWorkgroupSize = 128u;

    private readonly PhysicsChainPaletteAtlasAllocator _gpuBoundsSlotAllocator = new();
    private readonly List<PhysicsChainGpuBoundsWorkItem> _gpuBoundsWorkItems = [];
    private readonly List<PhysicsChainGpuBoundsCopyItem> _gpuBoundsCopyItems = [];
    private readonly List<uint> _gpuBoundsCommandScratch = [];
    private readonly List<GPUScene> _gpuBoundsScenes = [];
    private readonly Dictionary<PhysicsChainComponent, uint> _gpuBoundsSlotByComponent =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private XRShader? _gpuBoundsShader;
    private XRShader? _gpuBoundsToSceneShader;
    private XRRenderProgram? _gpuBoundsProgram;
    private XRRenderProgram? _gpuBoundsToSceneProgram;
    private XRDataBuffer<PhysicsChainGpuBoundsWorkItem>? _gpuBoundsWorkItemBuffer;
    private XRDataBuffer<PhysicsChainGpuBoundsCopyItem>? _gpuBoundsCopyItemBuffer;
    private XRDataBuffer<uint>? _gpuBoundsAtlasBuffer;
    private int _gpuBoundsDispatchCount;
    private int _gpuBoundsSceneCopyDispatchCount;
    private int _gpuBoundsPublishedCommandCount;

    private static readonly PhysicsChainComputePass BoundsCompletionPass = new(
        PhysicsChainComputePassKind.BoundsPublication,
        EMemoryBarrierMask.ShaderStorage);

    public PhysicsChainGpuBoundsDiagnostics GetGpuBoundsDiagnosticsSnapshot()
        => new(
            _gpuBoundsSlotAllocator.LiveSliceCount,
            _gpuBoundsSlotAllocator.HighWater,
            _gpuBoundsDispatchCount,
            _gpuBoundsSceneCopyDispatchCount,
            _gpuBoundsPublishedCommandCount,
            UsesCpuReadback: false);

    private bool PublishGpuDrivenBounds(
        IPhysicsChainComputeBackend backend,
        IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_particlesBuffer is null || requests.Count == 0 || !RuntimeEngine.IsRenderThread)
            return false;

        EnsureGpuBoundsPrograms();
        if (_gpuBoundsProgram is null || _gpuBoundsToSceneProgram is null)
            return false;
        if (!EnsureProgramLinked(_gpuBoundsProgram) || !EnsureProgramLinked(_gpuBoundsToSceneProgram))
            return false;

        _gpuBoundsSlotAllocator.BeginLayout();
        _gpuBoundsWorkItems.Clear();
        _gpuBoundsSlotByComponent.Clear();
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            if (!request.Component.HasGpuDrivenRenderers || request.ParticleOffset < 0 || request.Particles.Count == 0)
                continue;

            var key = new PhysicsChainPaletteSliceKey(request.Component, request.Component);
            PhysicsChainPaletteSlice slice = _gpuBoundsSlotAllocator.Acquire(key, 1u);
            _gpuBoundsSlotByComponent.Add(request.Component, slice.BaseElement);
            _gpuBoundsWorkItems.Add(new PhysicsChainGpuBoundsWorkItem(
                checked((uint)request.ParticleOffset),
                checked((uint)request.Particles.Count),
                CalculateConservativeInfluenceRadius(request.ParticleStaticData),
                slice.BaseElement));
        }
        _gpuBoundsSlotAllocator.EndLayout();

        if (_gpuBoundsWorkItems.Count == 0)
            return false;

        bool workItemsResized = EnsureBufferCapacity(
            ref _gpuBoundsWorkItemBuffer,
            "PhysicsChainGlobalBoundsWorkItems",
            checked((uint)_gpuBoundsWorkItems.Count));
        bool boundsResized = EnsureBufferCapacity(
            ref _gpuBoundsAtlasBuffer,
            "PhysicsChainGlobalBoundsAtlas",
            checked(Math.Max(_gpuBoundsSlotAllocator.HighWater, 1u) * 8u));
        if (_gpuBoundsWorkItemBuffer is null || _gpuBoundsAtlasBuffer is null)
            return false;

        uint workItemBytes = _gpuBoundsWorkItemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuBoundsWorkItems));
        PushBufferUpdate(_gpuBoundsWorkItemBuffer, workItemsResized, workItemBytes);
        RecordCpuUploadBytes(workItemsResized ? _gpuBoundsWorkItemBuffer.Length : workItemBytes, isBatched: true);
        if (!backend.EnsureGpuBufferReady(_particlesBuffer)
            || !backend.EnsureGpuBufferReady(_gpuBoundsWorkItemBuffer)
            || !backend.EnsureGpuBufferReady(_gpuBoundsAtlasBuffer))
            return false;

        _gpuBoundsProgram.Uniform("WorkItemCount", checked((uint)_gpuBoundsWorkItems.Count));
        _gpuBoundsProgram.BindBuffer(_particlesBuffer, 0);
        _gpuBoundsProgram.BindBuffer(_gpuBoundsWorkItemBuffer, 1);
        _gpuBoundsProgram.BindBuffer(_gpuBoundsAtlasBuffer, 2);
        _gpuBoundsProgram.DispatchCompute(checked((uint)_gpuBoundsWorkItems.Count), 1u, 1u);
        backend.CompletePass(BoundsCompletionPass);
        ++_gpuBoundsDispatchCount;

        CollectBatchedGpuDrivenBonePaletteBindings(requests);
        CollectGpuBoundsScenes();
        for (int sceneIndex = 0; sceneIndex < _gpuBoundsScenes.Count; ++sceneIndex)
            PublishGpuBoundsToScene(backend, _gpuBoundsScenes[sceneIndex]);
        return true;
    }

    private void CollectGpuBoundsScenes()
    {
        _gpuBoundsScenes.Clear();
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            if (binding.Component.World is not IRuntimeRenderWorld renderWorld)
                continue;

            GPUScene scene = renderWorld.VisualScene.GPUCommands;
            if (!_gpuBoundsScenes.Contains(scene))
                _gpuBoundsScenes.Add(scene);
        }
    }

    private void PublishGpuBoundsToScene(IPhysicsChainComputeBackend backend, GPUScene scene)
    {
        _gpuBoundsCopyItems.Clear();
        uint maximumCommandSlot = 0u;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            if (binding.Component.World is not IRuntimeRenderWorld renderWorld
                || !ReferenceEquals(renderWorld.VisualScene.GPUCommands, scene)
                || !_gpuBoundsSlotByComponent.TryGetValue(binding.Component, out uint sourceSlot)
                || !scene.TryGetCommandIndicesForRenderer(binding.Renderer, _gpuBoundsCommandScratch))
                continue;

            for (int commandIndex = 0; commandIndex < _gpuBoundsCommandScratch.Count; ++commandIndex)
            {
                uint targetSlot = _gpuBoundsCommandScratch[commandIndex];
                _gpuBoundsCopyItems.Add(new PhysicsChainGpuBoundsCopyItem(sourceSlot, targetSlot));
                maximumCommandSlot = Math.Max(maximumCommandSlot, targetSlot);
            }

            if (_gpuBoundsCommandScratch.Count > 0)
                scene.SetRendererOwnsGpuAabb(binding.Renderer, true);
        }

        if (_gpuBoundsCopyItems.Count == 0)
            return;

        scene.EnsureCommandAabbCapacity(maximumCommandSlot + 1u);
        XRDataBuffer? sceneBoundsBuffer = scene.CommandAabbBuffer;
        if (sceneBoundsBuffer is null || _gpuBoundsAtlasBuffer is null)
            return;

        bool copyItemsResized = EnsureBufferCapacity(
            ref _gpuBoundsCopyItemBuffer,
            "PhysicsChainGlobalBoundsCopyItems",
            checked((uint)_gpuBoundsCopyItems.Count));
        if (_gpuBoundsCopyItemBuffer is null)
            return;

        uint copyItemBytes = _gpuBoundsCopyItemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuBoundsCopyItems));
        PushBufferUpdate(_gpuBoundsCopyItemBuffer, copyItemsResized, copyItemBytes);
        RecordCpuUploadBytes(copyItemsResized ? _gpuBoundsCopyItemBuffer.Length : copyItemBytes, isBatched: true);
        if (!backend.EnsureGpuBufferReady(_gpuBoundsAtlasBuffer)
            || !backend.EnsureGpuBufferReady(sceneBoundsBuffer)
            || !backend.EnsureGpuBufferReady(_gpuBoundsCopyItemBuffer))
            return;

        _gpuBoundsToSceneProgram!.Uniform("CopyItemCount", checked((uint)_gpuBoundsCopyItems.Count));
        _gpuBoundsToSceneProgram.BindBuffer(_gpuBoundsAtlasBuffer, 0);
        _gpuBoundsToSceneProgram.BindBuffer(sceneBoundsBuffer, 1);
        _gpuBoundsToSceneProgram.BindBuffer(_gpuBoundsCopyItemBuffer, 2);
        uint groupCount = (checked((uint)_gpuBoundsCopyItems.Count) + BoundsCopyWorkgroupSize - 1u) / BoundsCopyWorkgroupSize;
        _gpuBoundsToSceneProgram.DispatchCompute(Math.Max(groupCount, 1u), 1u, 1u);
        backend.CompletePass(BoundsCompletionPass);
        ++_gpuBoundsSceneCopyDispatchCount;
        _gpuBoundsPublishedCommandCount += _gpuBoundsCopyItems.Count;
    }

    private void EnsureGpuBoundsPrograms()
    {
        if (_gpuBoundsProgram is null)
        {
            _gpuBoundsShader = ShaderHelper.LoadEngineShader(
                "Compute/PhysicsChain/PhysicsChainBounds.comp",
                EShaderType.Compute);
            _gpuBoundsProgram = new XRRenderProgram(true, false, _gpuBoundsShader);
        }

        if (_gpuBoundsToSceneProgram is null)
        {
            _gpuBoundsToSceneShader = ShaderHelper.LoadEngineShader(
                "Compute/PhysicsChain/PhysicsChainBoundsToScene.comp",
                EShaderType.Compute);
            _gpuBoundsToSceneProgram = new XRRenderProgram(true, false, _gpuBoundsToSceneShader);
        }
    }

    private static bool EnsureProgramLinked(XRRenderProgram program)
    {
        if (program.IsLinked)
            return true;
        if (program.LinkReady)
            program.Link();
        return program.IsLinked;
    }

    private static float CalculateConservativeInfluenceRadius(
        IReadOnlyList<GPUParticleStaticData> particles)
    {
        float totalBoneLength = 0.0f;
        float maximumParticleRadius = 0.0f;
        for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
        {
            GPUParticleStaticData particle = particles[particleIndex];
            totalBoneLength += MathF.Max(particle.BoneLength, 0.0f);
            maximumParticleRadius = MathF.Max(maximumParticleRadius, particle.Radius);
        }

        return totalBoneLength + maximumParticleRadius;
    }

    private void ResetGpuBoundsResources()
    {
        _gpuBoundsSlotAllocator.Reset();
        _gpuBoundsWorkItems.Clear();
        _gpuBoundsCopyItems.Clear();
        _gpuBoundsCommandScratch.Clear();
        _gpuBoundsScenes.Clear();
        _gpuBoundsSlotByComponent.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PhysicsChainGpuBoundsWorkItem(
        uint ParticleOffset,
        uint ParticleCount,
        float InfluenceRadius,
        uint BoundsSlot);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PhysicsChainGpuBoundsCopyItem(
        uint SourceSlot,
        uint TargetCommandSlot);

    private void DisposeGpuBoundsResources()
    {
        _gpuBoundsWorkItemBuffer?.Dispose();
        _gpuBoundsCopyItemBuffer?.Dispose();
        _gpuBoundsAtlasBuffer?.Dispose();
        _gpuBoundsProgram?.Destroy();
        _gpuBoundsToSceneProgram?.Destroy();
        _gpuBoundsShader?.Destroy();
        _gpuBoundsToSceneShader?.Destroy();
        _gpuBoundsWorkItemBuffer = null;
        _gpuBoundsCopyItemBuffer = null;
        _gpuBoundsAtlasBuffer = null;
        _gpuBoundsProgram = null;
        _gpuBoundsToSceneProgram = null;
        _gpuBoundsShader = null;
        _gpuBoundsToSceneShader = null;
        _gpuBoundsDispatchCount = 0;
        _gpuBoundsSceneCopyDispatchCount = 0;
        _gpuBoundsPublishedCommandCount = 0;
    }
}

public readonly record struct PhysicsChainGpuBoundsDiagnostics(
    int LiveSlotCount,
    uint SlotHighWater,
    int BoundsDispatchCount,
    int SceneCopyDispatchCount,
    int PublishedCommandCount,
    bool UsesCpuReadback);
