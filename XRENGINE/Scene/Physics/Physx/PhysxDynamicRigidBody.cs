using MagicPhysX;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Components.Physics;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxDynamicRigidBody : PhysxRigidBody, IAbstractDynamicRigidBody
    {
        private readonly unsafe PxRigidDynamic* _obj;

        public static Dictionary<nint, PhysxDynamicRigidBody> AllDynamic { get; } = [];
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
        }
        public void SetLinearVelocity(Vector3 value, bool wake = true)
        {
            PxVec3 v = value;
            _obj->SetLinearVelocityMut(&v, wake);
        }

        public PxRigidDynamicLockFlags LockFlags
        {
            get => _obj->GetRigidDynamicLockFlags();
            set => _obj->SetRigidDynamicLockFlagsMut(value);
        }

        public void SetLockFlag(PxRigidDynamicLockFlag flag, bool value)
        {
            _obj->SetRigidDynamicLockFlagMut(flag, value);
        }

        public float StabilizationThreshold
        {
            get => _obj->GetStabilizationThreshold();
            set => _obj->SetStabilizationThresholdMut(value);
        }

        public float SleepThreshold
        {
            get => _obj->GetSleepThreshold();
            set => _obj->SetSleepThresholdMut(value);
        }

        public float ContactReportThreshold
        {
            get => _obj->GetContactReportThreshold();
            set => _obj->SetContactReportThresholdMut(value);
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
                }
                else
                    _obj->SetKinematicTargetMut(null);
            }
        }

        public float WakeCounter
        {
            get => _obj->GetWakeCounter();
            set => _obj->SetWakeCounterMut(value);
        }

        public void WakeUp() => _obj->WakeUpMut();
        public void PutToSleep() => _obj->PutToSleepMut();

        public (uint minPositionIters, uint minVelocityIters) SolverIterationCounts
        {
            get
            {
                uint minPositionIters, minVelocityIters;
                _obj->GetSolverIterationCounts(&minPositionIters, &minVelocityIters);
                return (minPositionIters, minVelocityIters);
            }
            set => _obj->SetSolverIterationCountsMut(value.minPositionIters, value.minVelocityIters);
        }

        public PhysxDynamicRigidBody()
            : this(null, null) { }

        internal PhysxDynamicRigidBody(PxRigidDynamic* obj)
        {
            _obj = obj;
            CachePtr();
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
        }

        public PhysxDynamicRigidBody(
            Vector3? position,
            Quaternion? rotation)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            _obj = PhysxScene.PhysicsPtr->CreateRigidDynamicMut(&tfm);
            CachePtr();
        }

        public override void Release()
        {
            if (IsReleased)
                return;
            AllActors.Remove((nint)_obj);
            AllRigidActors.Remove((nint)_obj);
            AllDynamic.Remove((nint)_obj);
            base.Release();

        }

        private void CachePtr()
        {
            AllActors.Add((nint)_obj, this);
            AllRigidActors.Add((nint)_obj, this);
            AllDynamic.Add((nint)_obj, this);
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
                                OwningComponent.RigidBody = null;
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
                            OwningComponent.RigidBody = this;
                    }
                    break;
            }
        }
    }
}