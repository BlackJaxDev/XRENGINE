using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Renderer-owned controls for one distinct authored mesh. This is resource
/// state only; all draw paths keep using the original renderer and callbacks.
/// </summary>
internal sealed class XRMeshDeformationInputs : IDisposable
{
    private readonly XRMeshRenderer _owner;
    private readonly XRMesh _mesh;
    private XRMeshSkinningBufferState? _skinningState;
    private Matrix4x4? _bindRoot;
    private XRDataBuffer? _palette;
    private XRDataBuffer? _weights;
    private XRDataBuffer? _activeMorphs;
    private string[]? _shapeNames;
    private uint _shapeCount;
    private uint _activeCount;
    private ulong _poseVersion = 1;
    private ulong _morphVersion = 1;
    private object? _externalOwner;
    private XRDataBuffer? _externalPalette;
    private XRDataBuffer? _externalPreviousPalette;
    private uint _externalPaletteBase;
    private uint _externalPaletteCount;
    private XRMeshSkinningBufferState? _externalSkinningState;
    private Matrix4x4? _externalBindRoot;

    internal XRMeshDeformationInputs(XRMeshRenderer owner, XRMesh mesh)
    {
        _owner = owner;
        _mesh = mesh;
    }

    internal XRMeshDeformationInputSnapshot Prepare(bool skinning, bool blendshapes, bool publish, bool observePose)
    {
        _owner.ValidateResourcePublicationLease();
        bool external = RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader &&
            _externalPalette is not null && _externalPaletteCount > 0;
        bool sharedPalette = false;
        XRMeshDeformationInputSnapshot sharedInputs = default;
        if (skinning)
        {
            _mesh.EnsureComputeSkinningBuffers();
            if (external && (!ReferenceEquals(_externalSkinningState, _mesh.GetSkinningBufferStateSnapshot()) ||
                _externalBindRoot != _mesh.BindRootMatrix))
                throw new NotSupportedException("MeshDeformation.SourcePaletteGenerationChanged: republish the external palette for the selected mesh's current skinning generation and bind root.");
            if (!external && (_owner.HasExternalSkinPaletteSource || _owner.HasGpuDrivenBoneSource))
            {
                if (_owner.Mesh is not { } primary ||
                    !_owner.TryPrepareDeformationInputs(primary, true, false, publish, out sharedInputs, observePose))
                    throw new InvalidOperationException("MeshDeformation.SourcePalettePending: the exact shared primary palette is not yet available.");
                sharedPalette = HasExactRendererPaletteOrder();
                if (!sharedPalette)
                    throw new NotSupportedException("MeshDeformation.SourcePaletteMissing: a GPU-driven renderer must publish the selected submesh's exact palette when its bone order or bind transforms differ from the primary mesh.");
            }
            if (!external && !sharedPalette) RefreshPalette();
        }
        if (blendshapes)
        {
            EnsureMorphs();
            RefreshActiveMorphs();
        }
        return new(
            !skinning ? null : external ? _externalPalette : sharedPalette ? sharedInputs.Palette : _palette,
            !skinning ? null : external ? _externalPreviousPalette : sharedPalette ? sharedInputs.PreviousPalette : null,
            !skinning ? 0 : external ? _externalPaletteBase : sharedPalette ? sharedInputs.PaletteBase : 0,
            !skinning ? 0 : external ? _externalPaletteCount : sharedPalette ? sharedInputs.PaletteCount : checked((uint)_skinningState!.UtilizedBones.Length + 1),
            skinning && (external || sharedPalette),
            blendshapes ? _activeMorphs : null,
            blendshapes ? _activeCount : 0,
            sharedPalette ? sharedInputs.PoseVersion : _poseVersion,
            _morphVersion)
        {
            UsesLocalBoneOwnership = skinning && sharedPalette && sharedInputs.UsesLocalBoneOwnership,
        };
    }

    private bool HasExactRendererPaletteOrder()
    {
        if (_owner.Mesh is not { } primary || primary.BindRootMatrix != _mesh.BindRootMatrix) return false;
        XRMeshSkinningBufferState primaryState = primary.GetSkinningBufferStateSnapshot();
        XRMeshSkinningBufferState selected = _mesh.GetSkinningBufferStateSnapshot();
        if (primaryState.UtilizedBones.Length != selected.UtilizedBones.Length) return false;
        for (int index = 0; index < selected.UtilizedBones.Length; index++)
        {
            var left = primaryState.UtilizedBones[index];
            var right = selected.UtilizedBones[index];
            if (!ReferenceEquals(left.tfm, right.tfm) || left.invBindWorldMtx != right.invBindWorldMtx) return false;
        }
        return true;
    }

    private void RefreshPalette()
    {
        XRMeshSkinningBufferState state = _mesh.GetSkinningBufferStateSnapshot();
        uint count = checked((uint)state.UtilizedBones.Length + 1);
        if (!ReferenceEquals(_skinningState, state) || _bindRoot != _mesh.BindRootMatrix || _palette is null)
        {
            XRDataBuffer replacement;
            using (RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication())
            {
                replacement = new("SkinPaletteBuffer", EBufferTarget.ShaderStorageBuffer,
                    count, EComponentType.Float, 12, false, false)
                { Usage = EBufferUsage.StreamDraw, DisposeOnPush = false };
                replacement.Set(0, SkinPaletteMatrix.Identity);
                for (int index = 0; index < state.UtilizedBones.Length; index++)
                {
                    var bone = state.UtilizedBones[index];
                    replacement.Set(checked((uint)index + 1), XRMeshRenderer.ComposeSkinPalette(
                        _mesh, bone.invBindWorldMtx, XRMeshRenderer.GetCurrentBoneMatrix(bone.tfm)));
                }
                publication.Complete();
            }
            XRDataBuffer? previous = _palette;
            _palette = replacement;
            _skinningState = state;
            _bindRoot = _mesh.BindRootMatrix;
            _poseVersion++;
            previous?.Destroy();
            return;
        }

        uint first = uint.MaxValue, last = 0;
        for (int index = 0; index < state.UtilizedBones.Length; index++)
        {
            var bone = state.UtilizedBones[index];
            SkinPaletteMatrix value = XRMeshRenderer.ComposeSkinPalette(
                _mesh, bone.invBindWorldMtx, XRMeshRenderer.GetCurrentBoneMatrix(bone.tfm));
            uint slot = checked((uint)index + 1);
            if (_palette.Get<SkinPaletteMatrix>(checked(slot * 48)) is { } previous && previous.Equals(value)) continue;
            _palette.Set(slot, value);
            first = Math.Min(first, slot);
            last = slot;
        }
        if (first == uint.MaxValue) return;
        _poseVersion++;
        // Dirty publication must also survive a warm-up that refreshed CPU pose
        // inputs before the first real GPU producer is recorded.
        _palette.CommitDirtyElements(first, last - first + 1);
    }

    private void EnsureMorphs()
    {
        uint count = _mesh.BlendshapeCount;
        string[]? names = _mesh.BlendshapeNames;
        if (_weights is not null && _shapeCount == count && ReferenceEquals(_shapeNames, names)) return;
        uint capacity = Math.Max(4u, (count + 3u) & ~3u);
        XRDataBuffer weights, active;
        using (RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication())
        {
            weights = new("BlendshapeWeightsBuffer", EBufferTarget.ShaderStorageBuffer,
                capacity, EComponentType.Float, 1, false, false)
            { Usage = EBufferUsage.DynamicDraw, DisposeOnPush = false };
            active = new("BlendshapeActiveWeightsBuffer", EBufferTarget.ShaderStorageBuffer,
                capacity, EComponentType.Float, 2, false, false)
            { Usage = EBufferUsage.DynamicDraw, DisposeOnPush = false };
            for (uint index = 0; index < count; index++)
            {
                float value = 0;
                if (_weights is not null && names is not null && index < names.Length && _shapeNames is not null)
                {
                    // A topology replacement can reorder shapes. Retain controls by
                    // authored identity, never by the old primitive's numeric index.
                    int prior = Array.IndexOf(_shapeNames, names[index]);
                    if (prior >= 0 && prior < _shapeCount) value = _weights.GetFloat((uint)prior);
                }
                weights.SetFloat(index, value);
            }
            publication.Complete();
        }
        XRDataBuffer? priorWeights = _weights, priorActive = _activeMorphs;
        _weights = weights;
        _activeMorphs = active;
        _shapeNames = names;
        _shapeCount = count;
        _activeCount = 0;
        _morphVersion++;
        priorWeights?.Destroy();
        priorActive?.Destroy();
    }

    private void RefreshActiveMorphs()
    {
        uint count = 0;
        bool changed = false;
        for (uint shape = 0; shape < _shapeCount; shape++)
        {
            float weight = _weights!.GetFloat(shape);
            if (MathF.Abs(weight) <= _owner.BlendshapeActiveWeightThreshold ||
                !_owner.IsBlendshapeAllowedByLod(_mesh, checked((int)shape))) continue;
            Vector2 value = new(shape, weight);
            changed |= count >= _activeCount || _activeMorphs!.Get<Vector2>(checked(count * 8)) != value;
            _activeMorphs!.SetVector2(count++, value);
        }
        changed |= count != _activeCount;
        _activeCount = count;
        if (!changed) return;
        _morphVersion++;
        if (count != 0) _activeMorphs!.CommitDirtyElements(0, count);
    }

    internal float GetWeight(uint index)
    {
        EnsureMorphs();
        return index < _shapeCount ? _weights!.GetFloat(index) : 0;
    }

    internal void SetWeight(uint index, float value)
    {
        EnsureMorphs();
        if (index >= _shapeCount || _weights!.GetFloat(index).Equals(value)) return;
        _weights.SetFloat(index, value);
        _weights.CommitDirtyElements(index, 1);
        _morphVersion++;
        _owner.MarkSkinnedOutputDirty();
    }

    internal void SetExternalPalette(object owner, XRDataBuffer palette, XRDataBuffer? previous, uint first, uint count)
    {
        _mesh.EnsureComputeSkinningBuffers();
        _externalOwner = owner;
        _externalPalette = palette;
        _externalPreviousPalette = previous;
        _externalPaletteBase = first;
        _externalPaletteCount = count;
        _externalSkinningState = _mesh.GetSkinningBufferStateSnapshot();
        _externalBindRoot = _mesh.BindRootMatrix;
        _poseVersion++;
    }

    internal void ClearExternalPalette(object owner)
    {
        if (!ReferenceEquals(owner, _externalOwner)) return;
        _externalOwner = null;
        _externalPalette = null;
        _externalPreviousPalette = null;
        _externalPaletteBase = _externalPaletteCount = 0;
        _externalSkinningState = null;
        _poseVersion++;
    }

    public void Dispose()
    {
        _palette?.Destroy();
        _weights?.Destroy();
        _activeMorphs?.Destroy();
        _palette = _weights = _activeMorphs = null;
        _externalOwner = null;
        _externalPalette = _externalPreviousPalette = null;
        _externalSkinningState = null;
    }
}
