using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int MeshDescriptorPoolSlabAllocationCapacity = 64;
    private const int MeshOwnershipDiagnosticLimit = 64;

    private readonly object _meshDescriptorPoolSlabLock = new();
    private readonly Dictionary<MeshDescriptorPoolSlabKey, List<MeshDescriptorPoolSlab>>
        _meshDescriptorPoolSlabs = new();
    private int _meshOwnershipDiagnosticCount;

    internal readonly record struct MeshDescriptorPoolSlabKey(
        ulong PoolSizeFingerprint,
        int SetsPerAllocation,
        bool UpdateAfterBind);

    internal sealed class MeshDescriptorPoolSlab
    {
        public required MeshDescriptorPoolSlabKey Key { get; init; }
        public required DescriptorPool Pool { get; init; }
        public int IssuedAllocationCount;
        public int LiveAllocationCount;
    }

    internal sealed class MeshDescriptorPoolSlabLease
    {
        internal readonly MeshDescriptorPoolSlab _slab;
        internal MeshDescriptorPoolSlabLease(MeshDescriptorPoolSlab slab)
            => _slab = slab;

        internal DescriptorPool Pool => _slab.Pool;
        internal bool Released { get; set; }
    }

    internal bool TryAcquireMeshDescriptorPoolSlab(
        DescriptorPoolSize[] perAllocationPoolSizes,
        int setsPerAllocation,
        bool updateAfterBind,
        out MeshDescriptorPoolSlabLease? lease)
    {
        lease = null;
        if (setsPerAllocation <= 0 || perAllocationPoolSizes.Length == 0)
            return false;

        MeshDescriptorPoolSlabKey key = new(
            ComputeMeshDescriptorPoolSizeFingerprint(perAllocationPoolSizes),
            setsPerAllocation,
            updateAfterBind);

        lock (_meshDescriptorPoolSlabLock)
        {
            if (_meshDescriptorPoolSlabs.TryGetValue(key, out List<MeshDescriptorPoolSlab>? slabs))
            {
                for (int i = 0; i < slabs.Count; i++)
                {
                    MeshDescriptorPoolSlab existing = slabs[i];
                    if (existing.IssuedAllocationCount >= MeshDescriptorPoolSlabAllocationCapacity)
                        continue;
                    existing.IssuedAllocationCount++;
                    existing.LiveAllocationCount++;
                    lease = new MeshDescriptorPoolSlabLease(existing);
                    return true;
                }
            }
            else
            {
                slabs = [];
                _meshDescriptorPoolSlabs.Add(key, slabs);
            }

            DescriptorPoolSize[] slabPoolSizes = new DescriptorPoolSize[perAllocationPoolSizes.Length];
            for (int i = 0; i < perAllocationPoolSizes.Length; i++)
            {
                DescriptorPoolSize size = perAllocationPoolSizes[i];
                size.DescriptorCount = checked(size.DescriptorCount * MeshDescriptorPoolSlabAllocationCapacity);
                slabPoolSizes[i] = size;
            }

            DescriptorPool pool;
            fixed (DescriptorPoolSize* poolSizesPtr = slabPoolSizes)
            {
                DescriptorPoolCreateInfo poolInfo = new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit |
                        (updateAfterBind ? DescriptorPoolCreateFlags.UpdateAfterBindBit : 0),
                    PoolSizeCount = (uint)slabPoolSizes.Length,
                    PPoolSizes = poolSizesPtr,
                    MaxSets = checked((uint)(setsPerAllocation * MeshDescriptorPoolSlabAllocationCapacity)),
                };

                if (Api!.CreateDescriptorPool(Device, ref poolInfo, null, out pool) != Result.Success)
                    return false;
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
            RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(
                allocationVariants: 0,
                pools: 1,
                allocatedSets: 0,
                reservedSets: 0);
            MeshDescriptorPoolSlab slab = new()
            {
                Key = key,
                Pool = pool,
                IssuedAllocationCount = 1,
                LiveAllocationCount = 1,
            };
            slabs.Add(slab);
            lease = new MeshDescriptorPoolSlabLease(slab);
            return true;
        }
    }

    internal void ReleaseMeshDescriptorPoolSlab(
        MeshDescriptorPoolSlabLease? lease,
        DescriptorSet[][] descriptorSets,
        uint locallyOwnedSetMask)
    {
        if (lease is null || lease.Released)
            return;

        DescriptorPool pool = lease.Pool;
        bool retireWholePool = false;
        lock (_meshDescriptorPoolSlabLock)
        {
            if (lease.Released)
                return;
            lease.Released = true;

            MeshDescriptorPoolSlab slab = lease._slab;
            slab.LiveAllocationCount--;
            if (slab.LiveAllocationCount < 0)
                throw new InvalidOperationException("Mesh descriptor pool slab lease underflow.");
            if (slab.LiveAllocationCount == 0)
            {
                if (_meshDescriptorPoolSlabs.TryGetValue(slab.Key, out List<MeshDescriptorPoolSlab>? slabs))
                {
                    slabs.Remove(slab);
                    if (slabs.Count == 0)
                        _meshDescriptorPoolSlabs.Remove(slab.Key);
                }
                retireWholePool = true;
            }
        }

        if (retireWholePool)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(
                allocationVariants: 0,
                pools: -1,
                allocatedSets: 0,
                reservedSets: 0);
            RetireDescriptorPool(pool);
            return;
        }

        for (int frameIndex = 0; frameIndex < descriptorSets.Length; frameIndex++)
        {
            DescriptorSet[] sets = descriptorSets[frameIndex];
            for (int setIndex = 0; setIndex < sets.Length; setIndex++)
            {
                if ((locallyOwnedSetMask & (1u << setIndex)) == 0 || sets[setIndex].Handle == 0)
                    continue;
                RetireDescriptorSet(pool, sets[setIndex]);
            }
        }
    }

    internal void RecordMeshDescriptorOwnershipDiagnostic(
        string programName,
        string materialName,
        ulong layoutFingerprint,
        int descriptorFrameSlotCount,
        int allocatedSetCount,
        bool sharedMaterialTier)
    {
        int diagnosticIndex = Interlocked.Increment(ref _meshOwnershipDiagnosticCount);
        if (diagnosticIndex > MeshOwnershipDiagnosticLimit)
            return;

        FrameOpContext? context = ActiveLastActiveFrameOpContext;
        Debug.Vulkan(
            "[Vulkan.MeshOwnership] index={0}/{1} program='{2}' layout=0x{3:X16} material='{4}' " +
            "output={5} outputTarget='{6}' pipeline={7} viewport={8} frameSlots={9} " +
            "sets={10} sharedMaterial={11} planGeneration={12} descriptorGeneration={13}",
            diagnosticIndex,
            MeshOwnershipDiagnosticLimit,
            programName,
            layoutFingerprint,
            materialName,
            context?.ContextKind ?? EVulkanFrameOpContextKind.Unknown,
            context?.OutputTargetName ?? context?.OutputFrameBufferName ?? "<unattributed>",
            context?.PipelineIdentity ?? 0,
            context?.ViewportIdentity ?? 0,
            descriptorFrameSlotCount,
            allocatedSetCount,
            sharedMaterialTier,
            context?.ResourceGeneration ?? 0,
            context?.DescriptorGeneration ?? 0);
    }

    internal int ResolveMeshDescriptorViewFamilyIdentity()
    {
        FrameOpContext? context = ActiveLastActiveFrameOpContext;
        if (context is not { } value)
            return 0;

        // Exclude the rotating external target/image identity. Descriptor ownership is
        // isolated by the stable output/view family, while mutable target resources are
        // rewritten only within that family's pre-record phase.
        return HashCode.Combine(
            (int)value.ContextKind,
            value.PipelineIdentity,
            value.ViewportIdentity);
    }

    private static ulong ComputeMeshDescriptorPoolSizeFingerprint(DescriptorPoolSize[] poolSizes)
    {
        ulong hash = 1469598103934665603UL;
        for (int i = 0; i < poolSizes.Length; i++)
        {
            hash ^= (uint)poolSizes[i].Type;
            hash *= 1099511628211UL;
            hash ^= poolSizes[i].DescriptorCount;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
