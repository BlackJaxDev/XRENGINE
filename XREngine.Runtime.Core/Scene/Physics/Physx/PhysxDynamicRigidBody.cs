using XREngine.Scene.Physics.Physx;
using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe class PhysxDynamicRigidBody : PhysxRigidBody, IAbstractDynamicRigidBody
    {
        private readonly unsafe PxRigidDynamic* _obj;

        public static ConcurrentDictionary<nint, PhysxDynamicRigidBody> AllDynamic { get; } = new();
        public static PhysxDynamicRigidBody? Get(PxRigidDynamic* ptr)
            => AllDynamic.TryGetValue((nint)ptr, out var body) ? body : null;

        public PxRigidDynamic* DynamicPtr => _obj;
        public override PxRigidBody* BodyPtr => (PxRigidBody*)_obj;

        private Vector3 _cachedAngularVelocity;
        private Vector3 _cachedLinearVelocity;
        private bool _cachedIsSleeping = true;

        /// <summary>
        /// Returns the angular velocity captured after the latest completed simulation.
        /// </summary>
        public override Vector3 AngularVelocity => _cachedAngularVelocity;

        /// <summary>
        /// Returns the linear velocity captured after the latest completed simulation.
        /// </summary>
        public override Vector3 LinearVelocity => _cachedLinearVelocity;

        /// <summary>
        /// Returns the sleeping state captured after the latest completed simulation.
        /// </summary>
        public override bool IsSleeping => _cachedIsSleeping;

        /// <summary>
        /// Refreshes cached state from PhysX. Call only after <c>FetchResults</c>, while
        /// simulation is not running.
        /// </summary>
        public void RefreshCachedState()
        {
            _cachedAngularVelocity = _obj->GetAngularVelocity();
            _cachedLinearVelocity = _obj->GetLinearVelocity();
            _cachedIsSleeping = _obj->IsSleeping();
        }

        public void SetAngularVelocity(Vector3 value, bool wake = true)
        {
            _cachedAngularVelocity = value;
            if (RuntimePhysicsServices.Current.IsPhysicsThread)
            {
                SetAngularVelocityInternal(value, wake);
                return;
            }

            RuntimeThreadServices.Current.EnqueuePhysicsThread(
                () => SetAngularVelocityInternal(value, wake));
        }

        private void SetAngularVelocityInternal(Vector3 value, bool wake)
        {
            if (_obj is null || IsReleased)
                return;

            PxVec3 v = value;
            _obj->SetAngularVelocityMut(&v, wake);
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(SetAngularVelocity), $"value={value} wake={wake}");
        }

        public void SetLinearVelocity(Vector3 value, bool wake = true)
        {
            _cachedLinearVelocity = value;
            if (RuntimePhysicsServices.Current.IsPhysicsThread)
            {
                SetLinearVelocityInternal(value, wake);
                return;
            }

            RuntimeThreadServices.Current.EnqueuePhysicsThread(
                () => SetLinearVelocityInternal(value, wake));
        }

        private void SetLinearVelocityInternal(Vector3 value, bool wake)
        {
            if (_obj is null || IsReleased)
                return;

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
                if (RuntimePhysicsServices.Current.IsPhysicsThread)
                {
                    SetKinematicTargetInternal(value);
                    return;
                }

                RuntimeThreadServices.Current.EnqueuePhysicsThread(() => SetKinematicTargetInternal(value));
            }
        }

        private void SetKinematicTargetInternal((Vector3 position, Quaternion rotation)? value)
        {
            if (_obj is null || IsReleased)
                return;

            if (value.HasValue)
            {
                // Ensure body is kinematic.
                if (!Flags.HasFlag(PxRigidBodyFlags.Kinematic))
                    Flags |= PxRigidBodyFlags.Kinematic;

                var tfm = PhysxScene.MakeTransform(value.Value.position, value.Value.rotation);
                _obj->SetKinematicTargetMut(&tfm);
                //PhysxObjectLog.Modified(this, (nint)_obj, nameof(KinematicTarget), $"set pos={value.Value.position} rot={value.Value.rotation}");
            }
            else
            {
                // Clear kinematic target by making body non-kinematic.
                if (Flags.HasFlag(PxRigidBodyFlags.Kinematic))
                    Flags &= ~PxRigidBodyFlags.Kinematic;
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
            if (RuntimePhysicsServices.Current.IsPhysicsThread)
            {
                WakeUpInternal();
                return;
            }

            RuntimeThreadServices.Current.EnqueuePhysicsThread(WakeUpInternal);
        }
        private void WakeUpInternal()
        {
            if (_obj is null || IsReleased)
                return;

            // Snapshot restore can construct and reset bodies before the replacement
            // PxScene exists. PxRigidDynamic::wakeUp requires an attached actor, so let
            // PhysxScene.OnEnterPlayMode wake it after adding it to the live scene.
            if (ScenePtr is null)
            {
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(WakeUp), "ignored (detached)");
                return;
            }

            _obj->WakeUpMut();
            PhysxObjectLog.Modified(this, (nint)_obj, nameof(WakeUp));
        }

        public void PutToSleep()
        {
            if (RuntimePhysicsServices.Current.IsPhysicsThread)
            {
                PutToSleepInternal();
                return;
            }

            RuntimeThreadServices.Current.EnqueuePhysicsThread(PutToSleepInternal);
        }
        private void PutToSleepInternal()
        {
            if (_obj is null || IsReleased)
                return;

            if (ScenePtr is null)
            {
                PhysxObjectLog.Modified(this, (nint)_obj, nameof(PutToSleep), "ignored (detached)");
                return;
            }

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
            using var structObj = geometry.CreatePhysxGeometryData();
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

        internal override void RemoveFromCaches()
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

        private XRComponent? _owningComponent;
        public XRComponent? OwningComponent
        {
            get => _owningComponent;
            set => SetField(ref _owningComponent, value);
        }

        XRComponent? IAbstractDynamicRigidBody.OwningComponent
        {
            get => OwningComponent;
            set => OwningComponent = value;
        }

        public override XRComponent? GetOwningComponent()
            => OwningComponent;

    }
}
