using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MagicPhysX;
using XREngine.Data.Tools;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    /// <summary>
    /// Utility methods for converting CoACD convex hull output into PhysX convex meshes.
    /// </summary>
    public static unsafe class PhysxConvexHullCooker
    {
        private static readonly object _initLock = new();
        private static bool _initialized;
        private static PxCookingParams _baseCookingParams;

        public static void Initialize(PxPhysics* physics)
        {
            ArgumentNullException.ThrowIfNull(physics);
            if (_initialized)
                return;

            lock (_initLock)
            {
                if (_initialized)
                    return;

                PxTolerancesScale* scale = physics->GetTolerancesScale();
                _baseCookingParams = PxCookingParams_new(scale);

                _baseCookingParams.meshPreprocessParams =
                    PxMeshPreprocessingFlags.WeldVertices |
                    PxMeshPreprocessingFlags.EnableInertia;
                _baseCookingParams.convexMeshCookingType = PxConvexMeshCookingType.Quickhull;
                _baseCookingParams.gaussMapLimit = 64;
                _baseCookingParams.buildGPUData = true;
                _baseCookingParams.suppressTriangleMeshRemapTable = true;
                _baseCookingParams.planeTolerance = 0.00075f;

                _initialized = true;
            }
        }

        private static void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("PhysxConvexHullCooker.Initialize must be called before cooking convex meshes.");
        }

        public static PhysxConvexMesh CookHull(CoACD.ConvexHullMesh hull, PxConvexFlags extraFlags = 0, bool requestGpuData = false)
        {
            ArgumentNullException.ThrowIfNull(hull);

            if (!TryCookHull(hull, out PhysxConvexMesh? mesh, out string? failureMessage, extraFlags, requestGpuData))
                throw new InvalidOperationException(failureMessage ?? "PxCreateConvexMesh failed.");

            return mesh;
        }

        public static IReadOnlyList<PhysxConvexMesh> CookHulls(
            IReadOnlyList<CoACD.ConvexHullMesh> hulls,
            out int skippedHullCount,
            out string? firstFailureMessage,
            PxConvexFlags extraFlags = 0,
            bool requestGpuData = false)
        {
            EnsureInitialized();
            skippedHullCount = 0;
            firstFailureMessage = null;
            if (hulls == null || hulls.Count == 0)
                return [];

            List<PhysxConvexMesh> meshes = new(hulls.Count);

            foreach (var hull in hulls)
            {
                if (TryCookHull(hull, out PhysxConvexMesh? mesh, out string? failureMessage, extraFlags, requestGpuData))
                {
                    meshes.Add(mesh);
                    continue;
                }

                skippedHullCount++;
                firstFailureMessage ??= failureMessage;
            }

            return meshes;
        }

        private static bool TryCookHull(
            CoACD.ConvexHullMesh? hull,
            [NotNullWhen(true)]
            out PhysxConvexMesh? mesh,
            out string? failureMessage,
            PxConvexFlags extraFlags,
            bool requestGpuData)
        {
            EnsureInitialized();

            mesh = null;
            failureMessage = null;

            if (hull is null)
            {
                failureMessage = "Convex hull is null.";
                return false;
            }

            if (hull.Vertices.Length < 4)
            {
                failureMessage = "Convex hull requires at least four vertices.";
                return false;
            }

            PxConvexMeshDesc* descPtr = stackalloc PxConvexMeshDesc[1];
            *descPtr = PxConvexMeshDesc_new();
            descPtr->points.count = (uint)hull.Vertices.Length;
            descPtr->points.stride = (uint)sizeof(PxVec3);
            descPtr->vertexLimit = (ushort)Math.Clamp(hull.Vertices.Length, 4, 255);
            descPtr->polygonLimit = 0;
            descPtr->quantizedCount = 0;
            descPtr->sdfDesc = null;

            PxConvexFlags flags = PxConvexFlags.ComputeConvex | PxConvexFlags.CheckZeroAreaTriangles | extraFlags;
            if (requestGpuData)
                flags |= PxConvexFlags.GpuCompatible;
            descPtr->flags = flags;

            PxVec3[] vertexBuffer = new PxVec3[hull.Vertices.Length];
            for (int i = 0; i < vertexBuffer.Length; i++)
            {
                var v = hull.Vertices[i];
                vertexBuffer[i] = new PxVec3 { x = v.X, y = v.Y, z = v.Z };
            }

            PxCookingParams* paramsPtr = stackalloc PxCookingParams[1];
            *paramsPtr = _baseCookingParams;
            paramsPtr->buildGPUData = requestGpuData;

            fixed (PxVec3* vertexPtr = vertexBuffer)
            {
                descPtr->points.data = vertexPtr;

                if (!PxConvexMeshDesc_isValid(descPtr))
                {
                    failureMessage = "PxConvexMeshDesc validation failed for supplied hull.";
                    return false;
                }

                PxPhysics* physics = PhysxScene.PhysicsPtr;
                if (physics == null)
                {
                    failureMessage = "PhysX must be initialized before cooking convex meshes.";
                    return false;
                }

                PxConvexMeshCookingResult result = PxConvexMeshCookingResult.Success;
                PxInsertionCallback* insertion = physics->GetPhysicsInsertionCallbackMut();
                var meshPtr = phys_PxCreateConvexMesh(paramsPtr, descPtr, insertion, &result);
                if (meshPtr == null)
                {
                    failureMessage = $"PxCreateConvexMesh failed with result {result}.";
                    return false;
                }

                mesh = new PhysxConvexMesh(meshPtr);
                return true;
            }
        }
    }
}
