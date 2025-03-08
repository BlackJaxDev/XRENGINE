using MagicPhysX;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe abstract class Controller : XRBase
    {
        public abstract PxController* ControllerPtr { get; }

        public PhysxScene Scene => PhysxScene.Scenes[(nint)ControllerPtr->GetSceneMut()];

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
            set => ControllerPtr->SetUserDataMut(value);
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
            }
        }

        public float SlopeLimit
        {
            get => ControllerPtr->GetSlopeLimit();
            set => ControllerPtr->SetSlopeLimitMut(value);
        }

        public float StepOffset
        {
            get => ControllerPtr->GetStepOffset();
            set => ControllerPtr->SetStepOffsetMut(value);
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

        public void Move(Vector3 delta, float minDist, float elapsedTime, PxControllerFilters* filters, PxObstacleContext* obstacles)
        {
            PxVec3 d = PxVec3_new_3(delta.X, delta.Y, delta.Z);
            PxControllerCollisionFlags flags = ControllerPtr->MoveMut(&d, minDist, elapsedTime, filters, null);
            CollidingSides = (flags & PxControllerCollisionFlags.CollisionSides) != 0;
            CollidingUp = (flags & PxControllerCollisionFlags.CollisionUp) != 0;
            CollidingDown = (flags & PxControllerCollisionFlags.CollisionDown) != 0;
            //if (CollidingDown)
            //    Debug.Out("Colliding Down");
            //if (CollidingUp)
            //    Debug.Out("Colliding Up");
            //if (CollidingSides)
            //    Debug.Out("Colliding Sides");
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
            Scene.CreateOrCreateControllerManager().Controllers.Remove((nint)ControllerPtr);
            ControllerPtr->ReleaseMut();
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