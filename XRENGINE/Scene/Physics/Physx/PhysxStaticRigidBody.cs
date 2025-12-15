using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Components.Physics;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxStaticRigidBody : PhysxRigidActor, IAbstractStaticRigidBody
    {
        private readonly unsafe PxRigidStatic* _obj;

        public static ConcurrentDictionary<nint, PhysxStaticRigidBody> AllStaticRigidBodies { get; } = new();
        public static PhysxStaticRigidBody? GetStaticBody(PxRigidStatic* ptr)
            => AllStaticRigidBodies.TryGetValue((nint)ptr, out var body) ? body : null;

        public PhysxStaticRigidBody()
            : this(null, null) { }

        internal PhysxStaticRigidBody(PxRigidStatic* obj)
        {
            _obj = obj;
            CachePtr("from-existing");
        }

        public PhysxStaticRigidBody(
            Vector3? position,
            Quaternion? rotation)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            _obj = PhysxScene.PhysicsPtr->CreateRigidStaticMut(&tfm);
            CachePtr("empty");
        }
        public PhysxStaticRigidBody(
            PhysxShape shape,
            Vector3? position = null,
            Quaternion? rotation = null)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            _obj = PhysxScene.PhysicsPtr->PhysPxCreateStatic1(&tfm, shape.ShapePtr);
            CachePtr($"shape=0x{(nint)shape.ShapePtr:X}");
        }
        public PhysxStaticRigidBody(
            PhysxMaterial material,
            IPhysicsGeometry geometry,
            Vector3? position = null,
            Quaternion? rotation = null,
            Vector3? shapeOffsetTranslation = null,
            Quaternion? shapeOffsetRotation = null)
        {
            var tfm = PhysxScene.MakeTransform(position, rotation);
            var shapeTfm = PhysxScene.MakeTransform(shapeOffsetTranslation, shapeOffsetRotation);
            using var structObj = geometry.GetPhysxStruct();
            _obj = PhysxScene.PhysicsPtr->PhysPxCreateStatic(&tfm, structObj.ToStructPtr<PxGeometry>(), material.MaterialPtr, &shapeTfm);
            CachePtr("from-geometry");
        }

        private void CachePtr(string detail)
        {
            PhysxObjectLog.AddOrUpdate(AllActors, nameof(AllActors), (nint)_obj, this);
            PhysxObjectLog.AddOrUpdate(AllRigidActors, nameof(AllRigidActors), (nint)_obj, this);
            PhysxObjectLog.AddOrUpdate(AllStaticRigidBodies, nameof(AllStaticRigidBodies), (nint)_obj, this);
            PhysxObjectLog.Created(this, (nint)_obj, detail);
        }

        protected override void RemoveFromCaches()
        {
            PhysxObjectLog.RemoveIfSame(AllStaticRigidBodies, nameof(AllStaticRigidBodies), (nint)_obj, this);
            base.RemoveFromCaches();
        }

        public static PhysxStaticRigidBody CreatePlane(PxPlane plane, PhysxMaterial material)
        {
            var stat = PhysxScene.PhysicsPtr->PhysPxCreatePlane(&plane, material.MaterialPtr);
            return new PhysxStaticRigidBody(stat);
        }
        public static PhysxStaticRigidBody CreatePlane(Vector3 normal, float distance, PhysxMaterial material)
            => CreatePlane(PxPlane_new_1(normal.X, normal.Y, normal.Z, distance), material);
        public static PhysxStaticRigidBody CreatePlane(PhysxPlane plane, PhysxMaterial material)
            => CreatePlane(plane.InternalPlane, material);

        public override unsafe PxRigidActor* RigidActorPtr => (PxRigidActor*)_obj;
        public override unsafe PxActor* ActorPtr => (PxActor*)_obj;
        public override unsafe PxBase* BasePtr => (PxBase*)_obj;

        public override Vector3 LinearVelocity { get; } = Vector3.Zero;
        public override Vector3 AngularVelocity { get; } = Vector3.Zero;
        public override bool IsSleeping => true;

        private StaticRigidBodyComponent? _owningComponent;
        public StaticRigidBodyComponent? OwningComponent
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