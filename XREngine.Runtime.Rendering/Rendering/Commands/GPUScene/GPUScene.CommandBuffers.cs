// =====================================================================================
// GPUScene.CommandBuffers.cs - Double-buffered command buffer management, constants and SSBO state.
// Part of the GPUScene partial class. See GPUScene.cs for the canonical class summary.
// =====================================================================================

using XREngine.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {

        // -------------------------------------------------------------------------
        // Double-Buffered Command Buffer:
        // - _updatingCommandsBuffer: Written by Add/Remove on update/collect threads
        // - _allLoadedCommandsBuffer: Read by render thread
        // - SwapCommandBuffers() copies updating -> render during the swap phase
        // -------------------------------------------------------------------------
        
        /// <summary>
        /// Swaps the updating command buffer with the render command buffer.
        /// Call this from the swap buffers callback to make newly added/removed commands visible to the render thread.
        /// </summary>
        /// <remarks>
        /// This method copies data from the updating buffer to the render buffer, ensuring the render
        /// thread always reads a consistent snapshot while the update thread can continue modifying
        /// the updating buffer.
        /// </remarks>
        public void SwapCommandBuffers()
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.GPUScene.SwapCommandBuffers");

            using (_lock.EnterScope())
            {
                // P3: XRE_SKIP_COMMAND_SWAP_IF_CLEAN â€” short-circuit the Memory.Move + PushSubData
                // when the updating buffer's tracked content version has not advanced since the
                // previous swap. This is an env-var bisect for O-6; if it closes most of the
                // static-scene gap without visual artefacts, swap cost (and the implicit driver
                // sync it triggers against the previous-frame compute read of RenderCommandsBuffer)
                // is the dominant remaining cost.
                long currentVersion = System.Threading.Interlocked.Read(ref _updatingCommandsContentVersion);
                if (XREngine.Rendering.P3Diagnostics.SkipCommandSwapIfClean
                    && _lastSwappedCommandsContentVersion == currentVersion
                    && _updatingCommandCount == _totalCommandCount)
                {
                    XREngine.Rendering.P3Diagnostics.IncCommandSwapCleanSkipped();
                    return;
                }
                XREngine.Rendering.P3Diagnostics.IncCommandSwapExecuted();
                _lastSwappedCommandsContentVersion = currentVersion;

                // Copy the updating buffer data to the render buffer
                // This ensures the render buffer has the latest commands while keeping
                // the updating buffer's indices consistent with _commandIndexLookup
                if (_updatingCommandsBuffer is not null && _allLoadedCommandsBuffer is not null)
                {
                    // Ensure render buffer has sufficient capacity
                    if (_allLoadedCommandsBuffer.ElementCount < _updatingCommandsBuffer.ElementCount)
                        _allLoadedCommandsBuffer.Resize(_updatingCommandsBuffer.ElementCount);
                    
                    // Copy command data from updating to render buffer
                    if (_updatingCommandCount > 0)
                    {
                        uint elementCount = _updatingCommandCount.ClampMax(_updatingCommandsBuffer.ElementCount);
                        uint elementSize = _updatingCommandsBuffer.ElementSize;
                        if (elementSize == 0)
                            elementSize = (uint)(CommandFloatCount * sizeof(float));

                        uint byteCount = elementCount * elementSize;

                        if (_updatingCommandsBuffer.TryGetAddress(out var src) &&
                            _allLoadedCommandsBuffer.TryGetAddress(out var dst))
                        {
                            Memory.Move(dst, src, byteCount);
                            _allLoadedCommandsBuffer.PushSubData(0, byteCount);
                        }
                        else
                        {
                            // Both buffers should always have client-side sources; if not, the copy cannot proceed.
                            Debug.MeshesWarning("GPUScene: Command buffer TryGetAddress failed during swap â€” client-side source missing.");
                        }
                    }
                }

                CopyDrawIndexedSoABuffersToRenderSnapshot(_updatingCommandCount);
                CopyDirtyStableSoABuffersToRenderSnapshot();

                if (_updatingTransparencyMetadataBuffer is not null && _allLoadedTransparencyMetadataBuffer is not null)
                {
                    if (_allLoadedTransparencyMetadataBuffer.ElementCount < _updatingTransparencyMetadataBuffer.ElementCount)
                        _allLoadedTransparencyMetadataBuffer.Resize(_updatingTransparencyMetadataBuffer.ElementCount);

                    if (_updatingCommandCount > 0)
                    {
                        uint elementCount = _updatingCommandCount.ClampMax(_updatingTransparencyMetadataBuffer.ElementCount);
                        uint elementSize = _updatingTransparencyMetadataBuffer.ElementSize;
                        if (elementSize == 0)
                            elementSize = TransparencyMetadataUIntCount * sizeof(uint);

                        uint byteCount = elementCount * elementSize;

                        if (_updatingTransparencyMetadataBuffer.TryGetAddress(out var srcMeta) &&
                            _allLoadedTransparencyMetadataBuffer.TryGetAddress(out var dstMeta))
                        {
                            Memory.Move(dstMeta, srcMeta, byteCount);
                            _allLoadedTransparencyMetadataBuffer.PushSubData(0, byteCount);
                        }
                        else
                        {
                            // Both buffers should always have client-side sources; if not, the copy cannot proceed.
                            Debug.MeshesWarning("GPUScene: Transparency metadata buffer TryGetAddress failed during swap â€” client-side source missing.");
                        }
                    }
                }
                
                // Update the render count to match the updating count
                TotalCommandCount = _updatingCommandCount;

                // Update BVH
                if (_useInternalBvh)
                {
                    bool canRefit = _bvhReady && !_bvhDirty && _gpuBvhTree is not null && _bvhPrimitiveCount == _updatingCommandCount;
                    if (canRefit)
                        _bvhRefitPending = true;
                    else
                        MarkBvhDirtyUnlessSuppressed(_updatingCommandCount);
                }
            }
        }

        private static unsafe void CopyBufferRange(XRDataBuffer source, XRDataBuffer destination, uint startIndex, uint count)
        {
            if (count == 0u)
                return;

            if (destination.ElementCount < source.ElementCount)
                destination.Resize(source.ElementCount);

            uint elementSize = source.ElementSize;
            if (elementSize == 0u)
                return;

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            if (source.TryGetAddress(out var srcBase) &&
                destination.TryGetAddress(out var dstBase))
            {
                Memory.Move(dstBase + (int)byteOffset, srcBase + (int)byteOffset, byteCount);
                destination.PushSubData((int)byteOffset, byteCount);
            }
        }

        private void CopyDrawIndexedSoABuffersToRenderSnapshot(uint commandCount)
        {
            if (commandCount == 0u)
                return;

            if (_updatingDrawMetadataBuffer is not null && _allLoadedDrawMetadataBuffer is not null)
                CopyBufferRange(_updatingDrawMetadataBuffer, _allLoadedDrawMetadataBuffer, 0u, commandCount);

            if (_updatingBoundsBuffer is not null && _allLoadedBoundsBuffer is not null)
                CopyBufferRange(_updatingBoundsBuffer, _allLoadedBoundsBuffer, 0u, commandCount);

            _drawMetadataDirtyRange.Clear();
            _boundsDirtyRange.Clear();
        }

        private static void CopyDirtyRange(
            XRDataBuffer? source,
            XRDataBuffer? destination,
            ref DirtyRange dirtyRange)
        {
            if (!dirtyRange.HasValue || source is null || destination is null)
                return;

            CopyBufferRange(source, destination, dirtyRange.Min, dirtyRange.MaxExclusive - dirtyRange.Min);
            dirtyRange.Clear();
        }

        private void CopyDirtyStableSoABuffersToRenderSnapshot()
        {
            CopyDirtyRange(_updatingTransformBuffer, _allLoadedTransformBuffer, ref _transformDirtyRange);
            CopyDirtyRange(_updatingPrevTransformBuffer, _allLoadedPrevTransformBuffer, ref _prevTransformDirtyRange);
            CopyDirtyRange(_skinningPaletteBuffer, _skinningPaletteBuffer, ref _skinningPaletteDirtyRange);
            if (_materialStateDirtyRange.HasValue && _materialStateBuffer is not null)
            {
                uint byteOffset = _materialStateDirtyRange.Min * _materialStateBuffer.ElementSize;
                uint byteCount = (_materialStateDirtyRange.MaxExclusive - _materialStateDirtyRange.Min) * _materialStateBuffer.ElementSize;
                _materialStateBuffer.PushSubData((int)byteOffset, byteCount);
                _materialStateDirtyRange.Clear();
            }
        }

        /// <summary>
        /// Creates a new command buffer for storing GPU indirect render commands.
        /// </summary>
        private static XRDataBuffer MakeCommandsInputBuffer()
        {
            var buffer = new XRDataBuffer(
                $"RenderCommandsBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.Float,
                CommandFloatCount,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeDrawMetadataBuffer(string name)
        {
            var buffer = new XRDataBuffer(
                name,
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.UInt,
                DrawMetadataUIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeTransformBuffer(string name)
        {
            var buffer = new XRDataBuffer(
                name,
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.Float,
                TransformFloatCount,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeBoundsBuffer(string name)
        {
            var buffer = new XRDataBuffer(
                name,
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.Float,
                BoundsFloatCount,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeMaterialStateBuffer()
        {
            var buffer = new XRDataBuffer(
                "MaterialStateBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinMaterialStateCount,
                EComponentType.UInt,
                MaterialStateUIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeSkinningPaletteBuffer()
        {
            var buffer = new XRDataBuffer(
                "SkinningPaletteBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.Float,
                TransformFloatCount,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeTransparencyMetadataBuffer()
        {
            var buffer = new XRDataBuffer(
                "RenderTransparencyMetadataBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.UInt,
                TransparencyMetadataUIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
            };
            return buffer;
        }

        private static XRDataBuffer MakeLodTransitionBuffer()
        {
            var buffer = new XRDataBuffer(
                "RenderLodTransitionBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.UInt,
                LodTransitionUIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
                RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
            };
            InitializeLodTransitionBuffer(buffer);
            return buffer;
        }

        private static void InitializeLodTransitionBuffer(XRDataBuffer buffer)
        {
            // Generate() -> PostGenerated() already allocates GL storage and runs the initial
            // PushData() for resizable buffers, so an explicit PushSubData() here is a redundant
            // second upload. MapBufferData() is lazy-called by SyncLodTransitionBufferFromGpu()
            // the first time a CPU read is needed, so eager mapping just forces a driver sync on
            // the persistent-coherent allocation. Both were responsible for the multi-second
            // render-thread stall recovered as `MainThreadJobs.Normal.Invoke:GPUScene.LodTransitionBuffer.Initialize`
            // (see render-submission-perf-debug-plan.md Â§5.8 I1).
            if (RuntimeEngine.IsRenderThread)
                buffer.Generate();
            else
                RuntimeEngine.EnqueueMainThreadTask(buffer.Generate, "GPUScene.LodTransitionBuffer.Initialize");
        }

        private void EnsureLodTransitionBufferCapacity(uint requiredSize)
        {
            XRDataBuffer buffer = LodTransitionBuffer;
            if (requiredSize <= buffer.ElementCount)
                return;

            buffer.Resize(requiredSize);
            buffer.PushSubData();
        }

        private void SyncLodTransitionBufferFromGpu()
        {
            if (_lodTransitionBuffer is null)
                return;

            if (_lodTransitionBuffer.ActivelyMapping.Count == 0)
                _lodTransitionBuffer.MapBufferData();

            VoidPtr mapped = _lodTransitionBuffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
            if (!mapped.IsValid || !_lodTransitionBuffer.TryGetAddress(out VoidPtr cpuAddress) || !cpuAddress.IsValid)
                return;

            // Collect-visible mutates the CPU-side command buffers too, but only the render thread
            // may issue GL barriers. Off-thread callers fall back to the persistently mapped view.
            if (RuntimeEngine.IsRenderThread)
                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.ShaderStorage);

            Memory.Move(cpuAddress, mapped, _lodTransitionBuffer.Length);
        }

        /// <summary>
        /// Creates a new mesh data buffer for storing per-mesh metadata.
        /// </summary>
        private static XRDataBuffer MakeMeshDataBuffer()
        {
            var buffer = new XRDataBuffer(
                "MeshDataBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinMeshDataEntries,
                EComponentType.UInt,
                4,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false
            };
            return buffer;
        }

        private static XRDataBuffer MakeMeshletRangeBuffer()
        {
            var buffer = new XRDataBuffer(
                "MeshletRangeBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinMeshDataEntries,
                EComponentType.UInt,
                4,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
            };
            return buffer;
        }

        private static XRDataBuffer MakeMeshletDescriptorBuffer()
        {
            var buffer = new XRDataBuffer(
                "MeshletDescriptorBuffer",
                EBufferTarget.ShaderStorageBuffer,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
            };
            buffer.Allocate((uint)Marshal.SizeOf<GpuMeshletDescriptor>(), MinMeshletDescriptorEntries);
            return buffer;
        }

        private static XRDataBuffer MakeMeshletVertexIndexBuffer()
        {
            var buffer = new XRDataBuffer(
                "MeshletVertexIndexBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinMeshletIndexEntries,
                EComponentType.UInt,
                1,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
                PadEndingToVec4 = false,
            };
            return buffer;
        }

        private static XRDataBuffer MakeMeshletTriangleIndexBuffer()
        {
            var buffer = new XRDataBuffer(
                "MeshletTriangleIndexBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinMeshletIndexEntries,
                EComponentType.Byte,
                1,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
                PadEndingToVec4 = false,
            };
            return buffer;
        }

        /// <summary>The initial size of the command buffer. It will grow or shrink as needed at powers of two.</summary>
        public const uint MinCommandCount = 8;

        /// <summary>Number of 32-bit lanes per compact GPU command (80 bytes).</summary>
        public const int CommandFloatCount = 20;

        /// <summary>Number of uint components per hot GPU command (80 bytes).</summary>
        public const int CommandHotUIntCount = 20;

        public const uint DrawMetadataUIntCount = 16;
        public const uint TransformFloatCount = 16;
        public const uint BoundsFloatCount = 16;
        public const uint MaterialStateUIntCount = 8;
        private const uint MinMaterialStateCount = 16;

        /// <summary>Number of components in the visible count buffer.</summary>
        public const uint VisibleCountComponents = 3;

        /// <summary>Index for visible draw count in the visible count buffer.</summary>
        public const uint VisibleCountDrawIndex = 0;

        /// <summary>Index for visible instance count in the visible count buffer.</summary>
        public const uint VisibleCountInstanceIndex = 1;

        /// <summary>Index for overflow marker in the visible count buffer.</summary>
        public const uint VisibleCountOverflowIndex = 2;

        /// <summary>Number of uint components per transparency metadata entry.</summary>
        public const uint TransparencyMetadataUIntCount = 4;
        [StructLayout(LayoutKind.Sequential)]
        public struct GPULodTransitionState
        {
            public const uint ActiveFlag = 1u;

            public uint PreviousMeshID;
            public uint PreviousLODLevel;
            public uint Flags;
            public uint ProgressBits;

            public readonly float Progress
                => BitConverter.UInt32BitsToSingle(ProgressBits);

            public static GPULodTransitionState Active(uint previousMeshId, uint previousLodLevel, float progress)
                => new()
                {
                    PreviousMeshID = previousMeshId,
                    PreviousLODLevel = previousLodLevel,
                    Flags = ActiveFlag,
                    ProgressBits = BitConverter.SingleToUInt32Bits(progress),
                };
        }

        public const uint LodTransitionUIntCount = 4;

        /// <summary>Minimum capacity for mesh data entries buffer.</summary>
        private const uint MinMeshDataEntries = 16;
        private const uint MinMeshletDescriptorEntries = 1;
        private const uint MinMeshletIndexEntries = 1;

        // -------------------------------------------------------------------------
        // Command Buffer State: Buffers, counts, and tracking structures
        // -------------------------------------------------------------------------

        /// <summary>Maps XRMesh instances to unique GPU IDs.</summary>
        private readonly ConcurrentDictionary<XRMesh, uint> _meshIDMap = new();

        /// <summary>Next mesh ID to assign (incremented atomically).</summary>
        private uint _nextMeshID = 1;

        /// <summary>Lock for thread-safe access to command buffers.</summary>
        private readonly Lock _lock = new();

        /// <summary>Debug labels for meshes (for logging/debugging).</summary>
        private readonly ConcurrentDictionary<XRMesh, string> _meshDebugLabels = new();

        /// <summary>Meshes that failed GPU validation with their error messages.</summary>
        private readonly ConcurrentDictionary<XRMesh, string> _unsupportedMeshMessages = new();

        /// <summary>Buffer storing per-mesh metadata (index/vertex offsets).</summary>
        private XRDataBuffer? _meshDataBuffer;
        private XRDataBuffer? _meshletRangeBuffer;
        private XRDataBuffer? _meshletDescriptorBuffer;
        private XRDataBuffer? _meshletVertexIndexBuffer;
        private XRDataBuffer? _meshletTriangleIndexBuffer;
        private readonly List<GpuMeshletDescriptor> _meshletDescriptors = [];
        private readonly List<uint> _meshletVertexIndices = [];
        private readonly List<byte> _meshletTriangleIndices = [];
        private readonly Dictionary<uint, GpuMeshletRange> _meshletRangesByMeshId = [];
        private readonly Dictionary<uint, ulong> _meshletFreshnessByMeshId = [];
        private DirtyRange _meshletRangeDirtyRange;

        /// <summary>
        /// Buffer storing mesh index and vertex data for all submeshes in reference to the global VAO.
        /// Layout per entry (uint4): [IndexCount, FirstIndex, FirstVertex, Flags].
        /// </summary>
        public XRDataBuffer MeshDataBuffer => _meshDataBuffer ??= MakeMeshDataBuffer();

        public XRDataBuffer MeshletRangeBuffer => _meshletRangeBuffer ??= MakeMeshletRangeBuffer();

        public XRDataBuffer MeshletDescriptorBuffer => _meshletDescriptorBuffer ??= MakeMeshletDescriptorBuffer();

        public XRDataBuffer MeshletVertexIndexBuffer => _meshletVertexIndexBuffer ??= MakeMeshletVertexIndexBuffer();

        public XRDataBuffer MeshletTriangleIndexBuffer => _meshletTriangleIndexBuffer ??= MakeMeshletTriangleIndexBuffer();

        public int MeshletDescriptorCount => _meshletDescriptors.Count;

        public int MeshletVertexIndexCount => _meshletVertexIndices.Count;

        public int MeshletTriangleIndexByteCount => _meshletTriangleIndices.Count;

        public ulong MeshletBufferBytesResident =>
            GetBufferByteCount(_meshletRangeBuffer) +
            GetBufferByteCount(_meshletDescriptorBuffer) +
            GetBufferByteCount(_meshletVertexIndexBuffer) +
            GetBufferByteCount(_meshletTriangleIndexBuffer);

        private static ulong GetBufferByteCount(XRDataBuffer? buffer)
            => buffer is null ? 0UL : (ulong)buffer.ElementCount * buffer.ElementSize;

        /// <summary>
        /// Per-mesh metadata entry stored in MeshDataBuffer.
        /// </summary>
        public struct MeshDataEntry
        {
            /// <summary>Number of indices in this submesh.</summary>
            public uint IndexCount;

            /// <summary>First index offset in the atlas index buffer.</summary>
            public uint FirstIndex;

            /// <summary>First vertex offset in the atlas vertex buffers.</summary>
            public uint FirstVertex;

            /// <summary>Per-entry flags. Low bits encode the active atlas tier.</summary>
            public uint Flags;
        }

        private XRDataBuffer? _allLoadedDrawMetadataBuffer;
        private XRDataBuffer? _updatingDrawMetadataBuffer;
        private XRDataBuffer? _allLoadedTransformBuffer;
        private XRDataBuffer? _updatingTransformBuffer;
        private XRDataBuffer? _allLoadedPrevTransformBuffer;
        private XRDataBuffer? _updatingPrevTransformBuffer;
        private XRDataBuffer? _allLoadedBoundsBuffer;
        private XRDataBuffer? _updatingBoundsBuffer;
        private XRDataBuffer? _materialStateBuffer;
        private XRDataBuffer? _skinningPaletteBuffer;

        private DirtyRange _drawMetadataDirtyRange;
        private DirtyRange _transformDirtyRange;
        private DirtyRange _prevTransformDirtyRange;
        private DirtyRange _boundsDirtyRange;
        private DirtyRange _materialStateDirtyRange;
        private DirtyRange _skinningPaletteDirtyRange;

        public XRDataBuffer DrawMetadataBuffer => _allLoadedDrawMetadataBuffer ??= MakeDrawMetadataBuffer("DrawMetadataBuffer");
        private XRDataBuffer UpdatingDrawMetadataBuffer => _updatingDrawMetadataBuffer ??= MakeDrawMetadataBuffer("UpdatingDrawMetadataBuffer");
        public XRDataBuffer TransformBuffer => _allLoadedTransformBuffer ??= MakeTransformBuffer("TransformBuffer");
        private XRDataBuffer UpdatingTransformBuffer => _updatingTransformBuffer ??= MakeTransformBuffer("UpdatingTransformBuffer");
        public XRDataBuffer PrevTransformBuffer => _allLoadedPrevTransformBuffer ??= MakeTransformBuffer("PrevTransformBuffer");
        private XRDataBuffer UpdatingPrevTransformBuffer => _updatingPrevTransformBuffer ??= MakeTransformBuffer("UpdatingPrevTransformBuffer");
        public XRDataBuffer BoundsBuffer => _allLoadedBoundsBuffer ??= MakeBoundsBuffer("BoundsBuffer");
        private XRDataBuffer UpdatingBoundsBuffer => _updatingBoundsBuffer ??= MakeBoundsBuffer("UpdatingBoundsBuffer");
        public XRDataBuffer MaterialStateBuffer => _materialStateBuffer ??= MakeMaterialStateBuffer();
        public XRDataBuffer SkinningPaletteBuffer => _skinningPaletteBuffer ??= MakeSkinningPaletteBuffer();

        /// <summary>Render buffer - read by the render thread. Contains stable command data.</summary>
        private XRDataBuffer? _allLoadedCommandsBuffer;

        /// <summary>Render buffer - read by the render thread. Contains stable per-command transparency metadata.</summary>
        private XRDataBuffer? _allLoadedTransparencyMetadataBuffer;
    /// <summary>Per-command LOD transition state shared across frames.</summary>
    private XRDataBuffer? _lodTransitionBuffer;

        /// <summary>Updating buffer - written by Add/Remove operations. Swapped to render buffer.</summary>
        private XRDataBuffer? _updatingCommandsBuffer;

        /// <summary>Updating buffer - written by Add/Remove operations. Swapped to render buffer.</summary>
        private XRDataBuffer? _updatingTransparencyMetadataBuffer;

        /// <summary>
        /// Gets the render command buffer containing all commands for this scene.
        /// This buffer is read by the render thread and updated via <see cref="SwapCommandBuffers"/>.
        /// </summary>
        public XRDataBuffer AllLoadedCommandsBuffer => _allLoadedCommandsBuffer ??= MakeCommandsInputBuffer();
        public XRDataBuffer AllLoadedTransparencyMetadataBuffer => _allLoadedTransparencyMetadataBuffer ??= MakeTransparencyMetadataBuffer();
            public XRDataBuffer LodTransitionBuffer => _lodTransitionBuffer ??= MakeLodTransitionBuffer();
        
        /// <summary>
        /// Gets the updating command buffer being written to by Add/Remove operations.
        /// Swapped with AllLoadedCommandsBuffer via <see cref="SwapCommandBuffers"/>.
        /// </summary>
        private XRDataBuffer UpdatingCommandsBuffer => _updatingCommandsBuffer ??= MakeCommandsInputBuffer();
        private XRDataBuffer UpdatingTransparencyMetadataBuffer => _updatingTransparencyMetadataBuffer ??= MakeTransparencyMetadataBuffer();

        /// <summary>Debug/compatibility meshlet collection for the legacy direct task-dispatch path.</summary>
        private readonly MeshletCollection _meshlets = new();
        private bool _meshletsDirty = true;

        /// <summary>Gets the debug/compatibility meshlet collection for diagnostic direct mesh shader rendering.</summary>
        public MeshletCollection Meshlets => _meshlets;

        /// <summary>
        /// Renders the debug/compatibility meshlet path. Production meshlet storage is owned by
        /// MeshletRangeBuffer/MeshletDescriptorBuffer/MeshletVertexIndexBuffer/MeshletTriangleIndexBuffer.
        /// </summary>
        public bool RenderMeshlets(XRCamera camera, int renderPass)
            => RenderMeshlets(camera, renderPass, null);

        public bool RenderMeshlets(
            XRCamera camera,
            int renderPass,
            Func<GPUScene, uint, bool>? commandVisibility,
            bool meshletDebugDisplay = false)
        {
            if (camera is null)
                return false;

            EnsureDebugMeshletsReadyForRender();
            return _meshlets.Render(camera, renderPass, this, commandVisibility, meshletDebugDisplay);
        }

        private void EnsureDebugMeshletsReadyForRender()
        {
            if (!_meshletsDirty)
                return;

            using (_lock.EnterScope())
            {
                if (_meshletsDirty)
                    RebuildDebugMeshletCollectionFromUpdatingCommands();
            }
        }

        private void RebuildDebugMeshletCollectionFromUpdatingCommands()
        {
            _meshlets.Clear();

            foreach ((uint commandIndex, (IRenderCommandMesh command, int subMeshIndex) entry) in _commandIndexLookup.OrderBy(static kvp => kvp.Key))
            {
                IRenderCommandMesh meshCommand = entry.command;
                var subMeshes = meshCommand.Mesh?.GetMeshes();
                if (subMeshes is null || (uint)entry.subMeshIndex >= (uint)subMeshes.Length)
                    continue;

                (XRMesh? mesh, XRMaterial? materialSource) = subMeshes[entry.subMeshIndex];
                XRMaterial? material = meshCommand.MaterialOverride ?? materialSource;
                if (mesh is null || material is null)
                    continue;

                GetOrCreateMaterialID(material, out uint materialID);
                _meshlets.AddMaterial(materialID, MeshOptimizerIntegration.CreateMeshletMaterial(material));

                MeshletGenerationSettings? settings = meshCommand.Mesh?.SourceSubMeshAsset?.MeshOptimizer?.Meshlets;
                if (settings is { Enabled: false })
                    continue;

                Matrix4x4 modelMatrix = meshCommand.WorldMatrixIsModelMatrix ? meshCommand.WorldMatrix : Matrix4x4.Identity;
                _meshlets.AddMesh(mesh, commandIndex, materialID, meshCommand.RenderPass, modelMatrix, settings);
            }

            _meshletsDirty = false;
        }

        /// <summary>Bounding box encompassing all scene geometry.</summary>
        private AABB _bounds;

        /// <summary>Gets or sets the bounding box encompassing all scene geometry.</summary>
        public AABB Bounds
        {
            get => _bounds;
            set => SetField(ref _bounds, value);
        }

        /// <summary>Command count for the render buffer (read by render thread).</summary>
        private uint _totalCommandCount = 0;

        /// <summary>Command count for the updating buffer (written by Add/Remove).</summary>
        private uint _updatingCommandCount = 0;

        /// <summary>Conservative scene-wide count of commands that require skinning resources.</summary>
        private uint _skinnedCommandCount = 0;

        /// <summary>
        /// P3 instrumentation: monotonic version of the updating command buffer's content.
        /// Bumped wherever the updating buffer's bytes are mutated. Compared against
        /// <see cref="_lastSwappedCommandsContentVersion"/> inside <see cref="SwapCommandBuffers"/>
        /// when <c>XRE_SKIP_COMMAND_SWAP_IF_CLEAN=1</c> to short-circuit the Memory.Move +
        /// PushSubData when content has not changed since the last swap.
        /// Reserved for the O-6 implementation phase; for now the env-var gates it.
        /// </summary>
        private long _updatingCommandsContentVersion = 0;
        private long _lastSwappedCommandsContentVersion = -1;

        /// <summary>Bump the updating-command content version. Called from every mutation site.</summary>
        internal void MarkUpdatingCommandsDirty()
            => System.Threading.Interlocked.Increment(ref _updatingCommandsContentVersion);

        /// <summary>
        /// Gets the number of commands currently in the render buffer.
        /// Each command represents one submesh - a single <see cref="IRenderCommandMesh"/> 
        /// may produce multiple commands if it has multiple submeshes.
        /// </summary>
        public uint TotalCommandCount
        {
            get => _totalCommandCount;
            private set => SetField(ref _totalCommandCount, value);
        }

        /// <summary>
        /// Gets the number of registered commands with a non-zero skin id.
        /// Production meshlet rendering uses this as a conservative fallback gate until
        /// skinned meshlet vertex-weight buffers are scene-owned.
        /// </summary>
        public uint SkinnedCommandCount => _skinnedCommandCount;
        
        /// <summary>
        /// Gets or sets the command count for the updating buffer.
        /// This count is swapped to TotalCommandCount during <see cref="SwapCommandBuffers"/>.
        /// </summary>
        private uint UpdatingCommandCount
        {
            get => _updatingCommandCount;
            set
            {
                if (SetField(ref _updatingCommandCount, value))
                    MarkUpdatingCommandsDirty();
            }
        }

        /// <summary>Gets the current allocated capacity of the command buffer.</summary>
        public uint AllocatedMaxCommandCount => AllLoadedCommandsBuffer.ElementCount;

        /// <summary>
        /// Ensures command buffers can hold at least <paramref name="requiredCapacity"/> entries.
        /// Uses the existing power-of-two growth policy and never shrinks.
        /// </summary>
        public uint EnsureCommandCapacity(uint requiredCapacity)
        {
            uint safeRequired = Math.Max(requiredCapacity, MinCommandCount);
            using (_lock.EnterScope())
            {
                SyncLodTransitionBufferFromGpu();
                VerifyUpdatingBufferSize(safeRequired);
                VerifyCommandBufferSize(safeRequired);
                EnsureDrawIndexedSoACapacity(safeRequired);
                return AllLoadedCommandsBuffer.ElementCount;
            }
        }

        private static void EnsureBufferCapacity(XRDataBuffer buffer, uint requiredCapacity)
        {
            if (requiredCapacity > buffer.ElementCount)
                buffer.Resize(XRMath.NextPowerOfTwo(requiredCapacity).ClampMin(MinCommandCount));
        }

        private void EnsureDrawIndexedSoACapacity(uint requiredCapacity)
        {
            EnsureBufferCapacity(UpdatingDrawMetadataBuffer, requiredCapacity);
            EnsureBufferCapacity(DrawMetadataBuffer, requiredCapacity);
            EnsureBufferCapacity(UpdatingBoundsBuffer, requiredCapacity);
            EnsureBufferCapacity(BoundsBuffer, requiredCapacity);
        }

        private void EnsureTransformCapacity(uint requiredCapacity)
        {
            EnsureBufferCapacity(UpdatingTransformBuffer, requiredCapacity);
            EnsureBufferCapacity(TransformBuffer, requiredCapacity);
            EnsureBufferCapacity(UpdatingPrevTransformBuffer, requiredCapacity);
            EnsureBufferCapacity(PrevTransformBuffer, requiredCapacity);
        }

        private void EnsureMaterialStateCapacity(uint requiredCapacity)
        {
            if (requiredCapacity > MaterialStateBuffer.ElementCount)
                MaterialStateBuffer.Resize(XRMath.NextPowerOfTwo(requiredCapacity).ClampMin(MinMaterialStateCount));
        }

        private void EnsureSkinningPaletteCapacity(uint requiredCapacity)
            => EnsureBufferCapacity(SkinningPaletteBuffer, requiredCapacity);

        /// <summary>Maps mesh commands to their GPU command indices (for multi-submesh support).</summary>
        private readonly Dictionary<IRenderCommandMesh, List<uint>> _commandIndicesPerMeshCommand = [];

    }
}
