using MagicPhysX;
using XREngine.Data.Geometry;
using XREngine.Scene;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe abstract class PhysxActor : PhysxBase, IAbstractPhysicsActor
    {
        public abstract PxActor* ActorPtr { get; }
        public override unsafe PxBase* BasePtr => (PxBase*)ActorPtr;

        private ushort _collisionGroup;
        private PxGroupsMask _groupsMask;

        public static Dictionary<nint, PhysxActor> AllActors { get; } = [];
        public static PhysxActor? Get(PxActor* ptr)
            => AllActors.TryGetValue((nint)ptr, out var actor) ? actor : null;

        ~PhysxActor() => Release();

        public bool DebugVisualize
        {
            get => ActorFlags.HasFlag(PxActorFlags.Visualization);
            set => SetActorFlag(PxActorFlag.Visualization, value);
        }
        public bool GravityEnabled
        {
            get => !ActorFlags.HasFlag(PxActorFlags.DisableGravity);
            set => SetActorFlag(PxActorFlag.DisableGravity, !value);
        }
        public bool SimulationEnabled
        {
            get => !ActorFlags.HasFlag(PxActorFlags.DisableSimulation);
            set => SetActorFlag(PxActorFlag.DisableSimulation, !value);
        }
        public bool SendSleepNotifies
        {
            get => ActorFlags.HasFlag(PxActorFlags.SendSleepNotifies);
            set => SetActorFlag(PxActorFlag.SendSleepNotifies, value);
        }

        public PxActorFlags ActorFlags
        {
            get => ActorPtr->GetActorFlags();
            set => ActorPtr->SetActorFlagsMut(value);
        }

        public void SetActorFlag(PxActorFlag flag, bool value)
            => ActorPtr->SetActorFlagMut(flag, value);

        public byte DominanceGroup
        {
            get => ActorPtr->GetDominanceGroup();
            set => ActorPtr->SetDominanceGroupMut(value);
        }

        public byte OwnerClient
        {
            get => ActorPtr->GetOwnerClient();
            set => ActorPtr->SetOwnerClientMut(value);
        }

        public PxAggregate* Aggregate => ActorPtr->GetAggregate();

        public ushort CollisionGroup
        {
            get => _collisionGroup;
            set
            {
                if (_collisionGroup == value)
                    return;
                _collisionGroup = value;
                ActorPtr->PhysPxSetGroup(value);
                OnCollisionFilteringChanged();
            }
        }

        public PxGroupsMask GroupsMask
        {
            get => _groupsMask;
            set
            {
                if (MasksEqual(_groupsMask, value))
                    return;
                _groupsMask = value;
                var mask = value;
                ActorPtr->PhysPxSetGroupsMask(&mask);
                OnCollisionFilteringChanged();
            }
        }

        private static bool MasksEqual(in PxGroupsMask a, in PxGroupsMask b)
            => a.bits0 == b.bits0 && a.bits1 == b.bits1 && a.bits2 == b.bits2 && a.bits3 == b.bits3;

        protected virtual void OnCollisionFilteringChanged() { }

        public AABB GetWorldBounds(float inflation)
        {
            PxBounds3 bounds = ActorPtr->GetWorldBounds(inflation);
            return new AABB(bounds.minimum, bounds.maximum);
        }

        public string Name
        {
            get
            {
                byte* name = ActorPtr->GetName();
                return new string((sbyte*)name);
            }
            set
            {
                fixed (byte* name = System.Text.Encoding.UTF8.GetBytes(value))
                    ActorPtr->SetNameMut(name);
            }
        }

        public PxScene* ScenePtr => ActorPtr->GetScene();
        public PhysxScene? Scene => PhysxScene.Scenes.TryGetValue((nint)ScenePtr, out PhysxScene? scene) ? scene : null;

        public PxActorType ActorType => NativeMethods.PxActor_getType(ActorPtr);

        private bool _isReleased = false;
        public bool IsReleased
        {
            get => _isReleased;
            protected set => SetField(ref _isReleased, value);
        }

        public virtual void Release()
        {
            if (IsReleased)
                return;
            IsReleased = true;
            ActorPtr->ReleaseMut();
        }

        public void Destroy(bool wakeOnLostTouch = false)
        {
            Scene?.RemoveActor(this, wakeOnLostTouch);
            Release();
        }

        public event Action<PhysxScene>? AddedToScene;
        public event Action<PhysxScene>? RemovedFromScene;

        public void OnAddedToScene(PhysxScene physxScene)
        {
            AddedToScene?.Invoke(physxScene);
            if (this is PhysxRigidActor rigid)
                rigid.RefreshShapeFilterData();
        }
        public void OnRemovedFromScene(PhysxScene physxScene)
            => RemovedFromScene?.Invoke(physxScene);
    }
}