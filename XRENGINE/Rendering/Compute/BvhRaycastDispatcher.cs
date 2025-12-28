using Silk.NET.OpenGL;
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
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Dispatches GPU BVH raycasts and delivers the results once the GPU readback fence signals.
/// </summary>
public sealed class BvhRaycastDispatcher
{
    private const uint LocalSizeX = 32u;
    private const uint DefaultStackLimit = 64u;

    private bool _enabled = true;

    private readonly ConcurrentQueue<BvhRaycastRequest> _pendingRequests = new();
    private readonly ConcurrentQueue<BvhRaycastResult> _completedResults = new();
    private readonly ConcurrentQueue<Action> _completedCallbacks = new();
    private readonly List<InFlightRaycast> _inFlight = new();

    private XRShader? _raycastShader;
    private XRShader? _anyHitShader;
    private XRShader? _closestHitShader;
    private XRRenderProgram? _raycastProgram;
    private XRRenderProgram? _anyHitProgram;
    private XRRenderProgram? _closestHitProgram;

    /// <summary>
    /// Enqueues a raycast request. Thread-safe; actual dispatch occurs on the render thread.
    /// </summary>
    public void Enqueue(BvhRaycastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_enabled)
            return;

        if (request.RayCount == 0 || request.HitBuffer is null || request.RayBuffer is null || request.NodeBuffer is null || request.TriangleBuffer is null)
            return;

        _pendingRequests.Enqueue(request);
    }

    /// <summary>
    /// Attempts to dequeue a completed result without blocking.
    /// </summary>
    public bool TryDequeueResult(out BvhRaycastResult result)
        => _completedResults.TryDequeue(out result);

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

        var glRenderer = renderer as OpenGLRenderer;
        int budget = _pendingRequests.Count;

        while (budget-- > 0 && _pendingRequests.TryDequeue(out var request))
        {
            if (!DependenciesReady(request, glRenderer))
            {
                _pendingRequests.Enqueue(request);
                continue;
            }

            Dispatch(request, glRenderer);
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

        var glRenderer = AbstractRenderer.Current as OpenGLRenderer;
        for (int i = _inFlight.Count - 1; i >= 0; --i)
        {
            var entry = _inFlight[i];
            if (!IsFenceComplete(entry.Fence, glRenderer))
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

            if (glRenderer is not null && entry.Fence != IntPtr.Zero)
                glRenderer.Api.DeleteSync(entry.Fence);

            _inFlight.RemoveAt(i);
        }

        while (_completedCallbacks.TryDequeue(out var callback))
            callback();
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
        var glRenderer = AbstractRenderer.Current as OpenGLRenderer;
        foreach (var entry in _inFlight)
        {
            if (glRenderer is not null && entry.Fence != IntPtr.Zero)
                glRenderer.Api.DeleteSync(entry.Fence);
        }

        _inFlight.Clear();

        while (_pendingRequests.TryDequeue(out _)) { }
        while (_completedResults.TryDequeue(out _)) { }
        while (_completedCallbacks.TryDequeue(out _)) { }

        if (!string.IsNullOrWhiteSpace(reason))
            Debug.LogWarning($"[BvhRaycastDispatcher] Cleared GPU BVH raycasts ({reason}).");
    }

    private static bool DependenciesReady(BvhRaycastRequest request, OpenGLRenderer? glRenderer)
    {
        if (request.CpuDependency is not null && !request.CpuDependency())
            return false;

        if (glRenderer is null || request.DependencyFences is null)
            return true;

        foreach (IntPtr fence in request.DependencyFences)
        {
            if (fence == IntPtr.Zero)
                continue;

            var status = glRenderer.Api.ClientWaitSync(fence, 0u, 0u);
            if (status != GLEnum.AlreadySignaled && status != GLEnum.ConditionSatisfied)
                return false;
        }

        return true;
    }

    private void Dispatch(BvhRaycastRequest request, OpenGLRenderer? glRenderer)
    {
        XRRenderProgram program = ResolveProgram(request.Variant);

        EnsureReadbackMapping(request.HitBuffer);

        uint packetWidth = Math.Clamp(request.PacketWidth == 0u ? 1u : request.PacketWidth, 1u, LocalSizeX);
        uint groupsX = request.UsePacketMode
            ? Math.Max(1u, (request.RayCount + packetWidth - 1u) / packetWidth)
            : Math.Max(1u, (request.RayCount + LocalSizeX - 1u) / LocalSizeX);

        program.BindBuffer(request.RayBuffer, 0);
        program.BindBuffer(request.NodeBuffer, 1);
        program.BindBuffer(request.TriangleBuffer, 2);
        program.BindBuffer(request.HitBuffer, 3);

        program.Uniform("uRayCount", (int)request.RayCount);
        program.Uniform("uRootIndex", (int)request.RootNodeIndex);
        program.Uniform("uPacketWidth", (int)packetWidth);
        program.Uniform("uUsePacketMode", request.UsePacketMode ? 1 : 0);
        program.Uniform("uAnyHitMode", request.AnyHit ? 1 : 0);
        program.Uniform("uMaxStackDepth", (int)(request.MaxStackDepth ?? DefaultStackLimit));

        program.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        IntPtr fence = IntPtr.Zero;
        if (glRenderer is not null)
            fence = glRenderer.Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);

        uint stride = request.HitStrideBytes ?? (uint)Unsafe.SizeOf<GpuRaycastHit>();
        ulong requestedBytes = request.BytesToRead ?? ((ulong)stride * request.RayCount);
        uint clampedBytes = (uint)Math.Min(requestedBytes, (ulong)request.HitBuffer.Length);

        _inFlight.Add(new InFlightRaycast(request, fence, clampedBytes));
    }

    private XRRenderProgram ResolveProgram(BvhRaycastVariant variant)
    {
        return variant switch
        {
            BvhRaycastVariant.AnyHit => _anyHitProgram ??= new XRRenderProgram(true, false, _anyHitShader ??= ShaderHelper.LoadEngineShader("Compute/bvh_anyhit.comp", EShaderType.Compute)),
            BvhRaycastVariant.ClosestHit => _closestHitProgram ??= new XRRenderProgram(true, false, _closestHitShader ??= ShaderHelper.LoadEngineShader("Compute/bvh_closesthit.comp", EShaderType.Compute)),
            _ => _raycastProgram ??= new XRRenderProgram(true, false, _raycastShader ??= ShaderHelper.LoadEngineShader("Compute/bvh_raycast.comp", EShaderType.Compute))
        };
    }

    private static bool IsFenceComplete(IntPtr fence, OpenGLRenderer? glRenderer)
    {
        if (fence == IntPtr.Zero || glRenderer is null)
            return true;

        var status = glRenderer.Api.ClientWaitSync(fence, 0u, 0u);
        return status == GLEnum.AlreadySignaled || status == GLEnum.ConditionSatisfied;
    }

    private static void EnsureReadbackMapping(XRDataBuffer buffer)
    {
        if (buffer.ActivelyMapping.Count > 0)
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
