using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.RenderBench;

/// <summary>
/// Catalog-driven fixture implementation. Immutable CPU inputs and every reusable Vulkan object
/// are created before capture; only a mutation policy explicitly requesting churn may create a
/// native object while measured.
/// </summary>
internal sealed unsafe class SyntheticRenderBenchFixture : IRenderBenchFixture
{
    private readonly RenderProfileRecipe _recipe;
    private readonly int _chainCount;
    private readonly int _drawCount;
    private readonly int _descriptorCount;
    private readonly int _barrierCount;
    private readonly int _uploadBytes;
    private readonly int _passIterations;
    private readonly int _workerCount;
    private readonly ulong[] _immutableChains;
    private readonly ulong[] _loweredPackets;
    private VulkanExplicitTargetRendererHost _host = null!;
    private RenderBenchFullscreenPipeline? _pipeline;
    private Buffer _fixtureBuffer;
    private DeviceMemory _fixtureMemory;
    private Buffer _uploadStagingBuffer;
    private DeviceMemory _uploadStagingMemory;
    private Buffer _uploadDeviceBuffer;
    private DeviceMemory _uploadDeviceMemory;
    private DescriptorSetLayout _descriptorLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private DescriptorBufferInfo[] _descriptorInfos = [];
    private RenderBenchSecondaryRecorderWorker[] _secondaryWorkers = [];
    private CommandBuffer[] _activeSecondaries = [];
    private CountdownEvent? _secondaryCompletion;
    private RenderBenchWorkCounters _counters;
    private ulong _blackhole;
    private int _frameOrdinal;
    private bool _measuring;
    private bool _disposed;

    public SyntheticRenderBenchFixture(RenderBenchFixtureDefinition definition, RenderProfileRecipe recipe)
    {
        Definition = definition;
        _recipe = recipe;
        _chainCount = recipe.Workload.ChainCount ?? definition.DefaultChainCount;
        _drawCount = recipe.Workload.DrawCount ?? definition.DefaultDrawCount;
        _descriptorCount = recipe.Workload.DescriptorCount ?? definition.DefaultDescriptorCount;
        _barrierCount = recipe.Workload.BarrierCount ?? definition.DefaultBarrierCount;
        _uploadBytes = recipe.Workload.UploadBytes ?? definition.DefaultUploadBytes;
        _passIterations = recipe.Workload.PassIterations ?? definition.DefaultPassIterations;
        _workerCount = recipe.WorkerCounts[0];
        _immutableChains = GC.AllocateUninitializedArray<ulong>(_chainCount);
        _loweredPackets = GC.AllocateUninitializedArray<ulong>(_chainCount);
        uint random = unchecked((uint)recipe.Scene.RandomSeed);
        for (int index = 0; index < _immutableChains.Length; index++)
        {
            random = NextRandom(random);
            _immutableChains[index] = ((ulong)random << 32) | unchecked((uint)index);
            _loweredPackets[index] = RotateLeft(_immutableChains[index] ^ 0x9E3779B97F4A7C15UL, index & 63);
        }
        Manifest = new RenderBenchFixtureManifest(
            1, definition.Name, definition.Component, definition.Kind,
            ResolveContract(recipe.Contract.Inclusions, definition.Inclusions),
            ResolveContract(recipe.Contract.Exclusions, definition.Exclusions),
            _chainCount, _drawCount, _descriptorCount, _barrierCount, _uploadBytes,
            _passIterations, _workerCount, recipe.Mutation.Policy.ToString(),
            string.Join("+", recipe.Scene.OutputIdentities));
    }

    public RenderBenchFixtureDefinition Definition { get; }
    public RenderBenchFixtureManifest Manifest { get; }
    public RenderBenchWorkCounters Counters => _counters;
    public long WorkerAllocatedBytes => _secondaryWorkers.Sum(static worker => worker.AllocatedBytes);

    public void Prepare(VulkanExplicitTargetRendererHost host, RenderProfileRecipe recipe)
    {
        _host = host;
        switch (Definition.Kind)
        {
            case RenderBenchFixtureKind.SecondaryCommandRecording:
            case RenderBenchFixtureKind.CommandBufferReuse:
                PrepareSecondaryCommandBuffers();
                break;
            case RenderBenchFixtureKind.DescriptorPublication:
                PrepareDescriptors();
                break;
            case RenderBenchFixtureKind.Upload:
                PrepareUploadBuffers();
                break;
            case RenderBenchFixtureKind.GpuPass:
            case RenderBenchFixtureKind.FullPresentationless:
                _pipeline = new RenderBenchFullscreenPipeline(host, recipe, Definition.Name, _passIterations);
                break;
        }
    }

    public void BeginCapture()
    {
        _counters = default;
        if (_secondaryWorkers.Length > 0)
        {
            _secondaryCompletion!.Reset(_secondaryWorkers.Length);
            for (int index = 0; index < _secondaryWorkers.Length; index++)
                _secondaryWorkers[index].RequestCaptureBaseline(_secondaryCompletion);
            _secondaryCompletion.Wait();
        }
        _measuring = true;
    }

    public void EndCapture() => _measuring = false;

    public void RecordFrame(Vk api, CommandBuffer commandBuffer, VulkanRenderFrameTarget target)
    {
        RenderBenchWorkCounters frame = default;
        ApplyRequestedChurn(api);
        switch (Definition.Kind)
        {
            case RenderBenchFixtureKind.CommandChainSignature:
                ConsumeCommandChainSignatures();
                break;
            case RenderBenchFixtureKind.PacketLowering:
                ConsumeLoweredPackets();
                break;
            case RenderBenchFixtureKind.PrimaryCommandEncoding:
            case RenderBenchFixtureKind.ResourcePlanning:
                RecordGlobalBarriers(api, commandBuffer, Math.Max(0, _barrierCount - 2));
                frame = frame with { Barriers = Math.Max(0, _barrierCount - 2) };
                break;
            case RenderBenchFixtureKind.SecondaryCommandRecording:
            case RenderBenchFixtureKind.CommandBufferReuse:
                frame += RecordAndExecuteSecondaries(api, commandBuffer, target.FrameSlotIndex);
                break;
            case RenderBenchFixtureKind.DescriptorPublication:
                PublishDescriptors(api);
                frame = frame with
                {
                    Descriptors = _descriptorCount *
                        (_recipe.Mutation.Policy == RenderProfileMutationPolicy.DescriptorChurn ? 2L : 1L),
                };
                break;
            case RenderBenchFixtureKind.Upload:
                BufferCopy copy = new() { Size = unchecked((ulong)_uploadBytes) };
                api.CmdCopyBuffer(commandBuffer, _uploadStagingBuffer, _uploadDeviceBuffer, 1, in copy);
                frame = frame with { UploadBytes = _uploadBytes };
                break;
            case RenderBenchFixtureKind.GpuPass:
            case RenderBenchFixtureKind.FullPresentationless:
                int gpuBarriers = _pipeline!.Record(api, commandBuffer, target, _passIterations, _drawCount, unchecked((uint)_recipe.Scene.RandomSeed));
                frame = frame with { Draws = _drawCount, Barriers = gpuBarriers, PassIterations = _passIterations };
                CompleteFrame(frame);
                return;
        }

        RecordDeterministicOutput(api, commandBuffer, target);
        frame = frame with { Barriers = frame.Barriers + 2 };
        CompleteFrame(frame);
    }

    private void CompleteFrame(RenderBenchWorkCounters frame)
    {
        if (_measuring)
        {
            _counters += frame with
            {
                Submissions = 1,
                CommandBuffers = Math.Max(1, frame.CommandBuffers),
            };
        }
        _frameOrdinal++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ConsumeCommandChainSignatures()
    {
        ulong hash = 1469598103934665603UL;
        for (int index = 0; index < _immutableChains.Length; index++)
            hash = (hash ^ _immutableChains[index]) * 1099511628211UL;
        Volatile.Write(ref _blackhole, hash);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ConsumeLoweredPackets()
    {
        ulong lowered = 0;
        for (int index = 0; index < _loweredPackets.Length; index++)
            lowered ^= RotateLeft(_loweredPackets[index] + unchecked((uint)index), index & 31);
        Volatile.Write(ref _blackhole, lowered);
    }

    private void ApplyRequestedChurn(Vk api)
    {
        switch (_recipe.Mutation.Policy)
        {
            case RenderProfileMutationPolicy.ResourceChurn:
                BufferCreateInfo bufferInfo = new()
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = 4096,
                    Usage = BufferUsageFlags.TransferSrcBit,
                    SharingMode = SharingMode.Exclusive,
                };
                Ensure(api.CreateBuffer(_host.Device, in bufferInfo, null, out Buffer transient), "create churn buffer");
                api.DestroyBuffer(_host.Device, transient, null);
                break;
            case RenderProfileMutationPolicy.DescriptorChurn when _descriptorSet.Handle != 0:
                PublishDescriptors(api);
                break;
            case RenderProfileMutationPolicy.PipelineChurn when _pipeline is not null:
                _pipeline.RecreatePipeline();
                break;
        }
    }

    private void PrepareDescriptors()
    {
        (_fixtureBuffer, _fixtureMemory) = _host.Renderer.CreateBuffer(
            256,
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            default);
        DescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = unchecked((uint)_descriptorCount),
            StageFlags = ShaderStageFlags.All,
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        Ensure(_host.Api.CreateDescriptorSetLayout(_host.Device, in layoutInfo, null, out _descriptorLayout), "create fixture descriptor layout");
        DescriptorPoolSize poolSize = new() { Type = DescriptorType.StorageBuffer, DescriptorCount = unchecked((uint)_descriptorCount) };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Ensure(_host.Api.CreateDescriptorPool(_host.Device, in poolInfo, null, out _descriptorPool), "create fixture descriptor pool");
        DescriptorSetLayout layout = _descriptorLayout;
        DescriptorSetAllocateInfo allocation = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        Ensure(_host.Api.AllocateDescriptorSets(_host.Device, in allocation, out _descriptorSet), "allocate fixture descriptor set");
        _descriptorInfos = GC.AllocateUninitializedArray<DescriptorBufferInfo>(_descriptorCount);
        Array.Fill(_descriptorInfos, new DescriptorBufferInfo(_fixtureBuffer, 0, 256));
    }

    private void PublishDescriptors(Vk api)
    {
        if (_descriptorCount == 0)
            return;
        fixed (DescriptorBufferInfo* infos = _descriptorInfos)
        {
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = 0,
                DescriptorCount = unchecked((uint)_descriptorCount),
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = infos,
            };
            api.UpdateDescriptorSets(_host.Device, 1, in write, 0, null);
        }
    }

    private void PrepareUploadBuffers()
    {
        if (_uploadBytes <= 0)
            throw new InvalidOperationException("Upload fixtures require a positive upload_bytes value.");
        (_uploadStagingBuffer, _uploadStagingMemory) = _host.Renderer.CreateBuffer(
            unchecked((ulong)_uploadBytes),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            default);
        (_uploadDeviceBuffer, _uploadDeviceMemory) = _host.Renderer.CreateBuffer(
            unchecked((ulong)_uploadBytes),
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit,
            default);
        void* mapped = null;
        Ensure(_host.Api.MapMemory(_host.Device, _uploadStagingMemory, 0, unchecked((ulong)_uploadBytes), 0, &mapped), "map fixture upload staging buffer");
        Unsafe.InitBlockUnaligned(mapped, unchecked((byte)_recipe.Scene.RandomSeed), unchecked((uint)_uploadBytes));
        _host.Api.UnmapMemory(_host.Device, _uploadStagingMemory);
    }

    private void PrepareSecondaryCommandBuffers()
    {
        _secondaryWorkers = new RenderBenchSecondaryRecorderWorker[_workerCount];
        _activeSecondaries = new CommandBuffer[_workerCount];
        _secondaryCompletion = new CountdownEvent(_workerCount);
        for (int worker = 0; worker < _workerCount; worker++)
            _secondaryWorkers[worker] = new RenderBenchSecondaryRecorderWorker(
                _host, _recipe.FrameSlots, BarrierShare(worker), worker);
    }

    private RenderBenchWorkCounters RecordAndExecuteSecondaries(Vk api, CommandBuffer primary, uint frameSlot)
    {
        bool dirty = Definition.Kind == RenderBenchFixtureKind.SecondaryCommandRecording ||
            Definition.Name.Equals("command-buffer-forced-dirty", StringComparison.OrdinalIgnoreCase) ||
            _recipe.Mutation.Policy == RenderProfileMutationPolicy.ForcedDirtyEveryFrame ||
            (_recipe.Mutation.Policy == RenderProfileMutationPolicy.DirtyEveryNFrames && _frameOrdinal % _recipe.Mutation.DirtyEveryNFrames == 0);
        int totalSecondaryBarriers = 0;
        if (dirty)
        {
            _secondaryCompletion!.Reset(_workerCount);
            for (int worker = 0; worker < _workerCount; worker++)
                _secondaryWorkers[worker].RequestRecord(frameSlot, _secondaryCompletion);
            _secondaryCompletion.Wait();
        }
        for (int worker = 0; worker < _workerCount; worker++)
        {
            if (dirty)
                _secondaryWorkers[worker].ThrowIfFailed();
            _activeSecondaries[worker] = _secondaryWorkers[worker].GetBuffer(frameSlot);
            totalSecondaryBarriers += BarrierShare(worker);
        }
        fixed (CommandBuffer* buffers = _activeSecondaries)
            api.CmdExecuteCommands(primary, unchecked((uint)_activeSecondaries.Length), buffers);
        return new RenderBenchWorkCounters(
            0, 0, 0, _workerCount + 1, 0, totalSecondaryBarriers, 0, 0, 1);
    }

    private int BarrierShare(int worker)
    {
        int workBarriers = Math.Max(0, _barrierCount - 2);
        return workBarriers / _workerCount + (worker < workBarriers % _workerCount ? 1 : 0);
    }

    private static void RecordGlobalBarriers(Vk api, CommandBuffer commandBuffer, int count)
    {
        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit,
        };
        for (int index = 0; index < count; index++)
            api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, in barrier, 0, null, 0, null);
    }

    private void RecordDeterministicOutput(Vk api, CommandBuffer commandBuffer, VulkanRenderFrameTarget target)
    {
        ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, 1, 0, target.Layers);
        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = target.InitialColorLayout,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = target.InitialColorLayout == ImageLayout.Undefined ? 0 : AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            Image = target.ColorImage,
            SubresourceRange = range,
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toTransfer);
        uint seed = unchecked((uint)_recipe.Scene.RandomSeed);
        float animation = _recipe.Scene.AnimationIdentity.Equals("frozen", StringComparison.OrdinalIgnoreCase)
            ? 0
            : (float)((Math.Sin(_frameOrdinal * _recipe.Scene.FixedTimeStepSeconds * Math.Tau) + 1) * 0.0625);
        ClearColorValue color = new(
            ((seed >> 0) & 255) / 1020.0f + animation,
            ((seed >> 8) & 255) / 1020.0f + animation,
            ((seed >> 16) & 255) / 1020.0f + animation,
            1);
        api.CmdClearColorImage(commandBuffer, target.ColorImage, ImageLayout.TransferDstOptimal, in color, 1, in range);
        ImageMemoryBarrier toFinal = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = target.RequiredFinalColorLayout,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            Image = target.ColorImage,
            SubresourceRange = range,
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toFinal);
    }

    private static string[] ResolveContract(string[] requested, string[] defaults)
        => requested.Length == 0 ? defaults : requested;

    private static uint NextRandom(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> ((64 - bits) & 63));

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pipeline?.Dispose();
        _pipeline = null;
        for (int index = 0; index < _secondaryWorkers.Length; index++)
            _secondaryWorkers[index]?.Dispose();
        _secondaryCompletion?.Dispose();
        if (_descriptorPool.Handle != 0)
            _host.Api.DestroyDescriptorPool(_host.Device, _descriptorPool, null);
        if (_descriptorLayout.Handle != 0)
            _host.Api.DestroyDescriptorSetLayout(_host.Device, _descriptorLayout, null);
        if (_fixtureBuffer.Handle != 0)
            _host.Renderer.DestroyBuffer(_fixtureBuffer, _fixtureMemory);
        if (_uploadStagingBuffer.Handle != 0)
            _host.Renderer.DestroyBuffer(_uploadStagingBuffer, _uploadStagingMemory);
        if (_uploadDeviceBuffer.Handle != 0)
            _host.Renderer.DestroyBuffer(_uploadDeviceBuffer, _uploadDeviceMemory);
    }
}
