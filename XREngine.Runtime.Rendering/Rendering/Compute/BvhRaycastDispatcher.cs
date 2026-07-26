using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Dispatches GPU BVH raycasts and delivers the results once the GPU readback fence signals.
/// </summary>
public sealed class BvhRaycastDispatcher : IDisposable
{
    private const uint LocalSizeX = 32u;
    private const uint DefaultStackLimit = 64u;

    private bool _enabled = true;

    private readonly ConcurrentQueue<BvhRaycastRequest> _pendingRequests = new();
    private readonly ConcurrentQueue<BvhRaycastResult> _completedResults = new();
    private readonly ConcurrentQueue<Action> _completedCallbacks = new();
    private readonly List<InFlightRaycast> _inFlight = [];

    private XRShader? _raycastShader;
    private XRShader? _anyHitShader;
    private XRShader? _closestHitShader;
    private XRRenderProgram? _raycastProgram;
    private XRRenderProgram? _anyHitProgram;
    private XRRenderProgram? _closestHitProgram;
    private XRDataBuffer? _fallbackTraversalDiagnosticsBuffer;

    /// <summary>
    /// Enqueues a raycast request. Thread-safe; actual dispatch occurs on the render thread.
    /// </summary>
    public bool Enqueue(BvhRaycastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_enabled)
            return false;

        if (!IsBackendSupported())
        {
            WarnUnsupportedBackend();
            return false;
        }

        if (request.RayCount == 0 || request.HitBuffer is null || request.RayBuffer is null || request.NodeBuffer is null || request.TriangleBuffer is null)
            return false;

        _pendingRequests.Enqueue(request);
        return true;
    }

    /// <summary>
    /// Attempts to dequeue a completed result without blocking.
    /// </summary>
    public bool TryDequeueResult(out BvhRaycastResult result)
    {
        if (_completedResults.TryDequeue(out var dequeued))
        {
            result = dequeued;
            return true;
        }

        result = default!;
        return false;
    }

    /// <summary>
    /// Dispatch pending raycasts whose dependencies have been satisfied.
    /// Should be called from the render thread (GlobalPreRender).
    /// </summary>
    public void ProcessDispatches()
    {
        if (!_enabled)
            return;

        var renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        if (!((IRuntimeRendererHost)renderer).TryGetBackendCapability<IGpuFenceBackendCapability>(out var fenceCapability) ||
            fenceCapability is null)
        {
            WarnUnsupportedBackend();
            Reset("renderer backend does not support GPU BVH raycast fences/readback");
            return;
        }

        int budget = _pendingRequests.Count;

        while (budget-- > 0 && _pendingRequests.TryDequeue(out var request))
        {
            if (!DependenciesReady(request, fenceCapability))
            {
                _pendingRequests.Enqueue(request);
                continue;
            }

            // Gate on backend program link readiness. Binding a program whose
            // link is still queued on the shared GL context can deadlock the
            // render thread on NVIDIA (parallel-link worker hazard). Re-request
            // link while unlinked so backends can continue async work or retry
            // Vulkan links that were deferred until logical-device creation.
            XRRenderProgram program = ResolveProgram(request.Variant);
            if (!program.IsLinked)
            {
                program.Link();
                _pendingRequests.Enqueue(request);
                continue;
            }

            Dispatch(request, fenceCapability, program);
        }
    }

    /// <summary>
    /// Polls GPU fences, enqueues results, and executes any completion callbacks.
    /// Should be called from the render thread (GlobalPostRender).
    /// </summary>
    public void ProcessCompletions()
    {
        if (!_enabled)
            return;

        IRuntimeRendererHost? renderer = AbstractRenderer.Current;
        IGpuFenceBackendCapability? fenceCapability = null;
        if (renderer is not null)
            renderer.TryGetBackendCapability(out fenceCapability);
        if (fenceCapability is null && _inFlight.Count > 0)
        {
            WarnUnsupportedBackend();
            Reset("renderer backend changed while GPU BVH raycasts were in flight");
            return;
        }

        for (int i = _inFlight.Count - 1; i >= 0; --i)
        {
            var entry = _inFlight[i];
            if (!IsFenceComplete(entry.Fence, fenceCapability))
                continue;

            var raw = ReadResultBytes(entry.Request, entry.ExpectedBytes);
            int hitBytes = Math.Min(raw.Length, (int)entry.ExpectedBytes);
            int stride = Unsafe.SizeOf<GpuRaycastHit>();
            int parsedBytes = hitBytes - (hitBytes % stride);
            var hits = parsedBytes > 0
                ? MemoryMarshal.Cast<byte, GpuRaycastHit>(raw.AsSpan(0, parsedBytes)).ToArray()
                : Array.Empty<GpuRaycastHit>();
            var result = new BvhRaycastResult(entry.Request, hits, raw);

            _completedResults.Enqueue(result);
            if (entry.Request.Completed != null)
                _completedCallbacks.Enqueue(() => entry.Request.Completed!(result));

            fenceCapability?.DeleteFence(entry.Fence);

            _inFlight.RemoveAt(i);
        }

        while (_completedCallbacks.TryDequeue(out var callback))
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                // A throwing completion callback must not take down the render
                // thread silently; surface it and continue draining the queue.
                Debug.RenderingException(ex, "BVH raycast completion callback failed.");
            }
        }
    }

    public void SetEnabled(bool enabled, string? reason = null)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        if (!_enabled)
        {
            Reset(reason);
        }
        else
        {
            WarmShaders();
        }
    }

    public void WarmShaders()
    {
        ResolveProgram(BvhRaycastVariant.Default);
        ResolveProgram(BvhRaycastVariant.AnyHit);
        ResolveProgram(BvhRaycastVariant.ClosestHit);
    }

    public void Reset(string? reason = null)
    {
        IRuntimeRendererHost? renderer = AbstractRenderer.Current;
        IGpuFenceBackendCapability? fenceCapability = null;
        renderer?.TryGetBackendCapability(out fenceCapability);
        foreach (var entry in _inFlight)
            fenceCapability?.DeleteFence(entry.Fence);

        // Only treat this as a meaningful clear (and warn) when there was actually
        // outstanding work. On unsupported backends (e.g. Vulkan) Reset is invoked
        // every frame; warning unconditionally floods the meshes log with hundreds
        // of identical lines. Enqueue already rejects requests on those backends, so
        // after the first clear the queues stay empty and we go quiet.
        int clearedWork = _inFlight.Count + _pendingRequests.Count + _completedResults.Count + _completedCallbacks.Count;

        _inFlight.Clear();

        while (_pendingRequests.TryDequeue(out _)) { }
        while (_completedResults.TryDequeue(out _)) { }
        while (_completedCallbacks.TryDequeue(out _)) { }

        _fallbackTraversalDiagnosticsBuffer?.Dispose();
        _fallbackTraversalDiagnosticsBuffer = null;

        if (clearedWork > 0 && !string.IsNullOrWhiteSpace(reason))
            Debug.MeshesWarning($"[BvhRaycastDispatcher] Cleared GPU BVH raycasts ({reason}).");
    }

    private static bool DependenciesReady(BvhRaycastRequest request, IGpuFenceBackendCapability fenceCapability)
    {
        if (request.CpuDependency is not null && !request.CpuDependency())
            return false;

        if (request.DependencyFences is null)
            return true;

        foreach (IntPtr fence in request.DependencyFences)
        {
            if (fence == IntPtr.Zero)
                continue;

            if (!fenceCapability.IsFenceComplete(fence))
                return false;
        }

        return true;
    }

    private void Dispatch(BvhRaycastRequest request, IGpuFenceBackendCapability fenceCapability, XRRenderProgram program)
    {
        if (request.HitBuffer is null || request.RayBuffer is null || request.NodeBuffer is null || request.TriangleBuffer is null)
            return;

        EnsureReadbackMapping(request.HitBuffer);

        uint packetWidth = Math.Clamp(request.PacketWidth == 0u ? 1u : request.PacketWidth, 1u, LocalSizeX);
        uint groupsX = request.UsePacketMode
            ? Math.Max(1u, (request.RayCount + packetWidth - 1u) / packetWidth)
            : Math.Max(1u, (request.RayCount + LocalSizeX - 1u) / LocalSizeX);

        program.BindBuffer(request.RayBuffer, 0);
        program.BindBuffer(request.NodeBuffer, 1);
        program.BindBuffer(request.TriangleBuffer, 2);
        program.BindBuffer(request.HitBuffer, 3);
        XRDataBuffer traversalDiagnostics = request.TraversalDiagnosticsBuffer ?? EnsureFallbackTraversalDiagnosticsBuffer();
        program.BindBuffer(traversalDiagnostics, 4);

        program.Uniform("uRayCount", request.RayCount);
        program.Uniform("uRootIndex", request.RootNodeIndex);
        program.Uniform("uPacketWidth", packetWidth);
        program.Uniform("uUsePacketMode", request.UsePacketMode ? 1u : 0u);
        program.Uniform("uAnyHitMode", request.AnyHit ? 1u : 0u);
        program.Uniform("uMaxStackDepth", request.MaxStackDepth ?? DefaultStackLimit);
        program.Uniform("uDiagnosticsEnabled", request.TraversalDiagnosticsBuffer is not null ? 1u : 0u);

        using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Raycast, request.RayCount))
            program.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        IntPtr fence = fenceCapability.CreateCompletionFence();

        uint stride = request.HitStrideBytes ?? (uint)Unsafe.SizeOf<GpuRaycastHit>();
        ulong requestedBytes = request.BytesToRead ?? ((ulong)stride * request.RayCount);
        uint clampedBytes = (uint)Math.Min(requestedBytes, (ulong)request.HitBuffer.Length);

        _inFlight.Add(new InFlightRaycast(request, fence, clampedBytes));
    }

    private XRDataBuffer EnsureFallbackTraversalDiagnosticsBuffer()
    {
        if (_fallbackTraversalDiagnosticsBuffer is not null)
            return _fallbackTraversalDiagnosticsBuffer;

        var buffer = new XRDataBuffer(
            "BvhRaycastDispatcher.FallbackTraversalDiagnostics",
            EBufferTarget.ShaderStorageBuffer,
            4u,
            EComponentType.UInt,
            1,
            false,
            true)
        {
            Usage = EBufferUsage.DynamicDraw,
            Resizable = false,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = false,
        };
        buffer.SetDataRaw(new uint[4], 4);
        buffer.Generate();
        buffer.PushSubData();
        _fallbackTraversalDiagnosticsBuffer = buffer;
        return buffer;
    }

    public void Dispose()
    {
        Reset("dispatcher disposed");
        _raycastProgram?.Destroy();
        _anyHitProgram?.Destroy();
        _closestHitProgram?.Destroy();
        _raycastShader?.Destroy();
        _anyHitShader?.Destroy();
        _closestHitShader?.Destroy();
    }

    private XRRenderProgram ResolveProgram(BvhRaycastVariant variant)
    {
        return variant switch
        {
            BvhRaycastVariant.AnyHit => _anyHitProgram ??= new XRRenderProgram(true, false, _anyHitShader ??= ShaderHelper.LoadEngineShader("Compute/BVH/bvh_anyhit.comp", EShaderType.Compute)),
            BvhRaycastVariant.ClosestHit => _closestHitProgram ??= new XRRenderProgram(true, false, _closestHitShader ??= ShaderHelper.LoadEngineShader("Compute/BVH/bvh_closesthit.comp", EShaderType.Compute)),
            _ => _raycastProgram ??= new XRRenderProgram(true, false, _raycastShader ??= ShaderHelper.LoadEngineShader("Compute/BVH/bvh_raycast.comp", EShaderType.Compute))
        };
    }

    private static bool IsFenceComplete(IntPtr fence, IGpuFenceBackendCapability? fenceCapability)
        => fenceCapability?.IsFenceComplete(fence) == true;

    private static void EnsureReadbackMapping(XRDataBuffer buffer)
    {
        if (buffer.IsMapped)
            return;

        buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent;
        buffer.RangeFlags |= EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
        buffer.DisposeOnPush = false;
        buffer.Usage = EBufferUsage.StreamRead;
        buffer.Resizable = false;
        buffer.MapBufferData();
    }

    private static unsafe byte[] ReadResultBytes(BvhRaycastRequest request, uint expectedBytes)
    {
        uint byteLength = Math.Min(expectedBytes, request.HitBuffer?.Length ?? 0u);
        byte[] data = new byte[byteLength];

        VoidPtr mapped = request.HitBuffer?.GetMappedAddresses().FirstOrDefault() ?? VoidPtr.Zero;
        IntPtr mappedPtr = mapped.IsValid ? (IntPtr)mapped.Pointer : IntPtr.Zero;
        if (mappedPtr != IntPtr.Zero)
        {
            Marshal.Copy(mappedPtr, data, 0, (int)byteLength);
            return data;
        }

        var clientSource = request.HitBuffer?.ClientSideSource;
        if (clientSource is not null)
            Marshal.Copy((IntPtr)clientSource.Address.Pointer, data, 0, (int)Math.Min(byteLength, clientSource.Length));

        return data;
    }

    private static bool IsBackendSupported()
        => RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.OpenGL &&
           AbstractRenderer.Current is IRuntimeRendererHost renderer &&
           renderer.TryGetBackendCapability<IGpuFenceBackendCapability>(out _);

    private static void WarnUnsupportedBackend()
        => Debug.RenderingWarningEvery(
            "BvhRaycastDispatcher.UnsupportedBackend",
            TimeSpan.FromSeconds(5),
            "[BvhRaycastDispatcher] GPU BVH raycast is currently OpenGL-only because Vulkan fence/readback integration is not implemented; rejecting GPU raycast request.");

    private readonly record struct InFlightRaycast(BvhRaycastRequest Request, IntPtr Fence, uint ExpectedBytes);
}

public enum BvhRaycastVariant
{
    Default,
    AnyHit,
    ClosestHit
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuRaycastHit
{
    public float Distance;
    public uint ObjectId;
    public uint FaceIndex;
    public uint TriangleIndex;
    public System.Numerics.Vector3 Barycentric;
    public float Padding;
}

public sealed record BvhRaycastRequest
{
    public XRDataBuffer? RayBuffer { get; init; }
    public XRDataBuffer? NodeBuffer { get; init; }
    public XRDataBuffer? TriangleBuffer { get; init; }
    public XRDataBuffer? HitBuffer { get; init; }
    /// <summary>
    /// Optional four-uint GPU-resident buffer: trace count, maximum stack
    /// occupancy, stack overflows, and conservative range recoveries.
    /// </summary>
    public XRDataBuffer? TraversalDiagnosticsBuffer { get; init; }
    public uint RayCount { get; init; }
    public uint RootNodeIndex { get; init; }
    public uint PacketWidth { get; init; } = 1u;
    public bool UsePacketMode { get; init; }
    public bool AnyHit { get; init; }
    public uint? MaxStackDepth { get; init; }
    public BvhRaycastVariant Variant { get; init; } = BvhRaycastVariant.Default;
    public IEnumerable<IntPtr>? DependencyFences { get; init; }
    public Func<bool>? CpuDependency { get; init; }
    public ulong? BytesToRead { get; init; }
    public uint? HitStrideBytes { get; init; }
    public Action<BvhRaycastResult>? Completed { get; init; }
}

public sealed record BvhRaycastResult(BvhRaycastRequest Request, IReadOnlyList<GpuRaycastHit> Hits, IReadOnlyList<byte> RawBytes);
