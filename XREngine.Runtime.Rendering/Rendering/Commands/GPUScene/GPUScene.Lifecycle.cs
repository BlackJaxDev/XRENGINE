// =====================================================================================
// GPUScene.Lifecycle.cs - Init / dispose / per-frame lifecycle hooks.
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

        /// <summary>
        /// Initializes the GPU scene, creating all required buffers.
        /// </summary>
        public void Initialize()
        {
            _meshDataBuffer?.Destroy();
            _meshDataBuffer = MakeMeshDataBuffer();
            _meshletRangeBuffer?.Destroy();
            _meshletRangeBuffer = MakeMeshletRangeBuffer();
            _meshletDescriptorBuffer?.Destroy();
            _meshletDescriptorBuffer = MakeMeshletDescriptorBuffer();
            _meshletVertexIndexBuffer?.Destroy();
            _meshletVertexIndexBuffer = MakeMeshletVertexIndexBuffer();
            _meshletTriangleIndexBuffer?.Destroy();
            _meshletTriangleIndexBuffer = MakeMeshletTriangleIndexBuffer();
            _meshletDescriptors.Clear();
            _meshletVertexIndices.Clear();
            _meshletTriangleIndices.Clear();
            _meshletRangesByMeshId.Clear();
            _meshletFreshnessByMeshId.Clear();
            _meshletRangeDirtyRange.Clear();

            _lodTableBuffer?.Destroy();
            _lodTableBuffer = MakeLodTableBuffer();
            _lodRequestBuffer?.Destroy();
            _lodRequestBuffer = MakeLodRequestBuffer();

            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = MakeCommandsInputBuffer();

            _allLoadedDrawMetadataBuffer?.Destroy();
            _allLoadedDrawMetadataBuffer = MakeDrawMetadataBuffer("DrawMetadataBuffer");
            _allLoadedTransformBuffer?.Destroy();
            _allLoadedTransformBuffer = MakeTransformBuffer("TransformBuffer");
            _allLoadedPrevTransformBuffer?.Destroy();
            _allLoadedPrevTransformBuffer = MakeTransformBuffer("PrevTransformBuffer");
            _allLoadedBoundsBuffer?.Destroy();
            _allLoadedBoundsBuffer = MakeBoundsBuffer("BoundsBuffer");

            _allLoadedTransparencyMetadataBuffer?.Destroy();
            _allLoadedTransparencyMetadataBuffer = MakeTransparencyMetadataBuffer();
            
            _updatingCommandsBuffer?.Destroy();
            _updatingCommandsBuffer = MakeCommandsInputBuffer();

            _updatingDrawMetadataBuffer?.Destroy();
            _updatingDrawMetadataBuffer = MakeDrawMetadataBuffer("UpdatingDrawMetadataBuffer");
            _updatingTransformBuffer?.Destroy();
            _updatingTransformBuffer = MakeTransformBuffer("UpdatingTransformBuffer");
            _updatingPrevTransformBuffer?.Destroy();
            _updatingPrevTransformBuffer = MakeTransformBuffer("UpdatingPrevTransformBuffer");
            _updatingBoundsBuffer?.Destroy();
            _updatingBoundsBuffer = MakeBoundsBuffer("UpdatingBoundsBuffer");

            _updatingTransparencyMetadataBuffer?.Destroy();
            _updatingTransparencyMetadataBuffer = MakeTransparencyMetadataBuffer();

            _materialStateBuffer?.Destroy();
            _materialStateBuffer = MakeMaterialStateBuffer();
            _skinningPaletteBuffer?.Destroy();
            _skinningPaletteBuffer = MakeSkinningPaletteBuffer();
            _totalCommandCount = 0;
            _updatingCommandCount = 0;
            _skinnedCommandCount = 0;
        }

        /// <summary>
        /// Destroys the GPU scene and releases all resources.
        /// </summary>
        public void Destroy()
        {
            static void DestroyTierBuffers(AtlasTierState state)
            {
                state.Positions?.Destroy();
                state.Positions = null;
                state.Normals?.Destroy();
                state.Normals = null;
                state.Tangents?.Destroy();
                state.Tangents = null;
                state.UV0?.Destroy();
                state.UV0 = null;
                state.Indices?.Destroy();
                state.Indices = null;
                state.Dirty = false;
                state.VertexCount = 0;
                state.IndexCount = 0;
                state.LastUploadedVertexCount = 0;
                state.LastUploadedIndexCount = 0;
                state.Version = 0;
                state.IndexElementSize = IndexSize.FourBytes;
                state.MeshOffsets.Clear();
                state.IndirectFaceIndices.Clear();
            }

            _meshDataBuffer?.Destroy();
            _meshDataBuffer = null;
            _meshletRangeBuffer?.Destroy();
            _meshletRangeBuffer = null;
            _meshletDescriptorBuffer?.Destroy();
            _meshletDescriptorBuffer = null;
            _meshletVertexIndexBuffer?.Destroy();
            _meshletVertexIndexBuffer = null;
            _meshletTriangleIndexBuffer?.Destroy();
            _meshletTriangleIndexBuffer = null;
            _meshletDescriptors.Clear();
            _meshletVertexIndices.Clear();
            _meshletTriangleIndices.Clear();
            _meshletRangesByMeshId.Clear();
            _meshletFreshnessByMeshId.Clear();
            _meshletRangeDirtyRange.Clear();
            _lodTableBuffer?.Destroy();
            _lodTableBuffer = null;
            _lodRequestBuffer?.Destroy();
            _lodRequestBuffer = null;
            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = null;
            _allLoadedDrawMetadataBuffer?.Destroy();
            _allLoadedDrawMetadataBuffer = null;
            _allLoadedTransformBuffer?.Destroy();
            _allLoadedTransformBuffer = null;
            _allLoadedPrevTransformBuffer?.Destroy();
            _allLoadedPrevTransformBuffer = null;
            _allLoadedBoundsBuffer?.Destroy();
            _allLoadedBoundsBuffer = null;
            _allLoadedTransparencyMetadataBuffer?.Destroy();
            _allLoadedTransparencyMetadataBuffer = null;
            _updatingCommandsBuffer?.Destroy();
            _updatingCommandsBuffer = null;
            _updatingDrawMetadataBuffer?.Destroy();
            _updatingDrawMetadataBuffer = null;
            _updatingTransformBuffer?.Destroy();
            _updatingTransformBuffer = null;
            _updatingPrevTransformBuffer?.Destroy();
            _updatingPrevTransformBuffer = null;
            _updatingBoundsBuffer?.Destroy();
            _updatingBoundsBuffer = null;
            _updatingTransparencyMetadataBuffer?.Destroy();
            _updatingTransparencyMetadataBuffer = null;
            _materialStateBuffer?.Destroy();
            _materialStateBuffer = null;
            _skinningPaletteBuffer?.Destroy();
            _skinningPaletteBuffer = null;

            DestroyTierBuffers(_staticAtlas);
            DestroyTierBuffers(_dynamicAtlas);
            foreach (AtlasTierState streamingState in _streamingAtlases)
                DestroyTierBuffers(streamingState);

            _atlasPositions = null;
            _atlasNormals = null;
            _atlasTangents = null;
            _atlasUV0 = null;
            _atlasIndices = null;
            _atlasDirty = false;
            _atlasVertexCount = 0;
            _atlasIndexCount = 0;
            _atlasMeshOffsets.Clear();
            _atlasMeshRefCounts.Clear();
            _indirectFaceIndices.Clear();
            _atlasVersion = 0;
            _atlasIndexElementSize = IndexSize.FourBytes;
            _activeAtlasTiers.Clear();
            _streamingReservations.Clear();
            _streamingWriteSlot = 0;
            _streamingRenderSlot = 0;

            _commandAabbBuffer?.Destroy();
            _commandAabbBuffer = null;
            _commandAabbProgram?.Destroy();
            _commandAabbProgram = null;
            _commandAabbShader?.Destroy();
            _commandAabbShader = null;
            _gpuBvhTree?.Dispose();
            _gpuBvhTree = null;
            _meshIDMap.Clear();
            _materialIDMap.Clear();
            _idToMaterial.Clear();
            _stateClassRepresentativeMaterials.Clear();
            _materialStateByClass.Clear();
            _idToMesh.Clear();
            _renderableLogicalMeshIdMap.Clear();
            _standaloneLogicalMeshIdMap.Clear();
            _logicalMeshStates.Clear();
            _nextMeshID = 1;
            _nextMaterialID = 1;
            _nextLogicalMeshID = 1;
            _transformIdAllocator.Clear();
            _skinIdAllocator.Clear();
            _stateClassIdAllocator.Clear();
            _drawMetadataDirtyRange.Clear();
            _transformDirtyRange.Clear();
            _prevTransformDirtyRange.Clear();
            _boundsDirtyRange.Clear();
            _materialStateDirtyRange.Clear();
            _skinningPaletteDirtyRange.Clear();
            _totalCommandCount = 0;
            _updatingCommandCount = 0;
            _skinnedCommandCount = 0;
            _bounds = new AABB();
            _meshlets.Clear();
            _commandIndicesPerMeshCommand.Clear();
            _commandIndexLookup.Clear();
            _meshToIndexRemap.Clear();
            _meshDebugLabels.Clear();
            _unsupportedMeshMessages.Clear();
            _bvhReady = false;
            _bvhDirty = false;
            _bvhNodeCount = 0;
            _bvhPrimitiveCount = 0;
            _bvhRefitPending = false;
            _bvhBuildSuppressed = false;
            _bvhSuppressedCommandCount = 0;
            _meshletsDirty = true;
        }

    }
}
