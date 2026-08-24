using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Persistent worker owning one secondary command pool and one buffer per frame slot.</summary>
internal sealed unsafe class RenderBenchSecondaryRecorderWorker : IDisposable
{
    private readonly VulkanExplicitTargetRendererHost _host;
    private readonly int _barrierCount;
    private readonly AutoResetEvent _request = new(false);
    private readonly Thread _thread;
    private readonly CommandBuffer[] _buffers;
    private CommandPool _pool;
    private CountdownEvent? _completion;
    private Exception? _failure;
    private int _requestedSlot;
    private int _operation;
    private long _allocatedBefore;
    private long _allocatedAfter;
    private bool _stopping;

    public RenderBenchSecondaryRecorderWorker(
        VulkanExplicitTargetRendererHost host,
        uint frameSlots,
        int barrierCount,
        int workerIndex)
    {
        _host = host;
        _barrierCount = barrierCount;
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = host.GraphicsQueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Ensure(host.Api.CreateCommandPool(host.Device, in poolInfo, null, out _pool), "create worker secondary command pool");
        try
        {
            _buffers = new CommandBuffer[frameSlots];
            fixed (CommandBuffer* destination = _buffers)
            {
                CommandBufferAllocateInfo allocation = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = _pool,
                    Level = CommandBufferLevel.Secondary,
                    CommandBufferCount = frameSlots,
                };
                Ensure(host.Api.AllocateCommandBuffers(host.Device, in allocation, destination), "allocate worker secondary command buffers");
            }
            for (int index = 0; index < _buffers.Length; index++)
                Record(_buffers[index]);
            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"RenderBench.Secondary.{workerIndex}",
            };
            _thread.Start();
        }
        catch
        {
            if (_pool.Handle != 0)
                host.Api.DestroyCommandPool(host.Device, _pool, null);
            _pool = default;
            throw;
        }
    }

    public CommandBuffer GetBuffer(uint frameSlot) => _buffers[checked((int)frameSlot)];
    public long AllocatedBytes => Volatile.Read(ref _allocatedAfter) - Volatile.Read(ref _allocatedBefore);

    public void RequestCaptureBaseline(CountdownEvent completion)
    {
        _completion = completion;
        _operation = 2;
        _request.Set();
    }

    public void RequestRecord(uint frameSlot, CountdownEvent completion)
    {
        _requestedSlot = checked((int)frameSlot);
        _failure = null;
        _completion = completion;
        _operation = 1;
        _request.Set();
    }

    public void ThrowIfFailed()
    {
        if (_failure is not null)
            throw new InvalidOperationException($"Secondary recording worker '{_thread.Name}' failed.", _failure);
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _request.WaitOne();
            if (_stopping)
                return;
            try
            {
                if (_operation == 2)
                {
                    _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    _allocatedAfter = _allocatedBefore;
                }
                else
                {
                    Record(_buffers[_requestedSlot]);
                    _allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
                }
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
            finally
            {
                _completion!.Signal();
            }
        }
    }

    private void Record(CommandBuffer commandBuffer)
    {
        Ensure(_host.Api.ResetCommandBuffer(commandBuffer, 0), "reset worker secondary command buffer");
        CommandBufferBeginInfo begin = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.SimultaneousUseBit,
        };
        Ensure(_host.Api.BeginCommandBuffer(commandBuffer, in begin), "begin worker secondary command buffer");
        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit,
        };
        for (int index = 0; index < _barrierCount; index++)
        {
            _host.Api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 1, in barrier, 0, null, 0, null);
        }
        Ensure(_host.Api.EndCommandBuffer(commandBuffer), "end worker secondary command buffer");
    }

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }

    public void Dispose()
    {
        _stopping = true;
        _request.Set();
        _thread.Join();
        _request.Dispose();
        if (_pool.Handle != 0)
            _host.Api.DestroyCommandPool(_host.Device, _pool, null);
        _pool = default;
    }
}
