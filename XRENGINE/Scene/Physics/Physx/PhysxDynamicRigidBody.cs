using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Components.Physics;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxDynamicRigidBody : PhysxRigidBody, IAbstractDynamicRigidBody
    {
        private readonly unsafe PxRigidDynamic* _obj;

        public static ConcurrentDictionary<nint, PhysxDynamicRigidBody> AllDynamic { get; } = new();
        public static PhysxDynamicRigidBody? Get(PxRigidDynamic* ptr)
            => AllDynamic.TryGetValue((nint)ptr, out var body) ? body : null;

        public PxRigidDynamic* DynamicPtr => _obj;
        public override PxRigidBody* BodyPtr => (PxRigidBody*)_obj;

        public override Vector3 AngularVelocity => _obj->GetAngularVelocity();
        public override Vector3 LinearVelocity => _obj->GetLinearVelocity();
        public override bool IsSleeping => _obj->IsSleeping();

        public void SetAngularVelocity(Vector3 value, bool wake = true)
        {
            PxVec3 v = value;
            _obj->SetAngularVelocityMut(&v, wake);
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(SetAngularVelocity), $"value={value} wake={wake}");
        }
        public void SetLinearVelocity(Vector3 value, bool wake = true)
        {
            PxVec3 v = value;
            _obj->SetLinearVelocityMut(&v, wake);
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(SetLinearVelocity), $"value={value} wake={wake}");
        }

        public PxRigidDynamicLockFlags LockFlags
        {
            get => _obj->GetRigidDynamicLockFlags();
            set
            {
                var prev = _obj->GetRigidDynamicLockFlags();
                _obj->SetRigidDynamicLockFlagsMut(value);
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(LockFlags), $"{prev} -> {value}");
            }
        }

        public void SetLockFlag(PxRigidDynamicLockFlag flag, bool value)
        {
            _obj->SetRigidDynamicLockFlagMut(flag, value);
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(SetLockFlag), $"{flag}={value}");
        }

        public float StabilizationThreshold
        {
            get => _obj->GetStabilizationThreshold();
            set
            {
                var prev = _obj->GetStabilizationThreshold();
                _obj->SetStabilizationThresholdMut(value);
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(StabilizationThreshold), $"{prev} -> {value}");
            }
        }

        public float SleepThreshold
        {
            get => _obj->GetSleepThreshold();
            set
            {
                var prev = _obj->GetSleepThreshold();
                _obj->SetSleepThresholdMut(value);
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(SleepThreshold), $"{prev} -> {value}");
            }
        }

        public float ContactReportThreshold
        {
            get => _obj->GetContactReportThreshold();
            set
            {
                var prev = _obj->GetContactReportThreshold();
                _obj->SetContactReportThresholdMut(value);
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(ContactReportThreshold), $"{prev} -> {value}");
            }
        }

        public (Vector3 position, Quaternion rotation)? KinematicTarget
        {
            get
            {
                PxTransform tfm;
                bool hasTarget = _obj->GetKinematicTarget(&tfm);
                return hasTarget ? (tfm.p, tfm.q) : null;
            }
            set
            {
                if (value.HasValue)
                {
                    var tfm = PhysxScene.MakeTransform(value.Value.position, value.Value.rotation);
                    _obj->SetKinematicTargetMut(&tfm);
                    PhysxObjectLog.Modified(this, (nint)_obj, nameof(KinematicTarget), $"set pos={value.Value.position} rot={value.Value.rotation}");
                }
                else
                {
                    _obj->SetKinematicTargetMut(null);
                    PhysxObjectLog.Modified(this, (nint)_obj, nameof(KinematicTarget), "cleared");
                }
            }
        }

        public float WakeCounter
        {
            get => _obj->GetWakeCounter();
            set
            {
                var prev = _obj->GetWakeCounter();
                _obj->SetWakeCounterMut(value);
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(WakeCounter), $"{prev} -> {value}");
            }
        }

        public void WakeUp()
        {
            _obj->WakeUpMut();
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(WakeUp));
        }
        public void PutToSleep()
        {
            _obj->PutToSleepMut();
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(PutToSleep));
        }

        public (uint minPositionIters, uint minVelocityIters) SolverIterationCounts
        {
            get
            {
                uint minPositionIters, minVelocityIters;
                _obj->GetSolverIterationCounts(&minPositionIters, &minVelocityIters);
                return (minPositionIters, minVelocityIters);
            }
            set
            {
                _obj->SetSolverIterationCountsMut(value.minPositionIters, value.minVelocityIters);
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(SolverIterationCounts), $"pos={value.minPositionIters} vel={value.minVelocityIters}");
            }
        }

        public PhysxDynamicRigidBody()
            : this(null, null) { }

        internal PhysxDynamicRigidBody(PxRigidDynamic* obj)
        {
            _obj = obj;
            CachePtr();
            PhysxObjectLog.Created(this, (nint)_obj, "from-existing");
        }

        public PhysxDynamicRigidBody(
            PhysxMaterial material,
            IPhysicsGeometry geometry,
            float density,
            Vector3? position = null,
            Quaternion? rotation = null,
            Vector3? shapeOffsetTranslation = null,
            Quaternion? shapeOffsetRotation = null)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            var shapeTfm = PhysxScene.MakeTransform(shapeOffsetTranslation, shapeOffsetRotation);
            using var structObj = geometry.GetPhysxStruct();
            _obj = PhysxScene.PhysicsPtr->PhysPxCreateDynamic(&tfm, structObj.ToStructPtr<PxGeometry>(), material.MaterialPtr, density, &shapeTfm);
            CachePtr();
            PhysxObjectLog.Created(this, (nint)_obj, $"density={density}");
        }

        public PhysxDynamicRigidBody(
            PhysxShape shape,
            float density,
            Vector3? position = null,
            Quaternion? rotation = null)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            _obj = PhysxScene.PhysicsPtr->PhysPxCreateDynamic1(&tfm, shape.ShapePtr, density);
            CachePtr();
            PhysxObjectLog.Created(this, (nint)_obj, $"shape=0x{(nint)shape.ShapePtr:X} density={density}");
        }

        public PhysxDynamicRigidBody(
            Vector3? position,
            Quaternion? rotation)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            _obj = PhysxScene.PhysicsPtr->CreateRigidDynamicMut(&tfm);
            CachePtr();
            PhysxObjectLog.Created(this, (nint)_obj, "empty");
        }

        protected override void RemoveFromCaches()
        {
            PhysxObjectLog.RemoveIfSame(AllDynamic, nameof(AllDynamic), (nint)_obj, this);
            base.RemoveFromCaches();
        }

        private void CachePtr()
        {
            PhysxObjectLog.AddOrUpdate(AllActors, nameof(AllActors), (nint)_obj, this);
            PhysxObjectLog.AddOrUpdate(AllRigidActors, nameof(AllRigidActors), (nint)_obj, this);
            PhysxObjectLog.AddOrUpdate(AllDynamic, nameof(AllDynamic), (nint)_obj, this);
        }

        private DynamicRigidBodyComponent? _owningComponent;
        public DynamicRigidBodyComponent? OwningComponent
        {
            get => _owningComponent;
            set => SetField(ref _owningComponent, value);
        }

        public override XRComponent? GetOwningComponent()
            => OwningComponent;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(OwningComponent):
                        if (OwningComponent is not null)
                        {
                            if (OwningComponent.RigidBody == this)
                                OwningComponent.SetRigidBodyFromRigidBodyOwner(null);
                        }
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(OwningComponent):
                    if (OwningComponent is not null)
                    {
                        if (OwningComponent.RigidBody != this)
                            OwningComponent.SetRigidBodyFromRigidBodyOwner(this);
                    }
                    break;
            }
        }
    }
}