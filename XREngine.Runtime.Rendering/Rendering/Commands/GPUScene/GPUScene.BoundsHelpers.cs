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
            Vector3 xAxis = new(m.M11, m.M21, m.M31);
            Vector3 yAxis = new(m.M12, m.M22, m.M32);
            Vector3 zAxis = new(m.M13, m.M23, m.M33);

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
            Vector3 xAxis = new(m.M11, m.M21, m.M31);
            Vector3 yAxis = new(m.M12, m.M22, m.M32);
            Vector3 zAxis = new(m.M13, m.M23, m.M33);

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
            buffer.SetDataRawAtIndex(commandIndex, entry);

            const int CommandAabbStrideBytes = 32; // 8 floats
            buffer.PushSubData((int)(commandIndex * CommandAabbStrideBytes), CommandAabbStrideBytes);
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
            buffer.SetDataRawAtIndex(commandIndex, entry);

            const int CommandAabbStrideBytes = 32;
            buffer.PushSubData((int)(commandIndex * CommandAabbStrideBytes), CommandAabbStrideBytes);
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

    }
}
