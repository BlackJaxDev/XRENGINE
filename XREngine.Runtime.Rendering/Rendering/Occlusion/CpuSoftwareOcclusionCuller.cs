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
        private static readonly bool EnvironmentEnabled = ReadEnvironmentEnabled();

        private readonly MaskedOcclusionBuffer _buffer = new();
        private readonly MaskedOcclusionRasterizer _rasterizer = new();
        private readonly MaskedOcclusionAabbTester _tester = new();
        private readonly List<OccluderCandidate> _occluderCandidates = new(128);
        private readonly HashSet<uint> _selectedOccluderKeys = new();

        private XRCamera? _camera;
        private Matrix4x4 _viewProjectionMatrix;
        private bool _frameOpen;
        private bool _occludersSubmitted;
        private RenderCommandCollection? _sourceCommands;
        private int _frameOccludersSelected;
        private int _frameOccludersRasterized;
        private int _frameTestsRun;
        private int _viewportWidth;
        private int _viewportHeight;
        private ulong _renderFrameId;

        public static bool IsEnabled
        {
            get
            {
                if (RuntimeEngine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling)
                    return true;

                return EnvironmentEnabled;
            }
        }

        public bool IsFrameOpen => _frameOpen;

        public bool IsFrameInitializedFor(XRCamera camera, int viewportWidth, int viewportHeight)
            => _frameOpen &&
               ReferenceEquals(_camera, camera) &&
               _viewportWidth == viewportWidth &&
               _viewportHeight == viewportHeight &&
               _renderFrameId == RuntimeEngine.Rendering.State.RenderFrameId;

        internal bool HasOccludersFrom(RenderCommandCollection commands)
            => _occludersSubmitted && ReferenceEquals(_sourceCommands, commands);

        public void BeginFrame(XRCamera camera, int viewportWidth, int viewportHeight)
        {
            if (!IsEnabled)
                return;

            long start = Stopwatch.GetTimestamp();
            _camera = camera;
            _viewProjectionMatrix = camera.ViewProjectionMatrix;
            _viewportWidth = Math.Max(1, viewportWidth);
            _viewportHeight = Math.Max(1, viewportHeight);
            _renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            _frameOpen = true;
            _occludersSubmitted = false;
            _sourceCommands = null;
            _frameOccludersSelected = 0;
            _frameOccludersRasterized = 0;
            _frameTestsRun = 0;
            _selectedOccluderKeys.Clear();

            int bufferWidth = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocBufferWidth, 64, 4096);
            int bufferHeight = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocBufferHeight, 32, 4096);
            _buffer.Resize(bufferWidth, bufferHeight);
            _buffer.Clear();

            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            OcclusionTelemetry.RecordCpuSocFrameBegin(elapsedMs, RuntimeEngine.EffectiveSettings.CpuSocDebugForceVisible);
        }

        public void SubmitOccludersFromOpaqueCommands(RenderCommandCollection commands)
        {
            if (!IsEnabled || !_frameOpen || _camera is null || _occludersSubmitted)
                return;

            _occludersSubmitted = true;
            _sourceCommands = commands;
            long start = Stopwatch.GetTimestamp();
            SelectOccluders(commands);

            int triangleBudget = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocOccluderTriangleBudget, 0, 1_000_000);
            int rasterized = 0;
            for (int i = 0; i < _occluderCandidates.Count && triangleBudget > 0; i++)
            {
                OccluderCandidate candidate = _occluderCandidates[i];
                int drawn = _rasterizer.RasterizeMesh(
                    _buffer,
                    candidate.Mesh,
                    candidate.ModelMatrix,
                    _viewProjectionMatrix,
                    candidate.RenderOptions,
                    triangleBudget);

                if (drawn <= 0)
                    continue;

                triangleBudget -= drawn;
                rasterized++;
                _selectedOccluderKeys.Add(candidate.StableQueryKey);
            }

            _frameOccludersRasterized = rasterized;
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            OcclusionTelemetry.RecordCpuSocOccluders(_frameOccludersSelected, rasterized, _buffer.TilesClosed, elapsedMs);
        }

        public bool TestVisible(uint stableQueryKey, in AABB worldBounds)
        {
            if (!IsEnabled || !_frameOpen || _camera is null)
                return true;

            if (RuntimeEngine.EffectiveSettings.CpuSocDebugForceVisible)
                return true;

            if (_selectedOccluderKeys.Contains(stableQueryKey))
                return true;

            _frameTestsRun++;
            if (_frameOccludersRasterized == 0)
                return true;

            long start = Stopwatch.GetTimestamp();
            bool visible = _tester.TestVisible(_buffer, _viewProjectionMatrix, worldBounds);
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            OcclusionTelemetry.RecordCpuSocTest(elapsedMs, !visible);
            return visible;
        }

        public bool TestVisible(in AABB worldBounds)
            => TestVisible(0u, worldBounds);

        public CpuSoftwareOcclusionDebugReadback? ReadDebugBuffer()
            => RuntimeEngine.EffectiveSettings.CpuSocDebugVisualization && _frameOpen
                ? _buffer.CreateDebugReadback()
                : null;

        public int FrameOccludersSubmitted => _frameOccludersRasterized;
        public int FrameTestsRun => _frameTestsRun;

        private static bool ReadEnvironmentEnabled()
        {
            string? raw = Environment.GetEnvironmentVariable("XRE_CPU_SOC_OCCLUSION");
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            ReadOnlySpan<char> value = raw.AsSpan().Trim();
            return value.SequenceEqual("1".AsSpan()) ||
                   value.Equals("true".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private void SelectOccluders(RenderCommandCollection commands)
        {
            _occluderCandidates.Clear();
            int maxOccluders = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocMaxOccluders, 0, 4096);
            if (maxOccluders == 0)
                return;

            float minScreenArea = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuSocMinOccluderScreenArea, 0.0f, 1.0f);
            TryCollectOccludersForPass(commands, (int)EDefaultRenderPass.OpaqueDeferred, minScreenArea);
            TryCollectOccludersForPass(commands, (int)EDefaultRenderPass.OpaqueForward, minScreenArea);

            _occluderCandidates.Sort(static (a, b) =>
            {
                int scoreCompare = b.Score.CompareTo(a.Score);
                return scoreCompare != 0 ? scoreCompare : a.StableQueryKey.CompareTo(b.StableQueryKey);
            });
            if (_occluderCandidates.Count > maxOccluders)
                _occluderCandidates.RemoveRange(maxOccluders, _occluderCandidates.Count - maxOccluders);
            _frameOccludersSelected = _occluderCandidates.Count;
        }

        private void TryCollectOccludersForPass(RenderCommandCollection commands, int renderPass, float minScreenArea)
        {
            if (!commands.TryGetRenderingPassCommands(renderPass, out IReadOnlyCollection<RenderCommand>? renderCommands) ||
                renderCommands is null)
            {
                return;
            }

            foreach (RenderCommand command in renderCommands)
            {
                if (command is not IRenderCommandMesh meshCommand)
                    continue;

                if (!TryGetCommandSnapshot(
                    meshCommand,
                    out XRMeshRenderer? renderer,
                    out Matrix4x4 modelMatrix,
                    out XRMaterial? materialOverride,
                    out RenderingParameters? optionsOverride,
                    out uint instances))
                {
                    continue;
                }

                if (renderer is null || instances != 1 || IsCpuOcclusionExcluded(meshCommand))
                    continue;

                if (!IsRenderOptionsOccluderSafe(optionsOverride ?? materialOverride?.RenderOptions ?? renderer.Material?.RenderOptions))
                    continue;

                AABB? bounds = command.CullingVolume;
                if (!bounds.HasValue ||
                    !MaskedOcclusionAabbTester.TryProjectAabb(bounds.Value, _viewProjectionMatrix, _buffer.Width, _buffer.Height, out ProjectedAabb projected) ||
                    projected.OutsideFrustum ||
                    projected.NormalizedArea(_buffer.Width, _buffer.Height) < minScreenArea)
                {
                    continue;
                }

                if (renderer.Submeshes.Count == 0)
                {
                    XRMesh? mesh = renderer.Mesh;
                    XRMaterial? material = materialOverride ?? renderer.Material;
                    TryAddOccluder(command.StableQueryKey, mesh, material, optionsOverride, modelMatrix, projected);
                    continue;
                }

                for (int i = 0; i < renderer.Submeshes.Count; i++)
                {
                    XRMeshRenderer.SubMesh submesh = renderer.Submeshes[i];
                    XRMaterial? material = materialOverride ?? submesh.Material;
                    TryAddOccluder(command.StableQueryKey, submesh.Mesh, material, optionsOverride, modelMatrix, projected);
                }
            }
        }

        private void TryAddOccluder(
            uint stableQueryKey,
            XRMesh? mesh,
            XRMaterial? material,
            RenderingParameters? optionsOverride,
            in Matrix4x4 modelMatrix,
            in ProjectedAabb projected)
        {
            RenderingParameters? options = optionsOverride ?? material?.RenderOptions;
            if (mesh is null || !IsMeshOccluderSafe(mesh) || !IsRenderOptionsOccluderSafe(options))
                return;

            int triangleCount = mesh.Triangles?.Count ?? 0;
            if (triangleCount == 0)
                return;

            float score = projected.NormalizedArea(_buffer.Width, _buffer.Height) * triangleCount;
            _occluderCandidates.Add(new OccluderCandidate(stableQueryKey, mesh, options, modelMatrix, score));
        }

        internal static bool IsCpuOcclusionExcluded(IRenderCommandMesh command)
        {
            XRMaterial? material = command.MaterialOverride ?? command.Mesh?.Material;
            return IsCpuOcclusionExcluded(command, material, command.RenderOptionsOverride);
        }

        private static bool IsCpuOcclusionExcluded(
            IRenderCommandMesh command,
            XRMaterial? material,
            RenderingParameters? optionsOverride)
        {
            return command.RenderOptionsOverride?.ExcludeFromCpuOcclusion == true ||
                   optionsOverride?.ExcludeFromCpuOcclusion == true ||
                   material?.RenderOptions?.ExcludeFromCpuOcclusion == true;
        }

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
            float score)
        {
            public readonly uint StableQueryKey = stableQueryKey;
            public readonly XRMesh Mesh = mesh;
            public readonly RenderingParameters? RenderOptions = renderOptions;
            public readonly Matrix4x4 ModelMatrix = modelMatrix;
            public readonly float Score = score;
        }
    }
}
