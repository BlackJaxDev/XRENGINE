using Extensions;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Packs per-renderer animation inputs (bone matrices / inv bind matrices / blendshape weights)
/// into global SSBOs for the current render frame.
///
/// This is intended specifically for the compute skinning + blendshape prepass.
/// </summary>
internal sealed class GlobalAnimationInputBuffers : IDisposable
{
    private readonly Dictionary<XRMeshRenderer, Entry> _entries = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private ulong _frameId = ulong.MaxValue;

    private XRDataBuffer? _globalBoneMatrices;
    private XRDataBuffer? _globalBoneInvBindMatrices;
    private XRDataBuffer? _globalBlendshapeWeights;

    private uint _boneCursorElements;
    private uint _blendCursorElements;

    private bool _globalBonesDirty;
    private bool _globalBlendDirty;

    public XRDataBuffer? GlobalBoneMatrices => _globalBoneMatrices;
    public XRDataBuffer? GlobalBoneInvBindMatrices => _globalBoneInvBindMatrices;
    public XRDataBuffer? GlobalBlendshapeWeights => _globalBlendshapeWeights;

    public void Dispose()
    {
        _globalBoneMatrices?.Destroy();
        _globalBoneMatrices = null;

        _globalBoneInvBindMatrices?.Destroy();
        _globalBoneInvBindMatrices = null;

        _globalBlendshapeWeights?.Destroy();
        _globalBlendshapeWeights = null;

        _entries.Clear();
        _frameId = ulong.MaxValue;
        _boneCursorElements = 0;
        _blendCursorElements = 0;
        _globalBonesDirty = false;
        _globalBlendDirty = false;
    }

    public void BeginFrame(ulong frameId)
    {
        if (_frameId == frameId)
            return;

        _frameId = frameId;
        _entries.Clear();
        _boneCursorElements = 0;
        _blendCursorElements = 0;
        _globalBonesDirty = false;
        _globalBlendDirty = false;
    }

    public bool EnsurePackedForRenderer(XRMeshRenderer renderer, bool includeBones, bool includeBlendshapeWeights)
    {
        if (renderer is null)
            return false;

        if (!includeBones && !includeBlendshapeWeights)
            return false;

        if (_entries.TryGetValue(renderer, out var existing))
        {
            // If this renderer is already packed for the requested streams, nothing to do.
            if ((!includeBones || existing.HasBones) && (!includeBlendshapeWeights || existing.HasBlendshapeWeights))
                return false;
        }

        bool anyChange = false;

        if (!_entries.TryGetValue(renderer, out var entry))
            entry = new Entry();

        XRMesh? mesh = renderer.Mesh;
        if (mesh is null)
            return false;

        if (includeBones && !entry.HasBones)
        {
            uint boneCount = (uint)(mesh.UtilizedBones?.Length ?? 0);
            uint requiredElements = boneCount + 1u;

            if (requiredElements > 0u)
            {
                EnsureGlobalBoneBuffersCapacity(_boneCursorElements + requiredElements);

                // Copy the renderer's current bone matrices / inv bind matrices into the global buffers.
                // Keep the +1 identity slot at index 0 to preserve existing bone index semantics.
                CopyMatrixRange(renderer.BoneMatricesBuffer, _globalBoneMatrices, 0u, _boneCursorElements, requiredElements);
                CopyMatrixRange(renderer.BoneInvBindMatricesBuffer, _globalBoneInvBindMatrices, 0u, _boneCursorElements, requiredElements);

                entry.BoneBase = _boneCursorElements;
                entry.BoneCount = requiredElements;
                entry.HasBones = true;

                _boneCursorElements += requiredElements;
                _globalBonesDirty = true;
                anyChange = true;
            }
        }

        if (includeBlendshapeWeights && !entry.HasBlendshapeWeights)
        {
            uint blendCount = mesh.BlendshapeCount;
            uint requiredElements = blendCount.Align(4u);

            if (requiredElements > 0u)
            {
                EnsureGlobalBlendshapeWeightsCapacity(_blendCursorElements + requiredElements);

                CopyFloatRange(renderer.BlendshapeWeights, _globalBlendshapeWeights, 0u, _blendCursorElements, requiredElements);

                entry.BlendshapeWeightBase = _blendCursorElements;
                entry.BlendshapeWeightCount = requiredElements;
                entry.HasBlendshapeWeights = true;

                _blendCursorElements += requiredElements;
                _globalBlendDirty = true;
                anyChange = true;
            }
        }

        _entries[renderer] = entry;
        return anyChange;
    }

    public bool TryGetBoneSlice(XRMeshRenderer renderer, out uint baseElement, out uint elementCount)
    {
        baseElement = 0u;
        elementCount = 0u;

        if (renderer is null)
            return false;

        if (_entries.TryGetValue(renderer, out var entry) && entry.HasBones)
        {
            baseElement = entry.BoneBase;
            elementCount = entry.BoneCount;
            return true;
        }

        return false;
    }

    public bool TryGetBlendshapeWeightsSlice(XRMeshRenderer renderer, out uint baseElement, out uint elementCount)
    {
        baseElement = 0u;
        elementCount = 0u;

        if (renderer is null)
            return false;

        if (_entries.TryGetValue(renderer, out var entry) && entry.HasBlendshapeWeights)
        {
            baseElement = entry.BlendshapeWeightBase;
            elementCount = entry.BlendshapeWeightCount;
            return true;
        }

        return false;
    }

    public void PushIfDirty(bool pushBones, bool pushBlendshapeWeights)
    {
        // NOTE: PushSubData(offset, length) is currently unreliable on some backends;
        // we intentionally push full buffers when dirty.
        if (pushBones && _globalBonesDirty)
        {
            _globalBoneMatrices?.PushSubData();
            _globalBoneInvBindMatrices?.PushSubData();
            _globalBonesDirty = false;
        }

        if (pushBlendshapeWeights && _globalBlendDirty)
        {
            _globalBlendshapeWeights?.PushSubData();
            _globalBlendDirty = false;
        }
    }

    private void EnsureGlobalBoneBuffersCapacity(uint requiredElements)
    {
        if (_globalBoneMatrices is null)
        {
            _globalBoneMatrices = new XRDataBuffer(
                "GlobalBoneMatricesBuffer",
                EBufferTarget.ShaderStorageBuffer,
                requiredElements,
                EComponentType.Float,
                16,
                false,
                false)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
            };
        }
        else if (_globalBoneMatrices.ElementCount < requiredElements)
        {
            _globalBoneMatrices.Resize(requiredElements, copyData: true, alignClientSourceToPowerOf2: true);
        }

        if (_globalBoneInvBindMatrices is null)
        {
            _globalBoneInvBindMatrices = new XRDataBuffer(
                "GlobalBoneInvBindMatricesBuffer",
                EBufferTarget.ShaderStorageBuffer,
                requiredElements,
                EComponentType.Float,
                16,
                false,
                false)
            {
                Usage = EBufferUsage.StaticCopy,
                DisposeOnPush = false,
            };
        }
        else if (_globalBoneInvBindMatrices.ElementCount < requiredElements)
        {
            _globalBoneInvBindMatrices.Resize(requiredElements, copyData: true, alignClientSourceToPowerOf2: true);
        }

        // Ensure identity at element 0 for consistency when used with base=0 single-renderer cases.
        // Per-renderer slices also preserve identity at their own base element.
        _globalBoneMatrices.Set(0u, Matrix4x4.Identity);
        _globalBoneInvBindMatrices.Set(0u, Matrix4x4.Identity);
    }

    private void EnsureGlobalBlendshapeWeightsCapacity(uint requiredElements)
    {
        if (_globalBlendshapeWeights is null)
        {
            _globalBlendshapeWeights = new XRDataBuffer(
                "GlobalBlendshapeWeightsBuffer",
                EBufferTarget.ShaderStorageBuffer,
                requiredElements,
                EComponentType.Float,
                1,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false,
            };
        }
        else if (_globalBlendshapeWeights.ElementCount < requiredElements)
        {
            _globalBlendshapeWeights.Resize(requiredElements, copyData: true, alignClientSourceToPowerOf2: true);
        }
    }

    private static unsafe void CopyMatrixRange(XRDataBuffer? src, XRDataBuffer? dst, uint srcBaseElement, uint dstBaseElement, uint elementCount)
    {
        if (src is null || dst is null)
            return;

        uint byteCount = elementCount * src.ElementSize;
        int srcOffsetBytes = checked((int)(srcBaseElement * src.ElementSize));
        int dstOffsetBytes = checked((int)(dstBaseElement * dst.ElementSize));

        VoidPtr srcPtr = src.Address + srcOffsetBytes;
        VoidPtr dstPtr = dst.Address + dstOffsetBytes;
        Memory.Move(dstPtr, srcPtr, byteCount);
    }

    private static unsafe void CopyFloatRange(XRDataBuffer? src, XRDataBuffer? dst, uint srcBaseElement, uint dstBaseElement, uint elementCount)
    {
        if (src is null || dst is null)
            return;

        uint byteCount = elementCount * src.ElementSize;
        int srcOffsetBytes = checked((int)(srcBaseElement * src.ElementSize));
        int dstOffsetBytes = checked((int)(dstBaseElement * dst.ElementSize));

        VoidPtr srcPtr = src.Address + srcOffsetBytes;
        VoidPtr dstPtr = dst.Address + dstOffsetBytes;
        Memory.Move(dstPtr, srcPtr, byteCount);
    }

    private struct Entry
    {
        public bool HasBones;
        public uint BoneBase;
        public uint BoneCount;

        public bool HasBlendshapeWeights;
        public uint BlendshapeWeightBase;
        public uint BlendshapeWeightCount;
    }
}
