using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static MagicPhysX.NativeMethods;
using XREngine;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe abstract class PhysxRigidActor : PhysxActor, IAbstractRigidPhysicsActor
    {
        public abstract PxRigidActor* RigidActorPtr { get; }
        public override unsafe PxActor* ActorPtr => (PxActor*)RigidActorPtr;

        public abstract XRComponent? GetOwningComponent();

        public static ConcurrentDictionary<nint, PhysxRigidActor> AllRigidActors { get; } = new();
        public static PhysxRigidActor? Get(PxRigidActor* ptr)
            => AllRigidActors.TryGetValue((nint)ptr, out var actor) ? actor : null;

        public uint InternalActorIndex => RigidActorPtr->GetInternalActorIndex();

        //public void ApplyTransformTo(RigidBodyTransform transform)
        //{
        //    GetTransform(out var position, out var rotation);
        //    transform.Position = position;
        //    transform.Rotation = rotation;
        //}

        public void ApplyTransformTo(TransformBase transform)
        {
            GetTransform(out var position, out var rotation);
            transform.DeriveWorldMatrix(Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
        }

        public (Vector3 position, Quaternion rotation) WorldToLocal(Vector3 worldPosition, Quaternion worldRotation)
        {
            GetTransform(out var position, out var rotation);
            var invRotation = Quaternion.Inverse(rotation);
            var localPosition = Vector3.Transform(worldPosition - position, invRotation);
            var localRotation = Quaternion.Multiply(invRotation, worldRotation);
            return (localPosition, localRotation);
        }

        public (Vector3 position, Quaternion rotation) LocalToWorld(Vector3 localPosition, Quaternion localRotation)
        {
            GetTransform(out var position, out var rotation);
            var worldPosition = Vector3.Transform(localPosition, rotation) + position;
            var worldRotation = Quaternion.Multiply(rotation, localRotation);
            return (worldPosition, worldRotation);
        }

        public (Vector3 position, Quaternion rotation) Transform
        {
            get
            {
                GetTransform(out var position, out var rotation);
                return (position, rotation);
            }
            set => SetTransform(new() { p = value.position, q = value.rotation }, true);
        }

        private void GetTransform(out Vector3 position, out Quaternion rotation)
        {
            var pose = PxRigidActor_getGlobalPose(RigidActorPtr);
            position = new Vector3(pose.p.x, pose.p.y, pose.p.z);
            rotation = new Quaternion(pose.q.x, pose.q.y, pose.q.z, pose.q.w);
        }

        private void SetTransform(PxTransform pose, bool wake = true)
        {
            if (IsReleased)
            {
                PhysxObjectLog.Modified(this, (nint)RigidActorPtr, "SetTransform", "ignored (released)");
                return;
            }
            RigidActorPtr->SetGlobalPoseMut(&pose, wake);
            PhysxObjectLog.Modified(this, (nint)RigidActorPtr, "SetTransform", $"wake={wake}");
        }

        public void SetTransform(Vector3 position, Quaternion rotation, bool wake = true)
            => SetTransform(new() { p = position, q = rotation }, wake);

        public uint ConstraintCount
            => RigidActorPtr->GetNbConstraints();

        public uint ShapeCount
            => RigidActorPtr->GetNbShapes();

        public abstract Vector3 LinearVelocity { get; }
        public abstract Vector3 AngularVelocity { get; }
        public abstract bool IsSleeping { get; }

        public PxConstraint*[] GetConstraints()
        {
            var constraints = new PxConstraint*[ConstraintCount];
            fixed (PxConstraint** constraintsPtr = constraints)
                RigidActorPtr->GetConstraints(constraintsPtr, ConstraintCount, 0);
            return constraints;
        }

        public PhysxShape[] GetShapes()
        {
            if (Scene is null)
                return [];
            var shapes = new PxShape*[ShapeCount];
            fixed (PxShape** shapesPtr = shapes)
                RigidActorPtr->GetShapes(shapesPtr, ShapeCount, 0);
            var shapes2 = new PhysxShape[ShapeCount];
            for (int i = 0; i < ShapeCount; i++)
                shapes2[i] = Scene.GetShape(shapes[i])!;
            return shapes2;
        }

        public void AttachShape(PhysxShape shape)
        {
            if (IsReleased)
            {
                PhysxObjectLog.Modified(this, (nint)RigidActorPtr, nameof(AttachShape), "ignored (released)");
                return;
            }
            RigidActorPtr->AttachShapeMut(shape.ShapePtr);
            PhysxObjectLog.Modified(this, (nint)RigidActorPtr, nameof(AttachShape), $"shape=0x{(nint)shape.ShapePtr:X}");
            RefreshShapeFilterData();
        }

        public void DetachShape(PhysxShape shape, bool wakeOnLostTouch)
        {
            if (IsReleased)
            {
                PhysxObjectLog.Modified(this, (nint)RigidActorPtr, nameof(DetachShape), "ignored (released)");
                return;
            }
            RigidActorPtr->DetachShapeMut(shape.ShapePtr, wakeOnLostTouch);
            PhysxObjectLog.Modified(this, (nint)RigidActorPtr, nameof(DetachShape), $"shape=0x{(nint)shape.ShapePtr:X} wakeOnLostTouch={wakeOnLostTouch}");
        }

        protected override void OnCollisionFilteringChanged()
        {
            base.OnCollisionFilteringChanged();
            RefreshShapeFilterData();
        }

        public void RefreshShapeFilterData(bool includeQueryData = true)
        {
            int shapeCount = (int)ShapeCount;
            if (shapeCount == 0)
            {
                Debug.Physics("[PhysxRigidActor] No shapes to refresh for actorType={0} group={1}", GetType().Name, CollisionGroup);
                return;
            }

            PxFilterData filterData = BuildFilterData();
            PxShape** shapes = stackalloc PxShape*[shapeCount];
            RigidActorPtr->GetShapes(shapes, (uint)shapeCount, 0);
            for (int i = 0; i < shapeCount; i++)
            {
                var shapePtr = shapes[i];
                if (shapePtr is null)
                    continue;
                Scene?.GetShape(shapePtr);
                shapePtr->SetSimulationFilterDataMut(&filterData);
                if (includeQueryData)
                    shapePtr->SetQueryFilterDataMut(&filterData);

                var flags = shapePtr->GetFlags();
                Debug.Physics(
                    "[PhysxRigidActor] Shape flags actorType={0} shapeIndex={1} sim={2} query={3} trigger={4} visualization={5}",
                    GetType().Name,
                    i,
                    flags.HasFlag(PxShapeFlags.SimulationShape),
                    flags.HasFlag(PxShapeFlags.SceneQueryShape),
                    flags.HasFlag(PxShapeFlags.TriggerShape),
                    flags.HasFlag(PxShapeFlags.Visualization));
            }

            Scene?.ResetFiltering(this);
            Debug.Physics(
                "[PhysxRigidActor] Refreshed filter data actorType={0} shapes={1} word0=0x{2:X8} word1=0x{3:X8} word2=0x{4:X8} group={5} mask={6}",
                GetType().Name,
                shapeCount,
                filterData.word0,
                filterData.word1,
                filterData.word2,
                CollisionGroup,
                FormatGroupsMask(GroupsMask));
        }

        private PxFilterData BuildFilterData()
        {
            PxFilterData data = default;
            data.word0 = BuildGroupBits(CollisionGroup);
            var mask = GroupsMask;
            data.word1 = CombineMaskBits(mask.bits0, mask.bits1);
            data.word2 = CombineMaskBits(mask.bits2, mask.bits3);
            if (data.word1 == 0 && data.word2 == 0)
                data.word1 = uint.MaxValue;
            data.word3 = 0;
            return data;
        }

        private static uint BuildGroupBits(ushort group)
            => group < 32 ? 1u << group : group;

        private static uint CombineMaskBits(ushort low, ushort high)
            => (uint)low | ((uint)high << 16);

        private static string FormatGroupsMask(PxGroupsMask mask)
            => $"{mask.bits0:X4}:{mask.bits1:X4}:{mask.bits2:X4}:{mask.bits3:X4}";

        protected override void RemoveFromCaches()
        {
            if (RigidActorPtr is not null)
                PhysxObjectLog.RemoveIfSame(AllRigidActors, nameof(AllRigidActors), (nint)RigidActorPtr, this);
            base.RemoveFromCaches();
        }

        public override void Release()
            => base.Release();

        public PxQueryFilterCallback* CreateRaycastFilterCallback()
            => RigidActorPtr->CreateRaycastFilterCallback();
    }
}