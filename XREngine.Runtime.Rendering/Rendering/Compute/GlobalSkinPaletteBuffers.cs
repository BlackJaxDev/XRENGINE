using XREngine.Extensions;
using System;
using System.Collections.Generic;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Packs per-renderer skin palettes and blendshape weights into global SSBOs
/// for the current compute skinning frame.
/// </summary>
internal sealed class GlobalSkinPaletteBuffers : IDisposable
{
    private readonly Dictionary<XRMeshRenderer, Entry> _entries = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private ulong _frameId = ulong.MaxValue;

    private XRDataBuffer? _globalSkinPalette;
    private XRDataBuffer? _globalBlendshapeWeights;

    private uint _skinPaletteCursorElements;
    private uint _blendCursorElements;

    private bool _globalSkinPaletteDirty;
    private bool _globalBlendDirty;

    public XRDataBuffer? GlobalSkinPalette => _globalSkinPalette;
    public XRDataBuffer? GlobalBlendshapeWeights => _globalBlendshapeWeights;

    public void Dispose()
    {
        _globalSkinPalette?.Destroy();
        _globalSkinPalette = null;

        _globalBlendshapeWeights?.Destroy();
        _globalBlendshapeWeights = null;

        _entries.Clear();
        _frameId = ulong.MaxValue;
        _skinPaletteCursorElements = 0;
        _blendCursorElements = 0;
        _globalSkinPaletteDirty = false;
        _globalBlendDirty = false;
    }

    public void BeginFrame(ulong frameId)
    {
        if (_frameId == frameId)
            return;

        _frameId = frameId;
        _entries.Clear();
        _skinPaletteCursorElements = 0;
        _blendCursorElements = 0;
        _globalSkinPaletteDirty = false;
        _globalBlendDirty = false;
    }

    public bool EnsurePackedForRenderer(XRMeshRenderer renderer, bool includeSkinPalette, bool includeBlendshapeWeights)
    {
        if (renderer is null)
            return false;

        if (!includeSkinPalette && !includeBlendshapeWeights)
            return false;

        if (_entries.TryGetValue(renderer, out var existing))
        {
            if ((!includeSkinPalette || existing.HasSkinPalette) && (!includeBlendshapeWeights || existing.HasBlendshapeWeights))
                return false;
        }

        bool anyChange = false;

        if (!_entries.TryGetValue(renderer, out var entry))
            entry = new Entry();

        XRMesh? mesh = renderer.Mesh;
        if (mesh is null)
            return false;

        if (includeSkinPalette && !entry.HasSkinPalette)
        {
            XRDataBuffer? activeSkinPalette = renderer.ActiveSkinPaletteBuffer;
            uint requiredElements = renderer.ActiveSkinPaletteCount;

            if (activeSkinPalette is not null && requiredElements > 0u)
            {
                EnsureGlobalSkinPaletteCapacity(_skinPaletteCursorElements + requiredElements);
                if (!renderer.HasExternalSkinPaletteSource)
                    renderer.SyncDirtyBoneMatricesToClientBuffer();

                CopyBufferRange(activeSkinPalette, _globalSkinPalette, renderer.ActiveSkinPaletteBase, _skinPaletteCursorElements, requiredElements);

                entry.SkinPaletteBase = _skinPaletteCursorElements;
                entry.SkinPaletteCount = requiredElements;
                entry.HasSkinPalette = true;

                _skinPaletteCursorElements += requiredElements;
                _globalSkinPaletteDirty = true;
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

                CopyBufferRange(renderer.BlendshapeWeights, _globalBlendshapeWeights, 0u, _blendCursorElements, requiredElements);

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

    public bool TryGetSkinPaletteSlice(XRMeshRenderer renderer, out uint baseElement, out uint elementCount)
    {
        baseElement = 0u;
        elementCount = 0u;

        if (renderer is null)
            return false;

        if (_entries.TryGetValue(renderer, out var entry) && entry.HasSkinPalette)
        {
            baseElement = entry.SkinPaletteBase;
            elementCount = entry.SkinPaletteCount;
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

    public void PushIfDirty(bool pushSkinPalette, bool pushBlendshapeWeights)
    {
        if (pushSkinPalette && _globalSkinPaletteDirty)
        {
            _globalSkinPalette?.PushSubData();
            _globalSkinPaletteDirty = false;
        }

        if (pushBlendshapeWeights && _globalBlendDirty)
        {
            _globalBlendshapeWeights?.PushSubData();
            _globalBlendDirty = false;
        }
    }

    private void EnsureGlobalSkinPaletteCapacity(uint requiredElements)
    {
        if (_globalSkinPalette is null)
        {
            _globalSkinPalette = new XRDataBuffer(
                "GlobalSkinPaletteBuffer",
                EBufferTarget.ShaderStorageBuffer,
                requiredElements,
                EComponentType.Float,
                12,
                false,
                false)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
            };
        }
        else if (_globalSkinPalette.ElementCount < requiredElements)
        {
            _globalSkinPalette.Resize(requiredElements, copyData: true, alignClientSourceToPowerOf2: true);
        }

        _globalSkinPalette.Set(0u, SkinPaletteMatrix.Identity);
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

    private static unsafe void CopyBufferRange(XRDataBuffer? src, XRDataBuffer? dst, uint srcBaseElement, uint dstBaseElement, uint elementCount)
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
        public bool HasSkinPalette;
        public uint SkinPaletteBase;
        public uint SkinPaletteCount;

        public bool HasBlendshapeWeights;
        public uint BlendshapeWeightBase;
        public uint BlendshapeWeightCount;
    }
}
