using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Conservative CPU software occlusion culler. Opaque meshes selected from the current
    /// render-command buffer are rasterized into a small reciprocal-depth buffer, and later
    /// mesh AABBs are tested against that buffer before hardware-query or meshlet dispatch.
    /// </summary>
    public sealed class CpuSoftwareOcclusionCuller
    {
        private readonly MaskedOcclusionBuffer _leftBuffer = new();
        private readonly MaskedOcclusionBuffer _rightBuffer = new();
        private readonly MaskedOcclusionRasterizer _rasterizer = new();
        private const int MaxCandidateInspectionsPerFrame = 16_384;
        private const int MaxGeneralDeformationSubmeshInspections = 256;
        // This cap limits clear work as well as mask storage; it is a safety bound, not a profitability threshold.
        private const int MaxBufferPixelsPerEye = 262_144;

        // Fixed capacity prevents a transient scene burst from growing per-frame scratch.
        private readonly OccluderCandidate[] _occluderCandidates = new OccluderCandidate[4096];
        private readonly HashSet<uint> _selectedOccluderKeys = new();
        private readonly CpuSoftwareOcclusionRasterWorkBudget _rasterWorkBudget = new();
        private readonly CpuSoftwareOcclusionAabbTestWorkBudget _aabbTestWorkBudget = new();
        private readonly CpuSoftwareOcclusionProfitabilityPolicy _profitabilityPolicy = new();

        private XRCamera? _camera;
        private XRCamera? _rightEyeCamera;
        private Matrix4x4 _leftViewProjectionMatrix;
        private Matrix4x4 _rightViewProjectionMatrix;
        private bool _stereo;
        private bool _frameOpen;
        private bool _occludersSubmitted;
        private RenderCommandCollection? _sourceCommands;
        private int _frameOccludersSelected;
        private int _frameOccludersRasterized;
        private int _frameTestsRun;
        private int _candidateCount;
        private int _frameCandidatesInspected;
        private int _frameCandidatesDropped;
        private int _candidateCapacity;
        private double _completedSocCostMilliseconds;
        private int _completedSocCulled;
        private double _frameSocCostMilliseconds;
        private int _frameSocCulled;
        private bool _frameDebugBypass;
        private int _viewportWidth;
        private int _viewportHeight;
        private ulong _renderFrameId;

        public static bool IsEnabled
        {
            get
            {
                if (RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuSoftwareOcclusion)
                    return true;

                if (RuntimeEngine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling)
                    return true;

                return XREnvironment.IsEnabled(XREngineEnvironmentVariables.CpuSoftwareOcclusion);
            }
        }

        public bool IsFrameOpen => _frameOpen;

        /// <summary>
        /// Supplies a real, scoped CPU direct-submission timing sample for future opt-in SOC
        /// admission. Callers must not pass pipeline-wide or GPU timing here.
        /// </summary>
        public void RecordMeasuredCpuDrawSubmissionCost(double milliseconds, int drawCount)
            => _profitabilityPolicy.RecordMeasuredSubmissionCost(milliseconds, drawCount);

        public bool IsFrameInitializedFor(XRCamera camera, int viewportWidth, int viewportHeight)
            => _frameOpen &&
               ReferenceEquals(_camera, camera) &&
               _rightEyeCamera is null &&
               !_stereo &&
               _viewportWidth == viewportWidth &&
               _viewportHeight == viewportHeight &&
               _renderFrameId == RuntimeEngine.Rendering.State.RenderFrameId;

        public bool IsFrameInitializedFor(XRCamera camera, XRCamera? rightEyeCamera, int viewportWidth, int viewportHeight)
            => _frameOpen &&
               ReferenceEquals(_camera, camera) &&
               ReferenceEquals(_rightEyeCamera, rightEyeCamera) &&
               _viewportWidth == viewportWidth &&
               _viewportHeight == viewportHeight &&
               _renderFrameId == RuntimeEngine.Rendering.State.RenderFrameId;

        internal bool HasOccludersFrom(RenderCommandCollection commands)
            => _occludersSubmitted && ReferenceEquals(_sourceCommands, commands);

        public void BeginFrame(XRCamera camera, int viewportWidth, int viewportHeight)
            => BeginFrame(camera, rightEyeCamera: null, viewportWidth, viewportHeight);

        public void BeginFrame(XRCamera camera, XRCamera? rightEyeCamera, int viewportWidth, int viewportHeight)
        {
            if (!IsEnabled)
                return;

            if (_frameOpen && !_frameDebugBypass)
            {
                _completedSocCostMilliseconds = _frameSocCostMilliseconds;
                _completedSocCulled = _frameSocCulled;
                _profitabilityPolicy.RecordCompletedProbe(_completedSocCulled, _completedSocCostMilliseconds);
            }

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            bool forced = RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuSoftwareOcclusion ||
                          XREnvironment.IsEnabled(XREngineEnvironmentVariables.CpuSoftwareOcclusion);
            bool debugBypass = RuntimeEngine.EffectiveSettings.CpuSocDebugForceVisible;
            CpuSoftwareOcclusionProfitabilityAdmission admission = _profitabilityPolicy.Decide(frameId, forced, debugBypass);
            OcclusionTelemetry.RecordCpuSocProfitabilityDecision(admission.Decision);
            if (!admission.RunSoc)
            {
                _frameOpen = false;
                _frameDebugBypass = false;
                _occludersSubmitted = false;
                _sourceCommands = null;
                return;
            }

            long start = Stopwatch.GetTimestamp();
            _camera = camera;
            _rightEyeCamera = rightEyeCamera;
            _leftViewProjectionMatrix = camera.ViewProjectionMatrix;
            _rightViewProjectionMatrix = rightEyeCamera?.ViewProjectionMatrix ?? default;
            _stereo = rightEyeCamera is not null && !ReferenceEquals(camera, rightEyeCamera);
            _viewportWidth = Math.Max(1, viewportWidth);
            _viewportHeight = Math.Max(1, viewportHeight);
            _renderFrameId = frameId;
            _frameOpen = true;
            _frameDebugBypass = debugBypass;
            _occludersSubmitted = false;
            _sourceCommands = null;
            _frameOccludersSelected = 0;
            _frameOccludersRasterized = 0;
            _frameTestsRun = 0;
            _candidateCount = 0;
            _frameCandidatesInspected = 0;
            _frameCandidatesDropped = 0;
            ClearCandidateScratch();
            _selectedOccluderKeys.Clear();
            _rasterWorkBudget.Reset();
            _aabbTestWorkBudget.Reset();
            _frameSocCostMilliseconds = 0.0;
            _frameSocCulled = 0;

            GetBoundedBufferDimensions(
                Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocBufferWidth, 64, 4096),
                Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocBufferHeight, 32, 4096),
                out int bufferWidth,
                out int bufferHeight);
            _leftBuffer.Resize(bufferWidth, bufferHeight);
            _leftBuffer.Clear();
            if (_stereo)
            {
                _rightBuffer.Resize(bufferWidth, bufferHeight);
                _rightBuffer.Clear();
            }

            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _frameSocCostMilliseconds += elapsedMs;
            OcclusionTelemetry.RecordCpuSocFrameBegin(elapsedMs, RuntimeEngine.EffectiveSettings.CpuSocDebugForceVisible);
        }

        public void SubmitOccludersFromOpaqueCommands(RenderCommandCollection commands)
        {
            if (!IsEnabled || !_frameOpen || _camera is null || _occludersSubmitted)
                return;

            _occludersSubmitted = true;
            _sourceCommands = commands;
            SelectOccluders(
                commands,
                out double selectionMilliseconds,
                out double sortMilliseconds);

            long rasterStart = Stopwatch.GetTimestamp();
            int triangleBudget = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocOccluderTriangleBudget, 0, 1_000_000);
            int rasterized = 0;
            try
            {
                for (int i = 0; i < _candidateCount && triangleBudget > 0 && !_rasterWorkBudget.IsExhausted; i++)
                {
                    OccluderCandidate candidate = _occluderCandidates[i];
                    CpuSoftwareOcclusionRasterizationResult leftResult = _rasterizer.RasterizeMesh(
                        _leftBuffer,
                        candidate.Mesh,
                        candidate.ModelMatrix,
                        _leftViewProjectionMatrix,
                        candidate.RenderOptions,
                        triangleBudget,
                        _rasterWorkBudget);

                    CpuSoftwareOcclusionRasterizationResult rightResult = default;
                    if (_stereo)
                    {
                        rightResult = _rasterizer.RasterizeMesh(
                            _rightBuffer,
                            candidate.Mesh,
                            candidate.ModelMatrix,
                            _rightViewProjectionMatrix,
                            candidate.RenderOptions,
                            triangleBudget,
                            _rasterWorkBudget);
                    }

                    int inspected = Math.Max(leftResult.TrianglesInspected, rightResult.TrianglesInspected);
                    if (inspected <= 0)
                        continue;

                    triangleBudget -= inspected;
                    if (leftResult.WroteCoverage || rightResult.WroteCoverage)
                    {
                        rasterized++;
                        _selectedOccluderKeys.Add(candidate.StableQueryKey);
                    }
                }
            }
            finally
            {
                ClearCandidateScratch();
            }

            _frameOccludersRasterized = rasterized;
            double rasterMilliseconds =
                Stopwatch.GetElapsedTime(rasterStart).TotalMilliseconds;
            _frameSocCostMilliseconds += selectionMilliseconds + sortMilliseconds + rasterMilliseconds;
            int tilesClosed = _leftBuffer.TilesClosed + (_stereo ? _rightBuffer.TilesClosed : 0);
            OcclusionTelemetry.RecordCpuSocOccluders(
                _frameOccludersSelected,
                rasterized,
                tilesClosed,
                selectionMilliseconds,
                sortMilliseconds,
                rasterMilliseconds,
                _frameCandidatesInspected,
                _frameCandidatesDropped,
                _rasterWorkBudget.ReservedPixelWork,
                _rasterWorkBudget.ExecutedPixelWork,
                _rasterWorkBudget.ReservedTileWork,
                _rasterWorkBudget.SkippedTriangles,
                _rasterWorkBudget.IsExhausted);
        }

        public bool TestVisible(uint stableQueryKey, in AABB worldBounds)
        {
            if (!IsEnabled || !_frameOpen || _camera is null)
                return true;

            if (RuntimeEngine.EffectiveSettings.CpuSocDebugForceVisible)
                return true;

            if (_selectedOccluderKeys.Contains(stableQueryKey))
            {
                OcclusionTelemetry.RecordCpuSocSelfOccluderSkipped();
                return true;
            }

            _frameTestsRun++;
            if (_frameOccludersRasterized == 0)
            {
                OcclusionTelemetry.RecordCpuSocTested();
                return true;
            }

            long start = Stopwatch.GetTimestamp();
            bool completed = TryTestVisible(_leftBuffer, _leftViewProjectionMatrix, worldBounds, out bool visible) &&
                             (visible || !_stereo || TryTestVisible(_rightBuffer, _rightViewProjectionMatrix, worldBounds, out visible));
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _frameSocCostMilliseconds += elapsedMs;
            if (!completed)
            {
                OcclusionTelemetry.RecordCpuSocAabbTestBudgetBypassed();
                OcclusionTelemetry.RecordCpuSocTest(elapsedMs, culled: false);
                return true;
            }
            if (!visible)
                _frameSocCulled++;
            OcclusionTelemetry.RecordCpuSocTest(elapsedMs, !visible);
            return visible;
        }

        private bool TryTestVisible(
            MaskedOcclusionBuffer buffer,
            in Matrix4x4 viewProjectionMatrix,
            in AABB worldBounds,
            out bool visible)
        {
            visible = true;
            if (!_aabbTestWorkBudget.TryReserveQuery())
                return false;

            OcclusionTelemetry.RecordCpuSocAabbTestWork();
            if (!MaskedOcclusionAabbTester.TryProjectAabb(worldBounds, viewProjectionMatrix, buffer.Width, buffer.Height, out ProjectedAabb projected))
                return true;

            if (projected.OutsideFrustum)
            {
                visible = false;
                return true;
            }

            int tileWork = buffer.GetRectTileWork(projected.MinX, projected.MinY, projected.MaxXExclusive, projected.MaxYExclusive);
            if (!_aabbTestWorkBudget.TryReserveTileWork(tileWork))
                return false;

            OcclusionTelemetry.RecordCpuSocAabbTestTileWork(tileWork);
            visible = MaskedOcclusionAabbTester.TestVisible(buffer, projected);
            return true;
        }

        public bool TestVisible(in AABB worldBounds)
            => TestVisible(0u, worldBounds);

        public CpuSoftwareOcclusionDebugReadback? ReadDebugBuffer()
            => RuntimeEngine.EffectiveSettings.CpuSocDebugVisualization && _frameOpen
                ? _leftBuffer.CreateDebugReadback()
                : null;

        public int FrameOccludersSubmitted => _frameOccludersRasterized;
        public int FrameTestsRun => _frameTestsRun;

        private void SelectOccluders(
            RenderCommandCollection commands,
            out double selectionMilliseconds,
            out double sortMilliseconds)
        {
            long selectionStart = Stopwatch.GetTimestamp();
            selectionMilliseconds = 0.0;
            sortMilliseconds = 0.0;
            _candidateCount = 0;
            int maxOccluders = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocMaxOccluders, 0, 4096);
            _candidateCapacity = maxOccluders;
            int triangleBudget = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocOccluderTriangleBudget, 0, 1_000_000);
            if (maxOccluders == 0 || triangleBudget == 0)
                return;

            float minScreenArea = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocMinOccluderScreenArea, 0.0f, 1.0f);
            TryCollectOccludersForPass(commands, (int)EDefaultRenderPass.OpaqueDeferred, minScreenArea);
            if (_frameCandidatesInspected < MaxCandidateInspectionsPerFrame)
                TryCollectOccludersForPass(commands, (int)EDefaultRenderPass.OpaqueForward, minScreenArea);
            selectionMilliseconds =
                Stopwatch.GetElapsedTime(selectionStart).TotalMilliseconds;

            long sortStart = Stopwatch.GetTimestamp();
            SortCandidatesDescending();
            int write = 0;
            int remainingTriangles = triangleBudget;
            for (int read = 0; read < _candidateCount && write < maxOccluders; read++)
            {
                OccluderCandidate candidate = _occluderCandidates[read];
                if (candidate.TriangleCount > remainingTriangles)
                {
                    _frameCandidatesDropped++;
                    continue;
                }

                _occluderCandidates[write++] = candidate;
                remainingTriangles -= candidate.TriangleCount;
            }

            _candidateCount = write;
            _frameOccludersSelected = _candidateCount;
            sortMilliseconds =
                Stopwatch.GetElapsedTime(sortStart).TotalMilliseconds;
        }

        private void TryCollectOccludersForPass(RenderCommandCollection commands, int renderPass, float minScreenArea)
        {
            using var renderingBufferScope = commands.EnterRenderingBufferReadScope();
            if (!commands.TryGetPublishedPassCommandsUnderReadLock(renderPass, out ICollection<RenderCommand> renderCommands))
            {
                return;
            }

            if (renderCommands is List<RenderCommand> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (!TryCollectOccluderCandidate(list[i], minScreenArea))
                        return;
                }
                return;
            }

            if (renderCommands is SnapshotSortedRenderCommandCollection sorted)
            {
                SnapshotSortedRenderCommandCollection.Enumerator enumerator = sorted.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (!TryCollectOccluderCandidate(enumerator.Current, minScreenArea))
                        return;
                }
            }
            // Unknown collection implementations are intentionally skipped: they may not
            // provide an allocation-free, stable traversal under the published read lock.
        }

        private bool TryCollectOccluderCandidate(RenderCommand command, float minScreenArea)
        {
            if (!TryReserveCandidateInspection())
                return false;

            if (command is not IRenderCommandMesh meshCommand ||
                !TryGetCommandSnapshot(meshCommand, out XRMeshRenderer? renderer, out Matrix4x4 modelMatrix, out XRMaterial? materialOverride, out RenderingParameters? optionsOverride, out uint instances) ||
                renderer is null ||
                instances != 1)
            {
                return true;
            }

            if (!TryReserveCandidateInspections(renderer.Submeshes.Count))
                return false;

            if (IsCpuOcclusionExcluded(meshCommand, renderer.Submeshes.Count))
                return true;

            XRMaterial? rendererMaterial = materialOverride ?? renderer.Material;
            if (!IsMaterialOccluderSafe(rendererMaterial) ||
                !IsRenderOptionsOccluderSafe(optionsOverride ?? rendererMaterial?.RenderOptions))
            {
                return true;
            }

            AABB? bounds = command.CullingVolume;
            if (!bounds.HasValue ||
                !MaskedOcclusionAabbTester.TryProjectAabb(bounds.Value, _leftViewProjectionMatrix, _leftBuffer.Width, _leftBuffer.Height, out ProjectedAabb projected) ||
                projected.OutsideFrustum ||
                projected.NormalizedArea(_leftBuffer.Width, _leftBuffer.Height) < minScreenArea)
            {
                return true;
            }

            if (renderer.Submeshes.Count == 0)
            {
                XRMesh? mesh = renderer.Mesh;
                XRMaterial? material = materialOverride ?? renderer.Material;
                return TryAddOccluder(command.StableQueryKey, mesh, material, optionsOverride, modelMatrix, projected);
            }

            for (int i = 0; i < renderer.Submeshes.Count; i++)
            {
                XRMeshRenderer.SubMesh submesh = renderer.Submeshes[i];
                XRMaterial? material = materialOverride ?? submesh.Material;
                if (!TryAddOccluder(command.StableQueryKey, submesh.Mesh, material, optionsOverride, modelMatrix, projected))
                    return false;
            }

            return true;
        }

        private bool TryAddOccluder(
            uint stableQueryKey,
            XRMesh? mesh,
            XRMaterial? material,
            RenderingParameters? optionsOverride,
            in Matrix4x4 modelMatrix,
            in ProjectedAabb projected)
        {
            RenderingParameters? options = optionsOverride ?? material?.RenderOptions;
            if (mesh is null ||
                !IsMeshOccluderSafe(mesh) ||
                !IsMaterialOccluderSafe(material) ||
                !IsRenderOptionsOccluderSafe(options))
                return true;

            int triangleCount = mesh.Triangles?.Count ?? 0;
            if (triangleCount == 0)
                return true;

            float normalizedArea = projected.NormalizedArea(_leftBuffer.Width, _leftBuffer.Height);
            float score = MaskedOcclusionRasterizer.ComputeOccluderScore(normalizedArea, triangleCount);
            InsertCandidate(new OccluderCandidate(stableQueryKey, mesh, options, modelMatrix, score, triangleCount));
            return true;
        }

        private void InsertCandidate(in OccluderCandidate candidate)
        {
            if (_candidateCapacity == 0)
            {
                _frameCandidatesDropped++;
                return;
            }

            if (_candidateCount < _candidateCapacity)
            {
                _occluderCandidates[_candidateCount] = candidate;
                HeapifyUp(_candidateCount++);
                return;
            }

            if (!IsHigherPriority(candidate, _occluderCandidates[0]))
            {
                _frameCandidatesDropped++;
                return;
            }

            _occluderCandidates[0] = candidate;
            HeapifyDown(0, _candidateCount);
            _frameCandidatesDropped++;
        }

        private static bool IsHigherPriority(in OccluderCandidate left, in OccluderCandidate right)
        {
            int scoreCompare = left.Score.CompareTo(right.Score);
            return scoreCompare != 0 ? scoreCompare > 0 : left.StableQueryKey < right.StableQueryKey;
        }

        private static bool IsLowerPriority(in OccluderCandidate left, in OccluderCandidate right)
            => IsHigherPriority(right, left);

        private void HeapifyUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (!IsLowerPriority(_occluderCandidates[index], _occluderCandidates[parent]))
                    return;

                (_occluderCandidates[index], _occluderCandidates[parent]) =
                    (_occluderCandidates[parent], _occluderCandidates[index]);
                index = parent;
            }
        }

        private void HeapifyDown(int index, int count)
        {
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= count)
                    return;

                int smallest = left;
                int right = left + 1;
                if (right < count && IsLowerPriority(_occluderCandidates[right], _occluderCandidates[left]))
                    smallest = right;
                if (!IsLowerPriority(_occluderCandidates[smallest], _occluderCandidates[index]))
                    return;

                (_occluderCandidates[index], _occluderCandidates[smallest]) =
                    (_occluderCandidates[smallest], _occluderCandidates[index]);
                index = smallest;
            }
        }

        private void SortCandidatesDescending()
        {
            int count = _candidateCount;
            for (int sortedIndex = count - 1; sortedIndex >= 0; sortedIndex--)
            {
                OccluderCandidate lowest = _occluderCandidates[0];
                _candidateCount--;
                if (_candidateCount > 0)
                {
                    _occluderCandidates[0] = _occluderCandidates[_candidateCount];
                    HeapifyDown(0, _candidateCount);
                }

                _occluderCandidates[sortedIndex] = lowest;
            }

            _candidateCount = count;
        }

        private bool TryReserveCandidateInspection()
            => TryReserveCandidateInspections(1);

        private bool TryReserveCandidateInspections(int count)
        {
            if (count < 0 || count > MaxCandidateInspectionsPerFrame - _frameCandidatesInspected)
            {
                _frameCandidatesDropped += Math.Max(1, count);
                return false;
            }

            _frameCandidatesInspected += count;
            return true;
        }

        private void ClearCandidateScratch()
        {
            Array.Clear(_occluderCandidates);
            _candidateCount = 0;
        }

        private static void GetBoundedBufferDimensions(int requestedWidth, int requestedHeight, out int width, out int height)
        {
            width = requestedWidth;
            height = requestedHeight;
            long pixelCount = (long)width * height;
            if (pixelCount <= MaxBufferPixelsPerEye)
                return;

            float scale = MathF.Sqrt(MaxBufferPixelsPerEye / (float)pixelCount);
            width = Math.Max(MaskedOcclusionBuffer.TileWidth, (int)(width * scale) / MaskedOcclusionBuffer.TileWidth * MaskedOcclusionBuffer.TileWidth);
            height = Math.Max(MaskedOcclusionBuffer.TileHeight, (int)(height * scale) / MaskedOcclusionBuffer.TileHeight * MaskedOcclusionBuffer.TileHeight);
        }

        internal static bool IsCpuOcclusionExcluded(IRenderCommandMesh command)
        {
            XRMaterial? material = command.MaterialOverride ?? command.Mesh?.Material;
            return IsCpuOcclusionExcluded(command, material, command.RenderOptionsOverride, MaxGeneralDeformationSubmeshInspections);
        }

        private static bool IsCpuOcclusionExcluded(IRenderCommandMesh command, int maxDeformationSubmeshInspections)
        {
            XRMaterial? material = command.MaterialOverride ?? command.Mesh?.Material;
            return IsCpuOcclusionExcluded(command, material, command.RenderOptionsOverride, maxDeformationSubmeshInspections);
        }

        private static bool IsCpuOcclusionExcluded(
            IRenderCommandMesh command,
            XRMaterial? material,
            RenderingParameters? optionsOverride,
            int maxDeformationSubmeshInspections)
        {
            return UsesDeformedMesh(command, maxDeformationSubmeshInspections) ||
                   command.RenderOptionsOverride?.ExcludeFromCpuOcclusion == true ||
                   optionsOverride?.ExcludeFromCpuOcclusion == true ||
                   material?.RenderOptions?.ExcludeFromCpuOcclusion == true;
        }

        private static bool UsesDeformedMesh(IRenderCommandMesh command, int maxSubmeshInspections)
        {
            XRMeshRenderer? renderer = command.Mesh;
            if (renderer is null)
                return false;

            if (IsDeformedMesh(renderer.Mesh))
                return true;

            if (renderer.Submeshes.Count > maxSubmeshInspections)
                return true;

            for (int i = 0; i < renderer.Submeshes.Count; i++)
            {
                if (IsDeformedMesh(renderer.Submeshes[i].Mesh))
                    return true;
            }

            return false;
        }

        private static bool IsDeformedMesh(XRMesh? mesh)
            => mesh is not null && (mesh.HasSkinning || mesh.BlendshapeCount > 0);

        private static bool TryGetCommandSnapshot(
            IRenderCommandMesh command,
            out XRMeshRenderer? mesh,
            out Matrix4x4 modelMatrix,
            out XRMaterial? materialOverride,
            out RenderingParameters? renderOptionsOverride,
            out uint instances)
        {
            if (command is RenderCommandMesh3D command3D)
                return command3D.TryGetCpuOcclusionSnapshot(out mesh, out modelMatrix, out materialOverride, out renderOptionsOverride, out instances);

            mesh = command.Mesh;
            modelMatrix = command.WorldMatrixIsModelMatrix ? command.WorldMatrix : Matrix4x4.Identity;
            materialOverride = command.MaterialOverride;
            renderOptionsOverride = command.RenderOptionsOverride;
            instances = command.Instances;
            return mesh is not null;
        }

        internal static bool IsMeshOccluderSafe(XRMesh mesh)
        {
            return mesh.Type == EPrimitiveType.Triangles &&
                   mesh.Triangles is { Count: > 0 } &&
                   mesh.VertexCount > 0 &&
                   !mesh.HasSkinning &&
                   mesh.BlendshapeCount == 0;
        }

        internal static bool IsRenderOptionsOccluderSafe(RenderingParameters? options)
        {
            if (options is null)
                return true;

            DepthTest depth = options.DepthTest;
            return !HasEnabledBlending(options) &&
                   options.AlphaToCoverage != ERenderParamUsage.Enabled &&
                   !options.ExcludeFromCpuOcclusion &&
                   depth.Enabled != ERenderParamUsage.Disabled &&
                   depth.UpdateDepth &&
                   (depth.Function == EComparison.Less || depth.Function == EComparison.Lequal) &&
                   options.CullMode != ECullMode.Both;
        }

        internal static bool IsMaterialOccluderSafe(XRMaterial? material)
        {
            if (material is null)
                return true;

            ETransparencyMode mode = material.GetEffectiveTransparencyMode();
            if (mode == ETransparencyMode.Masked || mode == ETransparencyMode.AlphaToCoverage)
                return false;

            ETransparencyMode inferred = material.InferTransparencyMode();
            return inferred != ETransparencyMode.Masked && inferred != ETransparencyMode.AlphaToCoverage;
        }

        private static bool HasEnabledBlending(RenderingParameters options)
        {
            if (options.BlendModeAllDrawBuffers?.Enabled == ERenderParamUsage.Enabled)
                return true;

            Dictionary<uint, BlendMode>? blendModes = options.BlendModesPerDrawBuffer;
            if (blendModes is null)
                return false;

            foreach (BlendMode blendMode in blendModes.Values)
            {
                if (blendMode.Enabled == ERenderParamUsage.Enabled)
                    return true;
            }

            return false;
        }

        private readonly struct OccluderCandidate(
            uint stableQueryKey,
            XRMesh mesh,
            RenderingParameters? renderOptions,
            Matrix4x4 modelMatrix,
            float score,
            int triangleCount)
        {
            public readonly uint StableQueryKey = stableQueryKey;
            public readonly XRMesh Mesh = mesh;
            public readonly RenderingParameters? RenderOptions = renderOptions;
            public readonly Matrix4x4 ModelMatrix = modelMatrix;
            public readonly float Score = score;
            public readonly int TriangleCount = triangleCount;
        }
    }
}
