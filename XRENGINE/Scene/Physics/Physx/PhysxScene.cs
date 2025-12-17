using Extensions;
using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx.Joints;
using XREngine.Scene;
using static MagicPhysX.NativeMethods;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe partial class PhysxScene : AbstractPhysicsScene
    {
        private static PxFoundation* _foundationPtr;
        public static PxFoundation* FoundationPtr => _foundationPtr;

        private static PxPhysics* _physicsPtr;
        public static PxPhysics* PhysicsPtr => _physicsPtr;

        public static ConcurrentDictionary<nint, PhysxScene> Scenes { get; } = new();

        static PhysxScene()
        {
            Init();
        }
        public PhysxScene()
        {
            _visualizer = new(GetPoint, GetLine, GetTriangle);
        }

        public static void Init()
        {
            _foundationPtr = physx_create_foundation();
            _physicsPtr = physx_create_physics(_foundationPtr);
            PhysxConvexHullCooker.Initialize(_physicsPtr);
        }
        public static void Release()
        {
            _physicsPtr->ReleaseMut();
        }

        public static readonly PxVec3 DefaultGravity = new() { x = 0.0f, y = -9.81f, z = 0.0f };

        private PxCpuDispatcher* _dispatcher;
        private PxScene* _scene;

        private readonly InstancedDebugVisualizer _visualizer;

        private PxSimulationEventCallback* _simulationEventCallbackPtr;
        private GCHandle _simulationEventCallbackHandle;
        private bool _simulationEventCallbackHandleAllocated;

        //public PxPhysics* PhysicsPtr => _scene->GetPhysicsMut();

        public PxScene* ScenePtr => _scene;
        public PxCpuDispatcher* DispatcherPtr => _dispatcher;

        public override Vector3 Gravity
        {
            get
            {
                if (_scene is null)
                    return Vector3.Zero;

                return _scene->GetGravity();
            }
            set
            {
                if (_scene is null)
                    return;

                PxVec3 g = value;
                _scene->SetGravityMut(&g);
            }
        }

        public override void Destroy()
        {
            UnlinkVisualizationSettings();
            ReleaseSimulationEventCallbacks();

            if (_scene is not null)
            {
                Debug.Log(ELogCategory.Physics, "[PhysxScene] Clearing caches shapes={0} joints={1}", Shapes.Count, Joints.Count);
                Shapes.Clear();
                Joints.Clear();
                ContactJoints.Clear();
                DistanceJoints.Clear();
                D6Joints.Clear();
                FixedJoints.Clear();
                PrismaticJoints.Clear();
                RevoluteJoints.Clear();
                SphericalJoints.Clear();

                Scenes.TryRemove(new KeyValuePair<nint, PhysxScene>((nint)_scene, this));
                PhysxObjectLog.Released(this, (nint)_scene, "scene-destroy");
                _scene->ReleaseMut();
                _scene = null;
            }

            if (_dispatcher is not null)
            {
                ((PxDefaultCpuDispatcher*)_dispatcher)->ReleaseMut();
                _dispatcher = null;
            }
        }

        private unsafe void ConfigureSimulationEventCallbacks(ref PxSceneDesc sceneDesc)
        {
            ReleaseSimulationEventCallbacks();

            _simulationEventCallbackHandle = GCHandle.Alloc(this);
            _simulationEventCallbackHandleAllocated = true;

            SimulationEventCallbackInfo callbackInfo = default;
            void* handlePtr = (void*)GCHandle.ToIntPtr(_simulationEventCallbackHandle);

            callbackInfo.collision_callback = &OnContact;
            callbackInfo.collision_user_data = handlePtr;

            callbackInfo.trigger_callback = &OnTrigger;
            callbackInfo.trigger_user_data = handlePtr;

            callbackInfo.constraint_break_callback = &OnConstraintBreak;
            callbackInfo.constraint_break_user_data = handlePtr;

            callbackInfo.wake_sleep_callback = &OnWakeSleep;
            callbackInfo.wake_sleep_user_data = handlePtr;

            callbackInfo.advance_callback = &OnAdvance;
            callbackInfo.advance_user_data = handlePtr;

            _simulationEventCallbackPtr = create_simulation_event_callbacks(&callbackInfo);
            if (_simulationEventCallbackPtr is null)
            {
                Debug.Physics("[PhysxScene] Failed to create simulation event callbacks");
                ReleaseSimulationEventCallbacks();
                return;
            }

            sceneDesc.simulationEventCallback = _simulationEventCallbackPtr;
            //Debug.Physics("[PhysxScene] Simulation event callbacks configured");
        }

        private static bool TryGetSceneFromUserData(void* userData, out PhysxScene? scene)
        {
            scene = null;
            if (userData is null)
                return false;

            try
            {
                var handle = GCHandle.FromIntPtr((nint)userData);
                if (!handle.IsAllocated)
                    return false;
                scene = handle.Target as PhysxScene;
                return scene is not null;
            }
            catch
            {
                scene = null;
                return false;
            }
        }

        private void ReleaseSimulationEventCallbacks()
        {
            if (_simulationEventCallbackPtr is not null)
            {
                destroy_simulation_event_callbacks(_simulationEventCallbackPtr);
                _simulationEventCallbackPtr = null;
            }

            if (_simulationEventCallbackHandleAllocated)
            {
                _simulationEventCallbackHandle.Free();
                _simulationEventCallbackHandleAllocated = false;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe void OnContact(void* userData, PxContactPairHeader* pairHeader, PxContactPair* pairs, uint nbPairs)
        {
            if (userData is null || pairHeader is null || pairs is null || nbPairs == 0)
                return;

            if (!TryGetSceneFromUserData(userData, out var scene) || scene is null)
                return;
            // Intentionally no-op for now; ensures native never calls a null function pointer.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe void OnTrigger(void* userData, PxTriggerPair* pairs, uint count)
        {
            if (userData is null || pairs is null || count == 0)
                return;

            if (!TryGetSceneFromUserData(userData, out var scene) || scene is null)
                return;

            // Intentionally no-op for now; ensures native never calls a null function pointer.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe void OnConstraintBreak(void* userData, PxConstraintInfo* constraints, uint count)
        {
            if (userData is null || constraints is null || count == 0)
                return;

            if (!TryGetSceneFromUserData(userData, out var scene) || scene is null)
                return;

            // Intentionally no-op for now; ensures native never calls a null function pointer.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe void OnWakeSleep(void* userData, PxActor** actors, uint count, bool isWaking)
        {
            if (userData is null || actors is null || count == 0)
                return;

            if (!TryGetSceneFromUserData(userData, out var scene) || scene is null)
                return;

            // Intentionally no-op for now; ensures native never calls a null function pointer.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe void OnAdvance(void* userData, PxRigidBody** bodyBuffer, PxTransform* poseBuffer, uint count)
        {
            if (userData is null || bodyBuffer is null || poseBuffer is null || count == 0)
                return;

            if (!TryGetSceneFromUserData(userData, out var scene) || scene is null)
                return;

            // Intentionally no-op for now; ensures native never calls a null function pointer.
        }

        public override void Initialize()
        {
            //PxPvd pvd;
            //if (_physics->PhysPxInitExtensions(&pvd))
            //{

            //}
            var scale = _physicsPtr->GetTolerancesScale();
            //scale->length = 100;
            //scale->speed = 980;
            var sceneDesc = PxSceneDesc_new(scale);
            sceneDesc.gravity = DefaultGravity;
            sceneDesc.cpuDispatcher = _dispatcher = (PxCpuDispatcher*)phys_PxDefaultCpuDispatcherCreate(4, null, PxDefaultCpuDispatcherWaitForWorkMode.WaitForWork, 0);

            ConfigureSimulationEventCallbacks(ref sceneDesc);

            sceneDesc.filterShader = get_default_simulation_filter_shader();
            // Custom filter shader callback (managed -> native function pointer).
            // This applies the engine's group/mask filtering encoded into PxFilterData by PhysxRigidActor.RefreshShapeFilterData().
            // Keep this callback simple and conservative: if filter data is unset, allow the pair.
            var filterShaderCallback = (delegate* unmanaged[Cdecl]<FilterShaderCallbackInfo*, PxFilterFlags>)&CustomSimulationFilterShader;
            enable_custom_filter_shader(&sceneDesc, filterShaderCallback, 0u);

            sceneDesc.flags =
                PxSceneFlags.EnableCcd |
                PxSceneFlags.EnableActiveActors;
            //PxSceneFlags.EnableStabilization |
            //PxSceneFlags.EnableEnhancedDeterminism;
            
            // Use SAP broadphase like the working smoke test (GPU broadphase may have issues with CCT)
            sceneDesc.broadPhaseType = PxBroadPhaseType.Sap;
            // GPU memory config only applies to GPU broadphase, skip for SAP
            // var gpuMemorySettings = Engine.Rendering.Settings.PhysicsGpuMemorySettings;
            // sceneDesc.gpuDynamicsConfig = new PxgDynamicsMemoryConfig { ... };
            _scene = _physicsPtr->CreateSceneMut(&sceneDesc);
            PhysxObjectLog.Created(this, (nint)_scene, "scene-created");
            if (!Scenes.TryAdd((nint)_scene, this))
                Debug.Log(ELogCategory.Physics, "[PhysxScene] Duplicate scene ptr registered ptr=0x{0:X}", (nint)_scene);

            LinkVisualizationSettings();
        }

        private static bool IsUnsetFilterData(in PxFilterData fd)
            => fd.word0 == 0 && fd.word1 == 0 && fd.word2 == 0 && fd.word3 == 0;

        private static ulong Mask64FromFilterData(in PxFilterData fd)
            => ((ulong)fd.word2 << 32) | fd.word1;

        private static ulong GroupBit64FromFilterData(in PxFilterData fd)
        {
            uint w0 = fd.word0;
            if (w0 == 0)
                return 0;

            // If word0 is a bitmask (power-of-two), interpret it as such (supports groups 0..31).
            // Otherwise interpret word0 as a group index (supports groups 0..63).
            int groupIndex;
            if ((w0 & (w0 - 1)) == 0)
            {
                groupIndex = BitOperations.TrailingZeroCount(w0);
            }
            else
            {
                groupIndex = unchecked((int)w0);
            }

            if ((uint)groupIndex >= 64u)
                return 0;

            return 1UL << groupIndex;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe PxFilterFlags CustomSimulationFilterShader(FilterShaderCallbackInfo* info)
        {
            if (info is null || info->pairFlags is null)
                return 0;

            bool isTrigger0 = phys_PxFilterObjectIsTrigger(info->attributes0);
            bool isTrigger1 = phys_PxFilterObjectIsTrigger(info->attributes1);

            // PhysX behavior: trigger-trigger pairs do not generate trigger notifications.
            if (isTrigger0 && isTrigger1)
                return PxFilterFlags.Suppress;

            // Always accept triggers (subject to the above rule).
            if (isTrigger0 || isTrigger1)
            {
                *info->pairFlags = PxPairFlags.TriggerDefault;
                return 0;
            }

            // If filter data is missing/uninitialized for either shape, don't unexpectedly suppress the pair.
            // This keeps CCT/internal shapes from breaking if they don't populate PxFilterData.
            PxFilterData fd0 = info->filterData0;
            PxFilterData fd1 = info->filterData1;
            if (IsUnsetFilterData(fd0) || IsUnsetFilterData(fd1))
            {
                *info->pairFlags = PxPairFlags.ContactDefault | PxPairFlags.DetectCcdContact;
                return 0;
            }

            ulong group0 = GroupBit64FromFilterData(fd0);
            ulong group1 = GroupBit64FromFilterData(fd1);
            ulong mask0 = Mask64FromFilterData(fd0);
            ulong mask1 = Mask64FromFilterData(fd1);

            // Conservative fallback: if either group resolves to 0, allow.
            if (group0 == 0 || group1 == 0)
            {
                *info->pairFlags = PxPairFlags.ContactDefault | PxPairFlags.DetectCcdContact;
                return 0;
            }

            bool allow = (group0 & mask1) != 0 && (group1 & mask0) != 0;
            if (!allow)
                return PxFilterFlags.Suppress;

            *info->pairFlags = PxPairFlags.ContactDefault | PxPairFlags.DetectCcdContact;
            return 0;
        }

        public DataSource? _scratchBlock = new(32000, true);

        private readonly ConcurrentQueue<PhysxController> _pendingControllerReleases = new();
        internal void QueueControllerRelease(PhysxController controller)
        {
            if (controller is null)
                return;
            _pendingControllerReleases.Enqueue(controller);
        }

        public override unsafe void StepSimulation()
        {
            //using var t = Engine.Profiler.Start();

            float dt = Engine.Time.Timer.FixedUpdateDelta;
            //Debug.Physics($"[PhysxScene] StepSimulation dt={dt}");
            //System.IO.File.AppendAllText("physx_debug.log", $"[PhysxScene] StepSimulation dt={dt}\n");

            // Process deferred controller releases first so PxController::move never races PxController::release.
            while (_pendingControllerReleases.TryDequeue(out var pendingRelease))
            {
                try
                {
                    pendingRelease.ReleaseNativeNow();
                }
                catch (Exception ex)
                {
                    Debug.Log(ELogCategory.Physics, "[PhysxScene] Deferred controller release failed: {0}", ex);
                }
            }

            var controllers = _controllerManager?.Controllers?.Values;
            if (controllers is not null)
                foreach (var controller in controllers)
                    controller.ConsumeInputBuffer(dt);
                        
            Simulate(dt, null, true);
            if (!FetchResults(true, out uint error))
            {
                Debug.Out($"PhysX FetchResults error: {error}");
                return;
            }

            if (VisualizeEnabled)
                PopulateDebugBuffers();

            uint count;
            var ptr = _scene->GetActiveActorsMut(&count);
            for (int i = 0; i < count; i++)
                UpdatePhysicsActor(ptr, i);
            //Task.WaitAll(Enumerable.Range(0, (int)count).Select(i => Task.Run(() => UpdatePhysicsActor(ptr, i))));
            NotifySimulationStepped();
        }

        private static void UpdatePhysicsActor(PxActor** ptr, int i)
        {
            var actor = PhysxActor.Get(ptr[i]);
            switch (actor)
            {
                case PhysxDynamicRigidBody dynamicActor:
                    dynamicActor.OwningComponent?.RigidBodyTransform.OnPhysicsStepped();
                    break;
                case PhysxStaticRigidBody staticActor:
                    staticActor.OwningComponent?.RigidBodyTransform.OnPhysicsStepped();
                    break;
            }
        }

        public void Simulate(float elapsedTime, PxBaseTask* completionTask, bool controlSimulation)
            // Match smoke test: pass null scratch block instead of custom one
            => _scene->SimulateMut(elapsedTime, completionTask, null, 0, controlSimulation);
        public void Collide(float elapsedTime, PxBaseTask* completionTask, bool controlSimulation)
            => _scene->CollideMut(elapsedTime, completionTask, _scratchBlock is null ? null : _scratchBlock.Address.Pointer, _scratchBlock?.Length ?? 0, controlSimulation);
        public void FlushSimulation(bool sendPendingReports)
            => _scene->FlushSimulationMut(sendPendingReports);
        public void Advance(PxBaseTask* completionTask)
            => _scene->AdvanceMut(completionTask);
        public void FetchCollision(bool block)
            => _scene->FetchCollisionMut(block);
        public bool FetchResults(bool block, out uint errorState)
        {
            uint es = 0;
            var lastMoveQueue = _moveQueue;
            bool result = _scene->FetchResultsMut(block, &es);
            errorState = es;
            return result;
        }
        public bool FetchResultsStart(out PxContactPairHeader[] contactPairs, bool block)
        {
            PxContactPairHeader* ptr;
            uint numPairs;
            bool result = _scene->FetchResultsStartMut(&ptr, &numPairs, block);
            contactPairs = new PxContactPairHeader[numPairs];
            for (int i = 0; i < numPairs; i++)
                contactPairs[i] = *ptr++;
            return result;
        }
        public void ProcessCallbacks(PxBaseTask* continuation)
            => _scene->ProcessCallbacksMut(continuation);
        public void FetchResultsFinish(out uint errorState)
        {
            uint es = 0;
            _scene->FetchResultsFinishMut(&es);
            errorState = es;
        }

        public bool CheckResults(bool block)
            => _scene->CheckResultsMut(block);

        public void FetchResultsParticleSystem()
            => _scene->FetchResultsParticleSystemMut();

        public uint Timestamp
            => _scene->GetTimestamp();

        public PxBroadPhaseCallback* BroadPhaseCallbackPtr
        {
            get => _scene->GetBroadPhaseCallback();
            set => _scene->SetBroadPhaseCallbackMut(value);
        }
        public PxCCDContactModifyCallback* CcdContactModifyCallbackPtr
        {
            get => _scene->GetCCDContactModifyCallback();
            set => _scene->SetCCDContactModifyCallbackMut(value);
        }
        public PxContactModifyCallback* ContactModifyCallbackPtr
        {
            get => _scene->GetContactModifyCallback();
            set => _scene->SetContactModifyCallbackMut(value);
        }
        public PxSimulationEventCallback* SimulationEventCallbackPtr
        {
            get => _scene->GetSimulationEventCallback();
            set => _scene->SetSimulationEventCallbackMut(value);
        }

        public byte CreateClient()
            => _scene->CreateClientMut();

        public struct FilterShader
        {
            public void* data;
            public uint dataSize;
        }
        public FilterShader FilterShaderData
        {
            get
            {
                void* data = _scene->GetFilterShaderData();
                uint dataSize = _scene->GetFilterShaderDataSize();
                return new FilterShader { data = data, dataSize = dataSize };
            }
            set
            {
                _scene->SetFilterShaderDataMut(value.data, value.dataSize);
            }
        }

        public void AddActor(PhysxActor actor)
        {
            // PhysX disallows adding the same actor twice; this can happen for actors managed by other systems
            // (e.g. CCT hidden actor) where the actor is already inserted into a scene.
            if (actor.ScenePtr is not null)
            {
                if (actor.ScenePtr == _scene)
                {
                    Debug.Log(ELogCategory.Physics, "[PhysxScene] Skipped AddActor: actor already in this scene actorType={0} ptr=0x{1:X}", actor.GetType().Name, (nint)actor.ActorPtr);
                    return;
                }

                Debug.Log(ELogCategory.Physics, "[PhysxScene] Skipped AddActor: actor already in another scene actorType={0} ptr=0x{1:X}", actor.GetType().Name, (nint)actor.ActorPtr);
                return;
            }

            _scene->AddActorMut(actor.ActorPtr, null);
            actor.OnAddedToScene(this);
            Debug.Log(ELogCategory.Physics, "[PhysxScene] Added actorType={0} ptr=0x{1:X} group={2} mask={3}", actor.GetType().Name, (nint)actor.ActorPtr, actor is PhysxActor pa ? pa.CollisionGroup : (ushort)0, actor is PhysxActor pa2 ? FormatGroupsMask(pa2.GroupsMask) : "----");
        }
        public void AddActors(PhysxActor[] actors)
        {
            // Filter out actors already assigned to a scene to avoid PhysX invalid-operation warnings.
            var addList = new List<PhysxActor>(actors.Length);
            foreach (var a in actors)
            {
                if (a.ScenePtr is null)
                {
                    addList.Add(a);
                    continue;
                }

                if (a.ScenePtr == _scene)
                    Debug.Log(ELogCategory.Physics, "[PhysxScene] Skipped AddActors: actor already in this scene actorType={0} ptr=0x{1:X}", a.GetType().Name, (nint)a.ActorPtr);
                else
                    Debug.Log(ELogCategory.Physics, "[PhysxScene] Skipped AddActors: actor already in another scene actorType={0} ptr=0x{1:X}", a.GetType().Name, (nint)a.ActorPtr);
            }

            if (addList.Count == 0)
                return;

            PxActor** ptrs = stackalloc PxActor*[addList.Count];
            for (int i = 0; i < addList.Count; i++)
                ptrs[i] = addList[i].ActorPtr;
            _scene->AddActorsMut(ptrs, (uint)addList.Count);
            foreach (var actor in addList)
            {
                actor.OnAddedToScene(this);
                Debug.Log(ELogCategory.Physics, "[PhysxScene] Added actorType={0} ptr=0x{1:X} group={2} mask={3}", actor.GetType().Name, (nint)actor.ActorPtr, actor is PhysxActor pa ? pa.CollisionGroup : (ushort)0, actor is PhysxActor pa2 ? FormatGroupsMask(pa2.GroupsMask) : "----");
            }
        }
        public void RemoveActor(PhysxActor actor, bool wakeOnLostTouch = false)
        {
            _scene->RemoveActorMut(actor.ActorPtr, wakeOnLostTouch);
            actor.OnRemovedFromScene(this);
            Debug.Log(ELogCategory.Physics, "[PhysxScene] Removed actorType={0} ptr=0x{1:X}", actor.GetType().Name, (nint)actor.ActorPtr);
        }
        public void RemoveActors(PhysxActor[] actors, bool wakeOnLostTouch = false)
        {
            PxActor** ptrs = stackalloc PxActor*[actors.Length];
            for (int i = 0; i < actors.Length; i++)
                ptrs[i] = actors[i].ActorPtr;
            _scene->RemoveActorsMut(ptrs, (uint)actors.Length, wakeOnLostTouch);
            foreach (var actor in actors)
            {
                actor.OnRemovedFromScene(this);
                Debug.Log(ELogCategory.Physics, "[PhysxScene] Removed actorType={0} ptr=0x{1:X}", actor.GetType().Name, (nint)actor.ActorPtr);
            }
        }

        private static string FormatGroupsMask(PxGroupsMask mask)
            => $"{mask.bits0:X4}:{mask.bits1:X4}:{mask.bits2:X4}:{mask.bits3:X4}";

        private unsafe (string actor0, string actor1) DescribeContactActors(PxContactPairHeader* header)
        {
            PxRigidActor* actor0Ptr = null;
            PxRigidActor* actor1Ptr = null;

            if (header is not null)
            {
                ref var headerRef = ref *header;
                fixed (byte* actorBytes = headerRef.actors)
                {
                    var actors = (PxRigidActor**)actorBytes;
                    actor0Ptr = actors[0];
                    actor1Ptr = actors[1];
                }
            }

            return (DescribeActor(actor0Ptr), DescribeActor(actor1Ptr));
        }

        private unsafe (string shape0, string shape1) DescribePairShapes(PxContactPair* pair)
        {
            PxShape* shape0Ptr = null;
            PxShape* shape1Ptr = null;

            if (pair is not null)
            {
                ref var pairRef = ref *pair;
                fixed (byte* shapeBytes = pairRef.shapes)
                {
                    var shapes = (PxShape**)shapeBytes;
                    shape0Ptr = shapes[0];
                    shape1Ptr = shapes[1];
                }
            }

            return (DescribeShape(shape0Ptr), DescribeShape(shape1Ptr));
        }

        private unsafe string DescribeActor(PxRigidActor* actorPtr)
        {
            if (actorPtr is null)
                return "null-actor";

            var actor = PhysxRigidActor.Get(actorPtr);
            if (actor is null)
                return $"PxRigidActor* 0x{(nint)actorPtr:X}";

            var ownerName = actor.GetOwningComponent()?.Name ?? actor.GetType().Name;
            return $"{ownerName} [group={actor.CollisionGroup} mask={FormatGroupsMask(actor.GroupsMask)}]";
        }

        private unsafe string DescribeShape(PxShape* shapePtr)
        {
            if (shapePtr is null)
                return "null-shape";

            var shape = PhysxShape.Get(shapePtr);
            if (shape is null)
                return $"PxShape* 0x{(nint)shapePtr:X}";

            var actor = shape.GetActor();
            var ownerName = actor?.GetOwningComponent()?.Name ?? actor?.GetType().Name ?? "no-owner";
            var mask = actor is null ? "----" : FormatGroupsMask(actor.GroupsMask);
            var group = actor?.CollisionGroup ?? 0;
            return $"{shape.GetType().Name}:{shape.Name} owner={ownerName} group={group} mask={mask}";
        }


        public ConcurrentDictionary<nint, PhysxShape> Shapes { get; } = new();
        public PhysxShape? GetShape(PxShape* ptr)
        {
            if (ptr is null)
                return null;

            if (!Shapes.TryGetValue((nint)ptr, out var shape) || shape is null)
            {
                shape = PhysxShape.Get(ptr) ?? new PhysxShape(ptr);
                Shapes[(nint)ptr] = shape;
                Debug.Log(ELogCategory.Physics, "[PhysxScene] Registered shape ptr=0x{0:X}", (nint)ptr);

                /*
                var flags = ptr->GetFlags();
                Debug.Physics(
                    "[PhysxScene] Registered shape ptr=0x{0:X} sim={1} query={2} trigger={3}",
                    (nint)ptr,
                    flags.HasFlag(PxShapeFlags.SimulationShape),
                    flags.HasFlag(PxShapeFlags.SceneQueryShape),
                    flags.HasFlag(PxShapeFlags.TriggerShape));
                    */
            }

            return shape;
        }

        #region Joints

        public ConcurrentDictionary<nint, PhysxJoint> Joints { get; } = new();
        public PhysxJoint? GetJoint(PxJoint* ptr)
            => Joints.TryGetValue((nint)ptr, out var joint) ? joint : null;

        public ConcurrentDictionary<nint, PhysxJoint_Contact> ContactJoints { get; } = new();
        public PhysxJoint_Contact? GetContactJoint(PxContactJoint* ptr)
            => ContactJoints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_Contact NewContactJoint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxContactJointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_Contact(joint);
            Joints[(nint)joint] = jointObj;
            ContactJoints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_Contact ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        public ConcurrentDictionary<nint, PhysxJoint_Distance> DistanceJoints { get; } = new();
        public PhysxJoint_Distance? GetDistanceJoint(PxDistanceJoint* ptr)
            => DistanceJoints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_Distance NewDistanceJoint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxDistanceJointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_Distance(joint);
            Joints[(nint)joint] = jointObj;
            DistanceJoints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_Distance ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        public ConcurrentDictionary<nint, PhysxJoint_D6> D6Joints { get; } = new();
        public PhysxJoint_D6? GetD6Joint(PxD6Joint* ptr)
            => D6Joints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_D6 NewD6Joint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxD6JointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_D6(joint);
            Joints[(nint)joint] = jointObj;
            D6Joints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_D6 ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        public ConcurrentDictionary<nint, PhysxJoint_Fixed> FixedJoints { get; } = new();
        public PhysxJoint_Fixed? GetFixedJoint(PxFixedJoint* ptr)
            => FixedJoints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_Fixed NewFixedJoint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxFixedJointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_Fixed(joint);
            Joints[(nint)joint] = jointObj;
            FixedJoints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_Fixed ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        public ConcurrentDictionary<nint, PhysxJoint_Prismatic> PrismaticJoints { get; } = new();
        public PhysxJoint_Prismatic? GetPrismaticJoint(PxPrismaticJoint* ptr)
            => PrismaticJoints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_Prismatic NewPrismaticJoint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxPrismaticJointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_Prismatic(joint);
            Joints[(nint)joint] = jointObj;
            PrismaticJoints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_Prismatic ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        public ConcurrentDictionary<nint, PhysxJoint_Revolute> RevoluteJoints { get; } = new();
        public PhysxJoint_Revolute? GetRevoluteJoint(PxRevoluteJoint* ptr)
            => RevoluteJoints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_Revolute NewRevoluteJoint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxRevoluteJointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_Revolute(joint);
            Joints[(nint)joint] = jointObj;
            RevoluteJoints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_Revolute ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        public ConcurrentDictionary<nint, PhysxJoint_Spherical> SphericalJoints { get; } = new();
        public PhysxJoint_Spherical? GetSphericalJoint(PxSphericalJoint* ptr)
            => SphericalJoints.TryGetValue((nint)ptr, out var joint) ? joint : null;
        public PhysxJoint_Spherical NewSphericalJoint(PhysxRigidActor actor0, (Vector3 position, Quaternion rotation) localFrame0, PhysxRigidActor actor1, (Vector3 position, Quaternion rotation) localFrame1)
        {
            PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
            PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
            var joint = PhysicsPtr->PhysPxSphericalJointCreate(actor0.RigidActorPtr, &pxlocalFrame0, actor1.RigidActorPtr, &pxlocalFrame1);
            var jointObj = new PhysxJoint_Spherical(joint);
            Joints[(nint)joint] = jointObj;
            SphericalJoints[(nint)joint] = jointObj;
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + PhysxJoint_Spherical ptr=0x{0:X}", (nint)joint);
            return jointObj;
        }

        #endregion

        public static PxTransform MakeTransform(Vector3? position, Quaternion? rotation)
        {
            Quaternion q = rotation ?? Quaternion.Identity;
            Vector3 p = position ?? Vector3.Zero;
            PxVec3 pos = new() { x = p.X, y = p.Y, z = p.Z };
            PxQuat rot = new() { x = q.X, y = q.Y, z = q.Z, w = q.W };
            return PxTransform_new_5(&pos, &rot);
        }

        public PxSceneFlags Flags => _scene->GetFlags();
        public void SetFlag(PxSceneFlag flag, bool value)
            => _scene->SetFlagMut(flag, value);

        public PxSceneLimits Limits
        {
            get => _scene->GetLimits();
            set => _scene->SetLimitsMut(&value);
        }

        public void AddArticulation(PxArticulationReducedCoordinate* articulation)
            => _scene->AddArticulationMut(articulation);
        public void RemoveArticulation(PxArticulationReducedCoordinate* articulation, bool wakeOnLostTouch)
            => _scene->RemoveArticulationMut(articulation, wakeOnLostTouch);

        public void AddAggregate(PxAggregate* aggregate)
            => _scene->AddAggregateMut(aggregate);
        public void RemoveAggregate(PxAggregate* aggregate, bool wakeOnLostTouch)
            => _scene->RemoveAggregateMut(aggregate, wakeOnLostTouch);

        public void AddCollection(PxCollection* collection)
            => _scene->AddCollectionMut(collection);
        public uint GetActorCount(PxActorTypeFlags types)
            => _scene->GetNbActors(types);

        public PhysxActor[] GetActors(PxActorTypeFlags types)
        {
            uint count = GetActorCount(types);
            PxActor** ptrs = stackalloc PxActor*[(int)count];
            uint numWritten = _scene->GetActors(types, ptrs, count, 0);
            PhysxActor[] actors = new PhysxActor[count];
            for (int i = 0; i < count; i++)
                actors[i] = PhysxActor.Get(ptrs[i])!;
            return actors;
        }

        /// <summary>
        /// Requires PxSceneFlag::eENABLE_ACTIVE_ACTORS to be set.
        /// </summary>
        /// <returns></returns>
        public PhysxActor[] GetActiveActors()
        {
            uint count;
            PxActor** ptrs = _scene->GetActiveActorsMut(&count);
            PhysxActor[] actors = new PhysxActor[count];
            for (int i = 0; i < count; i++)
                actors[i] = PhysxActor.Get(ptrs[i])!;
            return actors;
        }

        public uint ArticulationCount => _scene->GetNbArticulations();

        public PxArticulationReducedCoordinate*[] GetArticulations()
        {
            uint count = ArticulationCount;
            PxArticulationReducedCoordinate** ptrs = stackalloc PxArticulationReducedCoordinate*[(int)count];
            uint numWritten = _scene->GetArticulations(ptrs, count, 0);
            PxArticulationReducedCoordinate*[] articulations = new PxArticulationReducedCoordinate*[count];
            for (int i = 0; i < count; i++)
                articulations[i] = ptrs[i];
            return articulations;
        }

        public uint ConstraintCount => _scene->GetNbConstraints();

        public PxConstraint*[] GetConstraints()
        {
            uint count = ConstraintCount;
            PxConstraint** ptrs = stackalloc PxConstraint*[(int)count];
            uint numWritten = _scene->GetConstraints(ptrs, count, 0);
            PxConstraint*[] constraints = new PxConstraint*[count];
            for (int i = 0; i < count; i++)
                constraints[i] = ptrs[i];
            return constraints;
        }

        public uint AggregateCount => _scene->GetNbAggregates();

        public PxAggregate*[] GetAggregates()
        {
            uint count = AggregateCount;
            PxAggregate** ptrs = stackalloc PxAggregate*[(int)count];
            uint numWritten = _scene->GetAggregates(ptrs, count, 0);
            PxAggregate*[] aggregates = new PxAggregate*[count];
            for (int i = 0; i < count; i++)
                aggregates[i] = ptrs[i];
            return aggregates;
        }

        public void SetDominanceGroupPair(byte group1, byte group2, PxDominanceGroupPair dominance)
            => _scene->SetDominanceGroupPairMut(group1, group2, &dominance);

        public PxDominanceGroupPair GetDominanceGroupPair(byte group1, byte group2)
            => _scene->GetDominanceGroupPair(group1, group2);

        public bool ResetFiltering(PhysxActor actor)
            => _scene->ResetFilteringMut(actor.ActorPtr);

        public bool ResetFiltering(PhysxRigidActor actor, PhysxShape[] shapes)
        {
            PxShape** shapes_ = stackalloc PxShape*[shapes.Length];
            for (int i = 0; i < shapes.Length; i++)
                shapes_[i] = shapes[i].ShapePtr;
            return _scene->ResetFilteringMut1(actor.RigidActorPtr, shapes_, (uint)shapes.Length);
        }

        public PxPairFilteringMode KinematicKinematicFilteringMode
            => _scene->GetKinematicKinematicFilteringMode();

        public PxPairFilteringMode StaticKinematicFilteringMode
            => _scene->GetStaticKinematicFilteringMode();

        public float BounceThresholdVelocity
        {
            get => _scene->GetBounceThresholdVelocity();
            set => _scene->SetBounceThresholdVelocityMut(value);
        }

        public uint CCDMaxPasses
        {
            get => _scene->GetCCDMaxPasses();
            set => _scene->SetCCDMaxPassesMut(value);
        }

        public float CCDMaxSeparation
        {
            get => _scene->GetCCDMaxSeparation();
            set => _scene->SetCCDMaxSeparationMut(value);
        }

        public float CCDThreshold
        {
            get => _scene->GetCCDThreshold();
            set => _scene->SetCCDThresholdMut(value);
        }

        public float MaxBiasCoefficient
        {
            get => _scene->GetMaxBiasCoefficient();
            set => _scene->SetMaxBiasCoefficientMut(value);
        }

        public float FrictionOffsetThreshold
        {
            get => _scene->GetFrictionOffsetThreshold();
            set => _scene->SetFrictionOffsetThresholdMut(value);
        }

        public float FrictionCorrelationDistance
        {
            get => _scene->GetFrictionCorrelationDistance();
            set => _scene->SetFrictionCorrelationDistanceMut(value);
        }

        public PxFrictionType FrictionType
            => _scene->GetFrictionType();

        public PxSolverType SolverType
            => _scene->GetSolverType();
        
        public bool SetVisualizationParameter(PxVisualizationParameter param, float value)
            => _scene->SetVisualizationParameterMut(param, value);

        public float GetVisualizationParameter(PxVisualizationParameter param)
            => _scene->GetVisualizationParameter(param);

        public AABB VisualizationCullingBox
        {
            get
            {
                PxBounds3 b = _scene->GetVisualizationCullingBox();
                return new AABB { Min = b.minimum, Max = b.maximum };
            }
            set
            {
                PxBounds3 b = new() { minimum = value.Min, maximum = value.Max };
                _scene->SetVisualizationCullingBoxMut(&b);
            }
        }

        public PxRenderBuffer* RenderBuffer
            => _scene->GetRenderBufferMut();

        public PxSimulationStatistics SimulationStatistics
        {
            get
            {
                if (_scene is null)
                {
                    Debug.Physics("[PhysxScene] SimulationStatistics requested before scene initialization");
                    return default;
                }
                if (!Scenes.TryGetValue((nint)_scene, out var owner) || !ReferenceEquals(owner, this))
                {
                    Debug.Physics("[PhysxScene] SimulationStatistics requested for stale scene pointer");
                    return default;
                }
                PxSimulationStatistics stats;
                _scene->GetSimulationStatistics(&stats);
                return stats;
            }
        }

        public PxBroadPhaseType BroadPhaseType
            => _scene->GetBroadPhaseType();

        public PxBroadPhaseCaps BroadPhaseCaps
        {
            get
            {
                PxBroadPhaseCaps caps;
                _scene->GetBroadPhaseCaps(&caps);
                return caps;
            }
        }

        public uint BroadPhaseRegionsCount
            => _scene->GetNbBroadPhaseRegions();
        public PxBroadPhaseRegionInfo[] GetBroadPhaseRegions(uint startIndex)
        {
            uint count = BroadPhaseRegionsCount;
            PxBroadPhaseRegionInfo* buffer = stackalloc PxBroadPhaseRegionInfo[(int)count];
            uint numWritten = _scene->GetBroadPhaseRegions(buffer, count, startIndex);
            PxBroadPhaseRegionInfo[] regions = new PxBroadPhaseRegionInfo[count];
            for (int i = 0; i < count; i++)
                regions[i] = buffer[i];
            return regions;
        }
        public uint AddBroadPhaseRegion(PxBroadPhaseRegion region, bool populateRegion)
            => _scene->AddBroadPhaseRegionMut(&region, populateRegion);
        public bool RemoveBroadPhaseRegion(uint handle)
            => _scene->RemoveBroadPhaseRegionMut(handle);

        public PxTaskManager* TaskManager
            => _scene->GetTaskManager();

        public void LockRead(byte* file, uint line)
            => _scene->LockReadMut(file, line);
        public void UnlockRead()
            => _scene->UnlockReadMut();
        public void LockWrite(byte* file, uint line)
            => _scene->LockWriteMut(file, line);
        public void UnlockWrite()
            => _scene->UnlockWriteMut();

        public void SetContactDataBlockCount(uint numBlocks)
            => _scene->SetNbContactDataBlocksMut(numBlocks);

        public uint ContactDataBlocksUsed
            => _scene->GetNbContactDataBlocksUsed();

        public uint MaxContactDataBlocksUsed
            => _scene->GetMaxNbContactDataBlocksUsed();

        public uint ContactReportStreamBufferSize
            => _scene->GetContactReportStreamBufferSize();

        public uint SolverBatchSize
        {
            get => _scene->GetSolverBatchSize();
            set => _scene->SetSolverBatchSizeMut(value);
        }

        public uint SolverArticulationBatchSize
        {
            get => _scene->GetSolverArticulationBatchSize();
            set => _scene->SetSolverArticulationBatchSizeMut(value);
        }

        public float WakeCounterResetValue
            => _scene->GetWakeCounterResetValue();

        public void ShiftOrigin(Vector3 shift)
        {
            PxVec3 s = shift;
            _scene->ShiftOriginMut(&s);
        }

        public PxPvdSceneClient* ScenePvdClient
            => _scene->GetScenePvdClientMut();

        public void CopyArticulationData(void* data, void* index, PxArticulationGpuDataType dataType, uint nbCopyArticulations, void* copyEvent)
            => _scene->CopyArticulationDataMut(data, index, dataType, nbCopyArticulations, copyEvent);

        public void ApplyArticulationData(void* data, void* index, PxArticulationGpuDataType dataType, uint nbUpdatedArticulations, void* waitEvent, void* signalEvent)
            => _scene->ApplyArticulationDataMut(data, index, dataType, nbUpdatedArticulations, waitEvent, signalEvent);
        
        public void CopySoftBodyData(void** data, void* dataSizes, void* softBodyIndices, PxSoftBodyDataFlag flag, uint nbCopySoftBodies, uint maxSize, void* copyEvent)
                => _scene->CopySoftBodyDataMut(data, dataSizes, softBodyIndices, flag, nbCopySoftBodies, maxSize, copyEvent);
        public void CopyContactData(void* data, uint maxContactPairs, void* numContactPairs, void* copyEvent)
            => _scene->CopyContactDataMut(data, maxContactPairs, numContactPairs, copyEvent);
        public void CopyBodyData(PxGpuBodyData* data, PxGpuActorPair* index, uint nbCopyActors, void* copyEvent)
            => _scene->CopyBodyDataMut(data, index, nbCopyActors, copyEvent);

        public void ApplySoftBodyData(void** data, void* dataSizes, void* softBodyIndices, PxSoftBodyDataFlag flag, uint nbUpdatedSoftBodies, uint maxSize, void* applyEvent)
            => _scene->ApplySoftBodyDataMut(data, dataSizes, softBodyIndices, flag, nbUpdatedSoftBodies, maxSize, applyEvent);
        public void ApplyActorData(void* data, PxGpuActorPair* index, PxActorCacheFlag flag, uint nbUpdatedActors, void* waitEvent, void* signalEvent)
            => _scene->ApplyActorDataMut(data, index, flag, nbUpdatedActors, waitEvent, signalEvent);

        public void ComputeDenseJacobians(PxIndexDataPair* indices, uint nbIndices, void* computeEvent)
            => _scene->ComputeDenseJacobiansMut(indices, nbIndices, computeEvent);

        public void ComputeGeneralizedMassMatrices(PxIndexDataPair* indices, uint nbIndices, void* computeEvent)
            => _scene->ComputeGeneralizedMassMatricesMut(indices, nbIndices, computeEvent);
        public void ComputeGeneralizedGravityForces(PxIndexDataPair* indices, uint nbIndices, void* computeEvent)
            => _scene->ComputeGeneralizedGravityForcesMut(indices, nbIndices, computeEvent);
        public void ComputeCoriolisAndCentrifugalForces(PxIndexDataPair* indices, uint nbIndices, void* computeEvent)
            => _scene->ComputeCoriolisAndCentrifugalForcesMut(indices, nbIndices, computeEvent);

        public PxgDynamicsMemoryConfig GetGpuDynamicsConfig()
            => _scene->GetGpuDynamicsConfig();

        public void ApplyParticleBufferData(uint* indices, PxGpuParticleBufferIndexPair* bufferIndexPair, PxParticleBufferFlags* flags, uint nbUpdatedBuffers, void* waitEvent, void* signalEvent)
            => _scene->ApplyParticleBufferDataMut(indices, bufferIndexPair, flags, nbUpdatedBuffers, waitEvent, signalEvent);

        public PxSceneReadLock* ReadLockNewAlloc(byte* file, uint line)
            => _scene->ReadLockNewAlloc(file, line);
        public PxSceneWriteLock* WriteLockNewAlloc(byte* file, uint line)
            => _scene->WriteLockNewAlloc(file, line);

        private ControllerManager? _controllerManager;
        public ControllerManager GetOrCreateControllerManager(bool lockingEnabled = false)
        {
            if (_controllerManager is not null)
                return _controllerManager;

            var mgrPtr = _scene->PhysPxCreateControllerManager(lockingEnabled);
            _controllerManager = new ControllerManager(mgrPtr);
            Debug.Log(ELogCategory.Physics, "[PhysxObj] + ControllerManager ptr=0x{0:X} lockingEnabled={1}", (nint)mgrPtr, lockingEnabled);
            return _controllerManager;
        }

        public void ReleaseControllerManager()
        {
            if (_controllerManager == null)
                return;

            Debug.Log(ELogCategory.Physics, "[PhysxScene] ReleaseControllerManager controllers={0}", _controllerManager.Controllers.Count);

            // Release controllers before releasing the manager.
            foreach (var controller in _controllerManager.Controllers.Values)
            {
                try
                {
                    controller.RequestRelease();
                    controller.ReleaseNativeNow();
                }
                catch (Exception ex)
                {
                    Debug.Log(ELogCategory.Physics, "[PhysxScene] Controller release during manager teardown failed: {0}", ex);
                }
            }
            
            _controllerManager.ControllerManagerPtr->ReleaseMut();
            Debug.Log(ELogCategory.Physics, "[PhysxObj] - ControllerManager ptr=0x{0:X}", (nint)_controllerManager.ControllerManagerPtr);
            _controllerManager = null;
        }

        public bool RaycastAny(
            Vector3 origin,
            Vector3 unitDir,
            float distance,
            out uint hitFaceIndex,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            PxQueryFilterCallback* filterCallback = null,
            PxQueryCache* cache = null)
        {
            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            PxVec3 o = origin;
            PxVec3 d = unitDir;
            PxQueryHit hit_;
            bool hasHit = _scene->QueryExtRaycastAny(
                (PxVec3*)Unsafe.AsPointer(ref o),
                (PxVec3*)Unsafe.AsPointer(ref d),
                distance,
                &hit_,
                &filterData,
                filterCallback,
                cache);
            hitFaceIndex = hit_.faceIndex;
            return hasHit;
        }

        /// <summary>
        /// Raycast returning a single result.
        /// Returns the first rigid actor that is hit along the ray.
        /// Data for a blocking hit will be returned as specified by the outputFlags field.
        /// Touching hits will be ignored.
        /// </summary>
        /// <param name="origin">Origin of the ray.</param>
        /// <param name="unitDir">Normalized direction of the ray.</param>
        /// <param name="distance">Length of the ray. Needs to be larger than 0.</param>
        /// <param name="outputFlags">Specifies which properties should be written to the hit information.</param>
        /// <param name="hit">Raycast hit information.</param>
        /// <param name="filterData">Filtering data and simple logic.</param>
        /// <param name="filterCallback">Custom filtering logic (optional). 
        /// Only used if the corresponding PxHitFlag flags are set. If NULL, all hits are assumed to be blocking.</param>
        /// <param name="cache">Cached hit shape (optional).
        /// Ray is tested against cached shape first then against the scene.
        /// Note: Filtering is not executed for a cached shape if supplied; instead, if a hit is found, it is assumed to be a blocking hit. 
        /// Note: Using past touching hits as cache will produce incorrect behavior since the cached hit will always be treated as blocking.</param>
        /// <returns></returns>
        public bool RaycastSingle(
            Vector3 origin,
            Vector3 unitDir,
            float distance,
            PxHitFlags outputFlags,
            out PxRaycastHit hit,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            PxQueryFilterCallback* filterCallback = null,
            PxQueryCache* cache = null)
        {
            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            PxVec3 o = origin;
            PxVec3 d = unitDir;
            PxRaycastHit hit_;
            bool hasHit = _scene->QueryExtRaycastSingle(
                &o,
                &d,
                distance,
                outputFlags,
                &hit_,
                &filterData,
                filterCallback,
                cache);
            hit = hit_;
            return hasHit;
        }

        public PxRaycastHit[] RaycastMultiple(
            Vector3 origin,
            Vector3 unitDir,
            float distance,
            PxHitFlags outputFlags,
            out bool blockingHit,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            PxQueryFilterCallback* filterCallback = null,
            PxQueryCache* cache = null,
            int maxHitCapacity = 32)
        {
            //TODO: avoid stackalloc and new array allocation

            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            PxVec3 o = origin;
            PxVec3 d = unitDir;
            PxRaycastHit* hitBuffer = stackalloc PxRaycastHit[maxHitCapacity];
            bool blockingHit_;
            int hitCount = _scene->QueryExtRaycastMultiple(
                &o,
                &d,
                distance,
                outputFlags,
                hitBuffer,
                (uint)maxHitCapacity,
                &blockingHit_,
                &filterData,
                filterCallback,
                cache);
            blockingHit = blockingHit_;
            PxRaycastHit[] hits = new PxRaycastHit[hitCount];
            for (int i = 0; i < hitCount; i++)
                hits[i] = hitBuffer[i];
            return hits;
        }

        public bool SweepAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            PxHitFlags hitFlags,
            out PxQueryHit hit,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            float inflation = 0.0f,
            PxQueryFilterCallback* filterCallback = null,
            PxQueryCache* cache = null)
        {
            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            PxVec3 d = unitDir;
            var t = MakeTransform(pose.position, pose.rotation);
            PxQueryHit hit_;
            using var structObj = geometry.GetPhysxStruct();
            bool hasHit = _scene->QueryExtSweepAny(
                structObj.Address.As<PxGeometry>(),
                &t,
                &d,
                distance,
                hitFlags,
                &hit_,
                &filterData,
                filterCallback,
                cache,
                inflation);
            hit = hit_;
            return hasHit;
        }

        //Note that the scene-level sweep query returns PxSweepHit structures,
        //while the object-level sweep query returns PxGeomSweepHit hits.
        //The difference is simply that PxSweepHit is augmented with PxRigidActor and PxShape pointers.
        public bool SweepSingle(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            PxHitFlags outputFlags,
            out PxSweepHit hit,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            float inflation = 0.0f,
            PxQueryFilterCallback* filterCallback = null,
            PxQueryCache* cache = null)
        {
            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            PxVec3 d = unitDir;
            var t = MakeTransform(pose.position, pose.rotation);
            PxSweepHit hit_;
            using var structObj = geometry.GetPhysxStruct();
            bool hasHit = _scene->QueryExtSweepSingle(
                structObj.Address.As<PxGeometry>(),
                &t,
                &d,
                distance,
                outputFlags,
                &hit_,
                &filterData,
                filterCallback,
                cache,
                inflation);
            hit = hit_;
            return hasHit;
        }

        //Note that the scene-level sweep query returns PxSweepHit structures,
        //while the object-level sweep query returns PxGeomSweepHit hits.
        //The difference is simply that PxSweepHit is augmented with PxRigidActor and PxShape pointers.
        public PxSweepHit[] SweepMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            PxHitFlags outputFlags,
            out bool blockingHit,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            float inflation = 0.0f,
            PxQueryFilterCallback* filterCallback = null,
            PxQueryCache* cache = null,
            int maxHitCapacity = 32)
        {
            //TODO: avoid stackalloc and new array allocation

            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            PxVec3 d = unitDir;
            var t = MakeTransform(pose.position, pose.rotation);
            bool blockingHit_;
            PxSweepHit* hitBuffer_ = stackalloc PxSweepHit[maxHitCapacity];
            using var structObj = geometry.GetPhysxStruct();
            int hitCount = _scene->QueryExtSweepMultiple(
                structObj.Address.As<PxGeometry>(),
                &t,
                &d,
                distance,
                outputFlags,
                hitBuffer_,
                (uint)maxHitCapacity,
                &blockingHit_,
                &filterData,
                filterCallback,
                cache,
                inflation);
            blockingHit = blockingHit_;
            PxSweepHit[] hits = new PxSweepHit[hitCount];
            for (int i = 0; i < hitCount; i++)
                hits[i] = hitBuffer_[i];
            return hits;
        }

        public PxOverlapHit[] OverlapMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            PxQueryFilterCallback* filterCallback = null,
            int maxHitCapacity = 32)
        {
            //TODO: avoid stackalloc and new array allocation

            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            var t = MakeTransform(pose.position, pose.rotation);
            PxOverlapHit* hitBuffer = stackalloc PxOverlapHit[maxHitCapacity];
            using var structObj = geometry.GetPhysxStruct();
            int hitCount = _scene->QueryExtOverlapMultiple(
                structObj.Address.As<PxGeometry>(),
                &t,
                hitBuffer,
                (uint)maxHitCapacity,
                &filterData,
                filterCallback);
            PxOverlapHit[] hits = new PxOverlapHit[hitCount];
            for (int i = 0; i < hitCount; i++)
                hits[i] = hitBuffer[i];
            return hits;
        }

        public bool OverlapAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            out PxOverlapHit hit,
            PxQueryFlags queryFlags,
            PxFilterData* filterMask = null,
            PxQueryFilterCallback* filterCallback = null)
        {
            var filterData = filterMask != null ? PxQueryFilterData_new_1(filterMask, queryFlags) : PxQueryFilterData_new_2(queryFlags);
            var t = MakeTransform(pose.position, pose.rotation);
            PxOverlapHit hit_;
            using var structObj = geometry.GetPhysxStruct();
            bool hasHit = _scene->QueryExtOverlapAny(
                structObj.Address.As<PxGeometry>(),
                &t,
                &hit_,
                &filterData,
                filterCallback);
            hit = hit_;
            return hasHit;
        }

        public PhysxBatchQuery CreateBatchQuery(
            PxQueryFilterCallback* queryFilterCallback,
            uint maxRaycastCount,
            uint maxRaycastTouchCount,
            uint maxSweepCount,
            uint maxSweepTouchCount,
            uint maxOverlapCount,
            uint maxOverlapTouchCount)
        {
            var ptr = _scene->PhysPxCreateBatchQueryExt(
                queryFilterCallback,
                maxRaycastCount,
                maxRaycastTouchCount,
                maxSweepCount,
                maxSweepTouchCount,
                maxOverlapCount,
                maxOverlapTouchCount);
            return new PhysxBatchQuery(ptr);
        }

        public PhysxBatchQuery CreateBatchQuery(
            PxQueryFilterCallback* queryFilterCallback,
            PxRaycastBuffer* raycastBuffers,
            uint maxRaycastCount,
            PxRaycastHit* raycastTouches,
            uint maxRaycastTouchCount,
            PxSweepBuffer* sweepBuffers,
            uint maxSweepCount,
            PxSweepHit* sweepTouches,
            uint maxSweepTouchCount,
            PxOverlapBuffer* overlapBuffers,
            uint maxOverlapCount,
            PxOverlapHit* overlapTouches,
            uint maxOverlapTouchCount)
        {
            var ptr = _scene->PhysPxCreateBatchQueryExt1(
                queryFilterCallback,
                raycastBuffers,
                maxRaycastCount,
                raycastTouches,
                maxRaycastTouchCount,
                sweepBuffers,
                maxSweepCount,
                sweepTouches,
                maxSweepTouchCount,
                overlapBuffers,
                maxOverlapCount,
                overlapTouches,
                maxOverlapTouchCount);
            return new PhysxBatchQuery(ptr);
        }

        public override void AddActor(IAbstractPhysicsActor actor)
        {
            if (actor is not PhysxActor physxActor)
                return;
            
            AddActor(physxActor);
        }

        public override void RemoveActor(IAbstractPhysicsActor actor)
        {
            if (actor is not PhysxActor physxActor)
                return;
            
            RemoveActor(physxActor);
        }

        public override void NotifyShapeChanged(IAbstractPhysicsActor actor)
        {
            //RemoveActor(actor);
            //AddActor(actor);
        }

        private static PxFilterData FilterDataFromLayerMask(LayerMask layerMask)
            => PxFilterData_new_2((uint)layerMask.Value, 0u, 0u, 0u);
        private static LayerMask LayerMaskFromFilterData(PxFilterData filterData)
            => new((int)filterData.word0);

        /// <summary>
        /// Gets all filtering-related data for a query.
        /// </summary>
        /// <param name="layerMask"></param>
        /// <param name="filter"></param>
        /// <param name="filterMask"></param>
        /// <param name="filterCallback"></param>
        /// <param name="hitFlags"></param>
        /// <param name="queryFlags"></param>
        /// <param name="sweepInflation"></param>
        private void GetFiltering(
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out PxFilterData filterMask,
            out PxQueryFilterCallback filterCallback,
            out PxHitFlags hitFlags,
            out PxQueryFlags queryFlags,
            out float sweepInflation)
        {
            PxFilterData filterMask2 = FilterDataFromLayerMask(layerMask);
            PxFilterData_setToDefault_mut(&filterMask2);
            filterMask = filterMask2;

            PxQueryHitType PreFilter(PxFilterData* filterData, PxShape* shape, PxRigidActor* actor, PxHitFlags queryFlags)
            {
                if (filter is PhysxQueryFilter physxFilter && physxFilter.PreFilter != null)
                {
                    var geo = GetShape(shape);
                    var rigidActor = PhysxRigidActor.Get(actor);
                    return physxFilter.PreFilter(*filterData, geo, rigidActor, queryFlags);
                }
                return PxQueryHitType.None;
            }

            PxQueryHitType PostFilter(PxFilterData* filterData, PxQueryHit* hit)
            {
                if (filter is PhysxQueryFilter physxFilter && physxFilter.PostFilter != null)
                {
                    return physxFilter.PostFilter(*filterData, *hit);
                }
                return PxQueryHitType.None;
            }

            void Destructor() { }

            filterCallback = new() { vtable_ = PhysxScene.Native.CreateVTable(PreFilter, PostFilter, Destructor) };

            hitFlags = PxHitFlags.Default;
            queryFlags = PxQueryFlags.Static | PxQueryFlags.Dynamic;
            sweepInflation = 0.0f;
            if (filter is PhysxQueryFilter physxFilter)
            {
                hitFlags = physxFilter.HitFlags;
                queryFlags = physxFilter.Flags;
                sweepInflation = physxFilter.SweepInflation;
            }
        }

        public delegate PxQueryHitType DelPreFilter(PxFilterData filterData, PhysxShape? shape, PhysxRigidActor? actor, PxHitFlags queryFlags);
        public delegate PxQueryHitType DelPostFilter(PxFilterData filterData, PxQueryHit hit);

        public struct PhysxQueryFilter : IAbstractQueryFilter
        {
            public DelPreFilter? PreFilter = null;
            public DelPostFilter? PostFilter = null;
            public PxQueryFlags Flags = PxQueryFlags.Static | PxQueryFlags.Dynamic;
            public PxHitFlags HitFlags = PxHitFlags.Default;
            public float SweepInflation = 0.0f;

            public PhysxQueryFilter()
            {

            }
        }

        private static RaycastHit ToRaycastHit(PxRaycastHit hit)
            => new()
            {
                Position = hit.position,
                Normal = hit.normal,
                Distance = hit.distance,
                FaceIndex = hit.faceIndex,
                UV = new Vector2(hit.u, hit.v),
            };

        private static SweepHit ToSweepHit(PxSweepHit hit)
        {
            return new SweepHit
            {
                Position = hit.position,
                Normal = hit.normal,
                Distance = hit.distance,
                FaceIndex = hit.faceIndex,
            };
        }

        private static OverlapHit ToOverlapHit(PxOverlapHit hit)
        {
            return new OverlapHit
            {
                FaceIndex = hit.faceIndex,
            };
        }

        public override bool RaycastAny(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex)
        {
            var start = worldSegment.Start;
            var end = worldSegment.End;
            var distance = worldSegment.Length;
            var unitDir = (end - start).Normalized();

            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out _,
                out PxQueryFlags queryFlags,
                out _);

            return RaycastAny(start, unitDir, distance, out hitFaceIndex, queryFlags, &filterMask, &filterCallback, null);
        }

        public override bool RaycastSingleAsync(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback)
        {
            var start = worldSegment.Start;
            var end = worldSegment.End;
            var distance = worldSegment.Length;
            var unitDir = (end - start).Normalized();

            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out PxHitFlags hitFlags,
                out PxQueryFlags queryFlags,
                out _);

            if (!RaycastSingle(
                start,
                unitDir,
                distance,
                hitFlags,
                out PxRaycastHit hit,
                queryFlags,
                &filterMask,
                &filterCallback,
                null))
                return false;

            PhysxScene.Native.FreeVTable(filterCallback.vtable_);

            PhysxRigidActor? actor = PhysxRigidActor.Get(hit.actor);
            if (actor is null)
                return true;

            XRComponent? component = actor.GetOwningComponent();
            if (component is null)
                return true;

            if (!results.TryGetValue(hit.distance, out var list))
                results.Add(hit.distance, list = []);

            list.Add((component, ToRaycastHit(hit)));
            return true;
        }

        public override bool RaycastMultiple(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            var start = worldSegment.Start;
            var end = worldSegment.End;
            var distance = worldSegment.Length;
            var unitDir = (end - start).Normalized();

            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out PxHitFlags hitFlags,
                out PxQueryFlags queryFlags,
                out _);

            var hits = RaycastMultiple(start, unitDir, distance, hitFlags, out bool blockingHit, queryFlags, &filterMask, &filterCallback, null, 32);

            PhysxScene.Native.FreeVTable(filterCallback.vtable_);

            foreach (var hit in hits)
            {
                PhysxRigidActor? actor = PhysxRigidActor.Get(hit.actor);
                if (actor is null)
                    continue;

                XRComponent? component = actor.GetOwningComponent();
                if (component is null)
                    continue;

                if (!results.TryGetValue(hit.distance, out var list))
                    results.Add(hit.distance, list = []);

                list.Add((component, ToRaycastHit(hit)));
            }
            return hits.Length > 0;
        }

        public override bool SweepAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex)
        {
            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out PxHitFlags hitFlags,
                out PxQueryFlags queryFlags,
                out float infl);

            bool hasHit = SweepAny(
                geometry,
                pose,
                unitDir,
                distance,
                hitFlags,
                out PxQueryHit hit,
                queryFlags,
                &filterMask,
                infl,
                &filterCallback,
                null);

            PhysxScene.Native.FreeVTable(filterCallback.vtable_);

            hitFaceIndex = hit.faceIndex;
            return hasHit;
        }

        public override bool SweepSingle(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out PxHitFlags hitFlags,
                out PxQueryFlags queryFlags,
                out float infl);

            if (SweepSingle(
                geometry,
                pose,
                unitDir,
                distance,
                hitFlags,
                out PxSweepHit hit,
                queryFlags,
                &filterMask,
                infl,
                &filterCallback,
                null))
            {
                AddSweepHit(results, hit);

                PhysxScene.Native.FreeVTable(filterCallback.vtable_);
                return true;
            }
            else
            {
                PhysxScene.Native.FreeVTable(filterCallback.vtable_);
                return false;
            }
        }

        private static void AddSweepHit(
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results,
            PxSweepHit hit)
        {
            PhysxRigidActor? actor = PhysxRigidActor.Get(hit.actor);
            if (actor is null)
                return;

            XRComponent? component = actor.GetOwningComponent();
            //if (component is null)
            //    return;

            if (!results.TryGetValue(hit.distance, out var list))
                results.Add(hit.distance, list = []);

            list.Add((component, ToSweepHit(hit)));
        }

        public override bool SweepMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out PxHitFlags hitFlags,
                out PxQueryFlags queryFlags,
                out float infl);

            var hits = SweepMultiple(
                geometry,
                pose,
                unitDir,
                distance,
                hitFlags,
                out bool blockingHit,
                queryFlags,
                &filterMask,
                infl,
                &filterCallback,
                null);

            PhysxScene.Native.FreeVTable(filterCallback.vtable_);

            foreach (var hit in hits)
            {
                PhysxRigidActor? actor = PhysxRigidActor.Get(hit.actor);
                if (actor is null)
                    continue;

                XRComponent? component = actor.GetOwningComponent();
                if (component is null)
                    continue;

                if (!results.TryGetValue(hit.distance, out var list))
                    results.Add(hit.distance, list = []);

                list.Add((component, ToSweepHit(hit)));
            }
            return hits.Length > 0;
        }

        public override bool OverlapAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out _,
                out PxQueryFlags queryFlags,
                out _);

            bool hasHit = OverlapAny(
                geometry,
                pose,
                out PxOverlapHit hit,
                queryFlags,
                &filterMask,
                &filterCallback);

            PhysxScene.Native.FreeVTable(filterCallback.vtable_);

            if (hasHit)
            {
                PhysxRigidActor? actor = PhysxRigidActor.Get(hit.actor);
                if (actor is null)
                    return false;

                XRComponent? component = actor.GetOwningComponent();
                if (component is null)
                    return false;

                var d = 0.0f;

                if (!results.TryGetValue(d, out var list))
                    results.Add(0.0f, list = []);

                list.Add((component, ToOverlapHit(hit)));
            }
            return hasHit;
        }

        public override bool OverlapMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            GetFiltering(
                layerMask,
                filter,
                out PxFilterData filterMask,
                out PxQueryFilterCallback filterCallback,
                out _,
                out PxQueryFlags queryFlags,
                out _);

            var hits = OverlapMultiple(geometry, pose, queryFlags, &filterMask, &filterCallback, 32);

            PhysxScene.Native.FreeVTable(filterCallback.vtable_);

            foreach (var hit in hits)
            {
                PhysxRigidActor? actor = PhysxRigidActor.Get(hit.actor);
                if (actor is null)
                    continue;

                XRComponent? component = actor.GetOwningComponent();
                if (component is null)
                    continue;

                var d = 0.0f;

                if (!results.TryGetValue(d, out var list))
                    results.Add(0.0f, list = []);

                list.Add((component, ToOverlapHit(hit)));
            }
            return hits.Length > 0;
        }

        public bool VisualizeEnabled
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.Scale, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.Scale) > 0.0f;
        }
        public bool VisualizeWorldAxes
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.WorldAxes, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.WorldAxes) > 0.0f;
        }
        public bool VisualizeBodyAxes
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.BodyAxes, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.BodyAxes) > 0.0f;
        }
        public bool VisualizeBodyMassAxes
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.BodyMassAxes, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.BodyMassAxes) > 0.0f;
        }
        public bool VisualizeBodyLinearVelocity
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.BodyLinVelocity, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.BodyLinVelocity) > 0.0f;
        }
        public bool VisualizeBodyAngularVelocity
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.BodyAngVelocity, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.BodyAngVelocity) > 0.0f;
        }
        public bool VisualizeContactPoint
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.ContactPoint, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.ContactPoint) > 0.0f;
        }
        public bool VisualizeContactNormal
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.ContactNormal, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.ContactNormal) > 0.0f;
        }
        public bool VisualizeContactError
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.ContactError, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.ContactError) > 0.0f;
        }
        public bool VisualizeContactForce
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.ContactForce, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.ContactForce) > 0.0f;
        }
        public bool VisualizeActorAxes
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.ActorAxes, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.ActorAxes) > 0.0f;
        }
        public bool VisualizeCollisionAabbs
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionAabbs, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionAabbs) > 0.0f;
        }
        public bool VisualizeCollisionShapes
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionShapes, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionShapes) > 0.0f;
        }
        public bool VisualizeCollisionAxes
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionAxes, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionAxes) > 0.0f;
        }
        public bool VisualizeCollisionCompounds
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionCompounds, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionCompounds) > 0.0f;
        }
        public bool VisualizeCollisionFaceNormals
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionFnormals, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionFnormals) > 0.0f;
        }
        public bool VisualizeCollisionEdges
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionEdges, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionEdges) > 0.0f;
        }
        public bool VisualizeCollisionStatic
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionStatic, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionStatic) > 0.0f;
        }
        public bool VisualizeCollisionDynamic
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CollisionDynamic, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CollisionDynamic) > 0.0f;
        }
        public bool VisualizeJointLocalFrames
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.JointLocalFrames, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.JointLocalFrames) > 0.0f;
        }
        public bool VisualizeJointLimits
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.JointLimits, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.JointLimits) > 0.0f;
        }
        public bool VisualizeCullBox
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.CullBox, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.CullBox) > 0.0f;
        }
        public bool VisualizeMbpRegions
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.MbpRegions, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.MbpRegions) > 0.0f;
        }
        public bool VisualizeSimulationMesh
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.SimulationMesh, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.SimulationMesh) > 0.0f;
        }
        public bool VisualizeSdf
        {
            set => _scene->SetVisualizationParameterMut(PxVisualizationParameter.Sdf, value ? 1.0f : 0.0f);
            get => _scene->GetVisualizationParameter(PxVisualizationParameter.Sdf) > 0.0f;
        }

        private void LinkVisualizationSettings()
        {
            var s = Engine.Rendering.Settings;
            s.PropertyChanging += Settings_PropertyChanging;
            s.PropertyChanged += Settings_PropertyChanged;
            s.PhysicsVisualizeSettings.PropertyChanged += PhysicsVisualizeSettings_PropertyChanged;
            CopyVisualizeSettings();
        }

        private void UnlinkVisualizationSettings()
        {
            var s = Engine.Rendering.Settings;
            s.PhysicsVisualizeSettings.PropertyChanged -= PhysicsVisualizeSettings_PropertyChanged;
            s.PropertyChanging -= Settings_PropertyChanging;
            s.PropertyChanged -= Settings_PropertyChanged;
        }

        private void Settings_PropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings):
                    Engine.Rendering.Settings.PhysicsVisualizeSettings.PropertyChanged -= PhysicsVisualizeSettings_PropertyChanged;
                    break;
            }
        }

        private void Settings_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings):
                    CopyVisualizeSettings();
                    Engine.Rendering.Settings.PhysicsVisualizeSettings.PropertyChanged += PhysicsVisualizeSettings_PropertyChanged;
                    break;
            }
        }

        private void CopyVisualizeSettings()
        {
            var s = Engine.Rendering.Settings.PhysicsVisualizeSettings;
            VisualizeEnabled = s.VisualizeEnabled;
            VisualizeWorldAxes = s.VisualizeWorldAxes;
            VisualizeBodyAxes = s.VisualizeBodyAxes;
            VisualizeBodyMassAxes = s.VisualizeBodyMassAxes;
            VisualizeBodyLinearVelocity = s.VisualizeBodyLinearVelocity;
            VisualizeBodyAngularVelocity = s.VisualizeBodyAngularVelocity;
            VisualizeContactPoint = s.VisualizeContactPoint;
            VisualizeContactNormal = s.VisualizeContactNormal;
            VisualizeContactError = s.VisualizeContactError;
            VisualizeContactForce = s.VisualizeContactForce;
            VisualizeActorAxes = s.VisualizeActorAxes;
            VisualizeCollisionAabbs = s.VisualizeCollisionAabbs;
            VisualizeCollisionShapes = s.VisualizeCollisionShapes;
            VisualizeCollisionAxes = s.VisualizeCollisionAxes;
            VisualizeCollisionCompounds = s.VisualizeCollisionCompounds;
            VisualizeCollisionFaceNormals = s.VisualizeCollisionFaceNormals;
            VisualizeCollisionEdges = s.VisualizeCollisionEdges;
            VisualizeCollisionStatic = s.VisualizeCollisionStatic;
            VisualizeCollisionDynamic = s.VisualizeCollisionDynamic;
            VisualizeJointLocalFrames = s.VisualizeJointLocalFrames;
            VisualizeJointLimits = s.VisualizeJointLimits;
            VisualizeCullBox = s.VisualizeCullBox;
            VisualizeMbpRegions = s.VisualizeMbpRegions;
            VisualizeSimulationMesh = s.VisualizeSimulationMesh;
            VisualizeSdf = s.VisualizeSdf;
        }

        private void PhysicsVisualizeSettings_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeEnabled):
                    VisualizeEnabled = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeEnabled;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeWorldAxes):
                    VisualizeWorldAxes = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeWorldAxes;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyAxes):
                    VisualizeBodyAxes = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyAxes;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyMassAxes):
                    VisualizeBodyMassAxes = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyMassAxes;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyLinearVelocity):
                    VisualizeBodyLinearVelocity = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyLinearVelocity;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyAngularVelocity):
                    VisualizeBodyAngularVelocity = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeBodyAngularVelocity;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactPoint):
                    VisualizeContactPoint = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactPoint;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactNormal):
                    VisualizeContactNormal = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactNormal;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactError):
                    VisualizeContactError = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactError;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactForce):
                    VisualizeContactForce = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeContactForce;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeActorAxes):
                    VisualizeActorAxes = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeActorAxes;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionAabbs):
                    VisualizeCollisionAabbs = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionAabbs;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionShapes):
                    VisualizeCollisionShapes = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionShapes;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionAxes):
                    VisualizeCollisionAxes = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionAxes;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionCompounds):
                    VisualizeCollisionCompounds = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionCompounds;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionFaceNormals):
                    VisualizeCollisionFaceNormals = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionFaceNormals;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionEdges):
                    VisualizeCollisionEdges = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionEdges;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionStatic):
                    VisualizeCollisionStatic = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionStatic;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionDynamic):
                    VisualizeCollisionDynamic = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCollisionDynamic;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeJointLocalFrames):
                    VisualizeJointLocalFrames = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeJointLocalFrames;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeJointLimits):
                    VisualizeJointLimits = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeJointLimits;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCullBox):
                    VisualizeCullBox = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeCullBox;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeMbpRegions):
                    VisualizeMbpRegions = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeMbpRegions;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeSimulationMesh):
                    VisualizeSimulationMesh = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeSimulationMesh;
                    break;
                case nameof(Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeSdf):
                    VisualizeSdf = Engine.Rendering.Settings.PhysicsVisualizeSettings.VisualizeSdf;
                    break;
            }
        }

        private PxDebugPoint* _debugPoints;
        private PxDebugLine* _debugLines;
        private PxDebugTriangle* _debugTriangles;

        private static ColorF4 ToColorF4(uint c) => new(
            ((c >> 00) & 0xFF) / 255.0f,
            ((c >> 08) & 0xFF) / 255.0f,
            ((c >> 16) & 0xFF) / 255.0f,
            ((c >> 24) & 0xFF) / 255.0f);

        private void PopulateDebugBuffers()
        {
            var rb = RenderBuffer;

            _visualizer.PointCount = rb->GetNbPoints();
            _visualizer.LineCount = rb->GetNbLines();
            _visualizer.TriangleCount = rb->GetNbTriangles();

            _debugPoints = rb->GetPoints();
            _debugLines = rb->GetLines();
            _debugTriangles = rb->GetTriangles();

            _visualizer.PopulateBuffers();
        }

        public override void DebugRender()
            => _visualizer.Render();

        private (Vector3 pos, ColorF4 color) GetPoint(int i)
        {
            var p = _debugPoints[i];
            return ((Vector3)p.pos, ToColorF4(p.color));
        }
        private (Vector3 pos0, Vector3 pos1, ColorF4 color) GetLine(int i)
        {
            var p = _debugLines[i];
            return ((Vector3)p.pos0, (Vector3)p.pos1, ToColorF4(p.color0));
        }
        private (Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color) GetTriangle(int i)
        {
            var p = _debugTriangles[i];
            return ((Vector3)p.pos0, (Vector3)p.pos1, (Vector3)p.pos2, ToColorF4(p.color0));
        }

        private static Queue<(Vector3 delta, float minDist, float elapsedTime)> _moveQueue = new();
        public static void LogControllerMove(Vector3 delta, float minDist, float elapsedTime)
        {
            _moveQueue.Enqueue((delta, minDist, elapsedTime));
            while (_moveQueue.Count > 10)
                _moveQueue.Dequeue();
        }
    }
}