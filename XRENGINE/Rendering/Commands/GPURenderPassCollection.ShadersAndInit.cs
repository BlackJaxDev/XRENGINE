using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        // Per-buffer remap flags so we only remap what actually needs it
        private bool _culledCountNeedsMap;
        private bool _drawCountNeedsMap;
        private bool _cullingOverflowNeedsMap;
        private bool _indirectOverflowNeedsMap;
        private bool _statsNeedsMap;
        private bool _truncationNeedsMap;

        private static void EnsurePersistentReadbackMapping(XRDataBuffer buffer)
        {
            // Never persistently map GL parameter buffers; drivers may stall or misbehave
            if (buffer.Target == EBufferTarget.ParameterBuffer)
                return;

            if (buffer.ActivelyMapping.Count > 0)
                return;

            buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent;
            buffer.RangeFlags   |= EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
            buffer.DisposeOnPush = false;
            buffer.Usage = EBufferUsage.StreamRead;
            buffer.Resizable = false;
            buffer.MapBufferData();
        }

        private void MapBuffers()
        {
            // Determine if any buffer specifically needs to be remapped
            bool anyFlagged = _culledCountNeedsMap || _drawCountNeedsMap || _cullingOverflowNeedsMap || _indirectOverflowNeedsMap || _statsNeedsMap || _truncationNeedsMap;

            // If nothing is flagged and we've already mapped previously, skip
            if (!anyFlagged && _buffersMapped)
            {
                Dbg("MapBuffers skipped; no buffers flagged for remap","Buffers");
                return;
            }

            Dbg("MapBuffers begin","Buffers");

            // If nothing is flagged but we haven't mapped yet, perform initial mapping for any existing buffers
            if (!anyFlagged && !_buffersMapped)
            {
                // Do NOT map parameter buffers; only map SSBO/flag buffers for readback
                if (_cullingOverflowFlagBuffer is not null)
                    EnsurePersistentReadbackMapping(_cullingOverflowFlagBuffer);
                if (_indirectOverflowFlagBuffer is not null)
                    EnsurePersistentReadbackMapping(_indirectOverflowFlagBuffer);
                if (_statsBuffer is not null)
                    EnsurePersistentReadbackMapping(_statsBuffer);
                if (_truncationFlagBuffer is not null)
                    EnsurePersistentReadbackMapping(_truncationFlagBuffer);

                _buffersMapped = true;
                Dbg("MapBuffers complete (initial)","Buffers");
                return;
            }

            // Otherwise, selectively map only the buffers that were flagged
            if (_culledCountNeedsMap && _culledCountBuffer is not null)
            {
                // Skip mapping for parameter buffers; just clear the flag
                _culledCountNeedsMap = false;
            }

            if (_drawCountNeedsMap && _drawCountBuffer is not null)
            {
                // Skip mapping for parameter buffers; just clear the flag
                _drawCountNeedsMap = false;
            }

            if (_cullingOverflowNeedsMap && _cullingOverflowFlagBuffer is not null)
            {
                EnsurePersistentReadbackMapping(_cullingOverflowFlagBuffer);
                _cullingOverflowNeedsMap = false;
            }

            if (_indirectOverflowNeedsMap && _indirectOverflowFlagBuffer is not null)
            {
                EnsurePersistentReadbackMapping(_indirectOverflowFlagBuffer);
                _indirectOverflowNeedsMap = false;
            }

            if (_statsNeedsMap && _statsBuffer is not null)
            {
                EnsurePersistentReadbackMapping(_statsBuffer);
                _statsNeedsMap = false;
            }

            if (_truncationNeedsMap && _truncationFlagBuffer is not null)
            {
                EnsurePersistentReadbackMapping(_truncationFlagBuffer);
                _truncationNeedsMap = false;
            }

            _buffersMapped = true;

            Dbg("MapBuffers complete","Buffers");
        }

        private void UnmapBuffers()
        {
            Dbg("UnmapBuffers begin","Buffers");

            _culledCountBuffer?.UnmapBufferData();
            _drawCountBuffer?.UnmapBufferData();
            _cullingOverflowFlagBuffer?.UnmapBufferData();
            _indirectOverflowFlagBuffer?.UnmapBufferData();
            //_histogramIndexBuffer?.UnmapBufferData();
            _statsBuffer?.UnmapBufferData();
            _truncationFlagBuffer?.UnmapBufferData();

            _buffersMapped = false;

            Dbg("UnmapBuffers complete","Buffers");
        }

        private void GenerateShaders()
        {
            Dbg("GenerateShaders start","Lifecycle");

            _cullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCulling.comp", EShaderType.Compute));
            _buildKeysComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderBuildKeys.comp", EShaderType.Compute));
            _buildGpuBatchesComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderBuildBatches.comp", EShaderType.Compute));
            //RadixIndexSortComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderRadixIndexSort.comp", EShaderType.Compute));
            _indirectRenderTaskShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderIndirect.comp", EShaderType.Compute));
            _resetCountersComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderResetCounters.comp", EShaderType.Compute));
            _extractSoAComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderExtractSoA.comp", EShaderType.Compute));
            _soACullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCullingSoA.comp", EShaderType.Compute));
            //HiZSoACullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderHiZSoACulling.comp", EShaderType.Compute));
            //_gatherProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderGather.comp", EShaderType.Compute));
            _copyCommandsProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCopyCommands.comp", EShaderType.Compute));
            _bvhFrustumCullProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Scene3D/RenderPipeline/bvh_frustum_cull.comp", EShaderType.Compute));

            // Phase 3: Hi-Z occlusion pyramid + refinement
            _hiZInitProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderHiZInit.comp", EShaderType.Compute));
            _hiZGenProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/HiZGen.comp", EShaderType.Compute));
            _hiZOcclusionProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderOcclusionHiZ.comp", EShaderType.Compute));
            _copyCount3Program = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCopyCount3.comp", EShaderType.Compute));

            Dbg("GenerateShaders complete","Lifecycle");
        }

        public void PreRenderInitialize(GPUScene scene)
        {
            Dbg("PreRenderInitialize","Lifecycle");
            try
            {
                using (_lock.EnterScope())
                {
                    uint max = scene.AllocatedMaxCommandCount;
                    if (_initialized)
                    {
                        VerifyBufferLengths(scene, max);
                        // Ensure EBO is synced with atlas (handles dynamic mesh adds/removes)
                        EnsureAtlasSynced(scene);
                    }
                    else
                        Initialize(scene, max);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Lifecycle")} Failed to initialize GPURenderPassCollection: {ex}");
                _initialized = false;
                Dbg("Initialization failed","Lifecycle");
            }
        }

        private void VerifyBufferLengths(GPUScene scene, uint max)
        {
            if (max == _lastMaxCommands)
                return;

            _lastMaxCommands = max;
            bool remapNeeded = RegenerateBuffers(scene);

            if (remapNeeded || !_buffersMapped)
                MapBuffers();

            Dbg($"Capacity change -> RegenerateBuffers newMax={max}", "Buffers");
        }

        private void Initialize(GPUScene scene, uint max)
        {
            _initialized = true;
            _lastMaxCommands = max;

            bool remapNeeded = RegenerateBuffers(scene);
            GenerateShaders();
            if (remapNeeded || !_buffersMapped)
                MapBuffers();
            MakeIndirectRenderer(scene);

            Dbg($"Initialized with capacity={max}", "Lifecycle");
        }

        public static int[]? GetIndices(GPUScene scene)
            => scene.IndirectFaceIndices?.SelectMany(x => new[] { x.Point0, x.Point1, x.Point2 }).ToArray();

        public static XRDataBuffer? GetIndexBuffer(GPUScene scene, out IndexSize elementSize)
        {
            elementSize = IndexSize.Byte;

            var indices = GetIndices(scene);
            if (indices is null || indices.Length == 0)
                return null;

            var buf = new XRDataBuffer(EBufferTarget.ElementArrayBuffer, true)
            {
                AttributeName = EPrimitiveType.Triangles.ToString()
            };

            //if (scene.AtlasVertexCount < byte.MaxValue)
            //{
            //    elementSize = IndexSize.Byte;
            //    buf.SetDataRaw(indices.Select(x => (byte)x), indices.Length);
            //}
            //else if (scene.AtlasVertexCount < short.MaxValue)
            //{
            //    elementSize = IndexSize.TwoBytes;
            //    buf.SetDataRaw(indices.Select(x => (ushort)x), indices.Length);
            //}
            //else
            //{
                elementSize = IndexSize.FourBytes;
                buf.SetDataRaw(indices);
            //}

            return buf;
        }

        private GPUScene? _subscribedScene;
        private uint _lastAtlasVersion;

        private void MakeIndirectRenderer(GPUScene scene)
        {
            scene.EnsureAtlasBuffers();
            scene.RebuildAtlasIfDirty();

            _indirectRenderer = new XRMeshRenderer();
            var defVer = _indirectRenderer.GetDefaultVersion();
            defVer.Generate();

            SyncIndirectRendererIndexBuffer(scene);

            if (scene.AtlasPositions is not null)
                _indirectRenderer.Buffers.Add(ECommonBufferType.Position.ToString(), scene.AtlasPositions);

            if (scene.AtlasNormals is not null)
                _indirectRenderer.Buffers.Add(ECommonBufferType.Normal.ToString(), scene.AtlasNormals);

            if (scene.AtlasTangents is not null)
                _indirectRenderer.Buffers.Add(ECommonBufferType.Tangent.ToString(), scene.AtlasTangents);

            if (scene.AtlasUV0 is not null)
            {
                string binding = $"{ECommonBufferType.TexCoord}{0}";
                _indirectRenderer.Buffers.Add(binding, scene.AtlasUV0);
            }

            // Subscribe to atlas rebuild events for EBO sync
            SubscribeToAtlasEvents(scene);
        }

        /// <summary>
        /// Subscribes to the scene's AtlasRebuilt event to keep the indirect renderer's EBO in sync.
        /// </summary>
        private void SubscribeToAtlasEvents(GPUScene scene)
        {
            if (_subscribedScene == scene)
                return;

            // Unsubscribe from previous scene
            if (_subscribedScene is not null)
                _subscribedScene.AtlasRebuilt -= OnAtlasRebuilt;

            _subscribedScene = scene;
            _lastAtlasVersion = scene.AtlasVersion;
            scene.AtlasRebuilt += OnAtlasRebuilt;
        }

        /// <summary>
        /// Called when the GPUScene atlas is rebuilt. Syncs the indirect renderer's index buffer.
        /// </summary>
        private void OnAtlasRebuilt(GPUScene scene)
        {
            if (_indirectRenderer is null)
                return;

            Dbg($"AtlasRebuilt event - syncing EBO (version {scene.AtlasVersion})", "Buffers");
            SyncIndirectRendererIndexBuffer(scene);
            _lastAtlasVersion = scene.AtlasVersion;
        }

        /// <summary>
        /// Syncs the indirect renderer's index buffer with the scene's atlas index buffer.
        /// Call this after atlas rebuild or when the index buffer may have changed.
        /// </summary>
        private void SyncIndirectRendererIndexBuffer(GPUScene scene)
        {
            if (_indirectRenderer is null)
                return;

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
                return;

            IndexSize elementSize = scene.AtlasIndexElementSize;
            XRDataBuffer? atlasIndexBuffer = scene.AtlasIndices;

            if (atlasIndexBuffer is null)
            {
                atlasIndexBuffer = GetIndexBuffer(scene, out elementSize);
            }

            if (atlasIndexBuffer is null)
            {
                Debug.LogWarning("Indirect renderer EBO sync failed: no index buffer available.");
                return;
            }

            if (renderer.TrySyncMeshRendererIndexBuffer(_indirectRenderer, atlasIndexBuffer, elementSize))
            {
                Dbg($"EBO synced: indexCount={scene.AtlasIndexCount}, elementSize={elementSize}", "Buffers");
            }
            else
            {
                Debug.LogWarning("Indirect renderer EBO sync failed: TrySyncMeshRendererIndexBuffer returned false.");
            }
        }

        /// <summary>
        /// Checks if the atlas has been rebuilt since last sync and updates if needed.
        /// Call this before rendering to ensure EBO is current.
        /// </summary>
        public void EnsureAtlasSynced(GPUScene scene)
        {
            if (scene.AtlasVersion != _lastAtlasVersion)
            {
                Dbg($"Atlas version mismatch (current={scene.AtlasVersion}, last={_lastAtlasVersion}) - syncing", "Buffers");
                SyncIndirectRendererIndexBuffer(scene);
                _lastAtlasVersion = scene.AtlasVersion;
            }
        }

        private bool RegenerateBuffers(GPUScene gpuScene)
        {
            Dbg("RegenerateBuffers begin","Buffers");
            uint capacity = gpuScene.AllocatedMaxCommandCount;

            if (_culledSceneToRenderBuffer is null || _culledSceneToRenderBuffer.ElementCount != capacity)
            {
                _culledSceneToRenderBuffer?.Destroy();
                _culledSceneToRenderBuffer = MakeCulledSceneToRenderBuffer(capacity);
            }

            // Track remap needs per-buffer
            EnsureIndirectDrawBuffer(capacity);
            _culledCountNeedsMap = EnsureParameterBuffer(ref _culledCountBuffer, "CulledCount", GPUScene.VisibleCountComponents);
            _culledCountNeedsMap |= EnsureParameterBuffer(ref _cullCountScratchBuffer, "CulledCountScratch", GPUScene.VisibleCountComponents);
            _drawCountNeedsMap = EnsureParameterBuffer(ref _drawCountBuffer, "DrawCount");
            _cullingOverflowNeedsMap = EnsureFlagBuffer(ref _cullingOverflowFlagBuffer, "CullingOverflowFlag");
            _indirectOverflowNeedsMap = EnsureFlagBuffer(ref _indirectOverflowFlagBuffer, "IndirectOverflowFlag");
            _cullingOverflowNeedsMap |= EnsureFlagBuffer(ref _occlusionOverflowFlagBuffer, "OcclusionOverflowFlag");

            if (_sortedCommandBuffer is null || _sortedCommandBuffer.ElementCount != capacity)
            {
                _sortedCommandBuffer?.Destroy();
                _sortedCommandBuffer = new XRDataBuffer("SortedCommands_Pass", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.Float, 32, false, false)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                };
                _sortedCommandBuffer.Generate();
            }

            //_histogramBuffer?.Destroy();
            //_histogramBuffer = new XRDataBuffer("Histogram_Pass", EBufferTarget.ShaderStorageBuffer, 256, EComponentType.UInt, 1, false, true)
            //{
            //    Usage = EBufferUsage.DynamicCopy,
            //    DisposeOnPush = false,
            //    Resizable = false,
            //};
            //_histogramBuffer.Generate();

            // Ensure material IDs buffer exists for batching keys
            EnsureMaterialIDs(capacity);
            EnsureGpuDrivenBatchingBuffers(capacity);
            EnsureViewSetBuffers(capacity);
            _statsNeedsMap |= EnsureStatsBuffer();

            // Phase 3: occlusion ping-pong buffer (same layout as CulledSceneToRenderBuffer)
            if (_occlusionCulledBuffer is null || _occlusionCulledBuffer.ElementCount != capacity)
            {
                _occlusionCulledBuffer?.Destroy();
                _occlusionCulledBuffer = new XRDataBuffer(
                    $"CulledCommandsBuffer_Occlusion",
                    EBufferTarget.ShaderStorageBuffer,
                    capacity,
                    EComponentType.Float,
                    GPUScene.CommandFloatCount,
                    false,
                    false)
                {
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    Resizable = false,
                    StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read,
                    RangeFlags = EBufferMapRangeFlags.Read,
                };
                _occlusionCulledBuffer.Generate();
            }

            // Aggregate whether any buffer mapping is pending
            bool anyRemapPending = _culledCountNeedsMap || _drawCountNeedsMap || _cullingOverflowNeedsMap || _indirectOverflowNeedsMap || _statsNeedsMap || _truncationNeedsMap;

            if (IndirectDebug.ValidateBufferLayouts)
                ValidateIndirectBufferLayout(capacity, anyRemapPending);

            Dbg($"RegenerateBuffers complete capacity={capacity}","Buffers");

            // Return whether any mapping work is pending (independent of indirect draw recreation)
            return anyRemapPending;
        }

        private bool EnsureIndirectDrawBuffer(uint capacity)
        {
            bool recreated = false;

            EBufferMapStorageFlags requiredStorage = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read;
            EBufferMapRangeFlags requiredRange = EBufferMapRangeFlags.Read;

            if (_indirectDrawBuffer is not null)
            {
                bool strideMismatch = _indirectDrawBuffer.ElementSize != _indirectCommandStride;
                bool countMismatch = _indirectDrawBuffer.ElementCount != capacity;
                bool missingStorage = (_indirectDrawBuffer.StorageFlags & requiredStorage) != requiredStorage;
                bool missingRange = (_indirectDrawBuffer.RangeFlags & requiredRange) != requiredRange;
                if (strideMismatch || countMismatch || missingStorage || missingRange)
                {
                    if (strideMismatch)
                        Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Indirect draw buffer stride mismatch detected. Forcing recreation.");
                    else if (countMismatch)
                        Dbg("Resizing indirect draw buffer to match new capacity.", "Buffers");
                    else
                        Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Indirect draw buffer missing required readback flags. Recreating with map-read support.");
                    _indirectDrawBuffer.Destroy();
                    _indirectDrawBuffer = null;
                }
            }

            if (_indirectDrawBuffer is null)
            {
                _indirectDrawBuffer = new XRDataBuffer("IndirectDraw_Pass", EBufferTarget.DrawIndirectBuffer, capacity, EComponentType.UInt, _indirectCommandComponentCount, false, true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                };
                _indirectDrawBuffer.StorageFlags |= requiredStorage;
                _indirectDrawBuffer.RangeFlags |= requiredRange;
                _indirectDrawBuffer.Generate();
                recreated = true;
            }

            return recreated;
        }

        private bool EnsureParameterBuffer(ref XRDataBuffer? buffer, string name, uint elementCount = 1, uint componentCount = 1)
        {
            bool requiresMapping = false;

            if (buffer is not null)
            {
                bool invalidLayout = buffer.ElementCount != elementCount || buffer.ComponentType != EComponentType.UInt || buffer.ComponentCount != componentCount;
                bool missingStorage = (buffer.StorageFlags & (EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read)) != (EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read);
                bool missingRange = !buffer.RangeFlags.HasFlag(EBufferMapRangeFlags.Read);

                if (invalidLayout || missingStorage || missingRange)
                {
                    Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Parameter buffer {name} missing required layout/flags. Recreating.");
                    buffer.Destroy();
                    buffer = null;
                    requiresMapping = true;
                }
            }

            if (buffer is null)
            {
                buffer = new XRDataBuffer(name, EBufferTarget.ParameterBuffer, elementCount, EComponentType.UInt, componentCount, false, true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = false,
                    PadEndingToVec4 = false,
                    StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read,
                    RangeFlags = EBufferMapRangeFlags.Read,
                };

                buffer.Generate();
                for (uint i = 0; i < elementCount; ++i)
                    buffer.SetDataRawAtIndex(i, 0u);
                buffer.PushSubData();
                requiresMapping = true;
            }

            // Parameter buffers participate in GPU writes via compute shaders and are rebound as
            // GL_PARAMETER_BUFFER for multi-draw indirect count submissions.  Several desktop
            // drivers will hard-stall if these buffers are created with the persistent/coherent
            // mapping bits set, so keep them as simple readback buffers that can be mapped on
            // demand.  We still request DynamicStorage so the GPU can update the contents without
            // forcing a reallocation, but omit Persistent/Coherent so glMapNamedBufferRange issues
            // a one-off read-only mapping instead of a long-lived persistent map.
            buffer.StorageFlags &= ~(EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent);
            buffer.RangeFlags &= ~(EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent);
            buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read;
            buffer.RangeFlags |= EBufferMapRangeFlags.Read;

            // Only force a remap before the initial mapping has occurred
            if (!requiresMapping && IndirectDebug.ForceParameterRemap && buffer is not null && !_buffersMapped)
            {
                buffer.UnmapBufferData();
                requiresMapping = true;
            }

            // If nothing has been mapped yet, mark for mapping; otherwise, keep persistent mapping
            if (buffer is not null && buffer.ActivelyMapping.Count == 0 && !_buffersMapped)
                requiresMapping = true;

            // Do not toggle a global mapping state here; just return the per-buffer requirement
            return requiresMapping;
        }

        private bool EnsureFlagBuffer(ref XRDataBuffer? buffer, string name)
        {
            bool requiresMapping = false;

            if (buffer is null)
            {
                buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, 1, EComponentType.UInt, 1, false, true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = false,
                    PadEndingToVec4 = false,
                };

                buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent;
                buffer.RangeFlags |= EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                buffer.Generate();
                buffer.SetDataRawAtIndex(0, 0u);
                buffer.PushSubData();

                // Map immediately on creation to avoid later stalls when GPU may be using the buffer
                EnsurePersistentReadbackMapping(buffer);
                requiresMapping = false; // already mapped
            }
            else
            {
                // If nothing has been mapped yet and we're still in pre-mapping stage, request mapping
                if (buffer.ActivelyMapping.Count == 0 && !_buffersMapped)
                    requiresMapping = true;
            }

            // Do not toggle a global mapping state here; just return the per-buffer requirement
            return requiresMapping;
        }

        private bool EnsureStatsBuffer()
        {
            bool requiresMapping = false;
            uint requiredElements = GpuStatsLayout.FieldCount;

            if (_statsBuffer is not null)
            {
                bool layoutMismatch = _statsBuffer.ElementCount < requiredElements ||
                                      _statsBuffer.ComponentType != EComponentType.UInt ||
                                      _statsBuffer.ComponentCount != 1;
                if (layoutMismatch)
                {
                    Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Stats buffer layout mismatch; recreating.");
                    _statsBuffer.Destroy();
                    _statsBuffer = null;
                    requiresMapping = true;
                }
            }

            if (_statsBuffer is null)
            {
                _statsBuffer = new XRDataBuffer("RenderStats", EBufferTarget.ShaderStorageBuffer, requiredElements, EComponentType.UInt, 1, false, true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = false,
                    PadEndingToVec4 = true,
                    StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
                    RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
                };
                _statsBuffer.Generate();
                _statsBuffer.SetDataRaw(new uint[requiredElements], (int)requiredElements);
                _statsBuffer.PushSubData();
                requiresMapping = true;
            }

            return requiresMapping;
        }

        private void ValidateIndirectBufferLayout(uint capacity, bool remapPending)
        {
            if (_indirectDrawBuffer is null)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Indirect draw buffer is null before rendering.");
                return;
            }

            if (_indirectDrawBuffer.ElementSize != _indirectCommandStride)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Indirect draw stride mismatch. Expected {_indirectCommandStride} bytes, buffer reports {_indirectDrawBuffer.ElementSize}.");
            }

            if (_indirectDrawBuffer.ElementCount < capacity)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Indirect draw buffer smaller than required capacity (buffer={_indirectDrawBuffer.ElementCount}, required={capacity}).");
            }

            if (_drawCountBuffer is not null && _drawCountBuffer.ElementSize < sizeof(uint))
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Draw count buffer is undersized ({_drawCountBuffer.ElementSize} bytes); expected at least {sizeof(uint)}.");
            }

            if (_culledCountBuffer is not null && _culledCountBuffer.ElementSize < sizeof(uint))
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Culled count buffer is undersized ({_culledCountBuffer.ElementSize} bytes); expected at least {sizeof(uint)}.");
            }

            if (_culledCountBuffer is not null && _culledCountBuffer.ElementCount < GPUScene.VisibleCountComponents)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Buffers")} Culled count buffer has insufficient elements (buffer={_culledCountBuffer.ElementCount}, expected>={GPUScene.VisibleCountComponents}).");
            }

            if (IndirectDebug.ValidateLiveHandles && !remapPending)
            {
                bool logBuffers = Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;
                if (!logBuffers)
                    return;

                if (_drawCountBuffer is not null && _drawCountBuffer.ActivelyMapping.Count == 0)
                    Debug.Out($"{FormatDebugPrefix("Buffers")} Draw count buffer is not mapped; GPU count reads may see stale data.");

                if (_culledCountBuffer is not null && _culledCountBuffer.ActivelyMapping.Count == 0)
                    Debug.Out($"{FormatDebugPrefix("Buffers")} Culled count buffer is not mapped; visibility counters may be invalid.");
            }
        }

        //private void EnsureSortBuffers(uint capacity)
        //{
        //    Dbg($"EnsureSortBuffers capacity={capacity}","Buffers");

        //    _keyIndexBufferA ??= new XRDataBuffer("KeyIndexA", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, 2, false, true)
        //    {
        //        Usage = EBufferUsage.DynamicCopy,
        //        DisposeOnPush = false,
        //        Resizable = true,
        //    };
        //    _keyIndexBufferA.Generate();

        //    _keyIndexBufferB ??= new XRDataBuffer("KeyIndexB", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, 2, false, true)
        //    {
        //        Usage = EBufferUsage.DynamicCopy,
        //        DisposeOnPush = false,
        //        Resizable = true,
        //    };
        //    _keyIndexBufferB.Generate();

        //    _histogramIndexBuffer ??= new XRDataBuffer("KeyHistogram", EBufferTarget.ShaderStorageBuffer, 256, EComponentType.UInt, 1, false, true)
        //    {
        //        Usage = EBufferUsage.DynamicCopy,
        //        DisposeOnPush = false,
        //        Resizable = false,
        //    };
        //    _histogramIndexBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
        //    _histogramIndexBuffer.Generate();

        //    _truncationFlagBuffer ??= new XRDataBuffer("IndirectTruncationFlag", EBufferTarget.ShaderStorageBuffer, 1, EComponentType.UInt, 1, false, true)
        //    {
        //        Usage = EBufferUsage.DynamicCopy,
        //        DisposeOnPush = false,
        //        Resizable = false,
        //    };
        //    _truncationFlagBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
        //    _truncationFlagBuffer.Generate();

        //    _statsBuffer ??= new XRDataBuffer("RenderStats", EBufferTarget.ShaderStorageBuffer, 5, EComponentType.UInt, 1, false, true)
        //    {
        //        Usage = EBufferUsage.DynamicCopy,
        //        DisposeOnPush = false,
        //        Resizable = false,
        //    };
        //    _statsBuffer.Generate();

        //    Dbg("EnsureSortBuffers complete", "Buffers");
        //}

        private void EnsureSoABuffers(uint capacity)
        {
            Dbg($"EnsureSoABuffers capacity = {capacity}", "SoA");

            uint sphereStride = 4;
            uint metaStride = 4;

            if (_soaBoundingSpheresA is null)
            {
                _soaBoundingSpheresA = new XRDataBuffer("SoA_Spheres_A", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.Float, sphereStride, false, false)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false
                };
                _soaBoundingSpheresA.Generate();
            }

            if (_soaMetadataA is null)
            {
                _soaMetadataA = new XRDataBuffer("SoA_Metadata_A", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, metaStride, false, false)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false
                };
                _soaMetadataA.Generate();
            }

            if (_soaBoundingSpheresB is null)
            {
                _soaBoundingSpheresB = new XRDataBuffer("SoA_Spheres_B", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.Float, sphereStride, false, false)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush=false
                };
                _soaBoundingSpheresB.Generate();
            }

            if (_soaMetadataB is null)
            {
                _soaMetadataB = new XRDataBuffer("SoA_Metadata_B", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, metaStride, false, false)
                {
                    Usage=EBufferUsage.DynamicCopy,
                    DisposeOnPush=false
                };
                _soaMetadataB.Generate();
            }
            
            Dbg("EnsureSoABuffers complete", "SoA");
        }

        private void EnsureIndexList(uint capacity)
        {
            if (_soaIndexList != null)
                return;
            
            _soaIndexList = new XRDataBuffer("SoA_IndexList", EBufferTarget.ShaderStorageBuffer, capacity + 1, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false
            };

            _soaIndexList.Generate();

            Dbg($"EnsureIndexList created capacity = {capacity}", "SoA");
        }

        private void EnsureMaterialIDs(uint capacity)
        {
            if (_materialIDsBuffer is null)
            {
                _materialIDsBuffer = new XRDataBuffer("MaterialIDs", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, 1, false, true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false
                };

                _materialIDsBuffer.Generate();

                Dbg($"EnsureMaterialIDs created capacity = {capacity}", "Materials");
            }
            else if (_materialIDsBuffer.ElementCount < capacity)
            {
                _materialIDsBuffer.Resize(capacity);

                Dbg($"EnsureMaterialIDs resized -> {capacity}", "Materials");
            }
        }

        private void EnsureGpuDrivenBatchingBuffers(uint capacity)
        {
            EnsureSortKeyBuffer(capacity);
            EnsureBatchRangeBuffer(capacity);
            EnsureBatchCountBuffer();
            EnsureInstanceDataBuffers(capacity);
            EnsureMaterialAggregationBuffer(1u);
        }

        private void EnsureSortKeyBuffer(uint capacity)
        {
            if (_keyIndexBufferA is null ||
                _keyIndexBufferA.ComponentType != EComponentType.UInt ||
                _keyIndexBufferA.ComponentCount != GPUBatchingLayout.SortKeyUIntCount)
            {
                _keyIndexBufferA?.Destroy();
                _keyIndexBufferA = new XRDataBuffer(
                    "GPUSortKeys_Pass",
                    EBufferTarget.ShaderStorageBuffer,
                    capacity,
                    EComponentType.UInt,
                    GPUBatchingLayout.SortKeyUIntCount,
                    false,
                    true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                    BindingIndexOverride = (uint)GPUBatchingBindings.BuildKeysSortKeys
                };
                _keyIndexBufferA.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
                _keyIndexBufferA.Generate();
                return;
            }

            if (_keyIndexBufferA.ElementCount < capacity)
                _keyIndexBufferA.Resize(capacity);
        }

        private void EnsureBatchRangeBuffer(uint capacity)
        {
            if (_gpuBatchRangeBuffer is null ||
                _gpuBatchRangeBuffer.ComponentType != EComponentType.UInt ||
                _gpuBatchRangeBuffer.ComponentCount != GPUBatchingLayout.BatchRangeUIntCount)
            {
                _gpuBatchRangeBuffer?.Destroy();
                _gpuBatchRangeBuffer = new XRDataBuffer(
                    "GPUBatchRanges_Pass",
                    EBufferTarget.ShaderStorageBuffer,
                    capacity,
                    EComponentType.UInt,
                    GPUBatchingLayout.BatchRangeUIntCount,
                    false,
                    true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                    BindingIndexOverride = (uint)GPUBatchingBindings.BuildBatchesBatchRanges,
                    StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read,
                    RangeFlags = EBufferMapRangeFlags.Read
                };
                _gpuBatchRangeBuffer.Generate();
                return;
            }

            if (_gpuBatchRangeBuffer.ElementCount < capacity)
                _gpuBatchRangeBuffer.Resize(capacity);
        }

        private void EnsureBatchCountBuffer()
        {
            if (_gpuBatchCountBuffer is not null)
                return;

            _gpuBatchCountBuffer = new XRDataBuffer(
                "GPUBatchCount_Pass",
                EBufferTarget.ShaderStorageBuffer,
                1u,
                EComponentType.UInt,
                1u,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = false,
                BindingIndexOverride = (uint)GPUBatchingBindings.BuildBatchesBatchCount,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read,
                RangeFlags = EBufferMapRangeFlags.Read
            };
            _gpuBatchCountBuffer.Generate();
            _gpuBatchCountBuffer.SetDataRawAtIndex(0u, 0u);
            _gpuBatchCountBuffer.PushSubData();
        }

        private void EnsureInstanceDataBuffers(uint capacity)
        {
            if (_instanceTransformBuffer is null ||
                _instanceTransformBuffer.ComponentType != EComponentType.Float ||
                _instanceTransformBuffer.ComponentCount != GPUBatchingLayout.InstanceTransformFloatCount)
            {
                _instanceTransformBuffer?.Destroy();
                _instanceTransformBuffer = new XRDataBuffer(
                    "GPUInstanceTransforms_Pass",
                    EBufferTarget.ShaderStorageBuffer,
                    capacity,
                    EComponentType.Float,
                    GPUBatchingLayout.InstanceTransformFloatCount,
                    false,
                    false)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                    BindingIndexOverride = (uint)GPUBatchingBindings.InstanceTransformBuffer
                };
                _instanceTransformBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
                _instanceTransformBuffer.Generate();
            }
            else if (_instanceTransformBuffer.ElementCount < capacity)
            {
                _instanceTransformBuffer.Resize(capacity);
            }

            if (_instanceSourceIndexBuffer is null)
            {
                _instanceSourceIndexBuffer = new XRDataBuffer(
                    "GPUInstanceSources_Pass",
                    EBufferTarget.ShaderStorageBuffer,
                    capacity,
                    EComponentType.UInt,
                    1u,
                    false,
                    true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                    BindingIndexOverride = (uint)GPUBatchingBindings.InstanceSourceIndexBuffer
                };
                _instanceSourceIndexBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
                _instanceSourceIndexBuffer.Generate();
            }
            else if (_instanceSourceIndexBuffer.ElementCount < capacity)
            {
                _instanceSourceIndexBuffer.Resize(capacity);
            }
        }

        private void EnsureMaterialAggregationBuffer(uint requiredEntries)
        {
            uint entryCount = Math.Max(1u, requiredEntries);
            if (_materialAggregationBuffer is null)
            {
                _materialAggregationBuffer = new XRDataBuffer(
                    "MaterialAggregationFlags",
                    EBufferTarget.ShaderStorageBuffer,
                    entryCount,
                    EComponentType.UInt,
                    1u,
                    false,
                    true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    DisposeOnPush = false,
                    Resizable = true,
                    BindingIndexOverride = (uint)GPUBatchingBindings.BuildBatchesMaterialAggregation
                };
                _materialAggregationBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
                _materialAggregationBuffer.Generate();
                return;
            }

            if (_materialAggregationBuffer.ElementCount < entryCount)
                _materialAggregationBuffer.Resize(entryCount);
        }
    }
}
