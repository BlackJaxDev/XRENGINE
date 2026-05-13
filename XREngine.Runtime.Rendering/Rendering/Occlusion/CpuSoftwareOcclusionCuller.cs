using System;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// C-CPU-3 scaffolding: CPU software-rasterizer occluder pass for occluder-driven
    /// occlusion culling without hardware-query latency. Inspired by Intel's
    /// MaskedOcclusionCulling (Masked SOC).
    ///
    /// STATUS: scaffold only. The rasterizer body is intentionally a no-op so the
    /// opt-in code path can be wired and validated without shipping an unverified
    /// SIMD rasterizer. <see cref="TestVisible"/> always returns true and
    /// <see cref="SubmitOccluder"/> is a no-op until a real port lands. Telemetry
    /// still records calls so the editor panel can show "scaffold engaged but
    /// no occluders rasterized" while the implementation is in flight.
    ///
    /// The full implementation contract this scaffold pre-commits to:
    /// <list type="bullet">
    /// <item>Hierarchical-Z style coverage buffer per camera/viewport.</item>
    /// <item>Conservative occluder rasterization from artist-tagged geometry.</item>
    /// <item>Conservative occludee bbox visibility query.</item>
    /// <item>SIMD path (Vector256/Vector512 where supported).</item>
    /// <item>Per-frame reset; no allocations on the hot path.</item>
    /// </list>
    ///
    /// Opt-in gating:
    /// <list type="bullet">
    /// <item>Settings: <c>EnableCpuSoftwareOcclusionCulling</c>.</item>
    /// <item>Env override: <c>XRE_CPU_SOC_OCCLUSION=1</c>.</item>
    /// <item>Disabled by default; never coerced on.</item>
    /// </list>
    /// </summary>
    public sealed class CpuSoftwareOcclusionCuller
    {
        private readonly object _lock = new();
        private bool _frameOpen;
        private int _frameOccludersSubmitted;
        private int _frameTestsRun;

        /// <summary>
        /// True when the user has opted into SOC via setting or env var. Cheap; safe to call
        /// per command. Reads <see cref="Engine.EffectiveSettings"/> which itself caches an
        /// env-var read.
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                if (Engine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling)
                    return true;
                string? raw = Environment.GetEnvironmentVariable("XRE_CPU_SOC_OCCLUSION");
                return !string.IsNullOrWhiteSpace(raw) && (raw.Trim() == "1" || raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Begin a new occluder rasterization frame for the given camera. Resets the
        /// coverage buffer and per-frame counters. No-op when <see cref="IsEnabled"/> is
        /// false.
        /// </summary>
        public void BeginFrame(XRCamera camera, int viewportWidth, int viewportHeight)
        {
            if (!IsEnabled)
                return;

            lock (_lock)
            {
                _frameOpen = true;
                _frameOccludersSubmitted = 0;
                _frameTestsRun = 0;
                // TODO: clear coverage buffer; reproject from previous frame if temporal
                // reuse is enabled; resize on viewport change.
            }
        }

        /// <summary>
        /// Submit an occluder bounding volume to be rasterized into the coverage buffer.
        /// Implementations should accept artist-tagged occluder geometry only — submitting
        /// every mesh defeats the purpose. No-op when <see cref="IsEnabled"/> is false or
        /// no frame is open.
        /// </summary>
        public void SubmitOccluder(in AABB worldBounds, in Matrix4x4 modelMatrix)
        {
            if (!IsEnabled)
                return;

            lock (_lock)
            {
                if (!_frameOpen)
                    return;
                _frameOccludersSubmitted++;
                // TODO: project bounds to NDC, conservative-rasterize into coverage buffer.
            }
        }

        /// <summary>
        /// Test whether an occludee's world-space AABB is potentially visible against the
        /// currently-rasterized coverage buffer. Returns true (visible) conservatively when
        /// SOC is disabled, no frame is open, or no occluders have been submitted — the
        /// caller must treat true as "no information; fall back to other culling".
        /// </summary>
        public bool TestVisible(in AABB worldBounds)
        {
            if (!IsEnabled)
                return true;

            lock (_lock)
            {
                if (!_frameOpen)
                    return true;

                _frameTestsRun++;
                OcclusionTelemetry.RecordCpuSocTested();

                if (_frameOccludersSubmitted == 0)
                    return true;

                // TODO: project bounds to NDC, sample coverage buffer at hierarchical
                // level matching the projected size, return false when fully covered.
                // Scaffold: always visible.
                return true;
            }
        }

        /// <summary>
        /// Number of occluders submitted this frame (telemetry).
        /// </summary>
        public int FrameOccludersSubmitted
        {
            get { lock (_lock) return _frameOccludersSubmitted; }
        }

        /// <summary>
        /// Number of visibility tests run this frame (telemetry).
        /// </summary>
        public int FrameTestsRun
        {
            get { lock (_lock) return _frameTestsRun; }
        }
    }
}
