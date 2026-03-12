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

        public uint MaxRigidContactCount
        {
            get => _maxRigidContactCount;
            set => SetField(ref _maxRigidContactCount, EnsureMinimum(value, MinEntryCount));
        }

        public uint MaxRigidPatchCount
        {
            get => _maxRigidPatchCount;
            set => SetField(ref _maxRigidPatchCount, EnsureMinimum(value, MinEntryCount));
        }

        public uint TempBufferCapacity
        {
            get => _tempBufferCapacity;
            set => SetField(ref _tempBufferCapacity, EnsureMinimum(value, MinByteCapacity));
        }

        public uint HeapCapacity
        {
            get => _heapCapacity;
            set => SetField(ref _heapCapacity, EnsureMinimum(value, MinByteCapacity));
        }

        public uint FoundLostPairsCapacity
        {
            get => _foundLostPairsCapacity;
            set => SetField(ref _foundLostPairsCapacity, EnsureMinimum(value, MinEntryCount));
        }

        public uint FoundLostAggregatePairsCapacity
        {
            get => _foundLostAggregatePairsCapacity;
            set => SetField(ref _foundLostAggregatePairsCapacity, EnsureMinimum(value, MinEntryCount));
        }

        public uint TotalAggregatePairsCapacity
        {
            get => _totalAggregatePairsCapacity;
            set => SetField(ref _totalAggregatePairsCapacity, EnsureMinimum(value, MinEntryCount));
        }

        public uint MaxSoftBodyContacts
        {
            get => _maxSoftBodyContacts;
            set => SetField(ref _maxSoftBodyContacts, EnsureMinimum(value, MinEntryCount));
        }

        public uint MaxFemClothContacts
        {
            get => _maxFemClothContacts;
            set => SetField(ref _maxFemClothContacts, EnsureMinimum(value, MinEntryCount));
        }

        public uint MaxParticleContacts
        {
            get => _maxParticleContacts;
            set => SetField(ref _maxParticleContacts, EnsureMinimum(value, MinEntryCount));
        }

        public uint CollisionStackSize
        {
            get => _collisionStackSize;
            set => SetField(ref _collisionStackSize, EnsureMinimum(value, MinByteCapacity));
        }

        public uint MaxHairContacts
        {
            get => _maxHairContacts;
            set => SetField(ref _maxHairContacts, EnsureMinimum(value, MinEntryCount));
        }
    }
}