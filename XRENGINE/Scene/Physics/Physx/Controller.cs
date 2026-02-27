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
    /// <summary>
    /// Abstract base class for PhysX character controllers (CCT).
    /// Provides thread-safe movement queuing, collision state tracking, and behavior callbacks.
    /// </summary>
    public unsafe abstract class PhysxController : XRBase
    {
        #region Nested Types

        /// <summary>
        /// Modern .NET 7+ VTable for PxUserControllerHitReport.
        /// Note that the order is reversed from the PhysX declaration due to MSVC struct layout, save for the destructor.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct PxUserControllerHitReportVTable
        {
            public delegate* unmanaged[Cdecl]<PxUserControllerHitReport*, PxControllerObstacleHit*, void> OnObstacleHit;
            public delegate* unmanaged[Cdecl]<PxUserControllerHitReport*, PxControllersHit*, void> OnControllerHit;
            public delegate* unmanaged[Cdecl]<PxUserControllerHitReport*, PxControllerShapeHit*, void> OnShapeHit;
            public delegate* unmanaged[Cdecl]<void*, void> Destructor;
        }

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

        #endregion

        #region Delegate Definitions

        // Public event delegates
        public delegate void ControllerHitDelegate(PhysxController controller, PxControllersHit* hit);
        public delegate void ShapeHitDelegate(PhysxController controller, PxControllerShapeHit* hit);
        public delegate void ObstacleHitDelegate(PhysxController controller, PxControllerObstacleHit* hit);

        // Static behavior callback delegates
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsShape2(PxShape* shape, PxActor* actor);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsObstacle2(PxObstacle* obstacle);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsController2(PxController* controller);

        #endregion

        #region Static Fields

        /// <summary>Maps native PxUserControllerHitReport pointers back to managed PhysxController instances.</summary>
        private static readonly ConcurrentDictionary<nint, PhysxController> _hitReportToController = new();

        /// <summary>Maps native PxControllerBehaviorCallback pointers back to managed PhysxController instances.</summary>
        private static readonly ConcurrentDictionary<nint, PhysxController> _behaviorCallbackToController = new();

        #endregion

        #region Instance Fields

        // Release state tracking
        private int _released;
        private int _nativeReleased;

        // Collision state
        private bool _collidingSides;
        private bool _collidingUp;
        private bool _collidingDown;

        // Native interop data sources
        private readonly DataSource _userControllerHitReportSource;
        private readonly DataSource _userControllerHitReportVTableSource;
        private readonly DataSource _controllerBehaviorCallbackSource;
        private readonly DataSource _controllerBehaviorCallbackVTableSource;

        // Movement input queue for thread-safe operation
        private readonly ConcurrentQueue<(Vector3 delta, float minDist, float elapsedTime)> _inputBuffer = new();

        // Behavior callback delegates (per-instance)
        private DelGetBehaviorFlagsShape2? _behaviorCallbackShape;
        private DelGetBehaviorFlagsObstacle2? _behaviorCallbackObstacle;
        private DelGetBehaviorFlagsController2? _behaviorCallbackController;

        #endregion

        #region Abstract Members

        /// <summary>Gets the native PhysX controller pointer.</summary>
        public abstract PxController* ControllerPtr { get; }

        #endregion

        #region Properties

        /// <summary>Gets or sets the controller manager this controller belongs to.</summary>
        internal ControllerManager? Manager { get; set; }

        /// <summary>Returns true if this controller has been released.</summary>
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>Gets the PhysX scene this controller belongs to.</summary>
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

        /// <summary>Gets the native hit report callback structure.</summary>
        public PxUserControllerHitReport* UserControllerHitReport
            => _userControllerHitReportSource.ToStructPtr<PxUserControllerHitReport>();

        /// <summary>Gets the native behavior callback structure.</summary>
        public PxControllerBehaviorCallback* ControllerBehaviorCallback
            => _controllerBehaviorCallbackSource.ToStructPtr<PxControllerBehaviorCallback>();

        /// <summary>Gets the controller type (capsule, box, etc.).</summary>
        public PxControllerShapeType Type => PxController_getType(ControllerPtr);

        /// <summary>Gets the associated rigid body actor, if any.</summary>
        public PhysxDynamicRigidBody? Actor
            => PhysxDynamicRigidBody.AllDynamic.TryGetValue((nint)ControllerPtr->GetActor(), out var value) ? value : null;

        /// <summary>Gets or sets custom user data pointer.</summary>
        public void* UserData
        {
            get => ControllerPtr->GetUserData();
            set
            {
                ControllerPtr->SetUserDataMut(value);
                //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} UserData=0x{1:X}", (nint)ControllerPtr, (nint)value);
            }
        }

        /// <summary>Gets the current controller state including touched shapes, actors, and obstacles.</summary>
        public (
            Vector3 deltaXP,
            PhysxShape? touchedShape,
            PhysxRigidActor? touchedActor,
            uint touchedObstacleHandle,
            PxControllerCollisionFlags collisionFlags,
            bool standOnAnotherCCT,
            bool standOnObstacle,
            bool isMovingUp)
            State
        {
            get
            {
                PxControllerState state;
                ControllerPtr->GetState(&state);
                return (
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

        /// <summary>Gets movement statistics for the controller.</summary>
        public (ushort IterationCount, ushort FullUpdateCount, ushort PartialUpdateCount, ushort TessellationCount) Stats
        {
            get
            {
                PxControllerStats stats;
                ControllerPtr->GetStats(&stats);
                return (stats.nbIterations, stats.nbFullUpdates, stats.nbPartialUpdates, stats.nbTessellation);
            }
        }

        /// <summary>Gets or sets the controller position (center).</summary>
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
                //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} Position={1}", (nint)ControllerPtr, value);
            }
        }

        /// <summary>Gets or sets the foot position of the controller.</summary>
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
                //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} FootPosition={1}", (nint)ControllerPtr, value);
            }
        }

        /// <summary>Gets or sets the up direction for the controller.</summary>
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
                //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} UpDirection={1}", (nint)ControllerPtr, value);
            }
        }

        /// <summary>Gets or sets the maximum slope angle the controller can walk on.</summary>
        public float SlopeLimit
        {
            get => ControllerPtr->GetSlopeLimit();
            set
            {
                ControllerPtr->SetSlopeLimitMut(value);
                //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} SlopeLimit={1}", (nint)ControllerPtr, value);
            }
        }

        /// <summary>Gets or sets the maximum height of obstacles the controller can step over.</summary>
        public float StepOffset
        {
            get => ControllerPtr->GetStepOffset();
            set
            {
                ControllerPtr->SetStepOffsetMut(value);
                //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller ptr=0x{0:X} StepOffset={1}", (nint)ControllerPtr, value);
            }
        }

        /// <summary>Gets or sets the contact offset for collision detection.</summary>
        public float ContactOffset
        {
            get => ControllerPtr->GetContactOffset();
            set => ControllerPtr->SetContactOffsetMut(value);
        }

        /// <summary>Returns true if the controller is colliding on its sides.</summary>
        public bool CollidingSides
        {
            get => _collidingSides;
            private set => SetField(ref _collidingSides, value);
        }

        /// <summary>Returns true if the controller is colliding above.</summary>
        public bool CollidingUp
        {
            get => _collidingUp;
            private set => SetField(ref _collidingUp, value);
        }

        /// <summary>Returns true if the controller is colliding below (standing on something).</summary>
        public bool CollidingDown
        {
            get => _collidingDown;
            private set => SetField(ref _collidingDown, value);
        }

        /// <summary>Callback for shape behavior flags. Invoked during CCT movement.</summary>
        public DelGetBehaviorFlagsShape2? BehaviorCallbackShape
        {
            get => _behaviorCallbackShape;
            set => _behaviorCallbackShape = value;
        }

        /// <summary>Callback for obstacle behavior flags. Invoked during CCT movement.</summary>
        public DelGetBehaviorFlagsObstacle2? BehaviorCallbackObstacle
        {
            get => _behaviorCallbackObstacle;
            set => _behaviorCallbackObstacle = value;
        }

        /// <summary>Callback for controller-vs-controller behavior flags. Invoked during CCT movement.</summary>
        public DelGetBehaviorFlagsController2? BehaviorCallbackController
        {
            get => _behaviorCallbackController;
            set => _behaviorCallbackController = value;
        }

        #endregion

        #region Events

        /// <summary>Raised when this controller collides with another controller.</summary>
        public event ControllerHitDelegate? ControllerHit;

        /// <summary>Raised when this controller collides with a shape.</summary>
        public event ShapeHitDelegate? ShapeHit;

        /// <summary>Raised when this controller collides with an obstacle.</summary>
        public event ObstacleHitDelegate? ObstacleHit;

        #endregion

        #region Constructor

        public PhysxController()
        {
            // PhysX declaration order: shape, controller, obstacle, destructor
            // MSVC declaration order: obstacle, controller, shape, destructor
            _userControllerHitReportVTableSource = DataSource.FromStruct(new PxUserControllerHitReportVTable
            {
                OnShapeHit = &OnShapeHitNative,
                OnControllerHit = &OnControllerHitNative,
                OnObstacleHit = &OnObstacleHitNative,
                Destructor = &HitReportDestructor
            });

            _userControllerHitReportSource = DataSource.FromStruct(new PxUserControllerHitReport()
            {
                vtable_ = _userControllerHitReportVTableSource.Address
            });

            // Register this instance for native callback lookup
            _hitReportToController[(nint)UserControllerHitReport] = this;

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

            // Register this instance for native behavior callback lookup
            _behaviorCallbackToController[(nint)ControllerBehaviorCallback] = this;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Queues a movement request to be processed during the physics simulation step.
        /// This method is thread-safe and avoids native crashes from concurrent PxController::move calls.
        /// </summary>
        /// <param name="delta">The displacement vector to move.</param>
        /// <param name="minDist">Minimum distance threshold for movement.</param>
        /// <param name="elapsedTime">Time elapsed since last move.</param>
        public void Move(Vector3 delta, float minDist, float elapsedTime)
        {
            if (IsReleased)
                return;

            // Native safety: PhysX controllers are not tolerant of NaN/Inf inputs.
            if (!float.IsFinite(delta.X) || 
                !float.IsFinite(delta.Y) || 
                !float.IsFinite(delta.Z) || 
                !float.IsFinite(minDist) || 
                !float.IsFinite(elapsedTime) || 
                elapsedTime <= 0.0f)
                return;
            
            _inputBuffer.Enqueue((delta, minDist, elapsedTime));
        }

        /// <summary>
        /// Consumes all queued movement inputs and applies them in a single move operation.
        /// Should be called from the physics simulation thread.
        /// </summary>
        /// <param name="delta">The simulation delta time (unused, per-input elapsed time is used).</param>
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

        /// <summary>Resizes the controller to the specified height.</summary>
        /// <param name="height">The new height for the controller.</param>
        public void Resize(float height)
            => ControllerPtr->ResizeMut(height);

        /// <summary>Invalidates the internal obstacle cache.</summary>
        public void InvalidateCache()
            => ControllerPtr->InvalidateCacheMut();

        /// <summary>Releases the controller. Alias for RequestRelease().</summary>
        public void Release()
            => RequestRelease();

        /// <summary>
        /// Marks the controller for release and schedules native release on the physics step.
        /// Safe to call from any thread.
        /// </summary>
        public void RequestRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller RequestRelease ptr=0x{0:X}", (nint)ControllerPtr);

            // Clear the input buffer
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

        #endregion

        #region Internal Methods

        /// <summary>
        /// Performs the actual native PxController release. Intended to be called from PhysxScene.StepSimulation.
        /// </summary>
        internal void ReleaseNativeNow()
        {
            if (Interlocked.Exchange(ref _nativeReleased, 1) != 0)
                return;

            //Debug.Log(ELogCategory.Physics, "[PhysxObj] - Controller ReleaseNativeNow ptr=0x{0:X}", (nint)ControllerPtr);

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

        #endregion

        #region Private Methods

        private void ConsumeMove(Vector3 delta, float minDist, float elapsedTime)
        {
            if (IsReleased)
                return;

            // Native safety: PhysX controllers are not tolerant of NaN/Inf inputs.
            if (!float.IsFinite(delta.X) || 
                !float.IsFinite(delta.Y) || 
                !float.IsFinite(delta.Z) || 
                !float.IsFinite(minDist) || 
                !float.IsFinite(elapsedTime) || elapsedTime <= 0.0f)
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

                // Test all callbacks (retained for debugging)
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

        #endregion

        #region Hit Report Native Entry Points

        /// <summary>
        /// Static native callback for shape hits. Routes to the appropriate managed instance.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnShapeHitNative(PxUserControllerHitReport* self, PxControllerShapeHit* hit)
        {
            if (_hitReportToController.TryGetValue((nint)self, out var controller))
                controller.ShapeHit?.Invoke(controller, hit);
        }

        /// <summary>
        /// Static native callback for controller-vs-controller hits. Routes to the appropriate managed instance.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnControllerHitNative(PxUserControllerHitReport* self, PxControllersHit* hit)
        {
            if (_hitReportToController.TryGetValue((nint)self, out var controller))
                controller.ControllerHit?.Invoke(controller, hit);
        }

        /// <summary>
        /// Static native callback for obstacle hits. Routes to the appropriate managed instance.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnObstacleHitNative(PxUserControllerHitReport* self, PxControllerObstacleHit* hit)
        {
            if (_hitReportToController.TryGetValue((nint)self, out var controller))
                controller.ObstacleHit?.Invoke(controller, hit);
        }

        /// <summary>
        /// Static native callback for hit report destructor.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void HitReportDestructor(void* self)
        {
            _hitReportToController.TryRemove((nint)self, out _);
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller HitReport Destructor ptr=0x{0:X}", (nint)self);
        }

        #endregion

        #region Behavior Callback Native Entry Points

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void BehaviorCallbackDestructor(void* self)
        {
            _behaviorCallbackToController.TryRemove((nint)self, out _);
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ Controller BehaviorCallback Destructor ptr=0x{0:X}", (nint)self);
        }

        /// <summary>
        /// .NET 7+ declaration for native callback so we don't need to allocate delegates - must be static.
        /// The return value is a hidden param passed by pointer.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void GetBehaviorFlagsShape(PxControllerBehaviorCallback* self, byte* retPtr, PxShape* shape, PxActor* actor)
        {
            //Debug.Out("[PhysxCCT] GetBehaviorFlagsShape called for shape=0x{0:X} and actor=0x{1:X}", (nint)shape, (nint)actor);

/*
            if (PhysxShape.All.TryGetValue((nint)shape, out var physxShape))
                Debug.Out("[PhysxCCT]   Shape is managed PhysxShape name='{0}' ptr=0x{1:X}", physxShape.Name, (nint)shape);
            else
                Debug.Out("[PhysxCCT]   Shape is unmanaged ptr=0x{0:X}", (nint)shape);

            if (PhysxActor.AllActors.TryGetValue((nint)actor, out var physxActor))
                Debug.Out("[PhysxCCT]   Actor is managed PhysxActor name='{0}' ptr=0x{1:X}", physxActor.Name, (nint)actor);
            else
                Debug.Out("[PhysxCCT]   Actor is unmanaged ptr=0x{0:X}", (nint)actor);
*/

            try
            {
                PxControllerBehaviorFlags flags = PxControllerBehaviorFlags.CctSlide;
                if (_behaviorCallbackToController.TryGetValue((nint)self, out var controller))
                    flags = controller.BehaviorCallbackShape?.Invoke(shape, actor) ?? PxControllerBehaviorFlags.CctSlide;

                //Debug.Out("[PhysxCCT]   Returning flags: 0x{0:X}", (byte)flags);
                *retPtr = (byte)flags;
            }
            catch (Exception ex)
            {
                Debug.PhysicsException(ex, "[PhysxCCT] Exception in shape callback.");
                *retPtr = (byte)PxControllerBehaviorFlags.CctSlide;
            }
        }

        /// <summary>
        /// .NET 7+ declaration for native callback so we don't need to allocate delegates - must be static.
        /// The return value is a hidden param passed by pointer.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void GetBehaviorFlagsObstacle(PxControllerBehaviorCallback* self, byte* retPtr, PxObstacle* obstacle)
        {
            //Debug.Out("[PhysxCCT] GetBehaviorFlagsObstacle called for obstacle=0x{0:X}", (nint)obstacle);

            try
            {
                PxControllerBehaviorFlags flags = PxControllerBehaviorFlags.CctSlide;
                if (_behaviorCallbackToController.TryGetValue((nint)self, out var controller))
                    flags = controller.BehaviorCallbackObstacle?.Invoke(obstacle) ?? PxControllerBehaviorFlags.CctSlide;

                //Debug.Out("[PhysxCCT]   Returning flags: 0x{0:X}", (byte)flags);
                *retPtr = (byte)flags;
            }
            catch (Exception ex)
            {
                Debug.PhysicsException(ex, "[PhysxCCT] Exception in obstacle callback.");
                *retPtr = (byte)PxControllerBehaviorFlags.CctSlide;
            }
        }

        /// <summary>
        /// .NET 7+ declaration for native callback so we don't need to allocate delegates - must be static.
        /// The return value is a hidden param passed by pointer.
        /// Note that CctCanRideOnObject is not supported for CCT-vs-CCT interactions; use CctUserDefinedRide instead.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void GetBehaviorFlagsController(PxControllerBehaviorCallback* self, byte* retPtr, PxController* controller)
        {
            //Debug.Out("[PhysxCCT] GetBehaviorFlagsController called for controller ptr=0x{0:X}", (nint)controller);

            try
            {
                PxControllerBehaviorFlags flags = PxControllerBehaviorFlags.CctSlide;
                if (_behaviorCallbackToController.TryGetValue((nint)self, out var managedController))
                {
                    flags = managedController.BehaviorCallbackController?.Invoke(controller) ?? PxControllerBehaviorFlags.CctSlide;
                    // PhysX note: eCCT_CAN_RIDE_ON_OBJECT is not supported for the CCT-vs-CCT case.
                    // Returning it can trip asserts / lead to undefined behavior, so always strip it.
                    flags &= ~PxControllerBehaviorFlags.CctCanRideOnObject;
                }

                //Debug.Out("[PhysxCCT]   Returning flags: 0x{0:X}", (byte)flags);
                *retPtr = (byte)flags;
            }
            catch (Exception ex)
            {
                Debug.PhysicsException(ex, "[PhysxCCT] Exception in controller callback.");
                *retPtr = (byte)PxControllerBehaviorFlags.CctSlide;
            }
        }

        #endregion
    }
}