using Jitter2;
using MagicPhysX;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine;
using static MagicPhysX.NativeMethods;
using static XREngine.Engine;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe abstract class Controller : XRBase
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

        private void Destructor() { }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelOnControllerHit(PxControllersHit* hit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelOnShapeHit(PxControllerShapeHit* hit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelOnObstacleHit(PxControllerObstacleHit* hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate PxControllerBehaviorFlags DelGetBehaviorFlagsShape(PxShape* shape, PxActor* actor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate PxControllerBehaviorFlags DelGetBehaviorFlagsObstacle(PxObstacle* obstacle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate PxControllerBehaviorFlags DelGetBehaviorFlagsController(PxController* controller);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DelDestructor();

        private readonly DelOnControllerHit? OnControllerHitInstance;
        private readonly DelOnShapeHit? OnShapeHitInstance;
        private readonly DelOnObstacleHit? OnObstacleHitInstance;

        private readonly DelGetBehaviorFlagsShape? GetBehaviorFlagsShapeInstance;
        private readonly DelGetBehaviorFlagsObstacle? GetBehaviorFlagsObstacleInstance;
        private readonly DelGetBehaviorFlagsController? GetBehaviorFlagsControllerInstance;

        private readonly DelDestructor DestructorInstance;

        public Controller()
        {
            OnControllerHitInstance = OnControllerHit;
            OnShapeHitInstance = OnShapeHit;
            OnObstacleHitInstance = OnObstacleHit;

            GetBehaviorFlagsShapeInstance = GetBehaviorFlagsShape;
            GetBehaviorFlagsObstacleInstance = GetBehaviorFlagsObstacle;
            GetBehaviorFlagsControllerInstance = GetBehaviorFlagsController;

            DestructorInstance = Destructor;

            _userControllerHitReportSource = DataSource.FromStruct(new PxUserControllerHitReport()
            {
                vtable_ = PhysxScene.Native.CreateVTable(OnShapeHitInstance, OnControllerHitInstance, OnObstacleHitInstance, DestructorInstance)
            });
            _controllerBehaviorCallbackSource = DataSource.FromStruct(new PxControllerBehaviorCallback()
            {
                vtable_ = PhysxScene.Native.CreateVTable(GetBehaviorFlagsShapeInstance, GetBehaviorFlagsControllerInstance, GetBehaviorFlagsObstacleInstance, DestructorInstance)
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

                // Build filters on-stack exactly like the working smoke test does.
                PxFilterData stackFilterData = PxFilterData_new_2(0, 0, 0, 0);
                // Re-enable only the CCT-vs-CCT filter callback (no query pre/post filters yet).
                // This lets us control whether controllers collide with other controllers.
                PxControllerFilterCallback* cctFilterCallback = mgr.ControllerFilterCallback;
                PxControllerFilters stackFilters = PxControllerFilters_new(&stackFilterData, null, cctFilterCallback);
                stackFilters.mFilterFlags = PxQueryFlags.Static | PxQueryFlags.Dynamic;

                // Pass null for obstacle context like the smoke test initially did
                PxObstacleContext* obstacleContext = null;
                
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
            while (_inputBuffer.TryDequeue(out var input))
                ConsumeMove(input.delta, input.minDist, input.elapsedTime);
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

        public delegate void ControllerHitDelegate(Controller controller, PxControllersHit* hit);
        public delegate void ShapeHitDelegate(Controller controller, PxControllerShapeHit* hit);
        public delegate void ObstacleHitDelegate(Controller controller, PxControllerObstacleHit* hit);

        public event ControllerHitDelegate? ControllerHit;
        public event ShapeHitDelegate? ShapeHit;
        public event ObstacleHitDelegate? ObstacleHit;

        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsShape2(PxShape* shape, PxActor* actor);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsObstacle2(PxObstacle* obstacle);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsController2(PxController* controller);

        public DelGetBehaviorFlagsController2? BehaviorCallbackController;
        public DelGetBehaviorFlagsObstacle2? BehaviorCallbackObstacle;
        public DelGetBehaviorFlagsShape2? BehaviorCallbackShape;
        private bool _collidingSides;
        private bool _collidingUp;
        private bool _collidingDown;

        internal void OnControllerHit(PxControllersHit* hit)
            => ControllerHit?.Invoke(this, hit);
        internal void OnShapeHit(PxControllerShapeHit* hit)
            => ShapeHit?.Invoke(this, hit);
        internal void OnObstacleHit(PxControllerObstacleHit* hit)
            => ObstacleHit?.Invoke(this, hit);

        internal PxControllerBehaviorFlags GetBehaviorFlagsShape(PxShape* shape, PxActor* actor)
            => BehaviorCallbackShape?.Invoke(shape, actor) ?? PxControllerBehaviorFlags.CctCanRideOnObject;
        internal PxControllerBehaviorFlags GetBehaviorFlagsObstacle(PxObstacle* obstacle)
            => BehaviorCallbackObstacle?.Invoke(obstacle) ?? PxControllerBehaviorFlags.CctCanRideOnObject;
        internal PxControllerBehaviorFlags GetBehaviorFlagsController(PxController* controller)
            => BehaviorCallbackController?.Invoke(controller) ?? PxControllerBehaviorFlags.CctCanRideOnObject;
    }
}