using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private static void EnsurePersistentReadbackMapping(XRDataBuffer buffer)
        {
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
            Dbg("MapBuffers begin","Buffers");

            if (_culledCountBuffer is not null)
                EnsurePersistentReadbackMapping(_culledCountBuffer);

            if (_drawCountBuffer is not null)
                EnsurePersistentReadbackMapping(_drawCountBuffer);

            if (_cullingOverflowFlagBuffer is not null)
                EnsurePersistentReadbackMapping(_cullingOverflowFlagBuffer);

            if (_indirectOverflowFlagBuffer is not null)
                EnsurePersistentReadbackMapping(_indirectOverflowFlagBuffer);

            if (_statsBuffer is not null)
                EnsurePersistentReadbackMapping(_statsBuffer);

            if (_truncationFlagBuffer is not null)
                EnsurePersistentReadbackMapping(_truncationFlagBuffer);

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

            Dbg("UnmapBuffers complete","Buffers");
        }

        private void GenerateShaders()
        {
            Dbg("GenerateShaders start","Lifecycle");

            _cullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCulling.comp", EShaderType.Compute));
            //_buildKeysComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderBuildKeys.comp", EShaderType.Compute));
            //RadixIndexSortComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderRadixIndexSort.comp", EShaderType.Compute));
            _indirectRenderTaskShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderIndirect.comp", EShaderType.Compute));
            _resetCountersComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderResetCounters.comp", EShaderType.Compute));
            _extractSoAComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderExtractSoA.comp", EShaderType.Compute));
            _soACullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCullingSoA.comp", EShaderType.Compute));
            //HiZSoACullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderHiZSoACulling.comp", EShaderType.Compute));
            _gatherProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderGather.comp", EShaderType.Compute));
            _copyCommandsProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCopyCommands.comp", EShaderType.Compute));

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
                        VerifyBufferLengths(scene, max);
                    else
                        Initialize(scene, max);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to initialize GPURenderPassCollection for pass {RenderPass}: {ex}");
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

            if (remapNeeded)
                MapBuffers();

            Dbg($"Capacity change -> RegenerateBuffers newMax={max}", "Buffers");
        }

        private void Initialize(GPUScene scene, uint max)
        {
            _initialized = true;
            _lastMaxCommands = max;

            bool remapNeeded = RegenerateBuffers(scene);
            GenerateShaders();
            if (remapNeeded)
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
        private void MakeIndirectRenderer(GPUScene scene)
        {
            scene.EnsureAtlasBuffers();

            _indirectRenderer = new XRMeshRenderer();
            var defVer = _indirectRenderer.GetDefaultVersion();
            defVer.Generate();

            //TODO: move indices into the XRMeshRenderer or XRMesh, no need to make again and again per api wrapper
            //That will avoid this bandaid situation
            var rend = AbstractRenderer.Current as OpenGLRenderer;
            if (rend?.TryGetAPIRenderObject(defVer, out var obj) ?? false)
            {
                GLMeshRenderer? r = obj as GLMeshRenderer;
                if (r is not null)
                {
                    var buf = GetIndexBuffer(scene, out IndexSize elementSize);
                    r.TriangleIndicesBuffer = rend.GenericToAPI<GLDataBuffer>(buf);
                }
            }

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
        }

        private bool RegenerateBuffers(GPUScene gpuScene)
        {
            Dbg("RegenerateBuffers begin","Buffers");
            uint capacity = gpuScene.AllocatedMaxCommandCount;
            bool remapNeeded = false;

            if (_culledSceneToRenderBuffer is null || _culledSceneToRenderBuffer.ElementCount != capacity)
            {
                _culledSceneToRenderBuffer?.Destroy();
                _culledSceneToRenderBuffer = MakeCulledSceneToRenderBuffer(capacity);
            }

            remapNeeded |= EnsureIndirectDrawBuffer(capacity);
            remapNeeded |= EnsureParameterBuffer(ref _culledCountBuffer, "CulledCount");
            remapNeeded |= EnsureParameterBuffer(ref _drawCountBuffer, "DrawCount");
            remapNeeded |= EnsureFlagBuffer(ref _cullingOverflowFlagBuffer, "CullingOverflowFlag");
            remapNeeded |= EnsureFlagBuffer(ref _indirectOverflowFlagBuffer, "IndirectOverflowFlag");

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

            if (IndirectDebug.ValidateBufferLayouts)
                ValidateIndirectBufferLayout(capacity, remapNeeded);

            Dbg($"RegenerateBuffers complete capacity={capacity}","Buffers");

            return remapNeeded;
        }

        private bool EnsureIndirectDrawBuffer(uint capacity)
        {
            bool recreated = false;

            if (_indirectDrawBuffer is not null)
            {
                bool strideMismatch = _indirectDrawBuffer.ElementSize != _indirectCommandStride;
                bool countMismatch = _indirectDrawBuffer.ElementCount != capacity;

                if (strideMismatch || countMismatch)
                {
                    if (strideMismatch)
                        Debug.LogWarning("Indirect draw buffer stride mismatch detected. Forcing recreation.");
                    else
                        Dbg("Resizing indirect draw buffer to match new capacity.", "Buffers");
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
                _indirectDrawBuffer.Generate();
                recreated = true;
            }

            return recreated;
        }

        private bool EnsureParameterBuffer(ref XRDataBuffer? buffer, string name)
        {
            bool created = false;

            if (buffer is not null)
            {
                bool invalidLayout = buffer.ElementCount != 1 || buffer.ComponentType != EComponentType.UInt;
                if (invalidLayout)
                {
                    Debug.LogWarning($"Parameter buffer {name} has unexpected layout. Recreating.");
                    buffer.Destroy();
                    buffer = null;
                }
            }

            if (buffer is null)
            {
                buffer = new XRDataBuffer(name, EBufferTarget.ParameterBuffer, 1, EComponentType.UInt, 1, false, true)
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
                created = true;
            }

            if (!created && IndirectDebug.ForceParameterRemap && buffer is not null)
            {
                buffer.UnmapBufferData();
                created = true;
            }

            return created || (buffer is not null && buffer.ActivelyMapping.Count == 0);
        }

        private bool EnsureFlagBuffer(ref XRDataBuffer? buffer, string name)
        {
            bool created = false;

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
                created = true;
            }

            return created || (buffer is not null && buffer.ActivelyMapping.Count == 0);
        }

        private void ValidateIndirectBufferLayout(uint capacity, bool remapPending)
        {
            if (_indirectDrawBuffer is null)
            {
                Debug.LogWarning("Indirect draw buffer is null before rendering.");
                return;
            }

            if (_indirectDrawBuffer.ElementSize != _indirectCommandStride)
            {
                Debug.LogWarning($"Indirect draw stride mismatch. Expected {_indirectCommandStride} bytes, buffer reports {_indirectDrawBuffer.ElementSize}.");
            }

            if (_indirectDrawBuffer.ElementCount < capacity)
            {
                Debug.LogWarning($"Indirect draw buffer smaller than required capacity (buffer={_indirectDrawBuffer.ElementCount}, required={capacity}).");
            }

            if (_drawCountBuffer is not null && _drawCountBuffer.ElementSize < sizeof(uint))
            {
                Debug.LogWarning($"Draw count buffer is undersized ({_drawCountBuffer.ElementSize} bytes); expected at least {sizeof(uint)}.");
            }

            if (_culledCountBuffer is not null && _culledCountBuffer.ElementSize < sizeof(uint))
            {
                Debug.LogWarning($"Culled count buffer is undersized ({_culledCountBuffer.ElementSize} bytes); expected at least {sizeof(uint)}.");
            }

            if (IndirectDebug.ValidateLiveHandles && !remapPending)
            {
                if (_drawCountBuffer is not null && _drawCountBuffer.ActivelyMapping.Count == 0)
                    Debug.LogWarning("Draw count buffer is not mapped; GPU count reads may see stale data.");

                if (_culledCountBuffer is not null && _culledCountBuffer.ActivelyMapping.Count == 0)
                    Debug.LogWarning("Culled count buffer is not mapped; visibility counters may be invalid.");
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
    }
}
