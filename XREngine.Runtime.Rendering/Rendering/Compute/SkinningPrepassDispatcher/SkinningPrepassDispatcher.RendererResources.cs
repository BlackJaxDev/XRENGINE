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
        private int _disposed;
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
        private const int OutputSlotCapacity = 4;
        private OutputSlot?[] _outputSlots = new OutputSlot?[OutputSlotCapacity];
        private int _outputSlotCount;
        private int _activeOutputSlot;
        private bool _hasValidOutput;
        private bool _lastDidSkinning;
        private bool _lastDidBlendshapes;
        private bool _lastUsedPrecombinedBlendshapes;
        private ulong _lastOutputVersion;
        private int _lastDispatchedPoseHash;
        private VulkanOutputReceipt? _outputReceipt;
        private AbstractRenderer? _outputBackendOwner;
        private OutputAllocationIdentity _outputAllocationIdentity;
        private bool _outputAttemptUnfenced;
        private ulong _outputAttemptId;
        private ulong _outputAttemptVersion;
        private string? _lastReuseRejection;

        public ulong LastComputePrepassFrameId;

        ~RendererResources()
        {
            Interlocked.Exchange(ref _outputReceipt, null)?.ReleaseReference();
        }

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

        public XRMeshRenderer.SkinnedOutputResourceSnapshot CaptureSkinnedOutputResources()
            => _renderer.CaptureSkinnedOutputResources();

        public XRDataBuffer? SkinnedBounds => _skinnedBounds;
        public bool HasValidSkinnedBounds => _hasValidSkinnedBounds && _skinnedBounds is not null;
        public uint SkinnedBoundsVec4Offset => _skinnedBoundsVec4Offset;
        public uint SkinnedBoundsWordOffset => _skinnedBoundsWordOffset;

        public bool Validate(XRMesh mesh, bool doSkinning, bool doBlendshapes, bool isInterleaved, bool usePrecombinedBlendshapes)
        {
            XRMeshSkinningBufferState skinningState = mesh.GetSkinningBufferStateSnapshot();
            XRMeshBlendshapeBufferState blendshapeState = mesh.GetBlendshapeBufferStateSnapshot();
            XRMeshRenderer.BoneResourceSnapshot boneState = _renderer.CaptureBoneResources();
            XRMeshRenderer.BlendshapeResourceSnapshot rendererBlendshapeState =
                _renderer.CaptureBlendshapeResources();

            // Validate read-side prerequisites first; output buffers are allocated only after
            // the mesh proves it can actually participate in the selected compute path.
            if (doSkinning)
            {
                XRDataBuffer? activePalette = _renderer.HasExternalSkinPaletteSource
                    ? _renderer.ActiveSkinPaletteBuffer
                    : boneState.SkinPalette;
                if (activePalette is null)
                    return false;
                if (skinningState.CoreIndices is null || skinningState.CoreWeights is null)
                    return false;
            }

            if (doBlendshapes)
            {
                if (usePrecombinedBlendshapes)
                {
                    if (!rendererBlendshapeState.IsPrecombinedValidFor(mesh)
                        || rendererBlendshapeState.PrecombinedPositions is null
                        || (mesh.HasNormals && rendererBlendshapeState.PrecombinedNormals is null)
                        || (mesh.HasTangents && rendererBlendshapeState.PrecombinedTangents is null))
                    {
                        return false;
                    }
                }
                else
                {
                    if (blendshapeState.SparseShapeRanges is null
                        || blendshapeState.SparseRecords is null
                        || blendshapeState.QuantizedDeltas is null
                        || blendshapeState.QuantizationMetadata is null)
                        return false;
                    if (rendererBlendshapeState.ActiveWeights is null)
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
            => (RuntimeEngine.Rendering.State.IsVulkan
                    ? HasVulkanOutputForCurrentAttempt(
                        SkinningPrepassDispatcher.ActiveProducerAttemptId,
                        doSkinning,
                        doBlendshapes,
                        usePrecombinedBlendshapes)
                    : LastComputePrepassFrameId == frameId && _hasValidOutput)
                && _lastDidSkinning == doSkinning
                && _lastDidBlendshapes == doBlendshapes
                && _lastUsedPrecombinedBlendshapes == usePrecombinedBlendshapes;

        public bool HasVulkanOutputForCurrentAttempt(
            ulong attemptId,
            bool doSkinning,
            bool doBlendshapes,
            bool usePrecombinedBlendshapes)
        {
            VulkanOutputReceipt? receipt = _outputReceipt;
            if (receipt is null ||
                attemptId == 0UL ||
                _outputAttemptId != attemptId ||
                !receipt.IsUsableForCurrentAttempt(attemptId) ||
                !ReferenceEquals(_outputBackendOwner, receipt.BackendOwner) ||
                !ReferenceEquals(AbstractRenderer.Current, receipt.BackendOwner) ||
                _outputAttemptVersion != _renderer.SkinnedOutputVersion ||
                _lastDidSkinning != doSkinning ||
                _lastDidBlendshapes != doBlendshapes ||
                _lastUsedPrecombinedBlendshapes != usePrecombinedBlendshapes ||
                (doSkinning && _lastDispatchedPoseHash != _renderer.ComputeCurrentBonePoseHash()) ||
                !TryCaptureOutputAllocationIdentity(receipt.BackendOwner, out OutputAllocationIdentity current) ||
                !_outputAllocationIdentity.Matches(current))
            {
                return false;
            }

            return true;
        }

        public bool TryCaptureConsumerWitness(
            ulong activeAttemptId,
            AbstractRenderer backendOwner,
            bool doSkinning,
            bool doBlendshapes,
            bool usePrecombinedBlendshapes,
            out DeformationOutputWitness witness)
        {
            witness = default;
            bool currentAttempt = HasVulkanOutputForCurrentAttempt(
                activeAttemptId,
                doSkinning,
                doBlendshapes,
                usePrecombinedBlendshapes);
            if (!currentAttempt &&
                !HasSubmittedOutputForConsumer(
                    backendOwner,
                    doSkinning))
            {
                return false;
            }

            VulkanOutputReceipt? receipt = _outputReceipt;
            if (receipt is null ||
                !ReferenceEquals(receipt.BackendOwner, backendOwner) ||
                !TryCaptureOutputAllocationIdentity(backendOwner, out OutputAllocationIdentity allocationIdentity) ||
                !_outputAllocationIdentity.Matches(allocationIdentity))
            {
                return false;
            }

            receipt.RetainAuthoringUse();
            witness = new DeformationOutputWitness(
                receipt,
                backendOwner,
                receipt.AttemptId,
                _outputAttemptVersion,
                _lastDispatchedPoseHash,
                currentAttempt ? doSkinning : _lastDidSkinning,
                _lastDidBlendshapes,
                _lastUsedPrecombinedBlendshapes,
                allocationIdentity);
            return true;
        }

        private bool HasSubmittedOutputForConsumer(
            AbstractRenderer backendOwner,
            bool doSkinning)
        {
            VulkanOutputReceipt? receipt = _outputReceipt;
            return receipt is not null &&
                   receipt.IsSubmittedAndHealthy &&
                   ReferenceEquals(receipt.BackendOwner, backendOwner) &&
                   ReferenceEquals(_outputBackendOwner, backendOwner) &&
                   _outputAttemptId == receipt.AttemptId &&
                   _lastDidSkinning == doSkinning &&
                   TryCaptureOutputAllocationIdentity(backendOwner, out OutputAllocationIdentity current) &&
                   _outputAllocationIdentity.Matches(current);
        }

        public bool ValidateConsumerWitness(in DeformationOutputWitness witness)
        {
            VulkanOutputReceipt receipt = witness.Receipt;
            if (!ReferenceEquals(_outputReceipt, receipt) ||
                !ReferenceEquals(_outputBackendOwner, witness.BackendOwner) ||
                _outputAttemptId != witness.AttemptId ||
                _outputAttemptVersion != witness.OutputVersion ||
                !ReferenceEquals(receipt.BackendOwner, witness.BackendOwner) ||
                receipt.AttemptId != witness.AttemptId ||
                !receipt.IsHealthyForFrozenConsumer ||
                !TryCaptureOutputAllocationIdentity(witness.BackendOwner, out OutputAllocationIdentity current) ||
                !witness.AllocationIdentity.Matches(current))
            {
                return false;
            }

            return true;
        }

        public bool CanReuseOutput(bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes)
        {
            if (RuntimeEngine.Rendering.State.IsVulkan && _outputReceipt is null)
            {
                _hasValidOutput = false;
                _lastReuseRejection = "No output receipt";
                return false;
            }

            if (!_hasValidOutput && !TryPromoteSubmittedOutput())
            {
                _lastReuseRejection = "Submitted output could not be promoted";
                return false;
            }

            if (_outputReceipt is { } receipt &&
                (!receipt.IsSubmittedAndHealthy ||
                 !ReferenceEquals(_outputBackendOwner, AbstractRenderer.Current) ||
                 !TryCaptureOutputAllocationIdentity(receipt.BackendOwner, out OutputAllocationIdentity current) ||
                 !_outputAllocationIdentity.Matches(current)))
            {
                _hasValidOutput = false;
                _renderer.MarkSkinnedOutputDirty();
                _lastReuseRejection = "Receipt, owner, or allocation identity changed";
                return false;
            }

            // Cached output is valid only for stable renderer-owned inputs. External/gpu-driven
            // skin palettes can change without this cache seeing a renderer dirty flag.
            if (doSkinning && !_seedInputsSettled)
            {
                _lastReuseRejection = "Skin palette seed is not settled";
                return false;
            }

            if (!_hasValidOutput
                || _renderer.SkinnedOutputDirty
                || _lastDidSkinning != doSkinning
                || _lastDidBlendshapes != doBlendshapes
                || _lastUsedPrecombinedBlendshapes != usePrecombinedBlendshapes
                || _renderer.HasPendingComputeSkinningInputChanges
                || _lastOutputVersion != _renderer.SkinnedOutputVersion
                || (doSkinning && (_renderer.HasExternalSkinPaletteSource || _renderer.HasGpuDrivenBoneSource)))
            {
                _lastReuseRejection = "Renderer input or output version changed";
                return false;
            }

            if (doSkinning && _lastDispatchedPoseHash != _renderer.ComputeCurrentBonePoseHash())
            {
                _lastReuseRejection = "Bone pose hash changed";
                return false;
            }

            _lastReuseRejection = null;
            return doSkinning || doBlendshapes;
        }

        public void MarkOutputValid(bool doSkinning, bool doBlendshapes, bool usePrecombinedBlendshapes, ulong dispatchedVersion)
        {
            _hasValidOutput = true;
            _lastDidSkinning = doSkinning;
            _lastDidBlendshapes = doBlendshapes;
            _lastUsedPrecombinedBlendshapes = usePrecombinedBlendshapes;
            _lastOutputVersion = dispatchedVersion;
            _lastDispatchedPoseHash = doSkinning ? _renderer.ComputeCurrentBonePoseHash() : 0;
            _renderer.MarkSkinnedOutputClean(dispatchedVersion);
        }

        public bool TryCaptureOutputAllocationIdentity(
            AbstractRenderer renderer,
            out OutputAllocationIdentity identity)
        {
            identity = default;
            if (renderer is not IRuntimeRendererHost host ||
                !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(
                    out IGpuBufferContentReuseCapability? capability) ||
                capability is null)
            {
                return false;
            }

            XRMeshRenderer.SkinnedOutputResourceSnapshot outputs =
                _renderer.CaptureSkinnedOutputResources();
            if (outputs.Positions is null && outputs.Normals is null &&
                outputs.Tangents is null && outputs.Interleaved is null)
            {
                return false;
            }
            if (!TryCapture(capability, outputs.Positions, out GpuBufferAllocationIdentity? positions) ||
                !TryCapture(capability, outputs.Normals, out GpuBufferAllocationIdentity? normals) ||
                !TryCapture(capability, outputs.Tangents, out GpuBufferAllocationIdentity? tangents) ||
                !TryCapture(capability, outputs.Interleaved, out GpuBufferAllocationIdentity? interleaved))
            {
                return false;
            }

            identity = new OutputAllocationIdentity(positions, normals, tangents, interleaved);
            return true;
        }

        public void MarkOutputAttemptAuthored(
            ulong attemptId,
            AbstractRenderer backendOwner,
            in OutputAllocationIdentity allocationIdentity,
            bool doSkinning,
            bool doBlendshapes,
            bool usePrecombinedBlendshapes,
            ulong outputVersion)
        {
            ClearOutputReceipt();
            _hasValidOutput = false;
            _outputAttemptUnfenced = false;
            _outputBackendOwner = backendOwner;
            _outputAllocationIdentity = allocationIdentity;
            _outputAttemptId = attemptId;
            _outputAttemptVersion = outputVersion;
            _lastDidSkinning = doSkinning;
            _lastDidBlendshapes = doBlendshapes;
            _lastUsedPrecombinedBlendshapes = usePrecombinedBlendshapes;
            _lastOutputVersion = outputVersion;
            _lastDispatchedPoseHash = doSkinning ? _renderer.ComputeCurrentBonePoseHash() : 0;
            LastComputePrepassFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        }

        public void AttachOutputReceipt(VulkanOutputReceipt receipt)
        {
            if (_outputAttemptId != receipt.AttemptId)
                return;

            _outputAttemptUnfenced = false;
            _outputReceipt = receipt;
            receipt.AddReference();
        }

        public bool CanOverwriteOutput(
            AbstractRenderer backendOwner,
            out string? failureReason,
            out OutputReuseFailureSnapshot failureSnapshot)
        {
            failureReason = null;
            failureSnapshot = default;
            if (_outputReceipt?.HasConsumerUses == true)
            {
                failureReason = "Vulkan deformation output is retained by a frozen consumer.";
                failureSnapshot = CaptureOutputReuseFailure(backendOwner, failureReason);
                return false;
            }

            if (_outputReceipt is null && !_outputAttemptUnfenced)
                return true;

            if (_outputReceipt?.Fence?.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
            {
                failureReason = "Vulkan deformation output is awaiting producer submission.";
                failureSnapshot = CaptureOutputReuseFailure(backendOwner, failureReason);
                return false;
            }

            if (backendOwner is not IRuntimeRendererHost host ||
                !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(
                    out IGpuBufferContentReuseCapability? capability) ||
                capability is null)
            {
                failureReason = "Vulkan deformation output has no buffer reuse capability.";
                failureSnapshot = CaptureOutputReuseFailure(backendOwner, failureReason);
                return false;
            }

            XRMeshRenderer.SkinnedOutputResourceSnapshot outputs =
                _renderer.CaptureSkinnedOutputResources();
            if (!CanReuse(capability, outputs.Positions) ||
                !CanReuse(capability, outputs.Normals) ||
                !CanReuse(capability, outputs.Tangents) ||
                !CanReuse(capability, outputs.Interleaved))
            {
                failureReason = "Vulkan deformation output allocation is not ready for content reuse.";
                failureSnapshot = CaptureOutputReuseFailure(backendOwner, failureReason);
                return false;
            }

            ClearOutputReceipt();
            _outputAttemptUnfenced = false;
            _hasValidOutput = false;
            return true;
        }

        private OutputReuseFailureSnapshot CaptureOutputReuseFailure(AbstractRenderer backendOwner, string reason)
        {
            IGpuBufferContentReuseCapability? capability = null;
            if (backendOwner is IRuntimeRendererHost host)
                host.TryGetBackendCapability(out capability);

            XRMeshRenderer.SkinnedOutputResourceSnapshot outputs = _renderer.CaptureSkinnedOutputResources();
            return new OutputReuseFailureSnapshot(
                _renderer.Mesh?.Name,
                reason,
                _lastReuseRejection,
                _seedInputsSettled,
                _hasValidOutput,
                _renderer.SkinnedOutputDirty,
                _renderer.HasPendingComputeSkinningInputChanges,
                _lastOutputVersion,
                _renderer.SkinnedOutputVersion,
                _outputAttemptId,
                _outputAttemptVersion,
                SkinningPrepassDispatcher.ActiveProducerAttemptId,
                LastComputePrepassFrameId,
                _outputReceipt?.AttemptId,
                _outputReceipt?.ProducerAccepted,
                _outputReceipt?.ReferenceCount,
                _outputReceipt?.ConsumerUseCount,
                _lastDispatchedPoseHash,
                _renderer.ComputeCurrentBonePoseHash(),
                _renderer.HasExternalSkinPaletteSource,
                _renderer.HasGpuDrivenBoneSource,
                _outputReceipt?.Fence?.SubmissionStatus,
                _outputReceipt?.HasConsumerUses == true,
                GetReuseStatus(capability, outputs.Positions),
                GetReuseStatus(capability, outputs.Normals),
                GetReuseStatus(capability, outputs.Tangents),
                GetReuseStatus(capability, outputs.Interleaved));
        }

        private static EGpuBufferContentReuseStatus? GetReuseStatus(
            IGpuBufferContentReuseCapability? capability,
            XRDataBuffer? buffer)
            => capability is not null && buffer is not null
                ? capability.QueryBufferContentReuse(buffer)
                : null;

        private static bool TryCapture(
            IGpuBufferContentReuseCapability capability,
            XRDataBuffer? buffer,
            out GpuBufferAllocationIdentity? captured)
        {
            captured = null;
            if (buffer is null)
                return true;
            if (!capability.TryCaptureBufferAllocationIdentity(buffer, out GpuBufferAllocationIdentity value))
                return false;
            captured = value;
            return true;
        }

        private static bool CanReuse(
            IGpuBufferContentReuseCapability capability,
            XRDataBuffer? buffer)
            => buffer is null || capability.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;

        public void MarkOutputAttemptUnfenced(
            ulong attemptId,
            AbstractRenderer backendOwner,
            ulong outputVersion,
            bool doSkinning,
            bool doBlendshapes,
            bool usePrecombinedBlendshapes)
        {
            ClearOutputReceipt();
            _hasValidOutput = false;
            _outputAttemptUnfenced = true;
            _outputAttemptId = attemptId;
            _outputAttemptVersion = outputVersion;
            _outputBackendOwner = backendOwner;
            _lastDidSkinning = doSkinning;
            _lastDidBlendshapes = doBlendshapes;
            _lastUsedPrecombinedBlendshapes = usePrecombinedBlendshapes;
            _renderer.MarkSkinnedOutputDirty();
        }

        public void MarkOutputReceiptUnavailable()
        {
            _hasValidOutput = false;
            _outputAttemptUnfenced = true;
            _renderer.MarkSkinnedOutputDirty();
        }

        private bool TryPromoteSubmittedOutput()
        {
            VulkanOutputReceipt? receipt = _outputReceipt;
            if (receipt is null)
                return false;

            if (!receipt.IsSubmittedAndHealthy ||
                !ReferenceEquals(_outputBackendOwner, receipt.BackendOwner) ||
                _outputAttemptVersion != _renderer.SkinnedOutputVersion ||
                !TryCaptureOutputAllocationIdentity(receipt.BackendOwner, out OutputAllocationIdentity current) ||
                !_outputAllocationIdentity.Matches(current))
            {
                if (receipt.Fence?.SubmissionStatus == EGpuFenceSubmissionStatus.Failed ||
                    receipt.Fence?.Poll() == EGpuFenceStatus.Failed)
                {
                    _hasValidOutput = false;
                    _renderer.MarkSkinnedOutputDirty();
                }
                return false;
            }

            _hasValidOutput = true;
            _renderer.MarkSkinnedOutputClean(_outputAttemptVersion);
            return true;
        }

        private void ClearOutputReceipt()
        {
            Interlocked.Exchange(ref _outputReceipt, null)?.ReleaseReference();
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
            XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
                _renderer.CaptureSkinnedOutputResources();
            XRDataBuffer? output = isInterleaved
                ? outputState.Interleaved
                : outputState.Positions;
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

            PackedUInt4 minBits = default;
            PackedUInt4 maxBits = default;
            if (!_skinnedBounds.TryReadMapped(bytes =>
            {
                ReadOnlySpan<PackedUInt4> values = MemoryMarshal.Cast<byte, PackedUInt4>(bytes);
                minBits = values[checked((int)_skinnedBoundsVec4Offset)];
                maxBits = values[checked((int)_skinnedBoundsVec4Offset + 1)];
                return true;
            }))
                return false;

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
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _renderer.EnterResourceTeardownGate();
            try
            {
                for (int i = 0; i < _outputSlotCount; i++)
                    _outputSlots[i]?.Destroy();
                _outputSlots = new OutputSlot?[OutputSlotCapacity];
                _outputSlotCount = 0;
                _activeOutputSlot = 0;
                if (_ownsSkinnedBoundsBuffer)
                    _skinnedBounds?.Destroy();
                DestroySkinPaletteSnapshots();
                _renderer.InstallSkinnedOutputResources(null, null, null, null);
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
                ClearOutputReceipt();
                _lastDidSkinning = false;
                _lastDidBlendshapes = false;
                _lastUsedPrecombinedBlendshapes = false;
                _lastOutputVersion = 0;
                _lastDispatchedPoseHash = 0;
            }
            finally
            {
                _renderer.ExitResourceTeardownGate();
            }

            GC.SuppressFinalize(this);
        }

        private static readonly PackedUInt4 PositiveInfinityPacked = PackedUInt4.FromVector(new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 1f));
        private static readonly PackedUInt4 NegativeInfinityPacked = PackedUInt4.FromVector(new Vector4(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, -1f));

        private static uint AlignUp(uint value, uint alignment)
            => alignment == 0u ? value : ((value + alignment - 1u) / alignment) * alignment;


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
