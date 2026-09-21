using System;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.GI.DDGI
{
    /// <summary>
    /// Authoritative runtime state object for DDGI probe volumes.
    /// Manages atlas dimensions, GPU buffer sizing, coordinate conversions, and buffer factories.
    /// </summary>
    public sealed partial class DDGIVolumeRuntimeState
    {
        /// <summary>
        /// Irradiance octahedral tile dimension in texels (4x4 interior + 1-texel border on each edge = 6x6).
        /// </summary>
        public const int IrradianceProbeSize = 6;

        /// <summary>
        /// Visibility octahedral tile dimension in texels (14x14 interior + 1-texel border on each edge = 16x16).
        /// </summary>
        public const int VisibilityProbeSize = 16;

        /// <summary>
        /// Default probe counts for bounded volume testing (16x8x16 = 2048 probes).
        /// </summary>
        public static readonly IVector3 DefaultProbeCounts = new(16, 8, 16);

        /// <summary>
        /// Default maximum number of cascades supported in the nested hierarchy.
        /// </summary>
        public const uint DefaultMaxCascades = 4u;

        /// <summary>
        /// Default total probe count buffer capacity across cascades.
        /// </summary>
        public const uint DefaultMaxProbes = 8192u;

        /// <summary>
        /// Default rays traced per probe per frame.
        /// </summary>
        public const uint DefaultRaysPerProbe = 128u;

        /// <summary>
        /// Default maximum updated probes per frame (2048 = update full grid).
        /// </summary>
        public const uint DefaultMaxUpdatedProbesPerFrame = 2048u;

        /// <summary>
        /// Default maximum rays per frame (2048 * 128 = 262,144 rays).
        /// </summary>
        public const uint DefaultMaxRays = DefaultMaxUpdatedProbesPerFrame * DefaultRaysPerProbe;

        /// <summary>
        /// Default irradiance atlas width (16 * 6 = 96 texels).
        /// </summary>
        public const uint DefaultIrradianceAtlasWidth = 16u * IrradianceProbeSize;

        /// <summary>
        /// Default irradiance atlas height ((8 * 16) * 6 = 768 texels).
        /// </summary>
        public const uint DefaultIrradianceAtlasHeight = (8u * 16u) * IrradianceProbeSize;

        /// <summary>
        /// Default visibility atlas width (16 * 16 = 256 texels).
        /// </summary>
        public const uint DefaultVisibilityAtlasWidth = 16u * VisibilityProbeSize;

        /// <summary>
        /// Default visibility atlas height ((8 * 16) * 16 = 2048 texels).
        /// </summary>
        public const uint DefaultVisibilityAtlasHeight = (8u * 16u) * VisibilityProbeSize;

        // Instance properties tracking current volume configuration
        public IVector3 ProbeCounts { get; private set; } = DefaultProbeCounts;
        public Vector3 HalfExtents { get; private set; } = new(10.0f, 6.0f, 10.0f);
        public Vector3 Origin { get; private set; } = Vector3.Zero;
        public int RaysPerProbe { get; private set; } = (int)DefaultRaysPerProbe;
        public int MaxProbesUpdatedPerFrame { get; private set; } = 0;
        public float Hysteresis { get; private set; } = 0.97f;
        public float NormalBias { get; private set; } = 0.1f;
        public float ViewBias { get; private set; } = 0.2f;
        public float ChebyshevPower { get; private set; } = 4.0f;
        public float Intensity { get; private set; } = 1.0f;
        public Vector3 Tint { get; private set; } = Vector3.One;
        public bool RelocationEnabled { get; private set; } = true;
        public bool ClassificationEnabled { get; private set; } = true;
        public float FixedTimeBudgetMs { get; private set; } = 0.0f;
        public float RelocationMinDistance { get; private set; } = 0.0f;
        public float RelocationStepSize { get; private set; } = 0.1f;
        public float BackfaceHitRatioThreshold { get; private set; } = 0.85f;
        public bool DebugDrawProbes { get; private set; } = false;
        public EDDGIDebugMode DebugMode { get; private set; } = EDDGIDebugMode.None;
        public uint FrameIndex { get; private set; } = 0;
        public bool IsInvalidated { get; private set; } = true;
        public ulong InvalidationRevision { get; private set; } = 1;
        public int ScheduledProbeCount { get; private set; } = (int)DefaultMaxProbes;
        public int ProbeUpdateOffset { get; private set; } = 0;
        public float MeasuredFrameTimeMs { get; set; } = 0.0f;
        public float MeasuredMillisecondsPerProbe { get; internal set; }
        public int ActiveProbeCount { get; set; } = 0;
        public int InactiveProbeCount { get; set; } = 0;
        public int SleepingProbeCount { get; set; } = 0;
        public int RelocatedProbeCount { get; set; } = 0;

        // Cascade hierarchy configuration
        public int CascadeCount { get; private set; } = 1;
        public bool CameraScrolling { get; private set; } = false;
        public float CascadeSpacingMultiplier { get; private set; } = 2.0f;
        public bool DisableCoarseVisibility { get; private set; } = true;
        public float CascadeBlendMargin { get; private set; } = 0.1f;
        public System.Collections.Generic.List<DDGICascadeRuntimeState> Cascades { get; } = new();
        public int ActiveCascadeIndex { get; set; } = 0;

        // Baked and update mode configuration
        public EDDGIUpdateMode UpdateMode { get; private set; } = EDDGIUpdateMode.Dynamic;
        public int SlowUpdateIntervalFrames { get; private set; } = 30;
        public string? BakedAssetPath { get; private set; }
        public DDGIBakedAsset? BakedAsset { get; private set; }
        public bool ApplyAmbientOcclusion { get; private set; } = true;

        public DDGIVolumeRuntimeState()
        {
            Cascades.Add(new DDGICascadeRuntimeState
            {
                CascadeIndex = 0,
                Origin = Origin,
                HalfExtents = HalfExtents,
                ProbeCounts = ProbeCounts,
                ProbeSpacing = ProbeSpacing,
                ProbeOffset = 0,
                UpdateInterval = 1,
                ScheduledProbeCount = ScheduledProbeCount,
                VisibilityEnabled = true
            });
        }

        public void Invalidate()
        {
            IsInvalidated = true;
            InvalidationRevision++;
            ProbeUpdateOffset = 0;
            for (int k = 0; k < Cascades.Count; k++)
            {
                Cascades[k].ProbeUpdateOffset = 0;
                Cascades[k].UpdatedProbeCount = 0;
            }
        }

        public float ResolveEffectiveHysteresis()
        {
            return GetActiveCascade().UpdatedProbeCount < TotalProbeCount ? 0.0f : Hysteresis;
        }

        public int ResolveScheduledProbeCount()
        {
            int total = TotalProbeCount;
            if (total <= 0)
                return 0;

            if (FixedTimeBudgetMs > 0.0f)
            {
                if (MeasuredMillisecondsPerProbe > 0.0f || (MeasuredFrameTimeMs > 0.001f && ScheduledProbeCount > 0))
                {
                    float msPerProbe = MeasuredMillisecondsPerProbe > 0.0f ? MeasuredMillisecondsPerProbe : MeasuredFrameTimeMs / ScheduledProbeCount;
                    int target = (int)(FixedTimeBudgetMs / msPerProbe);
                    int maximum = MaxProbesUpdatedPerFrame > 0 ? Math.Min(MaxProbesUpdatedPerFrame, total) : total;
                    return Math.Clamp(target, 1, maximum);
                }
                return MaxProbesUpdatedPerFrame > 0 ? Math.Min(MaxProbesUpdatedPerFrame, total) : Math.Min(total, 512);
            }

            if (MaxProbesUpdatedPerFrame > 0)
                return Math.Min(MaxProbesUpdatedPerFrame, total);

            return total;
        }

        public void OnFrameCompleted()
        {
            FrameIndex++;
            var updated = GetActiveCascade();
            int count = updated.ScheduledProbeCount;
            updated.UpdatedProbeCount = Math.Min(updated.TotalProbeCount, updated.UpdatedProbeCount + count);
            updated.ProbeUpdateOffset = (updated.ProbeUpdateOffset + count) % updated.TotalProbeCount;
            ProbeUpdateOffset = updated.ProbeUpdateOffset;
            IsInvalidated = false;
            for (int k = 0; k < Cascades.Count; k++)
                IsInvalidated |= Cascades[k].UpdatedProbeCount < Cascades[k].TotalProbeCount;
        }

        public void OnBakedDataUploaded()
        {
            IsInvalidated = false;
            for (int k = 0; k < Cascades.Count; k++)
                Cascades[k].UpdatedProbeCount = Cascades[k].TotalProbeCount;
        }

        public int TotalProbeCount => ProbeCounts.X * ProbeCounts.Y * ProbeCounts.Z;

        public Vector3 ProbeSpacing => new(
            ProbeCounts.X > 1 ? (HalfExtents.X * 2.0f) / (ProbeCounts.X - 1) : 0.0f,
            ProbeCounts.Y > 1 ? (HalfExtents.Y * 2.0f) / (ProbeCounts.Y - 1) : 0.0f,
            ProbeCounts.Z > 1 ? (HalfExtents.Z * 2.0f) / (ProbeCounts.Z - 1) : 0.0f);

        public Vector3 GridMin => CameraScrolling && Cascades.Count > 0
            ? Cascades[0].GridMin : GetGridMin(Origin, HalfExtents, ProbeCounts);
        public Vector3 GridMax => Origin + HalfExtents;

        public int IrradianceAtlasWidth => ComputeAtlasWidth(ProbeCounts.X, IrradianceProbeSize);
        public int IrradianceAtlasHeight => ComputeAtlasHeight(ProbeCounts.Y, ProbeCounts.Z, IrradianceProbeSize);

        public int VisibilityAtlasWidth => ComputeAtlasWidth(ProbeCounts.X, VisibilityProbeSize);
        public int VisibilityAtlasHeight => ComputeAtlasHeight(ProbeCounts.Y, ProbeCounts.Z, VisibilityProbeSize);

        public int ActiveRayBudget => ScheduledProbeCount * RaysPerProbe;

        /// <summary>
        /// Resolves which cascade in the hierarchy is scheduled to update on a given frame index based on per-cascade intervals.
        /// Near cascade updates most frequently; far cascades update less often.
        /// </summary>
        public int ResolveActiveCascadeIndex(uint frameIndex)
        {
            if (Cascades.Count <= 1)
                return 0;

            for (int k = Cascades.Count - 1; k >= 1; k--)
            {
                if (Cascades[k].ShouldUpdateThisFrame(frameIndex))
                    return k;
            }
            return 0;
        }

        /// <summary>
        /// Gets the cascade runtime state currently scheduled for active GPU update.
        /// </summary>
        public DDGICascadeRuntimeState GetActiveCascade()
        {
            int idx = Math.Clamp(ActiveCascadeIndex, 0, Math.Max(0, Cascades.Count - 1));
            return (Cascades.Count > 0 && idx < Cascades.Count) ? Cascades[idx] : (Cascades.Count > 0 ? Cascades[0] : null!);
        }

        /// <summary>
        /// Updates nested cascade origins to camera position with grid snapping.
        /// Eliminates probe grid swimming and temporal shimmering.
        /// </summary>
        public void UpdateCascades(Vector3 cameraPosition)
        {
            if (!CameraScrolling)
                return;

            bool changed = false;
            for (int k = 0; k < Cascades.Count; k++)
            {
                Vector3 oldOrigin = Cascades[k].Origin;
                Cascades[k].SnapCenterToGrid(cameraPosition);
                changed |= oldOrigin != Cascades[k].Origin;
            }
            // Atlas texels have no cyclic scroll mapping yet. Clear history before
            // tracing at the new origins rather than reassigning old lighting.
            if (changed)
                Invalidate();
        }

        /// <summary>
        /// Synchronizes cascade hierarchy settings from a DDGIVolumeComponent.
        /// </summary>
        public void SynchronizeCascades(DDGIVolumeComponent volume)
        {
            int count = Math.Clamp(volume.CascadeCount, 1, (int)DefaultMaxCascades);
            while (Cascades.Count < count)
            {
                Cascades.Add(new DDGICascadeRuntimeState { CascadeIndex = Cascades.Count });
            }
            while (Cascades.Count > count)
            {
                Cascades.RemoveAt(Cascades.Count - 1);
            }

            int currentProbeOffset = 0;
            Vector3 baseSpacing = ProbeSpacing;

            for (int k = 0; k < count; k++)
            {
                var cascade = Cascades[k];
                cascade.CascadeIndex = k;
                cascade.ProbeCounts = ProbeCounts;

                float multiplier = MathF.Pow(CascadeSpacingMultiplier, k);
                cascade.ProbeSpacing = baseSpacing * multiplier;
                cascade.HalfExtents = HalfExtents * multiplier;

                if (!CameraScrolling)
                {
                    cascade.Origin = Origin;
                }

                cascade.ProbeOffset = currentProbeOffset;
                currentProbeOffset += cascade.TotalProbeCount;

                // Update intervals: cascade 0 = 1, cascade 1 = 2, cascade 2 = 4, cascade 3 = 8
                cascade.UpdateInterval = 1 << k;

                // Coarse visibility omission: disable on outermost cascade when requested
                bool isOutermostCoarse = DisableCoarseVisibility && k == count - 1 && count > 1;
                cascade.VisibilityEnabled = !isOutermostCoarse;

                int cascadeTotal = cascade.TotalProbeCount;
                cascade.ScheduledProbeCount = Math.Min(ScheduledProbeCount, cascadeTotal);
            }
        }

        /// <summary>
        /// Synchronizes this runtime state with values authored on a DDGIVolumeComponent.
        /// </summary>
        public void Synchronize(DDGIVolumeComponent volume)
        {
            ProbeCounts = volume.ProbeCounts;
            HalfExtents = volume.HalfExtents;
            Origin = volume.SceneNode?.Transform?.WorldTranslation ?? Vector3.Zero;
            RaysPerProbe = volume.RaysPerProbe;
            MaxProbesUpdatedPerFrame = volume.MaxProbesUpdatedPerFrame;
            FixedTimeBudgetMs = volume.FixedTimeBudgetMs;
            RelocationMinDistance = volume.RelocationMinDistance;
            RelocationStepSize = volume.RelocationStepSize;
            BackfaceHitRatioThreshold = volume.BackfaceHitRatioThreshold;
            Hysteresis = volume.Hysteresis;
            NormalBias = volume.NormalBias;
            ViewBias = volume.ViewBias;
            ChebyshevPower = volume.ChebyshevPower;
            Intensity = volume.Intensity;
            Tint = new Vector3(volume.Tint.R, volume.Tint.G, volume.Tint.B);
            RelocationEnabled = volume.RelocationEnabled;
            ClassificationEnabled = volume.ClassificationEnabled;
            DebugDrawProbes = volume.DebugDrawProbes;
            DebugMode = volume.DebugMode;
            ScheduledProbeCount = ResolveScheduledProbeCount();

            CascadeCount = Math.Clamp(volume.CascadeCount, 1, (int)DefaultMaxCascades);
            CameraScrolling = volume.CameraScrolling;
            CascadeSpacingMultiplier = MathF.Max(1.0f, volume.CascadeSpacingMultiplier);
            DisableCoarseVisibility = volume.DisableCoarseVisibility;
            CascadeBlendMargin = Math.Clamp(volume.CascadeBlendMargin, 0.01f, 0.5f);
            SynchronizeCascades(volume);

            UpdateMode = volume.UpdateMode;
            SlowUpdateIntervalFrames = Math.Max(1, volume.SlowUpdateIntervalFrames);
            BakedAssetPath = volume.BakedAssetPath;
            BakedAsset = volume.BakedAsset;
            ApplyAmbientOcclusion = volume.ApplyAmbientOcclusion;
        }

        /// <summary>
        /// Determines whether active probe-ray generation, BVH tracing, hit shading, relocation, and atlas update compute passes should run this frame.
        /// </summary>
        public bool ShouldRunUpdatePasses(uint frameIndex)
        {
            if (UpdateMode == EDDGIUpdateMode.Baked)
                return false;

            if (UpdateMode == EDDGIUpdateMode.SlowUpdate)
            {
                if (IsInvalidated)
                    return true;

                uint interval = (uint)Math.Max(1, SlowUpdateIntervalFrames);
                return (frameIndex % interval) == 0;
            }

            return true;
        }

        /// <summary>
        /// Captures the current volume configuration and atlas byte layers into a DDGIBakedAsset.
        /// </summary>
        public DDGIBakedAsset CaptureBakedAsset(string name, byte[][]? irradianceLayers = null, byte[][]? visibilityLayers = null, DDGIProbeGPU[]? probes = null)
        {
            if (probes is null || probes.Length != checked(TotalProbeCount * CascadeCount) || irradianceLayers is null || visibilityLayers is null)
                throw new ArgumentException("A DDGI bake requires captured GPU probe records and both complete atlas arrays. Use DDGIBaking.TryCaptureFromPipeline on the render thread.");
            return new DDGIBakedAsset
            {
                VolumeName = name,
                ProbeCounts = ProbeCounts,
                HalfExtents = HalfExtents,
                Origin = Origin,
                CascadeCount = CascadeCount,
                CascadeSpacingMultiplier = CascadeSpacingMultiplier,
                IrradianceAtlasWidth = IrradianceAtlasWidth,
                IrradianceAtlasHeight = IrradianceAtlasHeight,
                VisibilityAtlasWidth = VisibilityAtlasWidth,
                VisibilityAtlasHeight = VisibilityAtlasHeight,
                NormalBias = NormalBias,
                ViewBias = ViewBias,
                ChebyshevPower = ChebyshevPower,
                Intensity = Intensity,
                Probes = probes,
                IrradianceAtlasLayers = irradianceLayers ?? Array.Empty<byte[]>(),
                VisibilityAtlasLayers = visibilityLayers ?? Array.Empty<byte[]>(),
            };
        }

        #region Atlas Coordinate and Indexing Conventions

        public static int ComputeAtlasWidth(int probeCountX, int probeTexels)
            => Math.Max(1, probeCountX) * probeTexels;

        public static int ComputeAtlasHeight(int probeCountY, int probeCountZ, int probeTexels)
            => Math.Max(1, probeCountY * probeCountZ) * probeTexels;

        public static void ComputeAtlasDimensions(IVector3 counts, int probeTexels, out int width, out int height)
        {
            width = ComputeAtlasWidth(counts.X, probeTexels);
            height = ComputeAtlasHeight(counts.Y, counts.Z, probeTexels);
        }

        /// <summary>
        /// Converts a linear probe index into 2D atlas tile coordinates (tx, ty).
        /// Atlas packing uses N_probesX columns and (N_probesY * N_probesZ) rows.
        /// </summary>
        public static (int tx, int ty) ComputeTileCoord(int probeIndex, int probeCountX)
        {
            int countX = Math.Max(1, probeCountX);
            return (probeIndex % countX, probeIndex / countX);
        }

        /// <summary>
        /// Converts 2D atlas tile coordinates (tx, ty) back to a linear probe index.
        /// </summary>
        public static int ComputeProbeIndex(int tx, int ty, int probeCountX)
            => tx + ty * Math.Max(1, probeCountX);

        /// <summary>
        /// Converts a linear probe index into 3D grid coordinates (gx, gy, gz).
        /// </summary>
        public static IVector3 ComputeProbeGridCoord(int probeIndex, IVector3 counts)
        {
            int cX = Math.Max(1, counts.X);
            int cY = Math.Max(1, counts.Y);
            int gx = probeIndex % cX;
            int remainder = probeIndex / cX;
            int gy = remainder % cY;
            int gz = remainder / cY;
            return new IVector3(gx, gy, gz);
        }

        /// <summary>
        /// Computes the resting world position of a probe before relocation.
        /// </summary>
        public static Vector3 ComputeProbeWorldPosition(int probeIndex, Vector3 gridMin, Vector3 probeSpacing, IVector3 counts)
        {
            IVector3 gc = ComputeProbeGridCoord(probeIndex, counts);
            return gridMin + new Vector3(gc.X * probeSpacing.X, gc.Y * probeSpacing.Y, gc.Z * probeSpacing.Z);
        }

        /// <summary>
        /// Strictly clamps a dynamic relocation offset to [-0.5 * spacing, +0.5 * spacing] along each axis (dual-grid constraint).
        /// </summary>
        public static Vector3 ClampRelocationOffset(Vector3 offset, Vector3 probeSpacing)
        {
            Vector3 maxOffset = 0.5f * probeSpacing;
            return Vector3.Clamp(offset, -maxOffset, maxOffset);
        }

        /// <summary>
        /// Validates whether a relocation offset lies strictly within the dual-grid Voronoi cell bounds.
        /// </summary>
        public static bool IsWithinDualGridBounds(Vector3 offset, Vector3 probeSpacing)
        {
            Vector3 maxOffset = 0.5f * probeSpacing;
            const float epsilon = 1e-4f;
            return MathF.Abs(offset.X) <= maxOffset.X + epsilon
                && MathF.Abs(offset.Y) <= maxOffset.Y + epsilon
                && MathF.Abs(offset.Z) <= maxOffset.Z + epsilon;
        }

        /// <summary>
        /// Resolves the actual probe index for a scheduled probe invocation with round-robin offset wrapping.
        /// </summary>
        public static int ComputeScheduledProbeIndex(int scheduledIndex, int probeOffset, int totalProbeCount)
        {
            int total = Math.Max(1, totalProbeCount);
            return (probeOffset + scheduledIndex) % total;
        }

        /// <summary>
        /// Snaps a 3D position to the nearest grid spacing interval along each axis.
        /// Prevents probe grid swimming and temporal shimmering when the camera moves.
        /// </summary>
        public static Vector3 SnapToGrid(Vector3 position, Vector3 spacing)
        {
            float sx = MathF.Abs(spacing.X) > 1e-4f ? MathF.Floor(position.X / spacing.X) * spacing.X : position.X;
            float sy = MathF.Abs(spacing.Y) > 1e-4f ? MathF.Floor(position.Y / spacing.Y) * spacing.Y : position.Y;
            float sz = MathF.Abs(spacing.Z) > 1e-4f ? MathF.Floor(position.Z / spacing.Z) * spacing.Z : position.Z;
            return new Vector3(sx, sy, sz);
        }

        /// <summary>
        /// Computes the exact GPU memory footprint in bytes for a DDGI cascade (irradiance atlas, visibility atlas, and probe state buffer).
        /// </summary>
        public static long ComputeCascadeMemoryBytes(int probeCountX, int probeCountY, int probeCountZ, bool visibilityEnabled)
        {
            int totalProbes = Math.Max(1, probeCountX) * Math.Max(1, probeCountY) * Math.Max(1, probeCountZ);

            // Irradiance atlas: probeCountX * 6 by (probeCountY * probeCountZ) * 6 texels @ R11G11B10F (4 bytes)
            int irrWidth = probeCountX * IrradianceProbeSize;
            int irrHeight = (probeCountY * probeCountZ) * IrradianceProbeSize;
            long irrBytes = (long)irrWidth * irrHeight * 4L;

            // Visibility atlas: probeCountX * 16 by (probeCountY * probeCountZ) * 16 texels @ RG16F (4 bytes)
            long visBytes = 0L;
            if (visibilityEnabled)
            {
                int visWidth = probeCountX * VisibilityProbeSize;
                int visHeight = (probeCountY * probeCountZ) * VisibilityProbeSize;
                visBytes = (long)visWidth * visHeight * 4L;
            }

            // Probe state buffer: 32 bytes per probe
            long probeBufferBytes = (long)totalProbes * Marshal.SizeOf<DDGIProbeGPU>();

            return irrBytes + visBytes + probeBufferBytes;
        }

        /// <summary>
        /// Computes the smooth transition blend factor across the outer margin of a cascade.
        /// Returns 0.0 inside the core cascade, ramping up to 1.0 at the outer boundary using smoothstep.
        /// </summary>
        public static float CalculateCascadeBlendFactor(Vector3 normalizedDistFromCenter, float blendMargin)
        {
            float maxDist = MathF.Max(MathF.Abs(normalizedDistFromCenter.X),
                            MathF.Max(MathF.Abs(normalizedDistFromCenter.Y),
                                      MathF.Abs(normalizedDistFromCenter.Z)));

            float blendStart = 1.0f - Math.Clamp(blendMargin, 0.01f, 0.5f);
            if (maxDist <= blendStart)
                return 0.0f;
            if (maxDist >= 1.0f)
                return 1.0f;

            float t = (maxDist - blendStart) / (1.0f - blendStart);
            return t * t * (3.0f - 2.0f * t); // smoothstep
        }

        /// <summary>
        /// Uploads cascade hierarchy uniform arrays to a shader program for multi-cascade DDGI sampling.
        /// </summary>
        public static void UploadCascadeUniforms(XRRenderProgram program, DDGIVolumeRuntimeState state)
        {
            int cascadeCount = Math.Clamp(state.CascadeCount, 1, (int)DefaultMaxCascades);
            program.Uniform("uCascadeCount", cascadeCount);
            program.Uniform("uCascadeBlendMargin", state.CascadeBlendMargin);
            program.Uniform("uDisableVisibilityOnCoarse", (state.DisableCoarseVisibility && cascadeCount > 1) ? 1 : 0);

            var gridMins = state._gridMins;
            var probeSpacings = state._probeSpacings;
            var probeCounts = state._probeCounts;
            var halfExtents = state._halfExtents;
            var probeOffsets = state._probeOffsets;

            for (int k = 0; k < 4; k++)
            {
                if (k < state.Cascades.Count)
                {
                    var c = state.Cascades[k];
                    gridMins[k] = c.GridMin;
                    probeSpacings[k] = c.ProbeSpacing;
                    probeCounts[k] = c.ProbeCounts;
                    halfExtents[k] = c.HalfExtents;
                    probeOffsets[k] = c.ProbeOffset;
                }
                else
                {
                    gridMins[k] = state.GridMin;
                    probeSpacings[k] = state.ProbeSpacing;
                    probeCounts[k] = state.ProbeCounts;
                    halfExtents[k] = state.HalfExtents;
                    probeOffsets[k] = 0;
                }
            }

            program.Uniform("uCascadeGridMin", gridMins);
            program.Uniform("uCascadeProbeSpacing", probeSpacings);
            program.Uniform("uCascadeProbeCounts", probeCounts);
            program.Uniform("uCascadeHalfExtents", halfExtents);
            program.Uniform("uCascadeProbeOffset", probeOffsets);
        }

        #endregion

        #region Buffer and Texture Factories

        private readonly Vector3[] _gridMins = new Vector3[4];
        private readonly Vector3[] _probeSpacings = new Vector3[4];
        private readonly IVector3[] _probeCounts = new IVector3[4];
        private readonly Vector3[] _halfExtents = new Vector3[4];
        private readonly int[] _probeOffsets = new int[4];

        public static Vector3 GetGridMin(Vector3 origin, Vector3 halfExtents, IVector3 counts)
            => origin - new Vector3(counts.X > 1 ? halfExtents.X : 0.0f,
                counts.Y > 1 ? halfExtents.Y : 0.0f, counts.Z > 1 ? halfExtents.Z : 0.0f);

        public static XRDataBuffer CreateDeclaredProbeBuffer(uint elementCount = DefaultMaxProbes)
            => CreateDeclaredBuffer<DDGIProbeGPU>(
                DDGIResourceNames.ProbeStateBuffer,
                elementCount,
                bindingIndex: 0u,
                padEndingToVec4: false);

        public static XRDataBuffer CreateDeclaredRayBuffer(uint elementCount = DefaultMaxRays)
            => CreateDeclaredBuffer<DDGIRayGPU>(
                DDGIResourceNames.RayBuffer,
                elementCount,
                bindingIndex: 1u,
                padEndingToVec4: false);

        public static XRDataBuffer CreateDeclaredHitBuffer(uint elementCount = DefaultMaxRays)
            => CreateDeclaredBuffer<DDGIHitGPU>(
                DDGIResourceNames.HitBuffer,
                elementCount,
                bindingIndex: 2u,
                padEndingToVec4: false);

        public static XRDataBuffer CreateDeclaredRayRadianceBuffer(uint elementCount = DefaultMaxRays)
            => CreateDeclaredBuffer<DDGIRayRadianceGPU>(
                DDGIResourceNames.RayRadianceBuffer,
                elementCount,
                bindingIndex: 3u,
                padEndingToVec4: false);

        private static XRDataBuffer<T> CreateDeclaredBuffer<T>(string name, uint elementCount, uint bindingIndex, bool padEndingToVec4)
            where T : unmanaged
        {
            var buffer = new XRDataBuffer<T>(name, EBufferTarget.ShaderStorageBuffer, elementCount)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = bindingIndex,
                DisposeOnPush = false,
                PadEndingToVec4 = padEndingToVec4
            };
            buffer.PushData();
            return buffer;
        }

        public static XRTexture2D CreateIrradianceAtlasTexture(int width = (int)DefaultIrradianceAtlasWidth, int height = (int)DefaultIrradianceAtlasHeight)
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                (uint)Math.Max(1, width),
                (uint)Math.Max(1, height),
                EPixelInternalFormat.R11fG11fB10f,
                EPixelFormat.Rgb,
                EPixelType.Float);
            t.Resizable = false;
            t.SmallestAllowedMipmapLevel = 0;
            t.SizedInternalFormat = ESizedInternalFormat.R11fG11fB10f;
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = DDGIResourceNames.IrradianceAtlas;
            t.Name = DDGIResourceNames.IrradianceAtlas;
            return t;
        }

        public static XRTexture2D CreateVisibilityAtlasTexture(int width = (int)DefaultVisibilityAtlasWidth, int height = (int)DefaultVisibilityAtlasHeight)
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                (uint)Math.Max(1, width),
                (uint)Math.Max(1, height),
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SmallestAllowedMipmapLevel = 0;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = DDGIResourceNames.VisibilityAtlas;
            t.Name = DDGIResourceNames.VisibilityAtlas;
            return t;
        }

        public static XRTexture2DArray CreateIrradianceAtlasTextureArray(
            uint layers = DefaultMaxCascades,
            int width = (int)DefaultIrradianceAtlasWidth,
            int height = (int)DefaultIrradianceAtlasHeight)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                layers,
                (uint)Math.Max(1, width),
                (uint)Math.Max(1, height),
                EPixelInternalFormat.R11fG11fB10f,
                EPixelFormat.Rgb,
                EPixelType.Float);
            t.Resizable = false;
            t.SmallestAllowedMipmapLevel = 0;
            foreach (XRTexture2D layer in t.Textures)
                layer.SmallestAllowedMipmapLevel = 0;
            t.SizedInternalFormat = ESizedInternalFormat.R11fG11fB10f;
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = DDGIResourceNames.IrradianceAtlas;
            t.Name = DDGIResourceNames.IrradianceAtlas;
            return t;
        }

        public static XRTexture2DArray CreateVisibilityAtlasTextureArray(
            uint layers = DefaultMaxCascades,
            int width = (int)DefaultVisibilityAtlasWidth,
            int height = (int)DefaultVisibilityAtlasHeight)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                layers,
                (uint)Math.Max(1, width),
                (uint)Math.Max(1, height),
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SmallestAllowedMipmapLevel = 0;
            foreach (XRTexture2D layer in t.Textures)
                layer.SmallestAllowedMipmapLevel = 0;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = DDGIResourceNames.VisibilityAtlas;
            t.Name = DDGIResourceNames.VisibilityAtlas;
            return t;
        }

        #endregion
    }
}
