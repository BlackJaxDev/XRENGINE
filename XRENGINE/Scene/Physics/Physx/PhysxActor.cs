using MagicPhysX;
using System.Collections.Concurrent;
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

        public static ConcurrentDictionary<nint, PhysxActor> AllActors { get; } = new();
        public static PhysxActor? Get(PxActor* ptr)
            => AllActors.TryGetValue((nint)ptr, out var actor) ? actor : null;

        ~PhysxActor()
        {
            try
            {
                if (ActorPtr is not null)
                    PhysxObjectLog.Modified(this, (nint)ActorPtr, "Finalizer", "invoked");
                else
                    Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ {0} Finalizer invoked (null ptr)", GetType().Name);
            }
            catch { }

            Release();
        }

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
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(ActorFlags), "ignored (released)");
                    return;
                }
                var prev = ActorPtr->GetActorFlags();
                ActorPtr->SetActorFlagsMut(value);
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(ActorFlags), $"{prev} -> {value}");
            }
        }

        public void SetActorFlag(PxActorFlag flag, bool value)
        {
            if (IsReleased)
            {
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(SetActorFlag), "ignored (released)");
                return;
            }
            ActorPtr->SetActorFlagMut(flag, value);
            PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(SetActorFlag), $"{flag}={value}");
        }

        public byte DominanceGroup
        {
            get => ActorPtr->GetDominanceGroup();
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(DominanceGroup), "ignored (released)");
                    return;
                }
                var prev = ActorPtr->GetDominanceGroup();
                ActorPtr->SetDominanceGroupMut(value);
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(DominanceGroup), $"{prev} -> {value}");
            }
        }

        public byte OwnerClient
        {
            get => ActorPtr->GetOwnerClient();
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(OwnerClient), "ignored (released)");
                    return;
                }
                var prev = ActorPtr->GetOwnerClient();
                ActorPtr->SetOwnerClientMut(value);
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(OwnerClient), $"{prev} -> {value}");
            }
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
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(CollisionGroup), $"-> {value}");
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
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(GroupsMask), $"-> {mask.bits0:X4}:{mask.bits1:X4}:{mask.bits2:X4}:{mask.bits3:X4}");
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
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(Name), "ignored (released)");
                    return;
                }
                fixed (byte* name = System.Text.Encoding.UTF8.GetBytes(value))
                    ActorPtr->SetNameMut(name);
                PhysxObjectLog.Modified(this, (nint)ActorPtr, nameof(Name), $"=\"{value}\"");
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

        protected virtual void RemoveFromCaches()
        {
            if (ActorPtr is null)
                return;
            PhysxObjectLog.RemoveIfSame(AllActors, nameof(AllActors), (nint)ActorPtr, this);
        }

        public virtual void Release()
        {
            if (IsReleased)
                return;
            IsReleased = true;
            RemoveFromCaches();
            PhysxObjectLog.Released(this, (nint)ActorPtr);
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
            PhysxObjectLog.Modified(this, (nint)ActorPtr, "OnAddedToScene", $"scene=0x{(nint)physxScene.ScenePtr:X}");
            AddedToScene?.Invoke(physxScene);
            if (this is PhysxRigidActor rigid)
                rigid.RefreshShapeFilterData();
        }
        public void OnRemovedFromScene(PhysxScene physxScene)
        {
            PhysxObjectLog.Modified(this, (nint)ActorPtr, "OnRemovedFromScene", $"scene=0x{(nint)physxScene.ScenePtr:X}");
            RemovedFromScene?.Invoke(physxScene);
        }
    }
}