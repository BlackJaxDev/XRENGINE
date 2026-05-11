using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

internal sealed class SkinnedMeshBoundsCalculator : IDisposable
{
    private const string ShaderPath = "Compute/Animation/SkinnedBounds.comp";
    private const string ReduceShaderPath = "Compute/Animation/SkinnedBoundsReduce.comp";
    private static readonly Lazy<SkinnedMeshBoundsCalculator> _instance = new(() => new SkinnedMeshBoundsCalculator());

    public static SkinnedMeshBoundsCalculator Instance => _instance.Value;

    private readonly object _syncRoot = new();
    private readonly ConditionalWeakTable<XRMesh, MeshResources> _resourceCache = [];
    private readonly List<MeshResources> _resourceList = [];

    // Registry of skinned RenderableMeshes that have been processed via the GPU bounds path.
    // Used by VPRC_BuildAccelerationStructure to refresh their command-AABBs into the BVH
    // leaf buffer every frame when SkinnedBoundsGpuDirectAabbWrite is enabled.
    private readonly object _registrySync = new();
    private readonly HashSet<RenderableMesh> _registeredSkinnedMeshes = [];
    // Re-used scratch list for Path A dispatch to avoid per-frame allocations.
    private readonly List<uint> _pathAScratchIndices = new(16);

    private XRShader? _shader;
    private XRRenderProgram? _program;
    private XRShader? _reduceShader;
    private XRRenderProgram? _reduceProgram;

    private SkinnedMeshBoundsCalculator()
    {
    }

    public bool TryCompute(RenderableMesh mesh, out Result result)
    {
        if (Engine.IsRenderThread)
            return TryComputeOnRenderThread(mesh, out result);

        // Never block worker/update threads waiting for render-thread execution.
        // Callers can fall back to CPU bounds or keep cached bounds for this frame.
        result = default;
        return false;
    }

    private bool TryComputeOnRenderThread(RenderableMesh mesh, out Result result)
    {
        result = default;

        if (AbstractRenderer.Current is null)
            return false;

        if (mesh?.IsSkinned != true)
            return false;

        var renderer = mesh.CurrentLODRenderer;
        var xrMesh = renderer?.Mesh;
        if (renderer is null || xrMesh is null || xrMesh.VertexCount <= 0)
            return false;

        if (!MeshSupportsGpuSkinning(xrMesh))
            return false;

        // Fast path: if SkinningPrepass already produced skinned positions for this renderer
        // this frame, reduce over them rather than re-running the full skin pass and reading
        // back every vertex. Costs ~32 bytes of readback (the bounds quads) instead of N*16.
        if (TryComputeFromPrepassOutput(mesh, renderer, xrMesh, out result))
            return true;

        if (renderer.ActiveBoneMatricesBuffer is null || renderer.ActiveBoneInvBindMatricesBuffer is null)
            return false;

        lock (_syncRoot)
        {
            EnsureInitialized();
            if (_program is null)
                return false;

            var resources = GetOrCreateResources(xrMesh);
            if (!resources.IsValid)
                return false;

            uint vertexCount = (uint)xrMesh.VertexCount;
            resources.EnsureCapacity(vertexCount);
            resources.ResetBoundsBuffer();
            renderer.ActiveBoneMatricesBuffer.SetBlockIndex(0);
            renderer.ActiveBoneInvBindMatricesBuffer.SetBlockIndex(1);

            _program.Uniform("vertexCount", vertexCount);
            _program.Uniform("hasSkinning", 1);
            _program.Uniform("boneMatrixBase", renderer.ActiveBoneMatrixBase);
            _program.Uniform("fallbackMatrix", mesh.Component.Transform.RenderMatrix);

            const uint groupSize = 256;
            uint groupsX = Math.Max(1u, (vertexCount + groupSize - 1u) / groupSize);
            _program.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);
            AbstractRenderer.Current?.WaitForGpu();

            if (!resources.TryReadPositions(xrMesh.VertexCount, out Vector3[] worldPositions))
                return false;

            // Use root bone's world matrix as the basis for skinned mesh bounds.
            // Bounds are calculated in root bone local space.
            Matrix4x4 basis = mesh.RootBone?.RenderMatrix ?? mesh.Component.Transform.RenderMatrix;
            Matrix4x4 invBasis = Matrix4x4.Invert(basis, out var inv) ? inv : Matrix4x4.Identity;

            var localPositions = new Vector3[worldPositions.Length];
            for (int i = 0; i < worldPositions.Length; i++)
                localPositions[i] = Vector3.Transform(worldPositions[i], invBasis);

            var bounds = CalculateBounds(localPositions);
            result = new Result(localPositions, bounds, basis);
            return true;
        }
    }

    private void EnsureInitialized()
    {
        if (_program is not null)
            return;

        _shader ??= ShaderHelper.LoadEngineShader(ShaderPath, EShaderType.Compute);
        _program = new XRRenderProgram(true, false, _shader);
    }

    private void EnsureReduceInitialized()
    {
        if (_reduceProgram is not null)
            return;

        _reduceShader ??= ShaderHelper.LoadEngineShader(ReduceShaderPath, EShaderType.Compute);
        _reduceProgram = new XRRenderProgram(true, false, _reduceShader);
    }

    /// <summary>
    /// Path A/B fast path: when <see cref="SkinningPrepassDispatcher"/> already produced
    /// skinned positions for this renderer this frame, dispatch the lightweight reduce
    /// shader over those positions to obtain the root-local AABB without redoing the
    /// (expensive) skinning work or reading every vertex back to the CPU.
    /// </summary>
    private bool TryComputeFromPrepassOutput(RenderableMesh mesh, XRMeshRenderer renderer, XRMesh xrMesh, out Result result)
    {
        result = default;

        if (xrMesh.Interleaved)
            return false; // SkinningPrepass disables compute skinning for interleaved meshes.

        var (positionsOut, _, _, _) = SkinningPrepassDispatcher.Instance.GetSkinnedBuffers(renderer);
        if (positionsOut is null)
            return false;

        lock (_syncRoot)
        {
            EnsureReduceInitialized();
            if (_reduceProgram is null)
                return false;

            var resources = GetOrCreateResources(xrMesh);
            if (!resources.IsValid)
                return false;

            uint vertexCount = (uint)xrMesh.VertexCount;
            resources.ResetBoundsBuffer();

            // Bind PositionsIn at slot 0 and the per-mesh bounds buffer at slot 1.
            positionsOut.SetBlockIndex(0);
            resources.BindBoundsBufferForReduce(1);

            _reduceProgram.Uniform("vertexCount", vertexCount);
            _reduceProgram.Uniform("slotIndex", 0u);
            _reduceProgram.Uniform("transformToWorld", 0); // produce root-local bounds
            _reduceProgram.Uniform("worldMatrix", Matrix4x4.Identity);

            const uint groupSize = 256;
            uint groupsX = Math.Max(1u, (vertexCount + groupSize - 1u) / groupSize);
            _reduceProgram.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);
            AbstractRenderer.Current?.WaitForGpu();

            if (!resources.TryReadBounds(out AABB localBounds))
                return false;

            // Bounds are already in root-bone-local space (SkinningPrepass output convention).
            Matrix4x4 basis = mesh.RootBone?.RenderMatrix ?? mesh.Component.Transform.RenderMatrix;
            // Empty positions array signals "GPU-only fast path": consumers that need
            // CPU triangle data (e.g. SkinnedMeshBvhScheduler with cooked CPU BVH) must
            // fall back via Engine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader.
            result = new Result(Array.Empty<Vector3>(), localBounds, basis);
            return true;
        }
    }

    /// <summary>
    /// Path A direct write: dispatch the reduce shader over the prepass-skinned
    /// positions and write world-space AABBs straight into <paramref name="targetScene"/>'s
    /// <see cref="GPUScene.CommandAabbBuffer"/> for every command owned by the renderer.
    /// This bypasses the CPU 8-corner transform performed by
    /// <c>GPUScene.WriteTightCommandAabb</c>; the renderer is registered as GPU-AABB
    /// owner so future calls to that method seed the +inf/-inf sentinel instead.
    /// </summary>
    /// <remarks>
    /// Caller must be on the render thread. Returns false if there is no prepass
    /// output for the renderer, no commands registered for it, or the BVH leaf
    /// buffer cannot be acquired.
    /// </remarks>
    public bool DispatchPathADirectWrite(RenderableMesh mesh, GPUScene targetScene, List<uint> scratchIndices)
    {
        if (mesh is null || targetScene is null || scratchIndices is null)
            return false;
        if (!Engine.IsRenderThread)
            return false;

        var renderer = mesh.CurrentLODRenderer;
        var xrMesh = renderer?.Mesh;
        if (renderer is null || xrMesh is null || xrMesh.Interleaved)
            return false;
        if (!MeshSupportsGpuSkinning(xrMesh))
            return false;

        var (positionsOut, _, _, _) = SkinningPrepassDispatcher.Instance.GetSkinnedBuffers(renderer);
        if (positionsOut is null)
            return false;

        if (!targetScene.TryGetCommandIndicesForRenderer(renderer, scratchIndices) || scratchIndices.Count == 0)
            return false;

        // Mark renderer as GPU-AABB owner so any CPU-side WriteTightCommandAabb
        // calls (insert/update paths) seed the sentinel instead of doing the
        // 8-corner transform that would be immediately overwritten anyway.
        targetScene.SetRendererOwnsGpuAabb(renderer, true);

        // Ensure capacity for the highest slot we're about to write.
        uint maxIndex = 0u;
        for (int i = 0; i < scratchIndices.Count; i++)
        {
            uint idx = scratchIndices[i];
            if (idx > maxIndex) maxIndex = idx;
        }
        targetScene.EnsureCommandAabbCapacity(maxIndex + 1u);

        var commandAabbBuffer = targetScene.CommandAabbBuffer;
        if (commandAabbBuffer is null)
            return false;

        Matrix4x4 basis = mesh.RootBone?.RenderMatrix ?? mesh.Component.Transform.RenderMatrix;
        uint vertexCount = (uint)xrMesh.VertexCount;
        if (vertexCount == 0u)
            return false;

        lock (_syncRoot)
        {
            EnsureReduceInitialized();
            if (_reduceProgram is null)
                return false;

            // Bind positions at slot 0 and the scene's command-AABB SSBO at slot 1
            // for the reducer's BoundsBits binding.
            positionsOut.SetBlockIndex(0);
            commandAabbBuffer.SetBlockIndex(1);

            _reduceProgram.Uniform("vertexCount", vertexCount);
            _reduceProgram.Uniform("transformToWorld", 1);
            _reduceProgram.Uniform("worldMatrix", basis);

            const uint groupSize = 256u;
            uint groupsX = Math.Max(1u, (vertexCount + groupSize - 1u) / groupSize);

            for (int i = 0; i < scratchIndices.Count; i++)
            {
                uint slot = scratchIndices[i];
                // Seed sentinel so atomic min/max produces correct envelope.
                targetScene.WriteCommandAabbSentinel(slot);

                _reduceProgram.Uniform("slotIndex", slot);
                _reduceProgram.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            }
        }
        return true;
    }

    /// <summary>
    /// Register a skinned mesh that should have its command-AABBs refreshed via Path A
    /// every frame the BVH is rebuilt. Call from <see cref="RenderableMesh"/> after a
    /// successful GPU bounds compute.
    /// </summary>
    public void RegisterSkinnedMesh(RenderableMesh mesh)
    {
        if (mesh is null)
            return;
        lock (_registrySync)
            _registeredSkinnedMeshes.Add(mesh);
    }

    /// <summary>
    /// Unregister a skinned mesh. Also clears its GPU-AABB ownership on the supplied
    /// scene so subsequent CPU writes go back through the 8-corner path.
    /// </summary>
    public void UnregisterSkinnedMesh(RenderableMesh mesh, GPUScene? scene = null)
    {
        if (mesh is null)
            return;
        lock (_registrySync)
            _registeredSkinnedMeshes.Remove(mesh);

        var renderer = mesh.CurrentLODRenderer;
        if (renderer is not null)
            scene?.SetRendererOwnsGpuAabb(renderer, false);
    }

    /// <summary>
    /// Render-thread per-frame entry point: for every registered skinned mesh whose
    /// VisualScene matches <paramref name="targetScene"/>, dispatch Path A so its
    /// world-space command AABBs land in the BVH leaf buffer before the BVH is built.
    /// </summary>
    public void RefreshAllSkinnedAabbs(GPUScene targetScene)
    {
        if (targetScene is null || !Engine.IsRenderThread)
            return;

        RenderableMesh[] snapshot;
        lock (_registrySync)
        {
            if (_registeredSkinnedMeshes.Count == 0)
                return;
            snapshot = new RenderableMesh[_registeredSkinnedMeshes.Count];
            _registeredSkinnedMeshes.CopyTo(snapshot);
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            var mesh = snapshot[i];
            // Only refresh meshes belonging to the same VisualScene as targetScene.
            var visualScene = mesh.World?.VisualScene;
            if (visualScene is null || !ReferenceEquals(visualScene.GPUCommands, targetScene))
                continue;

            DispatchPathADirectWrite(mesh, targetScene, _pathAScratchIndices);
        }
    }

    private MeshResources GetOrCreateResources(XRMesh mesh)
    {
        if (_resourceCache.TryGetValue(mesh, out MeshResources? existing) && existing is not null)
            return existing;

        MeshResources created = new(mesh);
        _resourceCache.Add(mesh, created);
        _resourceList.Add(created);
        return created;
    }

    private static bool MeshSupportsGpuSkinning(XRMesh mesh)
    {
        if (!mesh.HasSkinning)
            return false;

        if (mesh.MaxWeightCount <= 4)
            return true;

        return Engine.Rendering.Settings.OptimizeSkinningTo4Weights;
    }

    internal static AABB CalculateBounds(IReadOnlyList<Vector3> positions)
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);

        foreach (var position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return new AABB(min, max);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            foreach (var resource in _resourceList)
                resource.Dispose();
            _resourceList.Clear();

            _program?.Destroy();
            _shader?.Destroy();
            _program = null;
            _shader = null;

            _reduceProgram?.Destroy();
            _reduceShader?.Destroy();
            _reduceProgram = null;
            _reduceShader = null;
        }
    }

    public readonly struct Result(Vector3[] positions, AABB bounds, Matrix4x4 basis, bool isWorldSpace)
    {
        public Result(Vector3[] positions, AABB bounds)
            : this(positions, bounds, Matrix4x4.Identity, true)
        {
        }

        public Result(Vector3[] positions, AABB bounds, Matrix4x4 basis)
            : this(positions, bounds, basis, false)
        {
        }

        public Vector3[] Positions { get; } = positions;
        public AABB Bounds { get; } = bounds;
        /// <summary>
        /// Matrix that transforms the local bounds/positions back into render space when <see cref="IsWorldSpace"/> is false.
        /// </summary>
        public Matrix4x4 Basis { get; } = basis;
        public bool IsWorldSpace { get; } = isWorldSpace;
    }

    private sealed class MeshResources : IDisposable
    {
        private readonly XRDataBuffer? _positions;
        private readonly XRDataBuffer? _boneIndices;
        private readonly XRDataBuffer? _boneWeights;
        private readonly XRDataBuffer? _outputPositions;
        private readonly XRDataBuffer? _bounds;

        private static readonly UInt4 PositiveInfinityPacked = UInt4.FromVector(new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 1f));
        private static readonly UInt4 NegativeInfinityPacked = UInt4.FromVector(new Vector4(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, -1f));

        public bool IsValid { get; }

        public MeshResources(XRMesh mesh)
        {
            string meshName = string.IsNullOrWhiteSpace(mesh.Name) ? $"Mesh_{mesh.GetHashCode():X}" : mesh.Name;

            if (mesh.BoneWeightOffsets?.Integral != true)
                return;

            _positions = CloneSourceBuffer(mesh, ECommonBufferType.Position.ToString(), meshName, 4u);
            _boneIndices = mesh.BoneWeightOffsets.Clone(false, EBufferTarget.ShaderStorageBuffer);
            _boneWeights = mesh.BoneWeightCounts?.Clone(false, EBufferTarget.ShaderStorageBuffer);
            _outputPositions = new XRDataBuffer($"{meshName}_SkinnedBoundsOutput", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(1, mesh.VertexCount), EComponentType.Float, 4, false, false)
            {
                AttributeName = $"{meshName}_SkinnedBoundsOutput",
                ShouldMap = true,
                Resizable = false,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
                RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent
            };
            _bounds = new XRDataBuffer($"{meshName}_SkinnedBoundsReduction", EBufferTarget.ShaderStorageBuffer, 2, EComponentType.UInt, 4, false, false)
            {
                AttributeName = $"{meshName}_SkinnedBoundsReduction",
                Resizable = false,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage
            };

            if (_positions is null || _boneIndices is null || _boneWeights is null || _outputPositions is null || _bounds is null)
                return;

            _boneIndices.AttributeName = $"{meshName}_SkinnedBoundsIndices";
            _boneIndices.SetBlockIndex(2);

            _boneWeights.AttributeName = $"{meshName}_SkinnedBoundsWeights";
            _boneWeights.SetBlockIndex(3);

            _outputPositions.SetBlockIndex(5);
            _bounds.SetBlockIndex(6);

            IsValid = true;
        }

        public void EnsureCapacity(uint vertexCount)
        {
            if (_outputPositions is null)
                return;

            if (_outputPositions.ElementCount < vertexCount)
            {
                _outputPositions.Resize(vertexCount, false, true);
                _outputPositions.PushData();
            }

            if (!_outputPositions.IsMapped)
                _outputPositions.MapBufferData();
        }

        public bool TryReadPositions(int vertexCount, out Vector3[] positions)
        {
            positions = new Vector3[vertexCount];
            if (_outputPositions is null)
                return false;

            if (!TryGetMappedAddress(_outputPositions, out VoidPtr mappedAddress))
                return false;

            unsafe
            {
                Vector4* ptr = (Vector4*)mappedAddress.Pointer;
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector4 v = ptr[i];
                    positions[i] = new Vector3(v.X, v.Y, v.Z);
                }
            }

            return true;
        }

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

        public void ResetBoundsBuffer()
        {
            if (_bounds is null)
                return;

            _bounds.SetDataRawAtIndex(0u, PositiveInfinityPacked);
            _bounds.SetDataRawAtIndex(1u, NegativeInfinityPacked);
            _bounds.PushSubData();
        }

        public void BindBoundsBufferForReduce(uint binding)
        {
            _bounds?.SetBlockIndex(binding);
        }

        public bool TryReadBounds(out AABB bounds)
        {
            bounds = default;
            if (_bounds is null)
                return false;

            UInt4 minBits = _bounds.GetDataRawAtIndex<UInt4>(0);
            UInt4 maxBits = _bounds.GetDataRawAtIndex<UInt4>(1);

            Vector3 min = minBits.ToVector3();
            if (float.IsInfinity(min.X) || float.IsInfinity(min.Y) || float.IsInfinity(min.Z))
                return false;

            Vector3 max = maxBits.ToVector3();
            if (float.IsInfinity(max.X) || float.IsInfinity(max.Y) || float.IsInfinity(max.Z))
                return false;

            bounds = new AABB(min, max);
            return true;
        }

        public void Dispose()
        {
            _positions?.Destroy();
            _boneIndices?.Destroy();
            _boneWeights?.Destroy();
            _outputPositions?.Destroy();
            _bounds?.Destroy();
        }

        private static XRDataBuffer? CloneSourceBuffer(XRMesh mesh, string key, string meshName, uint bindingIndex)
        {
            if (!mesh.Buffers.TryGetValue(key, out XRDataBuffer? buffer) || buffer is null)
                return null;

            var clone = buffer.Clone(false, EBufferTarget.ShaderStorageBuffer);
            clone.AttributeName = $"{meshName}_{key}_SkinnedBounds";
            clone.SetBlockIndex(bindingIndex);
            return clone;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UInt4
        {
            public uint X;
            public uint Y;
            public uint Z;
            public uint W;

            public static UInt4 FromVector(Vector4 value)
                => new()
                {
                    X = BitConverter.SingleToUInt32Bits(value.X),
                    Y = BitConverter.SingleToUInt32Bits(value.Y),
                    Z = BitConverter.SingleToUInt32Bits(value.Z),
                    W = BitConverter.SingleToUInt32Bits(value.W)
                };

            public Vector3 ToVector3()
                => new(
                    BitConverter.UInt32BitsToSingle(X),
                    BitConverter.UInt32BitsToSingle(Y),
                    BitConverter.UInt32BitsToSingle(Z));
        }
    }
}
