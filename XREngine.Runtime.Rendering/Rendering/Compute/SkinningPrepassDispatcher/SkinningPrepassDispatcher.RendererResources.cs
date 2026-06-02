using XREngine.Data.Rendering;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    // Per-renderer cache and state shared by the residency, output-buffer, binding, and
    // diagnostic partials. Instances are held in a ConditionalWeakTable by the dispatcher.
    private sealed partial class RendererResources(XRMeshRenderer renderer)
    {
        private readonly XRMeshRenderer _renderer = renderer;
        private bool _seededFromRenderState;
        private bool _seedInputsSettled;
        private bool _settleLogged;
        private XRMesh? _lastMesh;
        private XRDataBuffer? _skinnedBounds;
        private bool _ownsSkinnedBoundsBuffer;
        private bool _hasValidSkinnedBounds;
        private uint _skinnedBoundsVec4Offset;
        private uint _skinnedBoundsWordOffset;

        private int _lastVertexCount;
        private bool _lastWasInterleaved;
        private bool _hasValidOutput;
        private bool _lastDidSkinning;
        private bool _lastDidBlendshapes;
        private bool _lastUsedPrecombinedBlendshapes;
        private ulong _lastOutputVersion;

        public ulong LastComputePrepassFrameId;

        /// <summary>
        /// Gets the output buffer containing skinned positions.
        /// </summary>
        public XRDataBuffer? SkinnedPositions => _renderer.SkinnedPositionsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned normals.
        /// </summary>
        public XRDataBuffer? SkinnedNormals => _renderer.SkinnedNormalsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned tangents.
        /// </summary>
        public XRDataBuffer? SkinnedTangents => _renderer.SkinnedTangentsBuffer;

        /// <summary>
        /// Gets the output buffer containing skinned interleaved data.
        /// </summary>
        public XRDataBuffer? SkinnedInterleaved => _renderer.SkinnedInterleavedBuffer;

        public XRDataBuffer? SkinnedBounds => _skinnedBounds;
        public bool HasValidSkinnedBounds => _hasValidSkinnedBounds && _skinnedBounds is not null;
        public uint SkinnedBoundsVec4Offset => _skinnedBoundsVec4Offset;
        public uint SkinnedBoundsWordOffset => _skinnedBoundsWordOffset;

        public bool Validate(XRMesh mesh, bool doSkinning, bool doBlendshapes, bool isInterleaved, bool usePrecombinedBlendshapes)
        {
            // Validate read-side prerequisites first; output buffers are allocated only after
            // the mesh proves it can actually participate in the selected compute path.
            if (doSkinning)
            {
                if (_renderer.ActiveSkinPaletteBuffer is null)
                    return false;
                if (!mesh.SupportsComputeSkinning)
                    return false;
            }

            if (doBlendshapes)
            {
                if (usePrecombinedBlendshapes)
                {
                    if (!_renderer.HasValidPrecombinedBlendshapeDeltas
                        || _renderer.PrecombinedBlendshapePositionsBuffer is null
                        || (mesh.HasNormals && _renderer.PrecombinedBlendshapeNormalsBuffer is null)
                        || (mesh.HasTangents && _renderer.PrecombinedBlendshapeTangentsBuffer is null))
                    {
                        return false;
                    }
                }
                else
                {
                    if (mesh.BlendshapeSparseShapeRanges is null
                        || mesh.BlendshapeSparseRecords is null
                        || mesh.BlendshapeQuantizedDeltas is null
                        || mesh.BlendshapeQuantizationMetadata is null)
                        return false;
                    if (_renderer.BlendshapeActiveWeights is null)
                        return false;
                }
            }

            if (isInterleaved)
            {
                if (mesh.InterleavedVertexBuffer is null)
                    return false;
            }
            else
            {
                if (mesh.PositionsBuffer is null)
                    return false;
            }

            EnsureOutputBuffers(mesh, isInterleaved);

            return true;
        }

        public bool HasFrameOutput(ulong frameId, bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes)
            => LastComputePrepassFrameId == frameId
                && _hasValidOutput
                && _lastDidSkinning == doSkinning
                && _lastDidBlendshapes == doBlendshapes
                && _lastUsedPrecombinedBlendshapes == usePrecombinedBlendshapes;

        public bool CanReuseOutput(bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes)
        {
            // Cached output is valid only for stable renderer-owned inputs. External/gpu-driven
            // skin palettes can change without this cache seeing a renderer dirty flag.
            if (doSkinning && !_seedInputsSettled)
                return false;

            if (!_hasValidOutput
                || _renderer.SkinnedOutputDirty
                || _lastDidSkinning != doSkinning
                || _lastDidBlendshapes != doBlendshapes
                || _lastUsedPrecombinedBlendshapes != usePrecombinedBlendshapes
                || _renderer.HasPendingComputeSkinningInputChanges
                || _lastOutputVersion != _renderer.SkinnedOutputVersion
                || (doSkinning && (_renderer.HasExternalSkinPaletteSource || _renderer.HasGpuDrivenBoneSource)))
            {
                return false;
            }

            return doSkinning || doBlendshapes;
        }

        public void MarkOutputValid(bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes)
        {
            _hasValidOutput = true;
            _lastDidSkinning = doSkinning;
            _lastDidBlendshapes = doBlendshapes;
            _lastUsedPrecombinedBlendshapes = usePrecombinedBlendshapes;
            _lastOutputVersion = _renderer.SkinnedOutputVersion;
            _renderer.MarkSkinnedOutputClean();
        }

        public XRDataBuffer? ResetSkinnedBoundsBuffer(XRMesh mesh)
        {
            if (_skinnedBounds is null || !_ownsSkinnedBoundsBuffer)
            {
                if (_ownsSkinnedBoundsBuffer)
                    _skinnedBounds?.Destroy();

                string meshName = string.IsNullOrWhiteSpace(mesh.Name) ? $"Mesh_{mesh.GetHashCode():X}" : mesh.Name;
                _skinnedBounds = new XRDataBuffer(
                    $"{meshName}_LiveGpuSkinnedBounds",
                    EBufferTarget.ShaderStorageBuffer,
                    2u,
                    EComponentType.UInt,
                    4u,
                    false,
                    false)
                {
                    AttributeName = $"{meshName}_LiveGpuSkinnedBounds",
                    ShouldMap = true,
                    Resizable = false,
                    StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
                    RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
                };
                _ownsSkinnedBoundsBuffer = true;
            }

            _skinnedBounds.SetDataRawAtIndex(0u, PositiveInfinityPacked);
            _skinnedBounds.SetDataRawAtIndex(1u, NegativeInfinityPacked);
            if (!_skinnedBounds.IsMapped)
                _skinnedBounds.MapBufferData();
            _skinnedBounds.PushSubData();
            _hasValidSkinnedBounds = false;
            _skinnedBoundsVec4Offset = 0u;
            _skinnedBoundsWordOffset = 0u;
            return _skinnedBounds;
        }

        public bool ResetSkinnedBoundsInOutput(XRMesh mesh, bool isInterleaved)
        {
            XRDataBuffer? output = isInterleaved
                ? _renderer.SkinnedInterleavedBuffer
                : _renderer.SkinnedPositionsBuffer;
            if (output is null)
                return false;

            if (_ownsSkinnedBoundsBuffer)
                _skinnedBounds?.Destroy();

            _skinnedBounds = output;
            _ownsSkinnedBoundsBuffer = false;

            if (isInterleaved)
            {
                uint strideWords = Math.Max(1u, mesh.InterleavedStride / sizeof(float));
                uint vertexWords = checked((uint)mesh.VertexCount * strideWords);
                _skinnedBoundsWordOffset = AlignUp(vertexWords, 4u);
                _skinnedBoundsVec4Offset = _skinnedBoundsWordOffset / 4u;
                output.SetDataRawAtIndex(_skinnedBoundsWordOffset, PositiveInfinityPacked);
                output.SetDataRawAtIndex(_skinnedBoundsWordOffset + 4u, NegativeInfinityPacked);
                output.PushSubData(checked((int)(_skinnedBoundsWordOffset * sizeof(uint))), 8u * sizeof(uint));
            }
            else
            {
                _skinnedBoundsVec4Offset = (uint)mesh.VertexCount;
                _skinnedBoundsWordOffset = _skinnedBoundsVec4Offset * 4u;
                output.SetDataRawAtIndex(_skinnedBoundsVec4Offset, PositiveInfinityPacked);
                output.SetDataRawAtIndex(_skinnedBoundsVec4Offset + 1u, NegativeInfinityPacked);
                output.PushSubData(checked((int)(_skinnedBoundsVec4Offset * output.ElementSize)), 2u * output.ElementSize);
            }

            _hasValidSkinnedBounds = false;
            return true;
        }

        public void MarkSkinnedBoundsValid()
            => _hasValidSkinnedBounds = _skinnedBounds is not null;

        public bool TryReadSkinnedBounds(out AABB bounds)
        {
            bounds = default;
            if (!HasValidSkinnedBounds || _skinnedBounds is null)
                return false;

            if (!TryGetMappedAddress(_skinnedBounds, out VoidPtr mappedAddress))
                return false;

            PackedUInt4 minBits;
            PackedUInt4 maxBits;
            unsafe
            {
                PackedUInt4* ptr = (PackedUInt4*)mappedAddress.Pointer + _skinnedBoundsVec4Offset;
                minBits = ptr[0];
                maxBits = ptr[1];
            }

            Vector3 min = minBits.ToVector3();
            Vector3 max = maxBits.ToVector3();
            if (float.IsInfinity(min.X) || float.IsInfinity(min.Y) || float.IsInfinity(min.Z) ||
                float.IsInfinity(max.X) || float.IsInfinity(max.Y) || float.IsInfinity(max.Z) ||
                !new AABB(min, max).IsValid)
            {
                return false;
            }

            bounds = new AABB(min, max);
            return true;
        }

        public void SyncDynamicBuffers(bool pushSkinPalette, bool pushBlendshapeWeights)
        {
            if (pushSkinPalette)
                _renderer.PushBoneMatricesToGPU();
            if (pushBlendshapeWeights)
                _renderer.PushBlendshapeWeightsToGPU();
        }

        public void Dispose()
        {
            _renderer.SkinnedPositionsBuffer?.Destroy();
            _renderer.SkinnedNormalsBuffer?.Destroy();
            _renderer.SkinnedTangentsBuffer?.Destroy();
            _renderer.SkinnedInterleavedBuffer?.Destroy();
            if (_ownsSkinnedBoundsBuffer)
                _skinnedBounds?.Destroy();
            _renderer.SkinnedPositionsBuffer = null;
            _renderer.SkinnedNormalsBuffer = null;
            _renderer.SkinnedTangentsBuffer = null;
            _renderer.SkinnedInterleavedBuffer = null;
            _skinnedBounds = null;
            _ownsSkinnedBoundsBuffer = false;
            _hasValidSkinnedBounds = false;
            _skinnedBoundsVec4Offset = 0u;
            _skinnedBoundsWordOffset = 0u;
            _lastVertexCount = 0;
            _lastWasInterleaved = false;
            _lastMesh = null;
            _hasValidOutput = false;
            _seededFromRenderState = false;
            _seedInputsSettled = false;
            _settleLogged = false;
            _renderer.ResetSkinPaletteSeedState();
            _lastDidSkinning = false;
            _lastDidBlendshapes = false;
            _lastUsedPrecombinedBlendshapes = false;
            _lastOutputVersion = 0;
        }

        private static readonly PackedUInt4 PositiveInfinityPacked = PackedUInt4.FromVector(new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 1f));
        private static readonly PackedUInt4 NegativeInfinityPacked = PackedUInt4.FromVector(new Vector4(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, -1f));

        private static uint AlignUp(uint value, uint alignment)
            => alignment == 0u ? value : ((value + alignment - 1u) / alignment) * alignment;

        private static bool TryGetMappedAddress(XRDataBuffer buffer, out VoidPtr mappedAddress)
        {
            foreach (VoidPtr address in buffer.GetMappedAddresses())
            {
                if (address != VoidPtr.Zero)
                {
                    mappedAddress = address;
                    return true;
                }
            }

            mappedAddress = VoidPtr.Zero;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PackedUInt4
        {
            public uint X;
            public uint Y;
            public uint Z;
            public uint W;

            public static PackedUInt4 FromVector(Vector4 value)
                => new()
                {
                    X = BitConverter.SingleToUInt32Bits(value.X),
                    Y = BitConverter.SingleToUInt32Bits(value.Y),
                    Z = BitConverter.SingleToUInt32Bits(value.Z),
                    W = BitConverter.SingleToUInt32Bits(value.W),
                };

            public Vector3 ToVector3()
                => new(
                    BitConverter.UInt32BitsToSingle(X),
                    BitConverter.UInt32BitsToSingle(Y),
                    BitConverter.UInt32BitsToSingle(Z));
        }
    }
}
