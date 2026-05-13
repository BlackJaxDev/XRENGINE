using Silk.NET.OpenGL;
using System;
using System.Buffers;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public partial class OpenGLRenderer
    {
        private const int GpuRenderStatsReadbackRingSize = 256;
        private const uint GpuRenderStatsReadbackInlineUIntCapacity = 64u;

        private enum GpuRenderStatsReadbackKind
        {
            DrawCountBuffer,
            StatsBuffer,
        }

        private sealed class GpuRenderStatsReadbackSlot
        {
            public uint BufferId;
            public uint CapacityBytes;
            public uint ByteCount;
            public uint ElementCount;
            public IntPtr Sync;
            public bool Active;
            public bool PublishDraws;
            public bool PublishTriangles;
            public GpuRenderStatsReadbackKind Kind;
        }

        private readonly GpuRenderStatsReadbackSlot?[] _gpuRenderStatsReadbackSlots = new GpuRenderStatsReadbackSlot?[GpuRenderStatsReadbackRingSize];
        private int _gpuRenderStatsReadbackCursor;
        private XRDataBuffer? _boundParameterBufferForStats;

        public override void PollGpuRenderStatsReadbacks()
        {
            if (!Engine.IsRenderThread)
            {
                Engine.EnqueueMainThreadTask(PollGpuRenderStatsReadbacks, "OpenGLRenderer.PollGpuRenderStatsReadbacks");
                return;
            }

            for (int i = 0; i < _gpuRenderStatsReadbackSlots.Length; ++i)
            {
                GpuRenderStatsReadbackSlot? slot = _gpuRenderStatsReadbackSlots[i];
                if (slot is not null && slot.Active)
                    TryConsumeGpuRenderStatsReadback(slot);
            }
        }

        public override bool QueueGpuRenderDrawCountReadback(XRDataBuffer drawCountBuffer, uint countByteOffset = 0, uint countElementCount = 1)
            => QueueGpuRenderStatsReadback(
                drawCountBuffer,
                countByteOffset,
                checked(countElementCount * (uint)sizeof(uint)),
                countElementCount,
                GpuRenderStatsReadbackKind.DrawCountBuffer,
                publishDraws: true,
                publishTriangles: false);

        public override bool QueueGpuRenderStatsBufferReadback(XRDataBuffer statsBuffer, bool publishDraws, bool publishTriangles)
        {
            if (!publishDraws && !publishTriangles)
                return false;

            return QueueGpuRenderStatsReadback(
                statsBuffer,
                0u,
                checked(GpuStatsLayout.FieldCount * (uint)sizeof(uint)),
                GpuStatsLayout.FieldCount,
                GpuRenderStatsReadbackKind.StatsBuffer,
                publishDraws,
                publishTriangles);
        }

        private bool QueueGpuRenderStatsReadback(
            XRDataBuffer sourceBuffer,
            uint sourceByteOffset,
            uint byteCount,
            uint elementCount,
            GpuRenderStatsReadbackKind kind,
            bool publishDraws,
            bool publishTriangles)
        {
            if (!Engine.Rendering.Stats.EnableTracking || sourceBuffer is null || byteCount == 0u || elementCount == 0u)
                return false;

            if (!Engine.IsRenderThread)
            {
                Engine.EnqueueMainThreadTask(
                    () => QueueGpuRenderStatsReadback(sourceBuffer, sourceByteOffset, byteCount, elementCount, kind, publishDraws, publishTriangles),
                    "OpenGLRenderer.QueueGpuRenderStatsReadback");
                return false;
            }

            ulong requestedEnd = (ulong)sourceByteOffset + byteCount;
            if (requestedEnd > sourceBuffer.Length)
                return false;

            PollGpuRenderStatsReadbacks();

            if (GenericToAPI<GLDataBuffer>(sourceBuffer) is not { } sourceGlBuffer)
                return false;

            sourceGlBuffer.EnsureStorageAllocatedForGpuCopy();
            uint sourceId = sourceGlBuffer.BindingId;
            if (sourceId == GLObjectBase.InvalidBindingId)
                return false;

            GpuRenderStatsReadbackSlot? slot = AcquireGpuRenderStatsReadbackSlot();
            if (slot is null)
                return false;

            EnsureGpuRenderStatsReadbackBuffer(slot, byteCount);
            if (slot.BufferId == GLObjectBase.InvalidBindingId)
                return false;

            MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command | EMemoryBarrierMask.BufferUpdate);
            Api.CopyNamedBufferSubData(sourceId, slot.BufferId, (nint)sourceByteOffset, 0, byteCount);

            IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            if (sync == IntPtr.Zero)
                return false;

            slot.ByteCount = byteCount;
            slot.ElementCount = elementCount;
            slot.Kind = kind;
            slot.PublishDraws = publishDraws;
            slot.PublishTriangles = publishTriangles;
            slot.Sync = sync;
            slot.Active = true;
            return true;
        }

        private GpuRenderStatsReadbackSlot? AcquireGpuRenderStatsReadbackSlot()
        {
            for (int i = 0; i < _gpuRenderStatsReadbackSlots.Length; ++i)
            {
                int index = (_gpuRenderStatsReadbackCursor + i) % _gpuRenderStatsReadbackSlots.Length;
                GpuRenderStatsReadbackSlot slot = _gpuRenderStatsReadbackSlots[index] ??= new GpuRenderStatsReadbackSlot();

                if (slot.Active && !TryConsumeGpuRenderStatsReadback(slot))
                    continue;

                _gpuRenderStatsReadbackCursor = (index + 1) % _gpuRenderStatsReadbackSlots.Length;
                return slot;
            }

            return null;
        }

        private unsafe void EnsureGpuRenderStatsReadbackBuffer(GpuRenderStatsReadbackSlot slot, uint byteCount)
        {
            if (slot.BufferId == GLObjectBase.InvalidBindingId)
                Api.CreateBuffers(1, out slot.BufferId);

            if (slot.BufferId == GLObjectBase.InvalidBindingId || slot.CapacityBytes >= byteCount)
                return;

            Api.NamedBufferData(slot.BufferId, byteCount, (void*)null, GLEnum.StreamRead);
            slot.CapacityBytes = byteCount;
        }

        private unsafe bool TryConsumeGpuRenderStatsReadback(GpuRenderStatsReadbackSlot slot)
        {
            if (!slot.Active)
                return true;

            GLEnum result = Api.ClientWaitSync(slot.Sync, 0u, 0u);
            if (result != GLEnum.AlreadySignaled && result != GLEnum.ConditionSatisfied)
                return false;

            Api.DeleteSync(slot.Sync);
            slot.Sync = IntPtr.Zero;

            uint inlineCount = Math.Min(slot.ElementCount, GpuRenderStatsReadbackInlineUIntCapacity);
            Span<uint> inlineValues = stackalloc uint[(int)inlineCount];
            uint[]? rented = null;
            Span<uint> values = slot.ElementCount <= GpuRenderStatsReadbackInlineUIntCapacity
                ? inlineValues[..(int)slot.ElementCount]
                : (rented = ArrayPool<uint>.Shared.Rent((int)slot.ElementCount)).AsSpan(0, (int)slot.ElementCount);

            try
            {
                fixed (uint* ptr = values)
                {
                    Api.BindBuffer(GLEnum.CopyReadBuffer, slot.BufferId);
                    Api.GetBufferSubData(GLEnum.CopyReadBuffer, IntPtr.Zero, slot.ByteCount, ptr);
                    Api.BindBuffer(GLEnum.CopyReadBuffer, 0);
                }

                PublishGpuRenderStatsReadback(slot, values);
                Engine.Rendering.Stats.RecordGpuReadbackBytes(slot.ByteCount);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<uint>.Shared.Return(rented);

                slot.Active = false;
                slot.ByteCount = 0u;
                slot.ElementCount = 0u;
                slot.PublishDraws = false;
                slot.PublishTriangles = false;
            }

            return true;
        }

        private static void PublishGpuRenderStatsReadback(GpuRenderStatsReadbackSlot slot, ReadOnlySpan<uint> values)
        {
            switch (slot.Kind)
            {
                case GpuRenderStatsReadbackKind.DrawCountBuffer:
                {
                    if (!slot.PublishDraws)
                        return;

                    ulong drawCount = 0ul;
                    for (int i = 0; i < values.Length; ++i)
                        drawCount += values[i];

                    if (drawCount > 0ul)
                        Engine.Rendering.Stats.IncrementDrawCalls(SaturateToInt(drawCount));
                    break;
                }

                case GpuRenderStatsReadbackKind.StatsBuffer:
                {
                    if (slot.PublishDraws && values.Length > (int)GpuStatsLayout.StatsDrawCount)
                    {
                        uint draws = values[(int)GpuStatsLayout.StatsDrawCount];
                        if (draws > 0u)
                            Engine.Rendering.Stats.IncrementDrawCalls(SaturateToInt(draws));
                    }

                    if (slot.PublishTriangles && values.Length > (int)GpuStatsLayout.StatsTriangleCount)
                    {
                        uint triangles = values[(int)GpuStatsLayout.StatsTriangleCount];
                        if (triangles > 0u)
                            Engine.Rendering.Stats.AddTrianglesRendered(SaturateToInt(triangles));
                    }
                    break;
                }
            }
        }

        private static int SaturateToInt(ulong value)
            => value > int.MaxValue ? int.MaxValue : (int)value;

        private static int SaturateToInt(uint value)
            => value > int.MaxValue ? int.MaxValue : (int)value;

        private void QueueBoundParameterDrawCountReadback(nuint countByteOffset)
        {
            if (_boundParameterBufferForStats is null || countByteOffset > uint.MaxValue)
                return;

            // The GpuIndirectZeroReadback strategy contractually forbids CPU readback of the
            // parameter (draw-count) buffer. Posting a sync + ClientWaitSync for every count-path
            // draw caused NVIDIA's driver to corrupt its internal sync-object list (verified via
            // dotnet-dump: render thread faulted inside glClientWaitSync at nvoglv64.dll+0x108f0cd
            // with FAST_FAIL_CORRUPT_LIST_ENTRY). Skip the readback under that strategy.
            if (Engine.Rendering.ResolveMeshSubmissionStrategy() == Data.Rendering.EMeshSubmissionStrategy.GpuIndirectZeroReadback)
                return;

            QueueGpuRenderDrawCountReadback(_boundParameterBufferForStats, (uint)countByteOffset, 1u);
        }

        private void DisposeGpuRenderStatsReadbacks()
        {
            if (ShouldOrphanGLHandlesForShutdown)
                return;

            for (int i = 0; i < _gpuRenderStatsReadbackSlots.Length; ++i)
            {
                GpuRenderStatsReadbackSlot? slot = _gpuRenderStatsReadbackSlots[i];
                if (slot is null)
                    continue;

                if (slot.Sync != IntPtr.Zero)
                {
                    Api.DeleteSync(slot.Sync);
                    slot.Sync = IntPtr.Zero;
                }

                if (slot.BufferId != GLObjectBase.InvalidBindingId)
                {
                    Api.DeleteBuffer(slot.BufferId);
                    slot.BufferId = GLObjectBase.InvalidBindingId;
                }

                slot.Active = false;
                slot.CapacityBytes = 0u;
            }
        }
    }
}
