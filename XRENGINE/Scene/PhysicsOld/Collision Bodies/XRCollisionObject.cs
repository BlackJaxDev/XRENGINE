using MemoryPack;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine.Physics
{
    public delegate void DelCollision(XRCollisionObject @this, XRCollisionObject other, XRContactInfo info, bool thisIsA);
    public delegate void DelOnHit(XRRigidBody me, XRRigidBody other, XRContactInfo collisionPoint);

    [MemoryPackable(GenerateType.NoGenerate)]
    public abstract partial class XRCollisionObject : XRAsset, IDisposable, IAbstractPhysicsActor
    {
        public event DelMatrixUpdate? TransformChanged;
        protected internal void OnTransformChanged(Matrix4x4 worldTransform)
            => TransformChanged?.Invoke(worldTransform);
        
        public event DelCollision? Collided, Overlapped;
        protected internal void OnCollided(XRCollisionObject other, XRContactInfo info, bool thisIsA)
            => Collided?.Invoke(this, other, info, thisIsA);
        protected internal void OnOverlapped(XRCollisionObject other, XRContactInfo info, bool thisIsA)
            => Overlapped?.Invoke(this, other, info, thisIsA);
        
        protected XRCollisionObject() { }

        public ICollidable? Owner { get; internal set; }

        //[PhysicsSupport(PhysicsLibrary.Bullet)]
        //public abstract int UniqueID { get; }
        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract int IslandTag { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract bool IsActive { get; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Matrix4x4 WorldTransform { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Matrix4x4 InterpolationWorldTransform { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Vector3 InterpolationLinearVelocity { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Vector3 InterpolationAngularVelocity { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float HitFraction { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float Friction { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float DeactivationTime { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float ContactProcessingThreshold { get; set; }
        
        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public virtual XRCollisionShape? CollisionShape { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract bool HasContactResponse { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract bool IsStatic { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract bool IsKinematic { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract bool CustomMaterialCallback { get; set; }
        
        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float CcdSweptSphereRadius { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float CcdSquareMotionThreshold { get; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float CcdMotionThreshold { get; set; }
        
        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Vector3 AnisotropicFriction { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract EBodyActivationState ActivationState { get; set; }
        
        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract bool MergesSimulationIslands { get; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float RollingFriction { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract float Restitution { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public virtual ushort CollidesWith { get; set; }
        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public virtual ushort CollisionGroup { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Vector3 AabbMin { get; set; }

        [PhysicsSupport(EPhysicsLibrary.Bullet)]
        public abstract Vector3 AabbMax { get; set; }

        protected ushort _previousCollidesWith = 0xFFFF;
        private bool _collisionEnabled;
        public bool CollisionEnabled
        {
            get => _collisionEnabled;
            set
            {
                HasContactResponse = _collisionEnabled = value;

                //if (_collisionEnabled)
                //    CollidesWith = _previousCollidesWith;
                //else
                //{
                //    _previousCollidesWith = CollidesWith;
                //    CollidesWith = 0;
                //}
            }
        }

        private bool _sleepingEnabled;
        public bool SleepingEnabled
        {
            get => _sleepingEnabled;
            set
            {
                _sleepingEnabled = value;
                if (_sleepingEnabled)
                {
                    if (ActivationState == EBodyActivationState.DisableSleep)
                        ActivationState = EBodyActivationState.WantsSleep;
                }
                else
                {
                    if (ActivationState != EBodyActivationState.DisableSimulation)
                        ActivationState = EBodyActivationState.DisableSleep;
                }
            }
        }

        private bool _simulatingPhysics = false;
        public bool SimulatingPhysics
        {
            get => _simulatingPhysics;
            set
            {
                _simulatingPhysics = value;
                if (!_simulatingPhysics)
                    StopSimulation();
                else
                    StartSimulation();
            }
        }

        protected virtual void StopSimulation()
        {
            IsStatic = true;
            ActivationState = EBodyActivationState.DisableSimulation;
        }
        protected virtual void StartSimulation()
        {
            IsStatic = false;
            WorldTransform = Owner?.CollidableWorldMatrix ?? Matrix4x4.Identity;

            if (_sleepingEnabled)
            {
                if (ActivationState == EBodyActivationState.DisableSleep)
                    ActivationState = EBodyActivationState.Active;
            }
            else
            {
                if (ActivationState != EBodyActivationState.DisableSimulation)
                    ActivationState = EBodyActivationState.DisableSleep;
            }
        }

        public abstract void Activate();
        public abstract void Activate(bool forceActivation);
        public abstract bool CheckCollideWith(XRCollisionObject collisionObject);
        public abstract void ForceActivationState(EBodyActivationState newState);
        public abstract void GetWorldTransform(out Matrix4x4 transform);

        [Flags]
        public enum EAnisotropicFrictionFlags
        {
            Disabled    = 0b00,
            Linear      = 0b01,
            Rolling     = 0b10,
        }

        public abstract bool HasAnisotropicFriction(EAnisotropicFrictionFlags frictionMode);
        public abstract bool HasAnisotropicFriction();
        public abstract void SetAnisotropicFriction(Vector3 anisotropicFriction);
        public abstract void SetAnisotropicFriction(Vector3 anisotropicFriction, EAnisotropicFrictionFlags frictionMode);
        public abstract void SetIgnoreCollisionCheck(XRCollisionObject collisionObject, bool ignoreCollisionCheck);

        public virtual void Dispose()
        {
            CollisionShape?.Dispose();
        }

        public void Destroy(bool wakeOnLostTouch = false)
        {

        }
    }
}
