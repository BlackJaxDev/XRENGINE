using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

internal sealed class SkinnedMeshBoundsCalculator : IDisposable
{
    private const string ShaderPath = "Compute/SkinnedBounds.comp";
    private static readonly Lazy<SkinnedMeshBoundsCalculator> _instance = new(() => new SkinnedMeshBoundsCalculator());

    public static SkinnedMeshBoundsCalculator Instance => _instance.Value;

    private readonly object _syncRoot = new();
    private readonly ConditionalWeakTable<XRMesh, MeshResources> _resourceCache = [];
    private readonly List<MeshResources> _resourceList = [];

    private XRShader? _shader;
    private XRRenderProgram? _program;

    private SkinnedMeshBoundsCalculator()
    {
    }

    public bool TryCompute(RenderableMesh mesh, out Result result)
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

        if (renderer.BoneMatricesBuffer is null || renderer.BoneInvBindMatricesBuffer is null)
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
            renderer.BoneMatricesBuffer.SetBlockIndex(0);
            renderer.BoneInvBindMatricesBuffer.SetBlockIndex(1);

            _program.Uniform("vertexCount", vertexCount);
            _program.Uniform("hasSkinning", 1);
            _program.Uniform("fallbackMatrix", mesh.Component.Transform.RenderMatrix);

            const uint groupSize = 256;
            uint groupsX = Math.Max(1u, (vertexCount + groupSize - 1u) / groupSize);
            _program.DispatchCompute(groupsX, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);
            AbstractRenderer.Current?.WaitForGpu();

            if (!resources.TryReadPositions(xrMesh.VertexCount, out Vector3[] worldPositions))
                return false;

            if (!resources.TryReadBounds(out var bounds))
                bounds = CalculateBounds(worldPositions);
            result = new Result(worldPositions, bounds);
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

    private static AABB CalculateBounds(IReadOnlyList<Vector3> positions)
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
        }
    }

    public readonly struct Result
    {
        public Result(Vector3[] positions, AABB bounds)
        {
            Positions = positions;
            Bounds = bounds;
        }

        public Vector3[] Positions { get; }
        public AABB Bounds { get; }
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
                ShouldMap = true
            };
            _bounds = new XRDataBuffer($"{meshName}_SkinnedBoundsReduction", EBufferTarget.ShaderStorageBuffer, 2, EComponentType.UInt, 4, false, false)
            {
                AttributeName = $"{meshName}_SkinnedBoundsReduction",
                ShouldMap = true,
                Resizable = false
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
                _outputPositions.Resize(vertexCount, false, true);
        }

        public bool TryReadPositions(int vertexCount, out Vector3[] positions)
        {
            positions = new Vector3[vertexCount];
            if (_outputPositions is null)
                return false;

            if (_outputPositions.ClientSideSource is null)
                return false;

            unsafe
            {
                Vector4* ptr = (Vector4*)_outputPositions.Address.Pointer;
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector4 v = ptr[i];
                    positions[i] = new Vector3(v.X, v.Y, v.Z);
                }
            }

            return true;
        }

        public void ResetBoundsBuffer()
        {
            if (_bounds is null)
                return;

            _bounds.SetDataRawAtIndex(0u, PositiveInfinityPacked);
            _bounds.SetDataRawAtIndex(1u, NegativeInfinityPacked);
            _bounds.PushSubData();
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
