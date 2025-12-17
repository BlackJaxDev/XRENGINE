using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;
using static MagicPhysX.NativeMethods;
using static XREngine.Engine;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe abstract class PhysxController : XRBase
    {
        public abstract PxController* ControllerPtr { get; }

        internal ControllerManager? Manager { get; set; }

        private int _released;
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        private int _nativeReleased;

        private readonly object _nativeCallLock = new();

        private bool _hasLoggedMoveDebug;

        public PhysxScene Scene
        {
            get
            {
                // Prefer the managed back-reference; querying the native pointer after release can crash.
                if (Manager is not null)
                    return Manager.Scene;
                var scenePtr = (nint)ControllerPtr->GetSceneMut();
                if (PhysxScene.Scenes.TryGetValue(scenePtr, out var scene))
                    return scene;
                throw new KeyNotFoundException($"PhysxScene not found for PxScene* 0x{scenePtr:X}");
            }
        }

        public PxUserControllerHitReport* UserControllerHitReport
            => _userControllerHitReportSource.ToStructPtr<PxUserControllerHitReport>();
        public PxControllerBehaviorCallback* ControllerBehaviorCallback
            => _controllerBehaviorCallbackSource.ToStructPtr<PxControllerBehaviorCallback>();

        private readonly DataSource _userControllerHitReportSource;
        private readonly DataSource _controllerBehaviorCallbackSource;
        private readonly DataSource _controllerBehaviorCallbackVTableSource;

        private static void HitReportDestructor(void* self)
        {
            Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller HitReport Destructor ptr=0x{0:X}", (nint)self);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void BehaviorCallbackDestructor(void* self)
        {
            Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller BehaviorCallback Destructor ptr=0x{0:X}", (nint)self);
        }

        // Legacy delegate signatures (not used by the current vtable wiring)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelOnControllerHit(void* self, PxControllersHit* hit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelOnShapeHit(void* self, PxControllerShapeHit* hit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelOnObstacleHit(void* self, PxControllerObstacleHit* hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        delegate PxControllerBehaviorFlags DelGetBehaviorFlagsShape(PxControllerBehaviorCallback* self, PxShape* shape, PxActor* actor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        delegate PxControllerBehaviorFlags DelGetBehaviorFlagsObstacle(PxControllerBehaviorCallback* self, PxObstacle* obstacle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        delegate PxControllerBehaviorFlags DelGetBehaviorFlagsController(PxControllerBehaviorCallback* self, PxController* controller);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelDestructor(void* self);

        // Instance delegates used for vtables (managed thunks -> unmanaged wrappers)
        private readonly DelOnControllerHit? OnControllerHitInstance;
        private readonly DelOnShapeHit? OnShapeHitInstance;
        private readonly DelOnObstacleHit? OnObstacleHitInstance;

        private readonly DelDestructor HitReportDestructorInstance = HitReportDestructor;

        /// <summary>
        /// Modern .NET 7+ VTable for PxControllerBehaviorCallback.
        /// Note that the order is reversed from the PhysX declaration due to MSVC struct layout, save for the destructor.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct PxControllerBehaviorCallbackVTable
        {
            public delegate* unmanaged[Cdecl]<PxControllerBehaviorCallback*, byte*, PxObstacle*, void> GetBehaviorFlagsObstacle;
            public delegate* unmanaged[Cdecl]<PxControllerBehaviorCallback*, byte*, PxController*, void> GetBehaviorFlagsController;
            public delegate* unmanaged[Cdecl]<PxControllerBehaviorCallback*, byte*, PxShape*, PxActor*, void> GetBehaviorFlagsShape;
            public delegate* unmanaged[Cdecl]<void*, void> Destructor;
        }
        public PhysxController()
        {
            // Allocate instance delegates to keep the GC alive and use managed thunks for the vtable entries.
            OnControllerHitInstance = OnControllerHit;
            OnShapeHitInstance = OnShapeHit;
            OnObstacleHitInstance = OnObstacleHit;

            // PhysX spec order: shape, controller, obstacle, destructor.
            _userControllerHitReportSource = DataSource.FromStruct(new PxUserControllerHitReport()
            {
                vtable_ = PhysxScene.Native.CreateVTable(
                    OnShapeHitInstance,
                    OnControllerHitInstance,
                    OnObstacleHitInstance,
                    HitReportDestructorInstance)
            });

            // PhysX declaration order: shape, controller, obstacle, destructor
            // MSVC declaration order: obstacle, controller, shape, destructor
            _controllerBehaviorCallbackVTableSource = DataSource.FromStruct(new PxControllerBehaviorCallbackVTable
            {
                GetBehaviorFlagsShape = &GetBehaviorFlagsShape,
                GetBehaviorFlagsController = &GetBehaviorFlagsController,
                GetBehaviorFlagsObstacle = &GetBehaviorFlagsObstacle,
                Destructor = &BehaviorCallbackDestructor
            });

            _controllerBehaviorCallbackSource = DataSource.FromStruct(new PxControllerBehaviorCallback()
            {
                vtable_ = _controllerBehaviorCallbackVTableSource.Address
            });
        }

        public void* UserData
        {
            get => ControllerPtr->GetUserData();
            set
            {
                ControllerPtr->SetUserDataMut(value);
                Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} UserData=0x{1:X}", (nint)ControllerPtr, (nint)value);
            }
        }

        public (Vector3 deltaXP, PhysxShape? touchedShape, PhysxRigidActor? touchedActor, uint touchedObstacleHandle, PxControllerCollisionFlags collisionFlags, bool standOnAnotherCCT, bool standOnObstacle, bool isMovingUp) State
        {
            get
            {
                PxControllerState state;
                ControllerPtr->GetState(&state);
                return(
                    state.deltaXP,
                    PhysxShape.All.TryGetValue((nint)state.touchedShape, out var shape) ? shape : null,
                    PhysxRigidActor.AllRigidActors.TryGetValue((nint)state.touchedActor, out var actor) ? actor : null,
                    state.touchedObstacleHandle,
                    (PxControllerCollisionFlags)state.collisionFlags,
                    state.standOnAnotherCCT,
                    state.standOnObstacle,
                    state.isMovingUp);
            }
        }
        public (ushort IterationCount, ushort FullUpdateCount, ushort PartialUpdateCount, ushort TessellationCount) Stats
        {
            get
            {
                PxControllerStats stats;
                ControllerPtr->GetStats(&stats);
                return (stats.nbIterations, stats.nbFullUpdates, stats.nbPartialUpdates, stats.nbTessellation);
            }
        }

        public Vector3 Position
        {
            get
            {
                PxExtendedVec3* pos = ControllerPtr->GetPosition();
                return new Vector3((float)pos->x, (float)pos->y, (float)pos->z);
            }
            set
            {
                PxExtendedVec3 pos = PxExtendedVec3_new_1(value.X, value.Y, value.Z);
                ControllerPtr->SetPositionMut(&pos);
                Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} Position={1}", (nint)ControllerPtr, value);
            }
        }

        public Vector3 FootPosition
        {
            get
            {
                PxExtendedVec3 pos = ControllerPtr->GetFootPosition();
                return new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            }
            set
            {
                PxExtendedVec3 pos = PxExtendedVec3_new_1(value.X, value.Y, value.Z);
                ControllerPtr->SetFootPositionMut(&pos);
                Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} FootPosition={1}", (nint)ControllerPtr, value);
            }
        }

        public Vector3 UpDirection
        {
            get
            {
                PxVec3 up = ControllerPtr->GetUpDirection();
                return new Vector3(up.x, up.y, up.z);
            }
            set
            {
                PxVec3 up = PxVec3_new_3(value.X, value.Y, value.Z);
                ControllerPtr->SetUpDirectionMut(&up);
                Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} UpDirection={1}", (nint)ControllerPtr, value);
            }
        }

        public float SlopeLimit
        {
            get => ControllerPtr->GetSlopeLimit();
            set
            {
                ControllerPtr->SetSlopeLimitMut(value);
                Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} SlopeLimit={1}", (nint)ControllerPtr, value);
            }
        }

        public float StepOffset
        {
            get => ControllerPtr->GetStepOffset();
            set
            {
                ControllerPtr->SetStepOffsetMut(value);
                Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} StepOffset={1}", (nint)ControllerPtr, value);
            }
        }

        public PhysxDynamicRigidBody? Actor => PhysxDynamicRigidBody.AllDynamic.TryGetValue((nint)ControllerPtr->GetActor(), out var value) ? value : null;

        public bool CollidingSides
        {
            get => _collidingSides;
            private set => SetField(ref _collidingSides, value);
        }
        public bool CollidingUp
        {
            get => _collidingUp;
            private set => SetField(ref _collidingUp, value);
        }
        public bool CollidingDown
        {
            get => _collidingDown;
            private set => SetField(ref _collidingDown, value);
        }

        public ConcurrentQueue<(Vector3 delta, float minDist, float elapsedTime)> _inputBuffer = new();

        // Queue movement so it runs during PhysxScene.StepSimulation (before simulate) instead of
        // from arbitrary tick threads. This avoids native stalls/crashes from calling PxController::move
        // concurrently with other scene/controller operations (PhysX scene/controller locking).
        public void Move(Vector3 delta, float minDist, float elapsedTime)
        {
            if (IsReleased)
                return;

            // Native safety: PhysX controllers are not tolerant of NaN/Inf inputs.
            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
                return;
            if (!float.IsFinite(minDist) || !float.IsFinite(elapsedTime) || elapsedTime <= 0.0f)
                return;

            _inputBuffer.Enqueue((delta, minDist, elapsedTime));
        }

        private void ConsumeMove(Vector3 delta, float minDist, float elapsedTime)
        {
            if (IsReleased)
                return;

            // Native safety: PhysX controllers are not tolerant of NaN/Inf inputs.
            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
                return;
            if (!float.IsFinite(minDist) || !float.IsFinite(elapsedTime) || elapsedTime <= 0.0f)
                return;

            PxVec3 d = PxVec3_new_3(delta.X, delta.Y, delta.Z);
            //lock (_nativeCallLock)
            {
                if (IsReleased)
                    return;

                // IMPORTANT: In our MagicPhysX/PhysX build, passing null PxControllerFilters can AV inside PxController::move.
                // Use the manager-provided filters (and an obstacle context if present) for stability.
                var mgr = Manager;
                if (mgr is null)
                    return;

                // Obstacles are optional. Only pass an obstacle context to PhysX when it actually contains
                // obstacles; otherwise avoid the obstacle codepath entirely.
                PxObstacleContext* obstacleContext = mgr.DefaultObstacleContextPtr;

                // PhysX caches touched obstacles. If the obstacle collection changes without cache invalidation,
                // the CCT can later dereference stale obstacle pointers.
                if (obstacleContext != null)
                    ControllerPtr->InvalidateCacheMut();

                //test all callbacks
                /*
                Debug.Out("[PhysxCCT] Testing GetBehaviorFlags actor-shape callback");
                PxControllerBehaviorFlags flag1 = PxControllerBehaviorCallback_getBehaviorFlags_mut(ControllerBehaviorCallback, (PxShape*)0xF00D, (PxActor*)0xFEED);
                Debug.Out("[PhysxCCT]   Returned flags: 0x{0:X}", (byte)flag1);

                Debug.Out("[PhysxCCT] Testing GetBehaviorFlags controller callback");
                PxControllerBehaviorFlags flag2 = PxControllerBehaviorCallback_getBehaviorFlags_mut_1(ControllerBehaviorCallback, (PxController*)0xF00D);
                Debug.Out("[PhysxCCT]   Returned flags: 0x{0:X}", (byte)flag2);

                Debug.Out("[PhysxCCT] Testing GetBehaviorFlags obstacle callback");
                PxControllerBehaviorFlags flag3 = PxControllerBehaviorCallback_getBehaviorFlags_mut_2(ControllerBehaviorCallback, (PxObstacle*)0xF00D);
                Debug.Out("[PhysxCCT]   Returned flags: 0x{0:X}", (byte)flag3);
                */

                PxControllerFilters stackFilters = *mgr.ControllerFilters;
                PxControllerCollisionFlags flags = ControllerPtr->MoveMut(&d, minDist, elapsedTime, &stackFilters, obstacleContext);
                CollidingSides = (flags & PxControllerCollisionFlags.CollisionSides) != 0;
                CollidingUp = (flags & PxControllerCollisionFlags.CollisionUp) != 0;
                CollidingDown = (flags & PxControllerCollisionFlags.CollisionDown) != 0;
            }
        }

        public void ConsumeInputBuffer(float delta)
        {
            if (IsReleased)
                return;

            if (_inputBuffer.IsEmpty)
                return;

            // Consume queued moves on the physics thread (PhysxScene.StepSimulation).
            // We keep per-call elapsedTime to match the producer tick's integration.
            float totalElapsed = 0.0f;
            Vector3 totalMove = Vector3.Zero;
            while (_inputBuffer.TryDequeue(out var input))
            {
                totalElapsed += input.elapsedTime;
                totalMove += input.delta;
            }
            ConsumeMove(totalMove, 0.001f, totalElapsed);
        }

        public void Resize(float height)
            => ControllerPtr->ResizeMut(height);

        public float ContactOffset
        {
            get => ControllerPtr->GetContactOffset();
            set => ControllerPtr->SetContactOffsetMut(value);
        }

        public void InvalidateCache()
            => ControllerPtr->InvalidateCacheMut();

        public void Release()
        {
            RequestRelease();
        }

        /// <summary>
        /// Marks the controller for release and schedules native release on the physics step.
        /// Safe to call from any thread.
        /// </summary>
        public void RequestRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller RequestRelease ptr=0x{0:X}", (nint)ControllerPtr);

            while (_inputBuffer.TryDequeue(out _)) { }

            // Prefer deferring to the physics thread.
            var scene = Manager?.Scene;
            if (scene is not null)
            {
                scene.QueueControllerRelease(this);
                return;
            }

            // Fallback: no managed scene available (shutdown path). Release immediately.
            ReleaseNativeNow();
        }

        /// <summary>
        /// Performs the actual native PxController release. Intended to be called from PhysxScene.StepSimulation.
        /// </summary>
        internal void ReleaseNativeNow()
        {
            if (Interlocked.Exchange(ref _nativeReleased, 1) != 0)
                return;

            Debug.Log(ELogCategory.Physics, "[PhysxObj] - Controller ReleaseNativeNow ptr=0x{0:X}", (nint)ControllerPtr);

            //lock (_nativeCallLock)
            {
                // Remove from the manager without querying native state.
                if (Manager is not null)
                    Manager.Controllers.TryRemove((nint)ControllerPtr, out _);
                else
                    Scene.GetOrCreateControllerManager().Controllers.TryRemove((nint)ControllerPtr, out _);

                ControllerPtr->ReleaseMut();
            }
        }

        public PxControllerShapeType Type => PxController_getType(ControllerPtr);

        public delegate void ControllerHitDelegate(PhysxController controller, PxControllersHit* hit);
        public delegate void ShapeHitDelegate(PhysxController controller, PxControllerShapeHit* hit);
        public delegate void ObstacleHitDelegate(PhysxController controller, PxControllerObstacleHit* hit);

        public event ControllerHitDelegate? ControllerHit;
        public event ShapeHitDelegate? ShapeHit;
        public event ObstacleHitDelegate? ObstacleHit;

        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsShape2(PxShape* shape, PxActor* actor);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsObstacle2(PxObstacle* obstacle);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsController2(PxController* controller);

        public static DelGetBehaviorFlagsController2? BehaviorCallbackController;
        public static DelGetBehaviorFlagsObstacle2? BehaviorCallbackObstacle;
        public static DelGetBehaviorFlagsShape2? BehaviorCallbackShape;
        private bool _collidingSides;
        private bool _collidingUp;
        private bool _collidingDown;

        internal void OnControllerHit(void* self, PxControllersHit* hit)
            => ControllerHit?.Invoke(this, hit);
        internal void OnShapeHit(void* self, PxControllerShapeHit* hit)
            => ShapeHit?.Invoke(this, hit);
        internal void OnObstacleHit(void* self, PxControllerObstacleHit* hit)
            => ObstacleHit?.Invoke(this, hit);

        /// <summary>
        /// .NET 7+ declaration for native callback so we don't need to allocate delegates - must be static.
        /// The return value is a hidden param passed by pointer.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="retPtr"></param>
        /// <param name="shape"></param>
        /// <param name="actor"></param>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void GetBehaviorFlagsShape(PxControllerBehaviorCallback* self, byte* retPtr, PxShape* shape, PxActor* actor)
        {
            Debug.Out("[PhysxCCT] GetBehaviorFlagsShape called for shape=0x{0:X} and actor=0x{1:X}", (nint)shape, (nint)actor);

            if (PhysxShape.All.TryGetValue((nint)shape, out var physxShape))
            {
                Debug.Out("[PhysxCCT]   Shape is managed PhysxShape name='{0}' ptr=0x{1:X}", physxShape.Name, (nint)shape);
            }
            else
            {
                Debug.Out("[PhysxCCT]   Shape is unmanaged ptr=0x{0:X}", (nint)shape);
            }
            if (PhysxActor.AllActors.TryGetValue((nint)actor, out var physxActor))
            {
                Debug.Out("[PhysxCCT]   Actor is managed PhysxActor name='{0}' ptr=0x{1:X}", physxActor.Name, (nint)actor);
            }
            else
            {
                Debug.Out("[PhysxCCT]   Actor is unmanaged ptr=0x{0:X}", (nint)actor);
            }

            try
            {
                var flags = BehaviorCallbackShape?.Invoke(shape, actor) ?? PxControllerBehaviorFlags.CctSlide;
                Debug.Out("[PhysxCCT]   Returning flags: 0x{0:X}", (byte)flags);
                *retPtr = (byte)flags;
            }
            catch (Exception ex)
            {
                Debug.Out("[PhysxCCT]   Exception in shape callback: {0}", ex.Message);
                *retPtr = (byte)PxControllerBehaviorFlags.CctSlide;
            }
        }

        /// <summary>
        /// .NET 7+ declaration for native callback so we don't need to allocate delegates - must be static.
        /// The return value is a hidden param passed by pointer.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="retPtr"></param>
        /// <param name="obstacle"></param>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void GetBehaviorFlagsObstacle(PxControllerBehaviorCallback* self, byte* retPtr, PxObstacle* obstacle)
        {
            Debug.Out("[PhysxCCT] GetBehaviorFlagsObstacle called for obstacle=0x{0:X}", (nint)obstacle);
            
            try
            {
                var flags = BehaviorCallbackObstacle?.Invoke(obstacle) ?? PxControllerBehaviorFlags.CctSlide;
                Debug.Out("[PhysxCCT]   Returning flags: 0x{0:X}", (byte)flags);
                *retPtr = (byte)flags;
            }
            catch (Exception ex)
            {
                Debug.Out("[PhysxCCT]   Exception in obstacle callback: {0}", ex.Message);
                *retPtr = (byte)PxControllerBehaviorFlags.CctSlide;
            }
        }

        /// <summary>
        /// .NET 7+ declaration for native callback so we don't need to allocate delegates - must be static.
        /// The return value is a hidden param passed by pointer.
        /// Note that CctCanRideOnObject is not supported for CCT-vs-CCT interactions; use CctUserDefinedRide instead.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="retPtr"></param>
        /// <param name="controller"></param>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void GetBehaviorFlagsController(PxControllerBehaviorCallback* self, byte* retPtr, PxController* controller)
        {
            Debug.Out("[PhysxCCT] GetBehaviorFlagsController called for controller ptr=0x{0:X}", (nint)controller);
            try
            {
                // PhysX note: eCCT_CAN_RIDE_ON_OBJECT is not supported for the CCT-vs-CCT case.
                // Returning it can trip asserts / lead to undefined behavior, so always strip it.
                var flags = BehaviorCallbackController?.Invoke(controller) ?? PxControllerBehaviorFlags.CctSlide;
                flags &= ~PxControllerBehaviorFlags.CctCanRideOnObject;

                Debug.Out("[PhysxCCT]   Returning flags: 0x{0:X}", (byte)flags);
                *retPtr = (byte)flags;
            }
            catch (Exception ex)
            {
                Debug.Out("[PhysxCCT]   Exception in controller callback: {0}", ex.Message);
                *retPtr = (byte)PxControllerBehaviorFlags.CctSlide;
            }
        }
    }
}