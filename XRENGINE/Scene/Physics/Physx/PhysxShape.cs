using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxShape : PhysxRefCounted, IAbstractPhysicsShape
    {
        private readonly unsafe PxShape* _shapePtr;

        public PhysxShape(PxShape* shape)
        {
            _shapePtr = shape;
            PhysxObjectLog.AddOrUpdate(All, nameof(All), (nint)shape, this);
            PhysxObjectLog.Created(this, (nint)shape, "from-existing");
        }
        public PhysxShape(IPhysicsGeometry geometry, PhysxMaterial material, PxShapeFlags flags, bool isExclusive = false)
        {
            using var geomStruct = geometry.GetPhysxStruct();
            PxShape* shape = PhysxScene.PhysicsPtr->CreateShapeMut(geomStruct.ToStructPtr<PxGeometry>(), material.MaterialPtr, isExclusive, flags);

            _shapePtr = shape;
            PhysxObjectLog.AddOrUpdate(All, nameof(All), (nint)shape, this);
            PhysxObjectLog.Created(this, (nint)shape, $"flags={flags} exclusive={isExclusive}");
        }

        public PxShape* ShapePtr => _shapePtr;

        public override unsafe PxBase* BasePtr => (PxBase*)_shapePtr;
        public override unsafe PxRefCounted* RefCountedPtr => (PxRefCounted*)_shapePtr;

        public static ConcurrentDictionary<nint, PhysxShape> All { get; } = new();
        public static PhysxShape? Get(PxShape* ptr)
            => All.TryGetValue((nint)ptr, out var shape) ? shape : null;

        /// <summary>
        /// If this shape is used for simulation.
        /// </summary>
        public bool SimulationShape
        {
            get => Flags.HasFlag(PxShapeFlags.SimulationShape);
            set => SetFlag(PxShapeFlag.SimulationShape, value);
        }
        /// <summary>
        /// If this shape is used for scene queries.
        /// </summary>
        public bool SceneQueryShape
        {
            get => Flags.HasFlag(PxShapeFlags.SceneQueryShape);
            set => SetFlag(PxShapeFlag.SceneQueryShape, value);
        }
        /// <summary>
        /// If this shape is a trigger.
        /// </summary>
        public bool TriggerShape
        {
            get => Flags.HasFlag(PxShapeFlags.TriggerShape);
            set => SetFlag(PxShapeFlag.TriggerShape, value);
        }
        /// <summary>
        /// If this shape is used for visualization.
        /// </summary>
        public bool Visualization
        {
            get => Flags.HasFlag(PxShapeFlags.Visualization);
            set => SetFlag(PxShapeFlag.Visualization, value);
        }

        public PxShapeFlags Flags
        {
            get => ShapePtr->GetFlags(); 
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(Flags), "ignored (released)");
                    return;
                }
                var prev = ShapePtr->GetFlags();
                ShapePtr->SetFlagsMut(value);
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(Flags), $"{prev} -> {value}");
            }
        }

        public void SetFlag(PxShapeFlag flag, bool value)
        {
            if (IsReleased)
            {
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(SetFlag), "ignored (released)");
                return;
            }
            ShapePtr->SetFlagMut(flag, value);
            PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(SetFlag), $"{flag}={value}");
        }

        public float ContactOffset
        {
            get => ShapePtr->GetContactOffset();
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(ContactOffset), "ignored (released)");
                    return;
                }
                var prev = ShapePtr->GetContactOffset();
                ShapePtr->SetContactOffsetMut(value);
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(ContactOffset), $"{prev} -> {value}");
            }
        }

        public override void Release()
        {
            if (IsReleased)
                return;

            _isReleased = true;

            // Remove from global + per-scene caches first so late lookups don't return a released wrapper.
            PhysxObjectLog.RemoveIfSame(All, nameof(All), (nint)_shapePtr, this);
            foreach (var scene in PhysxScene.Scenes.Values)
                scene.Shapes.TryRemove((nint)_shapePtr, out _);

            PhysxObjectLog.Released(this, (nint)_shapePtr, $"refCount={ReferenceCount}");
            ShapePtr->ReleaseMut();
        }

        public PxGeometry* Geometry
        {
            get => ShapePtr->GetGeometry();
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(Geometry), "ignored (released)");
                    return;
                }
                ShapePtr->SetGeometryMut(value);
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(Geometry));
            }
        }

        public unsafe PhysxRigidActor? GetActor()
            => PhysxRigidActor.Get(ShapePtr->GetActor());

        public (Vector3 position, Quaternion rotation) LocalPose
        {
            get
            {
                var tfm = ShapePtr->GetLocalPose();
                return (tfm.p, tfm.q);
            }
            set
            {
                var tfm = PhysxScene.MakeTransform(value.position, value.rotation);
                ShapePtr->SetLocalPoseMut(&tfm);
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(LocalPose), $"pos={value.position} rot={value.rotation}");
            }
        }

        public PxFilterData SimulationFilterData
        {
            get => ShapePtr->GetSimulationFilterData();
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(SimulationFilterData), "ignored (released)");
                    return;
                }
                ShapePtr->SetSimulationFilterDataMut(&value);
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(SimulationFilterData), $"word0=0x{value.word0:X8} word1=0x{value.word1:X8} word2=0x{value.word2:X8}");
            }
        }

        public PxFilterData QueryFilterData
        {
            get => ShapePtr->GetQueryFilterData();
            set
            {
                if (IsReleased)
                {
                    PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(QueryFilterData), "ignored (released)");
                    return;
                }
                ShapePtr->SetQueryFilterDataMut(&value);
                PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(QueryFilterData), $"word0=0x{value.word0:X8} word1=0x{value.word1:X8} word2=0x{value.word2:X8}");
            }
        }

        public PhysxMaterial[] Materials
        {
            get => GetMaterials();
            set => SetMaterials(value);
        }

        private unsafe void SetMaterials(PhysxMaterial[] materials)
        {
            PxMaterial** mats = stackalloc PxMaterial*[materials.Length];
            for (int i = 0; i < materials.Length; i++)
                mats[i] = materials[i].MaterialPtr;
            ShapePtr->SetMaterialsMut(mats, (ushort)materials.Length);
            PhysxObjectLog.Modified(this, (nint)_shapePtr, nameof(Materials), $"count={materials.Length}");
        }

        public unsafe ushort MaterialCount
            => ShapePtr->GetNbMaterials();

        private unsafe PhysxMaterial[] GetMaterials()
        {
            PxMaterial*[] materials = new PxMaterial*[MaterialCount];
            fixed (PxMaterial** materialsPtr = materials)
                ShapePtr->GetMaterials(materialsPtr, MaterialCount, 0u);
            PhysxMaterial[] mats = new PhysxMaterial[MaterialCount];
            for (int i = 0; i < MaterialCount; i++)
                mats[i] = PhysxMaterial.Get(materials[i])!;
            return mats;
        }

        public unsafe PxBaseMaterial* GetMaterialFromInternalFaceIndex(uint faceIndex)
            => ShapePtr->GetMaterialFromInternalFaceIndex(faceIndex);

        public float RestOffset
        {
            get => ShapePtr->GetRestOffset();
            set => ShapePtr->SetRestOffsetMut(value);
        }

        public float DensityForFluid
        {
            get => ShapePtr->GetDensityForFluid();
            set => ShapePtr->SetDensityForFluidMut(value);
        }

        public float TorsionalPatchRadius
        {
            get => ShapePtr->GetTorsionalPatchRadius();
            set => ShapePtr->SetTorsionalPatchRadiusMut(value);
        }

        public float MinTorsionalPatchRadius
        {
            get => ShapePtr->GetMinTorsionalPatchRadius();
            set => ShapePtr->SetMinTorsionalPatchRadiusMut(value);
        }

        public unsafe bool IsExclusive
            => ShapePtr->IsExclusive();

        public string Name
        {
            get => Marshal.PtrToStringAnsi((IntPtr)ShapePtr->GetName()) ?? string.Empty;
            set => ShapePtr->SetNameMut((byte*)Marshal.StringToHGlobalAnsi(value).ToPointer());
        }

        public unsafe PxQueryCache QueryCacheNew1(uint findex)
            => ShapePtr->QueryCacheNew1(findex);

        public unsafe (Vector3 position, Quaternion rotation) GetGlobalPose(PhysxRigidActor actor)
        {
            var tfm = ShapePtr->ExtGetGlobalPose(actor.RigidActorPtr);
            return (tfm.p, tfm.q);
        }

        public unsafe PxRaycastHit[] Raycast(PhysxRigidActor actor, Vector3 rayOrigin, Vector3 rayDir, float maxDist, PxHitFlags hitFlags, uint maxHits = 32)
        {
            PxVec3 ro = rayOrigin;
            PxVec3 rd = rayDir;
            PxRaycastHit[] rayHits = new PxRaycastHit[maxHits];
            fixed (PxRaycastHit* rayHitsPtr = rayHits)
            {
                uint num = ShapePtr->ExtRaycast(actor.RigidActorPtr, &ro, &rd, maxDist, hitFlags, maxHits, rayHitsPtr);
                PxRaycastHit[] hits = new PxRaycastHit[num];
                Array.Copy(rayHits, hits, num);
                return hits;
            }
        }

        public unsafe bool Overlap(PhysxRigidActor actor, IPhysicsGeometry otherGeom, (Vector3 position, Quaternion rotation) otherGeomPose)
        {
            var tfm = PhysxScene.MakeTransform(otherGeomPose.position, otherGeomPose.rotation);
            var structObj = otherGeom.GetPhysxStruct();
            return ShapePtr->ExtOverlap(actor.RigidActorPtr, structObj.Address.As<PxGeometry>(), &tfm);
        }

        public unsafe bool Sweep(PhysxRigidActor actor, Vector3 unitDir, float distance, IPhysicsGeometry otherGeom, (Vector3 position, Quaternion rotation) otherGeomPose, out PxSweepHit sweepHit, PxHitFlags hitFlags)
        {
            var tfm = PhysxScene.MakeTransform(otherGeomPose.position, otherGeomPose.rotation);
            PxSweepHit h;
            PxVec3 ud = unitDir;
            var structObj = otherGeom.GetPhysxStruct();
            bool result = ShapePtr->ExtSweep(actor.RigidActorPtr, &ud, distance, structObj.Address.As<PxGeometry>(), &tfm, &h, hitFlags);
            sweepHit = h;
            return result;
        }

        public unsafe AABB GetWorldBounds(PhysxRigidActor actor, float inflation)
        {
            var bounds = ShapePtr->ExtGetWorldBounds(actor.RigidActorPtr, inflation);
            return new AABB(bounds.minimum, bounds.maximum);
        }
    }
}