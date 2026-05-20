// =====================================================================================
// GPUScene.Soa.cs - Structure-of-Arrays scene database backing the GPU draw stream.
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

        private uint AllocateTransformId(in Matrix4x4 worldMatrix)
        {
            uint transformId = _transformIdAllocator.Allocate();
            EnsureTransformCapacity(transformId + 1u);
            TransformGpu transform = new(worldMatrix);
            UpdatingTransformBuffer.SetDataRawAtIndex(transformId, transform);
            UpdatingPrevTransformBuffer.SetDataRawAtIndex(transformId, transform);
            _transformDirtyRange.Mark(transformId);
            _prevTransformDirtyRange.Mark(transformId);
            return transformId;
        }

        private void ReleaseTransformId(uint transformId)
        {
            if (transformId == 0u)
                return;

            _transformIdAllocator.Release(transformId);
            if (transformId < UpdatingTransformBuffer.ElementCount)
            {
                UpdatingTransformBuffer.SetDataRawAtIndex(transformId, default(TransformGpu));
                UpdatingPrevTransformBuffer.SetDataRawAtIndex(transformId, default(TransformGpu));
                _transformDirtyRange.Mark(transformId);
                _prevTransformDirtyRange.Mark(transformId);
            }
        }

        private uint AllocateSkinId(bool skinned)
        {
            if (!skinned)
                return 0u;

            uint skinId = _skinIdAllocator.Allocate();
            EnsureSkinningPaletteCapacity(skinId + 1u);
            _skinningPaletteDirtyRange.Mark(skinId);
            _skinnedCommandCount++;
            return skinId;
        }

        private void ReleaseSkinId(uint skinId)
        {
            if (skinId == 0u)
                return;

            _skinIdAllocator.Release(skinId);
            if (_skinnedCommandCount > 0u)
                _skinnedCommandCount--;
            if (skinId < SkinningPaletteBuffer.ElementCount)
            {
                SkinningPaletteBuffer.SetDataRawAtIndex(skinId, default(TransformGpu));
                _skinningPaletteDirtyRange.Mark(skinId);
            }
        }

        public uint AllocateCustomStateClassId()
        {
            uint id = _stateClassIdAllocator.Allocate() + (uint)EGpuMaterialStateClass.Custom;
            EnsureMaterialStateCapacity(id + 1u);
            return id;
        }

        public void ReleaseCustomStateClassId(uint stateClassId)
        {
            if (stateClassId <= (uint)EGpuMaterialStateClass.Custom)
                return;

            _stateClassIdAllocator.Release(stateClassId - (uint)EGpuMaterialStateClass.Custom);
        }

        private static EGpuMaterialStateClass ResolveStateClass(XRMaterial material, int renderPass)
        {
            if (RuntimeEngine.Rendering.State.IsShadowPass)
                return EGpuMaterialStateClass.Shadow;

            ETransparencyMode mode = material.GetEffectiveTransparencyMode();
            if (material.IsTransparentLike(mode) ||
                renderPass == (int)EDefaultRenderPass.TransparentForward ||
                renderPass == (int)EDefaultRenderPass.WeightedBlendedOitForward ||
                renderPass == (int)EDefaultRenderPass.PerPixelLinkedListForward ||
                renderPass == (int)EDefaultRenderPass.DepthPeelingForward)
            {
                return EGpuMaterialStateClass.Transparent;
            }

            if (mode is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage ||
                renderPass == (int)EDefaultRenderPass.MaskedForward)
            {
                return EGpuMaterialStateClass.AlphaTested;
            }

            return renderPass == (int)EDefaultRenderPass.OpaqueDeferred
                ? EGpuMaterialStateClass.OpaqueDeferred
                : EGpuMaterialStateClass.OpaqueForward;
        }

        private uint ResolveStateClassId(XRMaterial material, int renderPass, uint materialId)
        {
            uint stateClassId = (uint)ResolveStateClass(material, renderPass);
            EnsureMaterialStateCapacity(stateClassId + 1u);

            if (!_stateClassRepresentativeMaterials.ContainsKey(stateClassId))
                _stateClassRepresentativeMaterials[stateClassId] = material;

            MaterialStateGpu state = new()
            {
                StateClassID = stateClassId,
                MaterialID = materialId,
                PipelineKey = stateClassId,
                OptionsBits = 0u,
                TransparencyMode = (uint)material.GetEffectiveTransparencyMode(),
                DescriptorStart = 0u,
                DescriptorCount = 0u,
                Flags = material.IsTransparentLike() ? 1u : 0u,
            };

            _materialStateByClass[stateClassId] = state;
            MaterialStateBuffer.SetDataRawAtIndex(stateClassId, state);
            _materialStateDirtyRange.Mark(stateClassId);
            return stateClassId;
        }

        private void WriteDrawMetadata(uint drawId, in GPUIndirectRenderCommand command)
        {
            EnsureDrawIndexedSoACapacity(drawId + 1u);
            UpdatingDrawMetadataBuffer.SetDataRawAtIndex(drawId, command.ToDrawMetadata(drawId));
            _drawMetadataDirtyRange.Mark(drawId);
        }

        private void WriteBounds(uint boundsId, in BoundsGpu bounds)
        {
            EnsureDrawIndexedSoACapacity(boundsId + 1u);
            UpdatingBoundsBuffer.SetDataRawAtIndex(boundsId, bounds);
            _boundsDirtyRange.Mark(boundsId);
        }

        private bool UpdateTransform(uint transformId, in Matrix4x4 worldMatrix)
        {
            if (transformId == 0u)
                return false;

            EnsureTransformCapacity(transformId + 1u);
            TransformGpu previous = UpdatingTransformBuffer.GetDataRawAtIndex<TransformGpu>(transformId);
            if (previous.WorldMatrix.Equals(worldMatrix))
                return false;

            UpdatingPrevTransformBuffer.SetDataRawAtIndex(transformId, previous);
            UpdatingTransformBuffer.SetDataRawAtIndex(transformId, new TransformGpu(worldMatrix));
            _prevTransformDirtyRange.Mark(transformId);
            _transformDirtyRange.Mark(transformId);
            return true;
        }

        private void ClearDrawIndexedSoA(uint drawId)
        {
            if (drawId < UpdatingDrawMetadataBuffer.ElementCount)
            {
                UpdatingDrawMetadataBuffer.SetDataRawAtIndex(drawId, default(DrawMetadata));
                _drawMetadataDirtyRange.Mark(drawId);
            }

            if (drawId < UpdatingBoundsBuffer.ElementCount)
            {
                UpdatingBoundsBuffer.SetDataRawAtIndex(drawId, default(BoundsGpu));
                _boundsDirtyRange.Mark(drawId);
            }
        }

    }
}
