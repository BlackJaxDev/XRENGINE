using MagicPhysX;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class ControllerManager : XRBase
    {
        #region Nested Types

        /// <summary>
        /// Modern .NET 7+ VTable for PxControllerFilterCallback.
        /// MSVC layout: destructor last, filter first.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct PxControllerFilterCallbackVTable
        {
            public delegate* unmanaged[Cdecl]<PxControllerFilterCallback*, PxController*, PxController*, byte> Filter;
            public delegate* unmanaged[Cdecl]<void*, void> Destructor;
        }

        /// <summary>
        /// Modern .NET 7+ VTable for PxQueryFilterCallback.
        /// MSVC layout: postFilter, preFilter, destructor.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct PxQueryFilterCallbackVTable
        {
            public delegate* unmanaged[Cdecl]<PxQueryFilterCallback*, byte*, PxFilterData*, PxQueryHit*, PxShape*, PxRigidActor*, void> PostFilter;
            public delegate* unmanaged[Cdecl]<PxQueryFilterCallback*, byte*, PxFilterData*, PxShape*, PxRigidActor*, PxHitFlags*, void> PreFilter;
            public delegate* unmanaged[Cdecl]<void*, void> Destructor;
        }

        #endregion

        #region Delegate Definitions

        /// <summary>Delegate for CCT-vs-CCT collision filtering.</summary>
        public delegate bool DelFilterControllerCollision(PxController* a, PxController* b);

        /// <summary>Delegate for pre-filter query callback.</summary>
        public delegate PxQueryHitType DelPreFilterCallback(PxFilterData* filterData, PxShape* shape, PxRigidActor* actor, PxHitFlags* queryFlags);

        /// <summary>Delegate for post-filter query callback.</summary>
        public delegate PxQueryHitType DelPostFilterCallback(PxFilterData* filterData, PxQueryHit* hit, PxShape* shape, PxRigidActor* actor);

        // Controller creation callback delegates (retained for public API)
        public delegate void DelOnControllerHit(PxControllersHit* hit);
        public delegate void DelOnShapeHit(PxControllerShapeHit* hit);
        public delegate void DelOnObstacleHit(PxControllerObstacleHit* hit);

        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsShape(PxShape* shape, PxActor* actor);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsObstacle(PxObstacle* obstacle);
        public delegate PxControllerBehaviorFlags DelGetBehaviorFlagsController(PxController* controller);

        #endregion

        #region Static Fields

        /// <summary>Maps native PxControllerFilterCallback pointers back to managed ControllerManager instances.</summary>
        private static readonly ConcurrentDictionary<nint, ControllerManager> _controllerFilterToManager = new();

        /// <summary>Maps native PxQueryFilterCallback pointers back to managed ControllerManager instances.</summary>
        private static readonly ConcurrentDictionary<nint, ControllerManager> _queryFilterToManager = new();

        #endregion

        #region Instance Fields

        // Native interop data sources
        private readonly DataSource _controllerFilterCallbackSource;
        private readonly DataSource _controllerFilterCallbackVTableSource;
        private readonly DataSource _queryFilterCallbackSource;
        private readonly DataSource _queryFilterCallbackVTableSource;
        private readonly DataSource _controllerFiltersSource;
        private readonly DataSource _filterDataSource;

        // Instance filter callbacks
        private DelFilterControllerCollision? _filterControllerCollision;
        private DelPreFilterCallback? _preFilterCallback;
        private DelPostFilterCallback? _postFilterCallback;

        #endregion

        #region Properties

        public PxControllerFilterCallback* ControllerFilterCallback
            => _controllerFilterCallbackSource.ToStructPtr<PxControllerFilterCallback>();

        public PxQueryFilterCallback* QueryFilterCallback
            => _queryFilterCallbackSource.ToStructPtr<PxQueryFilterCallback>();

        public PxControllerFilters* ControllerFilters
            => _controllerFiltersSource.ToStructPtr<PxControllerFilters>();

        public PxFilterData* FilterData
            => _filterDataSource.ToStructPtr<PxFilterData>();

        public PxControllerManager* ControllerManagerPtr { get; }

        public PxObstacleContext* DefaultObstacleContextPtr
            => ControllerManagerPtr->GetNbObstacleContexts() > 0 ? ControllerManagerPtr->GetObstacleContextMut(0) : null;

        public PhysxScene Scene
        {
            get
            {
                var scenePtr = (nint)ControllerManagerPtr->GetScene();
                if (PhysxScene.Scenes.TryGetValue(scenePtr, out var scene))
                    return scene;
                throw new KeyNotFoundException($"PhysxScene not found for PxScene* 0x{scenePtr:X}");
            }
        }

        public ConcurrentDictionary<nint, PhysxController> Controllers { get; } = [];

        public uint ControllerCount => ControllerManagerPtr->GetNbControllers();

        public PxQueryFlags QueryFlags
        {
            get => ControllerFilters->mFilterFlags;
            set => ControllerFilters->mFilterFlags = value;
        }

        /// <summary>
        /// Callback for CCT-vs-CCT collision filtering.
        /// Return true to allow collision, false to ignore.
        /// </summary>
        public DelFilterControllerCollision? FilterControllerCollisionCallback
        {
            get => _filterControllerCollision;
            set => _filterControllerCollision = value;
        }

        /// <summary>
        /// Callback for pre-filter query processing.
        /// Called before shape intersection tests.
        /// </summary>
        public DelPreFilterCallback? PreFilterCallbackDelegate
        {
            get => _preFilterCallback;
            set => _preFilterCallback = value;
        }

        /// <summary>
        /// Callback for post-filter query processing.
        /// Called after shape intersection tests.
        /// </summary>
        public DelPostFilterCallback? PostFilterCallbackDelegate
        {
            get => _postFilterCallback;
            set => _postFilterCallback = value;
        }

        public Dictionary<nint, ObstacleContext> ObstacleContexts { get; } = [];
        public uint ObstacleContextCount => ControllerManagerPtr->GetNbObstacleContexts();

        public PxRenderBuffer* RenderBuffer
            => ControllerManagerPtr->GetRenderBufferMut();

        #endregion

        #region Constructor

        public ControllerManager(PxControllerManager* manager)
        {
            ControllerManagerPtr = manager;

            //Debug.Log(ELogCategory.Physics, "[PhysxObj] + ControllerManager ptr=0x{0:X}", (nint)manager);

            // Create controller filter callback vtable
            _controllerFilterCallbackVTableSource = DataSource.FromStruct(new PxControllerFilterCallbackVTable
            {
                Filter = &FilterControllerCollisionNative,
                Destructor = &ControllerFilterDestructor
            });

            _controllerFilterCallbackSource = DataSource.FromStruct(new PxControllerFilterCallback()
            {
                vtable_ = _controllerFilterCallbackVTableSource.Address
            });

            // Register for native callback lookup
            _controllerFilterToManager[(nint)ControllerFilterCallback] = this;

            // Create query filter callback vtable
            _queryFilterCallbackVTableSource = DataSource.FromStruct(new PxQueryFilterCallbackVTable
            {
                PreFilter = &PreFilterCallbackNative,
                PostFilter = &PostFilterCallbackNative,
                Destructor = &QueryFilterDestructor
            });

            _queryFilterCallbackSource = DataSource.FromStruct(new PxQueryFilterCallback()
            {
                vtable_ = _queryFilterCallbackVTableSource.Address
            });

            // Register for native callback lookup
            _queryFilterToManager[(nint)QueryFilterCallback] = this;

            //CreateObstacleContext();

            PxFilterData filterData = PxFilterData_new_2(0, 0, 0, 0);
            _filterDataSource = DataSource.FromStruct(filterData);

            // IMPORTANT: Pass null callbacks to avoid native AV when PhysX invokes vtable-based callbacks.
            // The smoke test proved that null callbacks + Static|Dynamic flags work reliably.
            // Do NOT use Prefilter/Postfilter flags as they cause PhysX to invoke callbacks.
            var filter = PxControllerFilters_new(FilterData, QueryFilterCallback, ControllerFilterCallback);
            filter.mFilterFlags = PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter | PxQueryFlags.Postfilter;
            _controllerFiltersSource = DataSource.FromStruct(filter);

            //SetTessellation(true, 1.0f);
        }

        #endregion

        #region Controller Creation

        public PhysxController[] GetControllers()
        {
            uint count = ControllerCount;
            var controllers = new List<PhysxController>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var ptr = (nint)ControllerManagerPtr->GetControllerMut(i);
                if (Controllers.TryGetValue(ptr, out var controller))
                    controllers.Add(controller);
            }
            return [.. controllers];
        }
        public PhysxBoxController CreateAABBController(
            Vector3 position,
            Vector3 upDirection,
            float slopeLimit,
            float invisibleWallHeight,
            float maxJumpHeight,
            float contactOffset,
            float stepOffset,
            float density,
            float scaleCoeff,
            float volumeGrowth,
            PxControllerNonWalkableMode nonWalkableMode,
            PhysxMaterial material,
            byte clientID,
            void* userData,
            float halfHeight,
            float halfSideExtent,
            float halfForwardExtent)
        {
            PxBoxControllerDesc* desc = PxBoxControllerDesc_new_alloc();
            //desc->SetToDefaultMut();
            desc->halfHeight = halfHeight;
            desc->halfSideExtent = halfSideExtent;
            desc->halfForwardExtent = halfForwardExtent;
            desc->halfHeight = halfHeight;

            var genericDesc = (PxControllerDesc*)desc;
            var controller = new PhysxBoxController();
            SetGenericControllerParams(
                position,
                upDirection,
                slopeLimit,
                invisibleWallHeight,
                maxJumpHeight,
                contactOffset,
                stepOffset,
                density,
                scaleCoeff,
                volumeGrowth,
                controller.UserControllerHitReport,
                controller.ControllerBehaviorCallback,
                nonWalkableMode,
                material,
                clientID,
                userData,
                genericDesc);

            bool valid = desc->IsValid();
            if (!valid)
                throw new Exception("Invalid box controller description");

            controller.BoxControllerPtr = (PxBoxController*)ControllerManagerPtr->CreateControllerMut(genericDesc);
            controller.Manager = this;
            if (!Controllers.TryAdd((nint)controller.ControllerPtr, controller))
                Debug.Log(ELogCategory.Physics, "[PhysxCache] ! Controllers duplicate key ptr=0x{0:X}", (nint)controller.ControllerPtr);

            //Debug.Log(ELogCategory.Physics, "[PhysxObj] + BoxController ptr=0x{0:X} actor=0x{1:X}", (nint)controller.ControllerPtr, (nint)controller.ControllerPtr->GetActor());
            return controller;
        }

        public PhysxCapsuleController CreateCapsuleController(
            Vector3 position,
            Vector3 upDirection,
            float slopeLimit,
            float invisibleWallHeight,
            float maxJumpHeight,
            float contactOffset,
            float stepOffset,
            float density,
            float scaleCoeff,
            float volumeGrowth,
            PxControllerNonWalkableMode nonWalkableMode,
            PhysxMaterial material,
            byte clientID,
            void* userData,
            float radius,
            float height,
            PxCapsuleClimbingMode climbingMode)
        {
            var controller = new PhysxCapsuleController();

            PxCapsuleControllerDesc* desc = PxCapsuleControllerDesc_new_alloc();
            desc->SetToDefaultMut();
            desc->radius = radius;
            desc->height = height;
            desc->climbingMode = climbingMode;
            
            var genericDesc = (PxControllerDesc*)desc;
            SetGenericControllerParams(
                position,
                upDirection,
                slopeLimit,
                invisibleWallHeight,
                maxJumpHeight,
                contactOffset,
                stepOffset,
                density,
                scaleCoeff,
                volumeGrowth,
                controller.UserControllerHitReport,
                controller.ControllerBehaviorCallback,
                nonWalkableMode,
                material,
                clientID,
                userData,
                genericDesc);

            bool valid = desc->IsValid();
            if (!valid)
                throw new Exception("Invalid capsule controller description");

            controller.CapsuleControllerPtr = (PxCapsuleController*)ControllerManagerPtr->CreateControllerMut(genericDesc);
            controller.Manager = this;
            if (!Controllers.TryAdd((nint)controller.ControllerPtr, controller))
                Debug.Log(ELogCategory.Physics, "[PhysxCache] ! Controllers duplicate key ptr=0x{0:X}", (nint)controller.ControllerPtr);

            //Debug.Log(ELogCategory.Physics, "[PhysxObj] + CapsuleController ptr=0x{0:X} actor=0x{1:X}", (nint)controller.ControllerPtr, (nint)controller.ControllerPtr->GetActor());
            return controller;
        }

        #endregion

        #region Private Methods

        private void SetGenericControllerParams(
            Vector3 position,
            Vector3 upDirection,
            float slopeLimit,
            float invisibleWallHeight,
            float maxJumpHeight,
            float contactOffset,
            float stepOffset,
            float density,
            float scaleCoeff,
            float volumeGrowth,
            PxUserControllerHitReport* reportCallbackHit,
            PxControllerBehaviorCallback* behaviorCallback,
            PxControllerNonWalkableMode nonWalkableMode,
            PhysxMaterial material,
            byte clientID,
            void* userData,
            PxControllerDesc* genericDesc)
        {
            PxExtendedVec3 pos = PxExtendedVec3_new_1(position.X, position.Y, position.Z);
            genericDesc->position = pos;
            genericDesc->upDirection = upDirection;
            genericDesc->slopeLimit = slopeLimit;
            genericDesc->invisibleWallHeight = invisibleWallHeight;
            genericDesc->maxJumpHeight = maxJumpHeight;
            genericDesc->contactOffset = contactOffset;
            genericDesc->stepOffset = stepOffset;
            genericDesc->density = density;
            genericDesc->scaleCoeff = scaleCoeff;
            genericDesc->volumeGrowth = volumeGrowth;
            genericDesc->reportCallback = reportCallbackHit;
            genericDesc->behaviorCallback = behaviorCallback;
            genericDesc->nonWalkableMode = nonWalkableMode;
            genericDesc->material = material.MaterialPtr;
            genericDesc->clientID = clientID;
            genericDesc->userData = userData;
        }

        #endregion

        #region Public Methods

        public void DestroyAllControllers()
        {
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ ControllerManager DestroyAllControllers count={0}", Controllers.Count);
            foreach (var controller in Controllers.Values)
                controller.RequestRelease();
            ControllerManagerPtr->PurgeControllersMut();
            Controllers.Clear();
        }

        public void SetDebugRenderingFlags(PxControllerDebugRenderFlags flags)
            => ControllerManagerPtr->SetDebugRenderingFlagsMut(flags);

        public ObstacleContext[] GetObstacleContexts()
        {
            uint count = ObstacleContextCount;
            ObstacleContext[] contexts = new ObstacleContext[count];
            for (uint i = 0; i < count; i++)
                contexts[i] = ObstacleContexts[(nint)ControllerManagerPtr->GetObstacleContextMut(i)];
            return contexts;
        }
        public ObstacleContext GetObstacleContext(uint index)
            => ObstacleContexts[(nint)ControllerManagerPtr->GetObstacleContextMut(index)];
        public ObstacleContext CreateObstacleContext()
        {
            PxObstacleContext* context = ControllerManagerPtr->CreateObstacleContextMut();
            var obstacleContext = new ObstacleContext(context);
            ObstacleContexts.Add((nint)context, obstacleContext);
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] + ObstacleContext ptr=0x{0:X}", (nint)context);
            return obstacleContext;
        }
        public void ReleaseObstacleContext(ObstacleContext context)
        {
            context.Release();
            ObstacleContexts.Remove((nint)context.ContextPtr);
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] - ObstacleContext ptr=0x{0:X}", (nint)context.ContextPtr);
        }
        public void DestroyAllObstacleContexts()
        {
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ DestroyAllObstacleContexts count={0}", ObstacleContexts.Count);
            foreach (var context in ObstacleContexts.Values)
                context.Release();
            ObstacleContexts.Clear();
        }

        public void ComputeInteractions(float elapsedTime)
            => ControllerManagerPtr->ComputeInteractionsMut(elapsedTime, ControllerFilterCallback);
        public void SetTessellation(bool enabled, float maxEdgeLength)
            => ControllerManagerPtr->SetTessellationMut(enabled, maxEdgeLength);
        public void SetOverlapRecoveryModule(bool enabled)
            => ControllerManagerPtr->SetOverlapRecoveryModuleMut(enabled);
        public void SetPreciseSweeps(bool enabled)
            => ControllerManagerPtr->SetPreciseSweepsMut(enabled);
        public void SetPreventVerticalSlidingAgainstCeiling(bool enabled)
            => ControllerManagerPtr->SetPreventVerticalSlidingAgainstCeilingMut(enabled);

        /// <summary>
        /// Shift the origin of the character controllers and obstacle objects by the specified vector.
        /// The positions of all character controllers, obstacle objects and the corresponding data structures
        /// will get adjusted to reflect the shifted origin location
        /// (the shift vector will get subtracted from all character controller and obstacle object positions).
        /// 
        /// It is the user’s responsibility to keep track of the summed total origin shift 
        /// and adjust all input/output to/from PhysXCharacterKinematic accordingly.
        /// 
        /// This call will not automatically shift the PhysX scene and its objects.
        /// You need to call PxScene::shiftOrigin() seperately to keep the systems in sync.
        /// </summary>
        /// <param name="shift"></param>
        public void ShiftOrigin(Vector3 shift)
        {
            PxVec3 s = shift;
            ControllerManagerPtr->ShiftOriginMut(&s);
        }

        #endregion

        #region Filter Callback Native Entry Points

        /// <summary>
        /// Static native callback for CCT-vs-CCT filtering. Routes to the appropriate managed instance.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static byte FilterControllerCollisionNative(PxControllerFilterCallback* self, PxController* a, PxController* b)
        {
            if (!_controllerFilterToManager.TryGetValue((nint)self, out var manager))
                return 1; // Default: allow collision
            
            // If no callback is set, default to allowing collision (return true)
            bool result = manager.FilterControllerCollisionCallback?.Invoke(a, b) ?? true;
            return result ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Static native callback for controller filter destructor.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ControllerFilterDestructor(void* self)
        {
            _controllerFilterToManager.TryRemove((nint)self, out _);
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ ControllerFilterCallback Destructor ptr=0x{0:X}", (nint)self);
        }

        /// <summary>
        /// Static native callback for pre-filter query. Routes to the appropriate managed instance.
        /// The return value is a hidden param passed by pointer.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void PreFilterCallbackNative(PxQueryFilterCallback* self, byte* retPtr, PxFilterData* filterData, PxShape* shape, PxRigidActor* actor, PxHitFlags* queryFlags)
        {
            PxQueryHitType result = PxQueryHitType.Block;
            if (_queryFilterToManager.TryGetValue((nint)self, out var manager))
                result = manager.PreFilterCallbackDelegate?.Invoke(filterData, shape, actor, queryFlags) ?? PxQueryHitType.Block;

            *retPtr = (byte)result;
        }

        /// <summary>
        /// Static native callback for post-filter query. Routes to the appropriate managed instance.
        /// The return value is a hidden param passed by pointer.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void PostFilterCallbackNative(PxQueryFilterCallback* self, byte* retPtr, PxFilterData* filterData, PxQueryHit* hit, PxShape* shape, PxRigidActor* actor)
        {
            PxQueryHitType result = PxQueryHitType.Block;
            if (_queryFilterToManager.TryGetValue((nint)self, out var manager))
                result = manager.PostFilterCallbackDelegate?.Invoke(filterData, hit, shape, actor) ?? PxQueryHitType.Block;

            *retPtr = (byte)result;
        }

        /// <summary>
        /// Static native callback for query filter destructor.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void QueryFilterDestructor(void* self)
        {
            _queryFilterToManager.TryRemove((nint)self, out _);
            //Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ QueryFilterCallback Destructor ptr=0x{0:X}", (nint)self);
        }

        #endregion
    }
}