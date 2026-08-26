using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
namespace XREngine.Rendering;

public sealed partial class RuntimeWorldRenderer
{
    private const float EdgeBarycentricThreshold = 0.12f;
    private const float VertexBarycentricThreshold = 0.08f;
    private static readonly ConditionalWeakTable<RenderableMesh, GpuMeshBvhPickState> GpuMeshBvhPickStates = new();

    /// <summary>Enables precise mesh-BVH picking when the active backend supports its GPU readback path.</summary>
    public bool GpuMeshBvhPickingEnabled { get; set; } = true;
    private static void GpuPickLog(string message)
    {
        if (RuntimeEngine.Rendering.Settings.EnableGpuMeshBvhPickLogging)
            Debug.Out($"[GpuMeshBvhPick] {message}");
    }

    private static bool CanUseGpuMeshBvhPicking()
        => RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.OpenGL;

    private static void WarnGpuMeshBvhPickUnsupportedBackend()
        => Debug.RenderingWarningEvery("GpuMeshBvhPick.UnsupportedBackend", TimeSpan.FromSeconds(5),
            "[GpuMeshBvhPick] GPU mesh BVH picking is currently OpenGL-only; using coarse bounds picking for this backend.");

        private static (float? distance, object? data) DirectItemTest(RenderInfo3D item, Segment segment, ERaycastHitMode hitMode, bool gpuMeshBvhPickingEnabled)
        {
            if (item is not RenderInfo renderable)
                return (null, null);

            if (renderable.Owner is RenderableComponent renderableComponent &&
                TryIntersectRenderableComponent(renderableComponent, item, segment, hitMode, gpuMeshBvhPickingEnabled, out float meshDistance, out object? pickResult))
                return (meshDistance, pickResult);
            return (null, null);
        }

        private static bool TryIntersectRenderableComponent(
            RenderableComponent component,
            RenderInfo3D info,
            Segment worldSegment,
            ERaycastHitMode hitMode,
            bool gpuMeshBvhPickingEnabled,
            out float distance,
            out object? result)
        {
            distance = 0.0f;
            result = default;

            if (!TryFindRenderableMesh(component, info, out RenderableMesh? mesh))
                return false;

            if (mesh is null || ShouldIgnoreRenderableMesh(mesh))
                return false;

            if (TryGetGpuMeshBvhPickSubMesh(component, mesh, out _))
            {
                // Meshes that opt in to a GPU mesh BVH are picked through that BVH
                // only on backends with implemented GPU readback/fence support.
                if (!gpuMeshBvhPickingEnabled)
                    return false;

                if (CanUseGpuMeshBvhPicking())
                {
                    if (!GpuMeshBvhPickRayIntersectsRequestBounds(mesh, worldSegment, out float boundsDistance))
                        return false;

                    GpuMeshBvhPickCandidate candidate = QueueGpuMeshBvhPick(component, info, mesh, worldSegment, boundsDistance, hitMode, out GpuMeshBvhPickCandidate? lastHit);

                    if (candidate.IsComplete)
                    {
                        // The current ray has a definitive answer for this mesh.
                        if (!candidate.HasHit)
                            return false;

                        distance = candidate.Distance;
                        result = candidate.PickResult ?? candidate;
                        return true;
                    }

                    // Pending GPU readback: surface the most recent exact hit (one-frame latency)
                    // so hover highlighting tracks the cursor smoothly instead of snapping to the
                    // coarse bounding-volume distance while a fresh raycast is in flight.
                    if (lastHit is { HasHit: true })
                    {
                        distance = lastHit.Distance;
                        result = lastHit.PickResult ?? lastHit;
                        return true;
                    }

                    distance = candidate.CandidateDistance;
                    result = candidate;
                    return true;
                }

                WarnGpuMeshBvhPickUnsupportedBackend();
                return TryCreateUnsupportedGpuMeshBvhCoarsePick(
                    component,
                    info,
                    mesh,
                    worldSegment,
                    hitMode,
                    out distance,
                    out result);
            }

            if (!TryIntersectRenderableMesh(mesh, worldSegment, out distance, out Triangle worldTriangle, out Vector3 hitPoint, out IndexTriangle triangleIndices, out int triangleIndex))
                return false;

            MeshPickResult faceHit = new(component, mesh, worldTriangle, hitPoint, triangleIndex, triangleIndices);
            return TryBuildPickResult(hitMode, faceHit, out result);
        }

        private static bool TryBuildPickResult(ERaycastHitMode hitMode, MeshPickResult faceHit, out object? result)
        {
            switch (hitMode)
            {
                case ERaycastHitMode.Faces:
                    result = faceHit;
                    return true;
                case ERaycastHitMode.Lines:
                    if (TryBuildEdgePickResult(faceHit, out MeshEdgePickResult edgeHit))
                    {
                        result = edgeHit;
                        return true;
                    }
                    break;
                case ERaycastHitMode.Points:
                    if (TryBuildVertexPickResult(faceHit, out MeshVertexPickResult vertexHit))
                    {
                        result = vertexHit;
                        return true;
                    }
                    break;
            }

            result = null;
            return false;
        }

        private static bool TryBuildEdgePickResult(MeshPickResult faceHit, out MeshEdgePickResult result)
        {
            result = default;
            Triangle tri = faceHit.WorldTriangle;
            if (!tri.TryGetBarycentricCoordinates(faceHit.HitPoint, out Vector3 bary))
                return false;

            float bestWeight = float.MaxValue;
            Vector3 bestStart = default;
            Vector3 bestEnd = default;
            int bestEdgeIndex = -1;

            EvaluateEdge(bary.Z, tri.A, tri.B, 0);
            EvaluateEdge(bary.X, tri.B, tri.C, 1);
            EvaluateEdge(bary.Y, tri.C, tri.A, 2);

            if (bestWeight == float.MaxValue || bestEdgeIndex < 0)
                return false;

            Vector3 closest = ProjectPointOntoSegment(faceHit.HitPoint, bestStart, bestEnd);
            result = new MeshEdgePickResult(faceHit, bestStart, bestEnd, closest, bestEdgeIndex);
            return true;

            void EvaluateEdge(float coord, Vector3 start, Vector3 end, int edgeIndex)
            {
                if (coord > EdgeBarycentricThreshold || coord >= bestWeight)
                    return;
                bestWeight = coord;
                bestStart = start;
                bestEnd = end;
                bestEdgeIndex = edgeIndex;
            }
        }

        private static bool TryBuildVertexPickResult(MeshPickResult faceHit, out MeshVertexPickResult result)
        {
            result = default;
            Triangle tri = faceHit.WorldTriangle;
            if (!tri.TryGetBarycentricCoordinates(faceHit.HitPoint, out Vector3 bary))
                return false;

            float bestDelta = float.MaxValue;
            Vector3 bestVertex = default;
            int bestIndex = -1;

            EvaluateVertex(MathF.Abs(1.0f - bary.X), tri.A, faceHit.Indices.Point0);
            EvaluateVertex(MathF.Abs(1.0f - bary.Y), tri.B, faceHit.Indices.Point1);
            EvaluateVertex(MathF.Abs(1.0f - bary.Z), tri.C, faceHit.Indices.Point2);

            if (bestDelta > VertexBarycentricThreshold || bestIndex < 0)
                return false;

            result = new MeshVertexPickResult(faceHit, bestVertex, bestIndex);
            return true;

            void EvaluateVertex(float delta, Vector3 vertex, int vertexIndex)
            {
                if (delta >= bestDelta)
                    return;
                bestDelta = delta;
                bestVertex = vertex;
                bestIndex = vertexIndex;
            }
        }

        private static Vector3 ProjectPointOntoSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 edge = end - start;
            float lengthSq = edge.LengthSquared();
            if (lengthSq <= XRMath.Epsilon)
                return start;

            float t = Vector3.Dot(point - start, edge) / lengthSq;
            t = float.Clamp(t, 0.0f, 1.0f);
            return start + edge * t;
        }

        private static bool ShouldIgnoreRenderableMesh(RenderableMesh mesh)
        {
            foreach (var command in mesh.RenderInfo.RenderCommands)
            {
                if (command.RenderPass == (int)EDefaultRenderPass.Background)
                    return true;
            }

            foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
            {
                var material = lod.Renderer.Material;
                if (material is not null && material.RenderPass == (int)EDefaultRenderPass.Background)
                    return true;
            }

            return false;
        }

        private static bool TryFindRenderableMesh(RenderableComponent component, RenderInfo3D info, out RenderableMesh? mesh)
        {
            foreach (var candidate in component.Meshes)
            {
                if (ReferenceEquals(candidate.RenderInfo, info))
                {
                    mesh = candidate;
                    return true;
                }
            }

            mesh = null;
            return false;
        }

        /// <summary>
        /// Returns true when the component/mesh pair carries an opted-in GPU mesh BVH
        /// (<see cref="SubMesh.UseGpuMeshBvh"/>), exposing the owning <see cref="SubMesh"/>.
        /// Used to gate the GPU-mesh-BVH pick path on the mesh's bone-driven request bounds.
        /// </summary>
        private static bool TryGetGpuMeshBvhPickSubMesh(RenderableComponent component, RenderableMesh mesh, out SubMesh? subMesh)
        {
            subMesh = null;
            return component is ModelComponent model &&
                model.TryGetSourceSubMesh(mesh, out subMesh) &&
                subMesh.UseGpuMeshBvh;
        }

        /// <summary>
        /// Mirrors the BVH preview's hover gate: the pick ray must intersect the mesh's
        /// bone-driven request bounds (<see cref="RenderableMesh.TryGetGpuMeshBvhRequestWorldBounds"/>)
        /// before its GPU/CPU BVH is recalculated or triangle-tested.
        /// </summary>
        private static bool GpuMeshBvhPickRayIntersectsRequestBounds(RenderableMesh mesh, Segment worldSegment, out float distance)
        {
            distance = 0.0f;
            if (!mesh.TryGetGpuMeshBvhRequestWorldBounds(out AABB worldBounds) || !worldBounds.IsValid)
                return false;

            if (!GeoUtil.Intersect.SegmentWithAABB(worldSegment.Start, worldSegment.End, worldBounds.Min, worldBounds.Max, out Vector3 enterPoint, out _))
                return false;

            distance = (enterPoint - worldSegment.Start).Length();
            return true;
        }

        private static bool TryCreateUnsupportedGpuMeshBvhCoarsePick(
            RenderableComponent component,
            RenderInfo3D info,
            RenderableMesh mesh,
            Segment worldSegment,
            ERaycastHitMode hitMode,
            out float distance,
            out object? result)
        {
            result = null;
            if (!GpuMeshBvhPickRayIntersectsRequestBounds(mesh, worldSegment, out distance))
                return false;

            Vector3 hitPoint = PointAtSegmentDistance(worldSegment, distance);
            GpuMeshBvhPickCandidate candidate = new(component, mesh, info, worldSegment, distance, hitMode);
            candidate.CompleteHit(
                distance,
                hitPoint,
                Vector3.Zero,
                objectId: 0u,
                sortedTriangleIndex: uint.MaxValue,
                faceHit: null,
                pickResult: null);
            result = candidate;
            return true;
        }

        private static Vector3 PointAtSegmentDistance(Segment segment, float distance)
        {
            Vector3 delta = segment.End - segment.Start;
            float length = delta.Length();
            if (length <= 1e-5f)
                return segment.Start;

            float clampedDistance = Math.Clamp(distance, 0.0f, length);
            return segment.Start + delta / length * clampedDistance;
        }

        private static GpuMeshBvhPickCandidate QueueGpuMeshBvhPick(
            RenderableComponent component,
            RenderInfo3D info,
            RenderableMesh mesh,
            Segment worldSegment,
            float candidateDistance,
            ERaycastHitMode hitMode,
            out GpuMeshBvhPickCandidate? lastHit)
        {
            GpuMeshBvhPickState state = GpuMeshBvhPickStates.GetValue(mesh, static _ => new GpuMeshBvhPickState());
            GpuMeshBvhPickCandidate? superseded = null;
            bool enqueueDispatch = false;

            GpuMeshBvhPickCandidate candidate;
            lock (state)
            {
                lastHit = state.LastHit;

                if (state.Candidate is { } existing && GpuPickSegmentsMatch(existing.WorldSegment, worldSegment) && existing.HitMode == hitMode)
                    return existing;

                if (state.Candidate is { IsComplete: false } pending)
                    superseded = pending;

                candidate = new(component, mesh, info, worldSegment, candidateDistance, hitMode);
                state.Candidate = candidate;
                ++state.Generation;

                if (!state.DispatchQueued && !state.RaycastInFlight)
                {
                    state.DispatchQueued = true;
                    enqueueDispatch = true;
                }
            }

            superseded?.CompleteMiss();

            if (enqueueDispatch)
                EnqueueGpuMeshBvhPickDispatch(state);

            return candidate;
        }

        private static void EnqueueGpuMeshBvhPickDispatch(GpuMeshBvhPickState state)
            => RuntimeEngine.EnqueueMainThreadTask(
                () => DispatchLatestGpuMeshBvhPick(state),
                "RuntimeWorld.GpuMeshBvhPick");

        private static void DispatchLatestGpuMeshBvhPick(GpuMeshBvhPickState state)
        {
            GpuMeshBvhPickCandidate? candidate;
            int generation;
            lock (state)
            {
                state.DispatchQueued = false;
                candidate = state.Candidate;
                generation = state.Generation;

                if (candidate is null || candidate.IsComplete || state.RaycastInFlight)
                    return;

                state.RaycastInFlight = true;
            }

            if (!DispatchGpuMeshBvhPick(candidate.Component, candidate.RenderInfo, candidate.Mesh, candidate.WorldSegment, candidate, state, generation))
                FinishGpuMeshBvhPick(state, generation);
        }

        private static bool DispatchGpuMeshBvhPick(
            RenderableComponent component,
            RenderInfo3D info,
            RenderableMesh mesh,
            Segment worldSegment,
            GpuMeshBvhPickCandidate candidate,
            GpuMeshBvhPickState state,
            int generation)
        {
            try
            {
                var worldInstance = info.WorldInstance as IRuntimeRenderWorld;
                if (worldInstance is null)
                {
                    GpuPickLog("dispatch aborted: render info is not attached to a render world.");
                    candidate.CompleteMiss();
                    return false;
                }
                if (!worldInstance.GpuMeshBvhPickingEnabled)
                {
                    GpuPickLog("dispatch aborted: GPU mesh BVH picking was disabled before dispatch.");
                    candidate.CompleteMiss();
                    return false;
                }

                var renderer = mesh.GetCurrentOrFirstLodRenderer();
                var xrMesh = renderer?.Mesh;
                if (renderer is null || xrMesh is null)
                {
                    GpuPickLog("dispatch aborted: renderer or mesh is null.");
                    candidate.CompleteMiss();
                    return false;
                }

                bool skinned = xrMesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning;
                if (!mesh.PrepareGpuMeshBvh(realtimeSkinned: skinned))
                {
                    GpuPickLog($"dispatch aborted: PrepareGpuMeshBvh returned false (skinned={skinned}, tris={xrMesh.Triangles?.Count ?? 0}).");
                    candidate.CompleteMiss();
                    return false;
                }

                mesh.ClearGpuMeshBvhRefreshRequestIfPrepared();

                GpuMeshBvh? bvh = mesh.GpuMeshBvh;
                if (bvh is null || !bvh.IsBvhReady || bvh.BvhNodeBuffer is null || bvh.PackedTriangleBuffer is null)
                {
                    GpuPickLog($"dispatch aborted: BVH not ready (bvh={(bvh is null ? "null" : "set")}, ready={bvh?.IsBvhReady}, nodeBuf={(bvh?.BvhNodeBuffer is null ? "null" : "set")}, packedBuf={(bvh?.PackedTriangleBuffer is null ? "null" : "set")}).");
                    candidate.CompleteMiss();
                    return false;
                }

                if (!Matrix4x4.Invert(bvh.LocalToWorldMatrix, out Matrix4x4 worldToLocal))
                {
                    GpuPickLog("dispatch aborted: LocalToWorldMatrix is not invertible.");
                    candidate.CompleteMiss();
                    return false;
                }

                Vector3 localStart = Vector3.Transform(worldSegment.Start, worldToLocal);
                Vector3 localEnd = Vector3.Transform(worldSegment.End, worldToLocal);
                Vector3 localDelta = localEnd - localStart;
                float localLength = localDelta.Length();
                if (localLength <= 1e-5f)
                {
                    GpuPickLog("dispatch aborted: degenerate local ray length.");
                    candidate.CompleteMiss();
                    return false;
                }

                Vector3 localDirection = localDelta / localLength;
                if (!state.EnsureBuffers())
                {
                    GpuPickLog("dispatch aborted: EnsureBuffers failed.");
                    candidate.CompleteMiss();
                    return false;
                }

                state.UploadRay(localStart, localDirection, localLength);

                GpuPickLog($"dispatch enqueued: tris={bvh.TriangleCount}, nodes={bvh.BvhNodeCount}, gpuSkinned={bvh.LastUpdateUsedGpuSkinning}, localStart={localStart}, localDir={localDirection}, localLen={localLength:F3}.");

                bool enqueued = worldInstance.VisualScene.BvhRaycasts.Enqueue(new BvhRaycastRequest
                {
                    RayBuffer = state.RayBuffer,
                    NodeBuffer = bvh.BvhNodeBuffer,
                    TriangleBuffer = bvh.PackedTriangleBuffer,
                    HitBuffer = state.HitBuffer,
                    RayCount = 1u,
                    RootNodeIndex = 0u,
                    Variant = BvhRaycastVariant.ClosestHit,
                    Completed = result => CompleteGpuMeshBvhPick(component, mesh, worldSegment, bvh.LocalToWorldMatrix, localStart, localDirection, candidate, state, generation, result),
                });

                if (!enqueued)
                    candidate.CompleteMiss();

                return enqueued;
            }
            catch (Exception ex)
            {
                Debug.RenderingException(ex, "GPU mesh BVH pick dispatch failed.");
                candidate.CompleteMiss();
                return false;
            }
        }

        private static void CompleteGpuMeshBvhPick(
            RenderableComponent component,
            RenderableMesh mesh,
            Segment worldSegment,
            Matrix4x4 localToWorld,
            Vector3 localStart,
            Vector3 localDirection,
            GpuMeshBvhPickCandidate candidate,
            GpuMeshBvhPickState state,
            int generation,
            BvhRaycastResult result)
        {
            try
            {
                if (result.Hits.Count == 0)
                {
                    GpuPickLog("readback: no hit records returned.");
                    candidate.CompleteMiss();
                    ClearGpuMeshBvhLastHit(state, generation);
                    return;
                }

                GpuRaycastHit hit = result.Hits[0];
                if (hit.TriangleIndex == uint.MaxValue)
                {
                    GpuPickLog("readback: miss (TriangleIndex == uint.MaxValue).");
                    candidate.CompleteMiss();
                    ClearGpuMeshBvhLastHit(state, generation);
                    return;
                }

                Vector3 localHitPoint = localStart + localDirection * hit.Distance;
                Vector3 worldHitPoint = Vector3.Transform(localHitPoint, localToWorld);
                float worldDistance = (worldHitPoint - worldSegment.Start).Length();
                float worldSegmentLength = (worldSegment.End - worldSegment.Start).Length();
                if (worldDistance < 0.0f || worldDistance > worldSegmentLength)
                {
                    GpuPickLog($"readback: rejected (worldDistance={worldDistance:F3} outside [0,{worldSegmentLength:F3}], localDist={hit.Distance:F3}).");
                    candidate.CompleteMiss();
                    ClearGpuMeshBvhLastHit(state, generation);
                    return;
                }

                MeshPickResult? faceHit = BuildGpuMeshBvhFaceHit(component, mesh, localToWorld, worldHitPoint, hit);

                // Resolve the exact primitive (face / edge / vertex) for the requested hit mode
                // directly from the GPU-resolved triangle so hover snaps to the precise feature.
                object? pickResult = null;
                if (faceHit.HasValue)
                    TryBuildPickResult(candidate.HitMode, faceHit.Value, out pickResult);

                GpuPickLog($"readback: HIT tri={hit.TriangleIndex}, face={hit.FaceIndex}, dist={worldDistance:F3}, bary={hit.Barycentric}, mode={candidate.HitMode}, resolved={(pickResult?.GetType().Name ?? "none")}.");

                candidate.CompleteHit(worldDistance, worldHitPoint, hit.Barycentric, hit.ObjectId, hit.TriangleIndex, faceHit, pickResult);

                lock (state)
                {
                    if (state.Generation == generation)
                    {
                        state.LastHit = candidate;
                        state.Candidate = candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.RenderingException(ex, "GPU mesh BVH pick completion failed.");
                candidate.CompleteMiss();
            }
            finally
            {
                FinishGpuMeshBvhPick(state, generation);
            }
        }

        private static void FinishGpuMeshBvhPick(GpuMeshBvhPickState state, int generation)
        {
            bool enqueueNext = false;
            lock (state)
            {
                state.RaycastInFlight = false;
                if (state.Generation != generation &&
                    state.Candidate is { IsComplete: false } &&
                    !state.DispatchQueued)
                {
                    state.DispatchQueued = true;
                    enqueueNext = true;
                }
            }

            if (enqueueNext)
                EnqueueGpuMeshBvhPickDispatch(state);
        }

        private static void ClearGpuMeshBvhLastHit(GpuMeshBvhPickState state, int generation)
        {
            lock (state)
            {
                if (state.Generation == generation)
                    state.LastHit = null;
            }
        }

        private static MeshPickResult? BuildGpuMeshBvhFaceHit(
            RenderableComponent component,
            RenderableMesh mesh,
            Matrix4x4 localToWorld,
            Vector3 hitPoint,
            GpuRaycastHit hit)
        {
            var renderer = mesh.GetCurrentOrFirstLodRenderer();
            var xrMesh = renderer?.Mesh;
            var triangles = xrMesh?.Triangles;
            if (xrMesh is null || triangles is null || hit.FaceIndex >= (uint)triangles.Count)
                return null;

            IndexTriangle indices = triangles[(int)hit.FaceIndex];
            Triangle worldTriangle;
            if (mesh.GpuMeshBvh?.LastUpdateUsedGpuSkinning == true)
            {
                worldTriangle = new Triangle(hitPoint, hitPoint, hitPoint);
            }
            else
            {
                worldTriangle = new Triangle(
                    Vector3.Transform(xrMesh.GetPosition((uint)Math.Max(0, indices.Point0)), localToWorld),
                    Vector3.Transform(xrMesh.GetPosition((uint)Math.Max(0, indices.Point1)), localToWorld),
                    Vector3.Transform(xrMesh.GetPosition((uint)Math.Max(0, indices.Point2)), localToWorld));
            }

            return new MeshPickResult(component, mesh, worldTriangle, hitPoint, (int)hit.FaceIndex, indices);
        }

        private static bool GpuPickSegmentsMatch(Segment left, Segment right)
        {
            const float epsilonSq = 1e-4f;
            return Vector3.DistanceSquared(left.Start, right.Start) <= epsilonSq &&
                Vector3.DistanceSquared(left.End, right.End) <= epsilonSq;
        }

        private sealed class GpuMeshBvhPickState
        {
            private readonly GpuBvhPickRayInput[] _rayData = new GpuBvhPickRayInput[1];

            public XRDataBuffer? RayBuffer { get; private set; }
            public XRDataBuffer? HitBuffer { get; private set; }
            public GpuMeshBvhPickCandidate? Candidate { get; set; }
            public bool DispatchQueued { get; set; }
            public bool RaycastInFlight { get; set; }

            /// <summary>
            /// Most recent completed candidate that produced a hit. Surfaced as the placeholder
            /// result while a fresh raycast is in flight so hover highlighting stays continuous.
            /// </summary>
            public GpuMeshBvhPickCandidate? LastHit { get; set; }
            public int Generation { get; set; }

            public bool EnsureBuffers()
            {
                RayBuffer ??= new XRDataBuffer(
                    "GpuMeshBvhPick_Ray",
                    EBufferTarget.ShaderStorageBuffer,
                    1u,
                    EComponentType.Struct,
                    (uint)Marshal.SizeOf<GpuBvhPickRayInput>(),
                    false,
                    true)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    // The ray buffer is rewritten every frame with the current cursor ray. Keep it a
                    // plain mutable (resizable) buffer with no storage/range flags so PushData routes
                    // through glNamedBufferData. Immutable storage (any StorageFlags or persistent/
                    // coherent RangeFlags) makes re-uploads throw GL_INVALID_OPERATION
                    // ("Cannot modify immutable buffer") because the immutable-storage path attempts
                    // to re-run glNamedBufferStorage on an already-allocated immutable buffer.
                    Resizable = true,
                    DisposeOnPush = false,
                    PadEndingToVec4 = true,
                    ShouldMap = false,
                };

                HitBuffer ??= new XRDataBuffer(
                    "GpuMeshBvhPick_Hit",
                    EBufferTarget.ShaderStorageBuffer,
                    1u,
                    EComponentType.Struct,
                    (uint)Marshal.SizeOf<GpuRaycastHit>(),
                    false,
                    true)
                {
                    Usage = EBufferUsage.StreamRead,
                    Resizable = false,
                    DisposeOnPush = false,
                    PadEndingToVec4 = true,
                    ShouldMap = false,
                    StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
                    RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
                };

                return RayBuffer is not null && HitBuffer is not null;
            }

            public void UploadRay(Vector3 origin, Vector3 direction, float maxDistance)
            {
                _rayData[0] = new GpuBvhPickRayInput(new Vector4(origin, 0.0f), new Vector4(direction, maxDistance));
                RayBuffer!.SetDataRaw(_rayData);
                RayBuffer.PushData();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct GpuBvhPickRayInput(Vector4 origin, Vector4 direction)
        {
            public readonly Vector4 Origin = origin;
            public readonly Vector4 Direction = direction;
        }

        private static bool TryIntersectRenderableMesh(
            RenderableMesh mesh,
            Segment worldSegment,
            out float distance,
            out Triangle worldTriangle,
            out Vector3 hitPoint,
            out IndexTriangle triangleIndices,
            out int triangleIndex)
        {
            distance = 0.0f;
            worldTriangle = default;
            hitPoint = default;
            triangleIndices = new IndexTriangle();
            triangleIndex = -1;

            var renderer = mesh.GetCurrentOrFirstLodRenderer();
            if (renderer is null)
                return false;

            var material = renderer.Material;
            if (material is not null && material.RenderPass == (int)EDefaultRenderPass.Background)
                return false;

            var xrMesh = renderer.Mesh;
            if (xrMesh is null)
                return false;

            bool skinned = xrMesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning;

            var bvh = skinned ? mesh.GetSkinnedBvh(allowRebuild: false) : xrMesh.CachedBVHTree;
            if (bvh is null)
                return false;

            Vector3 segmentSpaceStart;
            Vector3 segmentSpaceEnd;
            Matrix4x4? spaceToWorld = null;

            if (skinned)
            {
                spaceToWorld = mesh.SkinnedBvhLocalToWorldMatrix;
                var worldToLocal = mesh.SkinnedBvhWorldToLocalMatrix;
                segmentSpaceStart = Vector3.Transform(worldSegment.Start, worldToLocal);
                segmentSpaceEnd = Vector3.Transform(worldSegment.End, worldToLocal);
            }
            else
            {
                var transform = mesh.Component.Transform;
                if (transform is null)
                    return false;
                spaceToWorld = transform.WorldMatrix;

                segmentSpaceStart = Vector3.Transform(worldSegment.Start, transform.InverseWorldMatrix);
                segmentSpaceEnd = Vector3.Transform(worldSegment.End, transform.InverseWorldMatrix);
            }

            Vector3 segmentSpaceDiff = segmentSpaceEnd - segmentSpaceStart;
            float segmentSpaceLength = segmentSpaceDiff.Length();
            if (segmentSpaceLength <= 1e-5f)
                return false;

            Vector3 segmentSpaceDir = segmentSpaceDiff / segmentSpaceLength;

            var matches = bvh.Traverse(node => GeoUtil.Intersect.SegmentWithAABB(segmentSpaceStart, segmentSpaceEnd, node.Min, node.Max, out _, out _));
            if (matches is null)
                return false;

            float bestDistance = float.MaxValue;
            Triangle? bestTriangle = null;

            foreach (var node in matches)
            {
                if (node.gobjects is null)
                    continue;

                foreach (var tri in node.gobjects)
                {
                    if (!GeoUtil.Intersect.RayWithTriangle(segmentSpaceStart, segmentSpaceDir, tri.A, tri.B, tri.C, out float hitDistance))
                        continue;

                    if (hitDistance < 0.0f || hitDistance > segmentSpaceLength)
                        continue;

                    if (hitDistance < bestDistance)
                    {
                        bestDistance = hitDistance;
                        bestTriangle = tri;
                    }
                }
            }

            if (bestTriangle is null)
                return false;

            Triangle localTriangle = bestTriangle.Value;
            if (xrMesh.TriangleLookup is { } lookup && lookup.TryGetValue(localTriangle, out var indices))
            {
                triangleIndices = indices.Indices;
                triangleIndex = indices.FaceIndex;
            }

            Vector3 spaceHitPoint = segmentSpaceStart + segmentSpaceDir * bestDistance;
            if (spaceToWorld is null)
                return false;

            hitPoint = Vector3.Transform(spaceHitPoint, spaceToWorld.Value);
            worldTriangle = new Triangle(
                Vector3.Transform(localTriangle.A, spaceToWorld.Value),
                Vector3.Transform(localTriangle.B, spaceToWorld.Value),
                Vector3.Transform(localTriangle.C, spaceToWorld.Value));

            distance = (hitPoint - worldSegment.Start).Length();
            float worldLength = (worldSegment.End - worldSegment.Start).Length();
            if (distance < 0.0f || distance > worldLength)
                return false;

            return true;
        }
}
