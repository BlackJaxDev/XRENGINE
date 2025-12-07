using MemoryPack;
using XREngine.Core.Files;

namespace XREngine
{
    [MemoryPackable]
    public partial class PhysicsGpuMemorySettings : XRAsset
    {
        private const uint MinByteCapacity = 1024u;
        private const uint MinEntryCount = 1024u;

        private uint _maxRigidContactCount = 1_500_000u;
        private uint _maxRigidPatchCount = 1_000_000u;
        private uint _tempBufferCapacity = 32u * 1024u * 1024u;
        private uint _heapCapacity = 256u * 1024u * 1024u;
        private uint _foundLostPairsCapacity = 1_000_000u;
        private uint _foundLostAggregatePairsCapacity = 500_000u;
        private uint _totalAggregatePairsCapacity = 1_500_000u;
        private uint _maxSoftBodyContacts = 250_000u;
        private uint _maxFemClothContacts = 250_000u;
        private uint _maxParticleContacts = 250_000u;
        private uint _collisionStackSize = 32u * 1024u * 1024u;
        private uint _maxHairContacts = 250_000u;

        private static uint EnsureMinimum(uint value, uint minimum)
            => value < minimum ? minimum : value;

        /// <summary>
        /// Maximum number of rigid contacts PhysX keeps resident on the GPU.
        /// </summary>
        public uint MaxRigidContactCount
        {
            get => _maxRigidContactCount;
            set => SetField(ref _maxRigidContactCount, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Maximum number of rigid contact patches stored on the GPU.
        /// </summary>
        public uint MaxRigidPatchCount
        {
            get => _maxRigidPatchCount;
            set => SetField(ref _maxRigidPatchCount, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Capacity of the temporary pinned host buffer used by GPU rigid bodies (bytes).
        /// </summary>
        public uint TempBufferCapacity
        {
            get => _tempBufferCapacity;
            set => SetField(ref _tempBufferCapacity, EnsureMinimum(value, MinByteCapacity));
        }

        /// <summary>
        /// Initial capacity of the combined GPU and pinned host heaps (bytes).
        /// </summary>
        public uint HeapCapacity
        {
            get => _heapCapacity;
            set => SetField(ref _heapCapacity, EnsureMinimum(value, MinByteCapacity));
        }

        /// <summary>
        /// Capacity of the found/lost pair buffers produced by the GPU broadphase (entries).
        /// </summary>
        public uint FoundLostPairsCapacity
        {
            get => _foundLostPairsCapacity;
            set => SetField(ref _foundLostPairsCapacity, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Capacity for aggregate-specific found/lost pairs emitted by the GPU broadphase (entries).
        /// </summary>
        public uint FoundLostAggregatePairsCapacity
        {
            get => _foundLostAggregatePairsCapacity;
            set => SetField(ref _foundLostAggregatePairsCapacity, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Upper bound on the total number of aggregate pairs tracked on the GPU (entries).
        /// </summary>
        public uint TotalAggregatePairsCapacity
        {
            get => _totalAggregatePairsCapacity;
            set => SetField(ref _totalAggregatePairsCapacity, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Maximum number of soft-body contacts simulated on the GPU.
        /// </summary>
        public uint MaxSoftBodyContacts
        {
            get => _maxSoftBodyContacts;
            set => SetField(ref _maxSoftBodyContacts, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Maximum number of FEM cloth contacts simulated on the GPU.
        /// </summary>
        public uint MaxFemClothContacts
        {
            get => _maxFemClothContacts;
            set => SetField(ref _maxFemClothContacts, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Maximum number of particle-system contacts simulated on the GPU.
        /// </summary>
        public uint MaxParticleContacts
        {
            get => _maxParticleContacts;
            set => SetField(ref _maxParticleContacts, EnsureMinimum(value, MinEntryCount));
        }

        /// <summary>
        /// Size of the GPU collision stack used for contact generation (bytes).
        /// </summary>
        public uint CollisionStackSize
        {
            get => _collisionStackSize;
            set => SetField(ref _collisionStackSize, EnsureMinimum(value, MinByteCapacity));
        }

        /// <summary>
        /// Maximum number of hair contacts simulated on the GPU.
        /// </summary>
        public uint MaxHairContacts
        {
            get => _maxHairContacts;
            set => SetField(ref _maxHairContacts, EnsureMinimum(value, MinEntryCount));
        }
    }
}