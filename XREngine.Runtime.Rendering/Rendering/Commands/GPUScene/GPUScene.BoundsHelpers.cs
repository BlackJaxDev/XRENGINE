// =====================================================================================
// GPUScene.BoundsHelpers.cs - World-space bounds math + GPU-owned command AABB API.
// Part of the GPUScene partial class. See GPUScene.cs for the canonical class summary.
// =====================================================================================

using XREngine.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {

        private static float ComputeMaxAxisScale(in Matrix4x4 m)
        {
            // System.Numerics uses basis columns for Vector3.Transform:
            // x' = x*M11 + y*M21 + z*M31 + M41, etc.
            Vector3 xAxis = new(m.M11, m.M12, m.M13);
            Vector3 yAxis = new(m.M21, m.M22, m.M23);
            Vector3 zAxis = new(m.M31, m.M32, m.M33);

            float sx = xAxis.Length();
            float sy = yAxis.Length();
            float sz = zAxis.Length();

            float s = MathF.Max(sx, MathF.Max(sy, sz));
            if (float.IsNaN(s) || float.IsInfinity(s) || s < 0f)
                return 0f;

            return s;
        }

        private static float ComputeMaxAxisScale(in AffineMatrix4x3 m)
        {
            Vector3 xAxis = new(m.M11, m.M12, m.M13);
            Vector3 yAxis = new(m.M21, m.M22, m.M23);
            Vector3 zAxis = new(m.M31, m.M32, m.M33);

            float sx = xAxis.Length();
            float sy = yAxis.Length();
            float sz = zAxis.Length();

            float s = MathF.Max(sx, MathF.Max(sy, sz));
            if (float.IsNaN(s) || float.IsInfinity(s) || s < 0f)
                return 0f;

            return s;
        }

        private static void SetWorldSpaceBoundingSphere(ref GPUIndirectRenderCommand cmd, in AABB localBounds, in Matrix4x4 modelMatrix)
        {
            Vector3 localCenter = localBounds.Center;
            float localRadius = localBounds.HalfExtents.Length();

            Vector3 worldCenter;
            float maxScale;
            if (AffineMatrix4x3.TryFromMatrix4x4(modelMatrix, out AffineMatrix4x3 affineModelMatrix))
            {
                worldCenter = affineModelMatrix.TransformPosition(localCenter);
                maxScale = ComputeMaxAxisScale(affineModelMatrix);
            }
            else
            {
                worldCenter = Vector3.Transform(localCenter, modelMatrix);
                maxScale = ComputeMaxAxisScale(modelMatrix);
            }
            float worldRadius = localRadius * maxScale;

            if (float.IsNaN(worldRadius) || float.IsInfinity(worldRadius) || worldRadius < 0f)
                worldRadius = 0f;

            cmd.SetBoundingSphere(worldCenter, worldRadius);
        }

        private static BoundsGpu ComputeWorldBoundsGpu(in AABB localBounds, in Matrix4x4 modelMatrix, uint version)
        {
            Vector3 localCenter = localBounds.Center;
            float localRadius = localBounds.HalfExtents.Length();

            Vector3 worldCenter;
            float maxScale;
            if (AffineMatrix4x3.TryFromMatrix4x4(modelMatrix, out AffineMatrix4x3 affineModelMatrix))
            {
                worldCenter = affineModelMatrix.TransformPosition(localCenter);
                maxScale = ComputeMaxAxisScale(affineModelMatrix);
            }
            else
            {
                worldCenter = Vector3.Transform(localCenter, modelMatrix);
                maxScale = ComputeMaxAxisScale(modelMatrix);
            }

            float worldRadius = localRadius * maxScale;
            if (float.IsNaN(worldRadius) || float.IsInfinity(worldRadius) || worldRadius < 0f)
                worldRadius = 0f;

            localBounds.GetCorners(
                out Vector3 c0,
                out Vector3 c1,
                out Vector3 c2,
                out Vector3 c3,
                out Vector3 c4,
                out Vector3 c5,
                out Vector3 c6,
                out Vector3 c7);

            Vector3 w0, w1, w2, w3, w4, w5, w6, w7;
            if (AffineMatrix4x3.TryFromMatrix4x4(modelMatrix, out AffineMatrix4x3 affine))
            {
                w0 = affine.TransformPosition(c0);
                w1 = affine.TransformPosition(c1);
                w2 = affine.TransformPosition(c2);
                w3 = affine.TransformPosition(c3);
                w4 = affine.TransformPosition(c4);
                w5 = affine.TransformPosition(c5);
                w6 = affine.TransformPosition(c6);
                w7 = affine.TransformPosition(c7);
            }
            else
            {
                w0 = Vector3.Transform(c0, modelMatrix);
                w1 = Vector3.Transform(c1, modelMatrix);
                w2 = Vector3.Transform(c2, modelMatrix);
                w3 = Vector3.Transform(c3, modelMatrix);
                w4 = Vector3.Transform(c4, modelMatrix);
                w5 = Vector3.Transform(c5, modelMatrix);
                w6 = Vector3.Transform(c6, modelMatrix);
                w7 = Vector3.Transform(c7, modelMatrix);
            }

            Vector3 min = Vector3.Min(Vector3.Min(Vector3.Min(w0, w1), Vector3.Min(w2, w3)),
                                      Vector3.Min(Vector3.Min(w4, w5), Vector3.Min(w6, w7)));
            Vector3 max = Vector3.Max(Vector3.Max(Vector3.Max(w0, w1), Vector3.Max(w2, w3)),
                                      Vector3.Max(Vector3.Max(w4, w5), Vector3.Max(w6, w7)));

            return new BoundsGpu
            {
                BoundingSphere = new Vector4(worldCenter, worldRadius),
                AabbMin = new Vector4(min, 0f),
                AabbMax = new Vector4(max, 0f),
                BoundsVersion = version,
            };
        }

        private static BoundsGpu ComputeRenderCullingBoundsGpu(
            RenderInfo? renderInfo,
            in AABB fallbackLocal,
            in Matrix4x4 fallbackMatrix,
            uint version)
        {
            AABB localBounds = fallbackLocal;
            Matrix4x4 basis = fallbackMatrix;

            if (renderInfo is RenderInfo3D info3d)
            {
                if (info3d.LocalCullingVolume is AABB localOverride)
                    localBounds = localOverride;
                basis = info3d.CullingOffsetMatrix;
            }

            return ComputeWorldBoundsGpu(localBounds, basis, version);
        }

        // Tight world-space AABB for a single command, written into _commandAabbBuffer.
        // Layout matches the shader-side Aabb struct in bvh_nodes.glslinc / bvh_build.comp:
        //   vec4 minBounds; vec4 maxBounds;   (8 floats, 32 bytes)
        // The W components are unused and kept at 0.0 for alignment.
        [StructLayout(LayoutKind.Sequential)]
        private struct CommandWorldAabb
        {
            public Vector4 Min;
            public Vector4 Max;
        }

        private const uint CommandAabbStrideBytes = 8u * sizeof(float);

        private readonly record struct AabbTransferBatch(uint DirtyLeafCount, ulong UploadBytes, ulong CopyBytes);

        private void WriteCommandAabb(uint commandIndex, in CommandWorldAabb entry, bool uploadImmediately, bool forceDirty = false)
        {
            XRDataBuffer? buffer = _commandAabbBuffer;
            if (buffer is null || commandIndex >= buffer.ElementCount)
                return;

            CommandWorldAabb previous = buffer.GetDataRawAtIndex<CommandWorldAabb>(commandIndex);
            if (!uploadImmediately && !forceDirty && previous.Min == entry.Min && previous.Max == entry.Max)
                return;

            buffer.SetDataRawAtIndex(commandIndex, entry);
            if (uploadImmediately)
            {
                buffer.PushSubData(checked((int)(commandIndex * CommandAabbStrideBytes)), CommandAabbStrideBytes);
                _pendingCommandAabbUploadBytes += CommandAabbStrideBytes;
            }
            else
                _commandAabbDirtyRange.Mark(commandIndex);

            EnsureCommandAabbDirtyLeafCapacity(buffer.ElementCount);
            if (!_commandAabbDirtyLeaves[commandIndex])
            {
                _commandAabbDirtyLeaves[commandIndex] = true;
                _pendingCommandAabbDirtyLeafCount++;
                _commandAabbAccountingRange.Mark(commandIndex);
            }

            Interlocked.Increment(ref _commandAabbRevision);
            Vector3 min = new(entry.Min.X, entry.Min.Y, entry.Min.Z);
            Vector3 max = new(entry.Max.X, entry.Max.Y, entry.Max.Z);
            if (!uploadImmediately && _hasBvhNormalizationBounds && IsFinite(min) && IsFinite(max) &&
                !Contains(_bvhNormalizationBounds, min, max))
            {
                MarkBvhDirty(GpuBvhRebuildReason.NormalizationDomainEscaped);
                return;
            }

            if (_bvhReady && !_bvhDirty && _bvhPrimitiveCount == UpdatingCommandCount)
                _bvhRefitPending = true;
        }

        /// <summary>
        /// Mirrors a swap-removed command's world AABB into its new command slot.
        /// Command metadata and BVH leaf input use the same dense index space and
        /// must move together.
        /// </summary>
        private void MoveCommandAabb(uint sourceIndex, uint targetIndex)
        {
            XRDataBuffer? buffer = _commandAabbBuffer;
            if (buffer is null || sourceIndex >= buffer.ElementCount || targetIndex >= buffer.ElementCount)
                return;

            CommandWorldAabb moved = buffer.GetDataRawAtIndex<CommandWorldAabb>(sourceIndex);
            WriteCommandAabb(targetIndex, moved, uploadImmediately: false);
        }

        private AabbTransferBatch FlushCommandAabbDirtyRange()
        {
            if (_commandAabbDirtyRange.HasValue && _commandAabbBuffer is not null)
            {
                uint min = _commandAabbDirtyRange.Min;
                uint maxExclusive = Math.Min(_commandAabbDirtyRange.MaxExclusive, _commandAabbBuffer.ElementCount);
                if (maxExclusive > min)
                {
                    uint bytes = (maxExclusive - min) * CommandAabbStrideBytes;
                    _commandAabbBuffer.PushSubData(checked((int)(min * CommandAabbStrideBytes)), bytes);
                    _pendingCommandAabbUploadBytes += bytes;
                }
            }

            var result = new AabbTransferBatch(
                _pendingCommandAabbDirtyLeafCount,
                _pendingCommandAabbUploadBytes,
                _pendingCommandAabbCopyBytes);
            if (_commandAabbAccountingRange.HasValue)
            {
                uint min = _commandAabbAccountingRange.Min;
                uint maxExclusive = Math.Min(_commandAabbAccountingRange.MaxExclusive, (uint)_commandAabbDirtyLeaves.Length);
                for (uint i = min; i < maxExclusive; ++i)
                    _commandAabbDirtyLeaves[i] = false;
            }
            _commandAabbDirtyRange.Clear();
            _commandAabbAccountingRange.Clear();
            _pendingCommandAabbDirtyLeafCount = 0u;
            _pendingCommandAabbUploadBytes = 0u;
            _pendingCommandAabbCopyBytes = 0u;
            return result;
        }

        private void EnsureCommandAabbDirtyLeafCapacity(uint count)
        {
            if ((uint)_commandAabbDirtyLeaves.Length >= count)
                return;

            Array.Resize(ref _commandAabbDirtyLeaves, checked((int)NextPowerOfTwo(Math.Max(count, 1u))));
        }

        /// <summary>
        /// Initializes historical command AABBs when GPU BVH ownership is enabled
        /// after commands already exist. BoundsGpu is the authoritative render
        /// snapshot; the command sphere and configured world bounds are conservative
        /// recovery sources for malformed entries.
        /// </summary>
        private void BackfillCommandAabbsFromRenderSnapshot(uint commandCount)
        {
            XRDataBuffer? commandAabbs = _commandAabbBuffer;
            XRDataBuffer? commands = _allLoadedCommandsBuffer;
            if (commandAabbs is null || commands is null)
                return;

            uint count = Math.Min(commandCount, Math.Min(commandAabbs.ElementCount, commands.ElementCount));
            for (uint commandIndex = 0u; commandIndex < count; ++commandIndex)
            {
                // The skinned-bounds reducer owns these slots and has already
                // published its sentinel/output in the acceleration-structure
                // pass. CPU backfill must not overwrite GPU-produced bounds.
                if (IsCommandOwnedByGpuAabb(commandIndex))
                    continue;

                GPUIndirectRenderCommand command = commands.GetDataRawAtIndex<GPUIndirectRenderCommand>(commandIndex);
                uint boundsId = command.BoundsID;
                if (_allLoadedDrawMetadataBuffer is not null && commandIndex < _allLoadedDrawMetadataBuffer.ElementCount)
                    boundsId = _allLoadedDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(commandIndex).BoundsID;

                BoundsGpu bounds = default;
                bool hasBounds = false;
                XRDataBuffer? boundsBuffer = _allLoadedBoundsBuffer;
                if (boundsBuffer is not null && boundsId < boundsBuffer.ElementCount)
                {
                    bounds = boundsBuffer.GetDataRawAtIndex<BoundsGpu>(boundsId);
                    hasBounds = bounds.BoundsVersion != 0u;
                }

                CommandWorldAabb entry = CreateBackfillCommandAabb(hasBounds, bounds, command.BoundingSphere, _bounds);
                WriteCommandAabb(commandIndex, entry, uploadImmediately: false, forceDirty: true);
            }

            _commandAabbPublishedContentVersion = _lastSwappedCommandsContentVersion;
            _commandAabbBackfillRequired = count != commandCount ||
                _commandAabbPublishedContentVersion != Interlocked.Read(ref _updatingCommandsContentVersion);
        }

        private static CommandWorldAabb CreateBackfillCommandAabb(
            bool hasBounds,
            in BoundsGpu bounds,
            in Vector4 commandSphere,
            in AABB configuredBounds)
        {
            Vector3 min = bounds.AabbMin.XYZ();
            Vector3 max = bounds.AabbMax.XYZ();
            if (hasBounds && IsFinite(min) && IsFinite(max) &&
                min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z)
            {
                return new CommandWorldAabb { Min = new Vector4(min, 0.0f), Max = new Vector4(max, 0.0f) };
            }

            Vector3 center = commandSphere.XYZ();
            float radius = commandSphere.W;
            if (IsFinite(center) && float.IsFinite(radius) && radius >= 0.0f)
            {
                Vector3 extent = new(radius);
                return new CommandWorldAabb
                {
                    Min = new Vector4(center - extent, 0.0f),
                    Max = new Vector4(center + extent, 0.0f),
                };
            }

            if (configuredBounds.IsValid && IsFinite(configuredBounds.Min) && IsFinite(configuredBounds.Max))
            {
                return new CommandWorldAabb
                {
                    Min = new Vector4(configuredBounds.Min, 0.0f),
                    Max = new Vector4(configuredBounds.Max, 0.0f),
                };
            }

            // Last-resort finite conservative domain. Keeping the entry valid is
            // safer than letting an uninitialized zero/sentinel box enter Morton build.
            const float FallbackExtent = 1.0e10f;
            return new CommandWorldAabb
            {
                Min = new Vector4(-FallbackExtent, -FallbackExtent, -FallbackExtent, 0.0f),
                Max = new Vector4(FallbackExtent, FallbackExtent, FallbackExtent, 0.0f),
            };
        }

        /// <summary>
        /// Computes a tight world-space AABB from <paramref name="renderInfo"/>'s culling
        /// volume + basis matrix (falling back to <paramref name="fallbackLocal"/> +
        /// <paramref name="fallbackMatrix"/> when the renderable hasn't yet populated those),
        /// and writes it into <see cref="_commandAabbBuffer"/> at <paramref name="commandIndex"/>.
        /// Only meaningful when the internal BVH is active; callers must gate accordingly.
        ///
        /// When the renderer owning this command has been registered via
        /// <see cref="SetRendererOwnsGpuAabb"/>, this method writes a +inf/-inf sentinel
        /// instead so the GPU reducer (<c>SkinnedBoundsReduce.comp</c>) can monotonically
        /// fill in the actual world-space bounds via atomic min/max each frame.
        /// </summary>
        private void WriteTightCommandAabb(
            uint commandIndex,
            RenderInfo? renderInfo,
            in AABB fallbackLocal,
            in Matrix4x4 fallbackMatrix)
        {
            // Path A: GPU owns this slot. Seed with +inf/-inf so the per-frame atomic
            // reduce produces the correct envelope. Do not perform the CPU 8-corner
            // transform, which would just be overwritten by the reducer.
            if (IsCommandOwnedByGpuAabb(commandIndex))
            {
                WriteCommandAabbSentinel(commandIndex);
                return;
            }

            AABB localBounds = fallbackLocal;
            Matrix4x4 basis = fallbackMatrix;

            if (renderInfo is RenderInfo3D info3d)
            {
                if (info3d.LocalCullingVolume is AABB localOverride)
                    localBounds = localOverride;
                basis = info3d.CullingOffsetMatrix;
            }

            // 8-corner AABB transform (handles arbitrary rotation/scale tightly).
            localBounds.GetCorners(
                out Vector3 c0,
                out Vector3 c1,
                out Vector3 c2,
                out Vector3 c3,
                out Vector3 c4,
                out Vector3 c5,
                out Vector3 c6,
                out Vector3 c7);

            Vector3 w0, w1, w2, w3, w4, w5, w6, w7;
            if (AffineMatrix4x3.TryFromMatrix4x4(basis, out AffineMatrix4x3 affine))
            {
                w0 = affine.TransformPosition(c0);
                w1 = affine.TransformPosition(c1);
                w2 = affine.TransformPosition(c2);
                w3 = affine.TransformPosition(c3);
                w4 = affine.TransformPosition(c4);
                w5 = affine.TransformPosition(c5);
                w6 = affine.TransformPosition(c6);
                w7 = affine.TransformPosition(c7);
            }
            else
            {
                Matrix4x4 m = basis;
                w0 = Vector3.Transform(c0, m);
                w1 = Vector3.Transform(c1, m);
                w2 = Vector3.Transform(c2, m);
                w3 = Vector3.Transform(c3, m);
                w4 = Vector3.Transform(c4, m);
                w5 = Vector3.Transform(c5, m);
                w6 = Vector3.Transform(c6, m);
                w7 = Vector3.Transform(c7, m);
            }

            Vector3 min = Vector3.Min(Vector3.Min(Vector3.Min(w0, w1), Vector3.Min(w2, w3)),
                                      Vector3.Min(Vector3.Min(w4, w5), Vector3.Min(w6, w7)));
            Vector3 max = Vector3.Max(Vector3.Max(Vector3.Max(w0, w1), Vector3.Max(w2, w3)),
                                      Vector3.Max(Vector3.Max(w4, w5), Vector3.Max(w6, w7)));

            EnsureCommandAabbBuffer(Math.Max(commandIndex + 1u, UpdatingCommandCount));
            var buffer = _commandAabbBuffer;
            if (buffer is null)
                return;

            var entry = new CommandWorldAabb
            {
                Min = new Vector4(min, 0f),
                Max = new Vector4(max, 0f),
            };
            WriteCommandAabb(commandIndex, entry, uploadImmediately: false);
        }

        // -------------------------------------------------------------------------
        // Path A (GPU-direct skinned-recompute bounds) public API
        // -------------------------------------------------------------------------
        // Renderers that produce their world-space leaf AABB via the GPU reducer
        // (SkinnedBoundsReduce.comp running on SkinningPrepass output) opt in by
        // calling SetRendererOwnsGpuAabb(renderer, true). All commands for that
        // renderer then bypass the CPU 8-corner transform in WriteTightCommandAabb
        // and receive a +inf/-inf sentinel instead, which the reducer fills in
        // via atomic min/max each frame.
        //
        // The reducer expects the bounds buffer to be a packed uvec4[] where
        //     slot N stores: BoundsBits[2*N+0] = (min.x|y|z bits, _),
        //                    BoundsBits[2*N+1] = (max.x|y|z bits, _).
        // _commandAabbBuffer already matches this layout (Vector4 Min; Vector4 Max
        // per element = 32 bytes), so slotIndex == commandIndex directly.

        private readonly HashSet<XRMeshRenderer> _gpuAabbRenderers = [];
        private static readonly Vector4 _gpuAabbSentinelMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 0f);
        private static readonly Vector4 _gpuAabbSentinelMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, 0f);

        /// <summary>
        /// The raw command-AABB SSBO used as BVH leaf bounds. May be null until the
        /// first command has been registered while the internal BVH is enabled.
        /// </summary>
        public XRDataBuffer? CommandAabbBuffer => _commandAabbBuffer;

        /// <summary>
        /// Ensures <see cref="CommandAabbBuffer"/> has at least <paramref name="minCount"/>
        /// slots allocated.
        /// </summary>
        public void EnsureCommandAabbCapacity(uint minCount)
            => EnsureCommandAabbBuffer(Math.Max(minCount, UpdatingCommandCount));

        /// <summary>
        /// Writes a +inf/-inf sentinel into the supplied slot so a subsequent atomic
        /// reduce produces the correct envelope. Pushes the 32-byte subrange.
        /// </summary>
        public void WriteCommandAabbSentinel(uint commandIndex)
        {
            EnsureCommandAabbBuffer(Math.Max(commandIndex + 1u, UpdatingCommandCount));
            var buffer = _commandAabbBuffer;
            if (buffer is null)
                return;

            var entry = new CommandWorldAabb
            {
                Min = _gpuAabbSentinelMin,
                Max = _gpuAabbSentinelMax,
            };
            // The following GPU reduction consumes this sentinel immediately,
            // so this path cannot wait for the normal coalesced publication.
            WriteCommandAabb(commandIndex, entry, uploadImmediately: true);
        }

        /// <summary>
        /// Mark or unmark a renderer as owner of its world-space command AABBs.
        /// When marked, <see cref="WriteTightCommandAabb"/> writes sentinels for the
        /// renderer's commands instead of the CPU 8-corner transform.
        /// </summary>
        public void SetRendererOwnsGpuAabb(XRMeshRenderer renderer, bool enabled)
        {
            if (renderer is null)
                return;

            if (enabled)
                _gpuAabbRenderers.Add(renderer);
            else
                _gpuAabbRenderers.Remove(renderer);
        }

        /// <summary>True when the given renderer's command AABBs are produced on the GPU.</summary>
        public bool IsRendererOwnsGpuAabb(XRMeshRenderer renderer)
            => renderer is not null && _gpuAabbRenderers.Contains(renderer);

        private bool IsCommandOwnedByGpuAabb(uint commandIndex)
        {
            if (_gpuAabbRenderers.Count == 0)
                return false;
            if (!_commandIndexLookup.TryGetValue(commandIndex, out var entry))
                return false;
            var renderer = entry.command?.Mesh;
            return renderer is not null && _gpuAabbRenderers.Contains(renderer);
        }

        private const float BvhConfiguredBoundsMaxAxisDilution = 2.0f;
        private const float BvhNormalizationHysteresisMaxVolumeRatio = 4.0f;

        private AABB ResolveBvhNormalizationBounds(uint commandCount)
        {
            Vector3 liveMin = new(float.PositiveInfinity);
            Vector3 liveMax = new(float.NegativeInfinity);
            uint validCount = 0u;

            if (_commandAabbBuffer is not null)
            {
                uint count = Math.Min(commandCount, _commandAabbBuffer.ElementCount);
                for (uint i = 0u; i < count; ++i)
                {
                    CommandWorldAabb aabb = _commandAabbBuffer.GetDataRawAtIndex<CommandWorldAabb>(i);
                    Vector3 min = aabb.Min.XYZ();
                    Vector3 max = aabb.Max.XYZ();
                    if (!IsFinite(min) || !IsFinite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                        continue;

                    liveMin = Vector3.Min(liveMin, min);
                    liveMax = Vector3.Max(liveMax, max);
                    validCount++;
                }
            }

            AABB configured = _bounds;
            bool configuredValid = configured.IsValid && IsFinite(configured.Min) && IsFinite(configured.Max);
            if (validCount == 0u)
                return configuredValid ? configured : AABB.FromCenterSize(Vector3.Zero, Vector3.One);

            Vector3 liveSize = Vector3.Max(liveMax - liveMin, new Vector3(1e-3f));
            Vector3 margin = Vector3.Max(liveSize * 0.1f, new Vector3(0.5f));
            AABB candidate = new(liveMin - margin, liveMax + margin);

            // Prefer explicitly configured world bounds when they contain the
            // scene without diluting Morton precision by more than one octree level.
            if (configuredValid && Contains(configured, liveMin, liveMax))
            {
                Vector3 configuredSize = configured.Max - configured.Min;
                if (configuredSize.X <= liveSize.X * BvhConfiguredBoundsMaxAxisDilution &&
                    configuredSize.Y <= liveSize.Y * BvhConfiguredBoundsMaxAxisDilution &&
                    configuredSize.Z <= liveSize.Z * BvhConfiguredBoundsMaxAxisDilution)
                    candidate = configured;
            }

            // Retain a still-useful prior domain to avoid rebuild oscillation.
            if (_hasBvhNormalizationBounds &&
                Contains(_bvhNormalizationBounds, candidate.Min, candidate.Max) &&
                Volume(_bvhNormalizationBounds) <= Volume(candidate) * BvhNormalizationHysteresisMaxVolumeRatio)
                return _bvhNormalizationBounds;

            _bvhNormalizationBounds = candidate;
            _hasBvhNormalizationBounds = true;
            return candidate;
        }

        private static bool Contains(in AABB outer, in Vector3 min, in Vector3 max)
            => min.X >= outer.Min.X && min.Y >= outer.Min.Y && min.Z >= outer.Min.Z &&
               max.X <= outer.Max.X && max.Y <= outer.Max.Y && max.Z <= outer.Max.Z;

        private static bool IsFinite(in Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static float Volume(in AABB bounds)
        {
            Vector3 size = Vector3.Max(bounds.Max - bounds.Min, new Vector3(1e-3f));
            return size.X * size.Y * size.Z;
        }

        /// <summary>
        /// Fills <paramref name="output"/> with every active GPU command index that
        /// belongs to <paramref name="renderer"/>. Returns true if at least one was found.
        /// </summary>
        public bool TryGetCommandIndicesForRenderer(XRMeshRenderer renderer, List<uint> output)
        {
            output?.Clear();
            if (renderer is null || output is null || _commandIndicesPerMeshCommand.Count == 0)
                return false;

            bool any = false;
            foreach (var kvp in _commandIndicesPerMeshCommand)
            {
                if (!ReferenceEquals(kvp.Key.Mesh, renderer))
                    continue;

                var list = kvp.Value;
                for (int i = 0; i < list.Count; i++)
                    output.Add(list[i]);

                any |= list.Count > 0;
            }
            return any;
        }

        /// <summary>
        /// Revokes GPU ownership and republishes conservative CPU-authored bounds for
        /// every command owned by <paramref name="renderer"/>. This is the explicit
        /// recovery path when the per-frame skinned-bounds reduction cannot dispatch;
        /// stale GPU data must never remain reachable by the scene BVH.
        /// </summary>
        public void RestoreCpuCommandAabbsForRenderer(
            XRMeshRenderer renderer,
            RenderInfo renderInfo,
            List<uint> scratchIndices)
        {
            SetRendererOwnsGpuAabb(renderer, false);
            if (!TryGetCommandIndicesForRenderer(renderer, scratchIndices))
                return;

            for (int i = 0; i < scratchIndices.Count; i++)
            {
                uint commandIndex = scratchIndices[i];
                if (!_commandIndexLookup.TryGetValue(commandIndex, out var entry))
                    continue;

                IRenderCommandMesh command = entry.command;
                (XRMesh? mesh, XRMaterial? material)[]? subMeshes = command.Mesh?.GetMeshes();
                if (subMeshes is null || (uint)entry.subMeshIndex >= (uint)subMeshes.Length)
                    continue;

                XRMesh? mesh = subMeshes[entry.subMeshIndex].mesh;
                if (mesh is null)
                    continue;

                Matrix4x4 modelMatrix = command.WorldMatrixIsModelMatrix
                    ? command.WorldMatrix
                    : Matrix4x4.Identity;
                WriteTightCommandAabb(commandIndex, renderInfo, mesh.Bounds, modelMatrix);
            }
        }

    }
}
