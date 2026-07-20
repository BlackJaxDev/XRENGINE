using XREngine.Scene.Physics.Physx;
using Assimp;
using MagicPhysX;
using System.Collections.Concurrent;
using XREngine.Scene.Physics;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxMaterial : AbstractPhysicsMaterial
    {
        private readonly unsafe PxMaterial* _materialPtr;

        private bool _isReleased;
        public bool IsReleased => _isReleased;

        public static ConcurrentDictionary<nint, PhysxMaterial> All { get; } = new();
        public static PhysxMaterial? Get(PxMaterial* ptr)
            => All.TryGetValue((nint)ptr, out var material) ? material : null;

        public PhysxMaterial()
        {
            _materialPtr = PhysxScene.PhysicsPtr->CreateMaterialMut(0.0f, 0.0f, 0.0f);
            PhysxObjectLog.AddOrUpdate(All, nameof(All), (nint)MaterialPtr, this);
            PhysxObjectLog.Created(this, (nint)MaterialPtr, "default");
        }
        public PhysxMaterial(
            float staticFriction,
            float dynamicFriction,
            float restitution)
        {
            _materialPtr = PhysxScene.PhysicsPtr->CreateMaterialMut(staticFriction, dynamicFriction, restitution);
            PhysxObjectLog.AddOrUpdate(All, nameof(All), (nint)MaterialPtr, this);
            PhysxObjectLog.Created(this, (nint)MaterialPtr, $"sf={staticFriction} df={dynamicFriction} r={restitution}");
        }
        public PhysxMaterial(PxMaterial* materialPtr)
        {
            _materialPtr = materialPtr;
            PhysxObjectLog.AddOrUpdate(All, nameof(All), (nint)MaterialPtr, this);
            PhysxObjectLog.Created(this, (nint)MaterialPtr, "from-existing");
        }
        public PhysxMaterial(
            float staticFriction,
            float dynamicFriction,
            float restitution,
            float damping,
            PhysicsMaterialCombineMode frictionCombineMode,
            PhysicsMaterialCombineMode restitutionCombineMode,
            bool disableFriction,
            bool disableStrongFriction,
            bool improvedPatchFriction,
            bool compliantContact)
        {
            _materialPtr = PhysxScene.PhysicsPtr->CreateMaterialMut(staticFriction, dynamicFriction, restitution);
            Damping = damping;
            FrictionCombineMode = frictionCombineMode;
            RestitutionCombineMode = restitutionCombineMode;
            DisableFriction = disableFriction;
            DisableStrongFriction = disableStrongFriction;
            ImprovedPatchFriction = improvedPatchFriction;
            CompliantContact = compliantContact;

            PhysxObjectLog.AddOrUpdate(All, nameof(All), (nint)MaterialPtr, this);
            PhysxObjectLog.Created(this, (nint)MaterialPtr, "custom");
        }

        public PxMaterial* MaterialPtr => _materialPtr;

        public void Release()
        {
            if (_isReleased)
                return;
            _isReleased = true;

            PhysxObjectLog.RemoveIfSame(All, nameof(All), (nint)MaterialPtr, this);
            PhysxObjectLog.Released(this, (nint)MaterialPtr);

            // PxMaterial is ref-counted in PhysX.
            PxRefCounted_release_mut((PxRefCounted*)_materialPtr);
        }

        public override float StaticFriction
        {
            get => _materialPtr->GetStaticFriction();
            set
            {
                var prev = _materialPtr->GetStaticFriction();
                _materialPtr->SetStaticFrictionMut(value);
                PhysxObjectLog.Modified(this, (nint)MaterialPtr, nameof(StaticFriction), $"{prev} -> {value}");
            }
        }
        public override float DynamicFriction
        {
            get => _materialPtr->GetDynamicFriction();
            set
            {
                var prev = _materialPtr->GetDynamicFriction();
                _materialPtr->SetDynamicFrictionMut(value);
                PhysxObjectLog.Modified(this, (nint)MaterialPtr, nameof(DynamicFriction), $"{prev} -> {value}");
            }
        }
        public override float Restitution
        {
            get => _materialPtr->GetRestitution();
            set
            {
                var prev = _materialPtr->GetRestitution();
                _materialPtr->SetRestitutionMut(value);
                PhysxObjectLog.Modified(this, (nint)MaterialPtr, nameof(Restitution), $"{prev} -> {value}");
            }
        }
        public override float Damping
        {
            get => _materialPtr->GetDamping();
            set
            {
                var prev = _materialPtr->GetDamping();
                _materialPtr->SetDampingMut(value);
                PhysxObjectLog.Modified(this, (nint)MaterialPtr, nameof(Damping), $"{prev} -> {value}");
            }
        }
        public override PhysicsMaterialCombineMode FrictionCombineMode
        {
            get => Conv(_materialPtr->GetFrictionCombineMode());
            set
            {
                var prev = Conv(_materialPtr->GetFrictionCombineMode());
                _materialPtr->SetFrictionCombineModeMut(Conv(value));
                PhysxObjectLog.Modified(this, (nint)MaterialPtr, nameof(FrictionCombineMode), $"{prev} -> {value}");
            }
        }
        public override PhysicsMaterialCombineMode RestitutionCombineMode
        {
            get => Conv(_materialPtr->GetRestitutionCombineMode());
            set
            {
                var prev = Conv(_materialPtr->GetRestitutionCombineMode());
                _materialPtr->SetRestitutionCombineModeMut(Conv(value));
                PhysxObjectLog.Modified(this, (nint)MaterialPtr, nameof(RestitutionCombineMode), $"{prev} -> {value}");
            }
        }
        public PxMaterialFlags MaterialFlags
        {
            get => _materialPtr->GetFlags();
            set => _materialPtr->SetFlagsMut(value);
        }
        public override bool DisableFriction
        {
            get => (MaterialFlags & PxMaterialFlags.DisableFriction) != 0;
            set
            {
                if (value)
                    MaterialFlags |= PxMaterialFlags.DisableFriction;
                else
                    MaterialFlags &= ~PxMaterialFlags.DisableFriction;
            }
        }
        public override bool DisableStrongFriction
        {
            get => (MaterialFlags & PxMaterialFlags.DisableStrongFriction) != 0;
            set
            {
                if (value)
                    MaterialFlags |= PxMaterialFlags.DisableStrongFriction;
                else
                    MaterialFlags &= ~PxMaterialFlags.DisableStrongFriction;
            }
        }
        public override bool ImprovedPatchFriction
        {
            get => (MaterialFlags & PxMaterialFlags.ImprovedPatchFriction) != 0;
            set
            {
                if (value)
                    MaterialFlags |= PxMaterialFlags.ImprovedPatchFriction;
                else
                    MaterialFlags &= ~PxMaterialFlags.ImprovedPatchFriction;
            }
        }
        public override bool CompliantContact
        {
            get => (MaterialFlags & PxMaterialFlags.CompliantContact) != 0;
            set
            {
                if (value)
                    MaterialFlags |= PxMaterialFlags.CompliantContact;
                else
                    MaterialFlags &= ~PxMaterialFlags.CompliantContact;
            }
        }

        private static PxCombineMode Conv(PhysicsMaterialCombineMode mode)
            => mode switch
            {
                PhysicsMaterialCombineMode.Average => PxCombineMode.Average,
                PhysicsMaterialCombineMode.Min => PxCombineMode.Min,
                PhysicsMaterialCombineMode.Multiply => PxCombineMode.Multiply,
                PhysicsMaterialCombineMode.Max => PxCombineMode.Max,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };

        private static PhysicsMaterialCombineMode Conv(PxCombineMode mode)
            => mode switch
            {
                PxCombineMode.Average => PhysicsMaterialCombineMode.Average,
                PxCombineMode.Min => PhysicsMaterialCombineMode.Min,
                PxCombineMode.Multiply => PhysicsMaterialCombineMode.Multiply,
                PxCombineMode.Max => PhysicsMaterialCombineMode.Max,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
    }
}
