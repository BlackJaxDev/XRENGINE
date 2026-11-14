using System.Collections.Generic;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Shadows;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    /// <summary>
    /// A caster that reports changes in two distinct update frames no more than this many
    /// frames apart becomes dynamic.
    /// </summary>
    private const ulong ShadowCasterPromotionWindowFrames = 8UL;

    /// <summary>A dynamic caster without a change for this many update frames is due for demotion.</summary>
    private const ulong ShadowCasterQuietFrames = 60UL;

    /// <summary>Minimum number of update frames between two demotion batches.</summary>
    private const ulong ShadowCasterDemotionBatchSpacingFrames = 30UL;

    // Casters that motion made dynamic. Renderables with committed deformation bounds are
    // dynamic by type and never enter this list, so they never demote.
    private readonly List<RenderInfo3D> _motionDynamicShadowCasters = new(64);
    private readonly object _shadowCasterClassificationLock = new();
    private ulong _lastShadowCasterDemotionBatchFrame;

    /// <summary>
    /// Records a change of <paramref name="info"/> for its motion classification and promotes
    /// it to the dynamic set when it changed in two distinct update frames within
    /// <see cref="ShadowCasterPromotionWindowFrames"/>. A dynamic caster that changes again
    /// stays dynamic. One-off changes (a door that opens once, an editor move) do not promote:
    /// they make the static caster tiles stale instead. Called from the thread that reports
    /// the change. It allocates only when the moving-caster list grows.
    /// </summary>
    internal void NoteShadowCasterMotion(RenderInfo3D info)
    {
        ulong frame = RuntimeRenderingHostServices.FrameTiming.UpdateFrameId;
        lock (_shadowCasterClassificationLock)
        {
            if (frame != info.ShadowCasterLastChangeFrame)
            {
                info.ShadowCasterPreviousChangeFrame = info.ShadowCasterLastChangeFrame;
                info.ShadowCasterLastChangeFrame = frame;
            }

            if (info.IsShadowCasterDynamic)
                return;

            ulong previous = info.ShadowCasterPreviousChangeFrame;
            if (previous == 0UL || frame - previous > ShadowCasterPromotionWindowFrames)
                return;

            _motionDynamicShadowCasters.Add(info);
            info.SetShadowCasterMotionDynamic(true);
            DirectionalShadowStaticCacheDiagnostics.RecordPromotion();
        }
    }

    /// <summary>
    /// Demotes the moving casters that have been quiet for <see cref="ShadowCasterQuietFrames"/>
    /// update frames, all in one batch. A demotion moves a caster from the dynamic overlay
    /// into the static caster tile, so every cascade that it meets must draw its static tile
    /// again. To keep this cost inside the refresh limit, the caller passes
    /// <paramref name="releaseAllowed"/> true only in a plan frame without camera refreshes,
    /// and batches are at least <see cref="ShadowCasterDemotionBatchSpacingFrames"/> frames
    /// apart. A due caster stays in the overlay until a batch takes it. Each demoted caster
    /// reports a static change, so the cascades that it meets re-render their static tile with
    /// the caster in it. Casters that left this scene leave the list. Runs on the collect
    /// thread and does not allocate.
    /// </summary>
    internal void ProcessShadowCasterDemotions(bool releaseAllowed)
    {
        ulong frame = RuntimeRenderingHostServices.FrameTiming.UpdateFrameId;
        lock (_shadowCasterClassificationLock)
        {
            int count = _motionDynamicShadowCasters.Count;
            if (count == 0)
            {
                DirectionalShadowStaticCacheDiagnostics.SetPendingDemotions(0);
                return;
            }

            int due = 0;
            for (int i = count - 1; i >= 0; i--)
            {
                RenderInfo3D info = _motionDynamicShadowCasters[i];
                if (!ReferenceEquals(info.RegisteredVisualScene, this))
                {
                    // The caster left the scene. Its draw rows leave with it.
                    RemoveMotionDynamicShadowCasterAt(i);
                    info.SetShadowCasterMotionDynamic(false);
                    continue;
                }

                if (frame - info.ShadowCasterLastChangeFrame >= ShadowCasterQuietFrames)
                    due++;
            }

            DirectionalShadowStaticCacheDiagnostics.SetPendingDemotions(due);
            if (due == 0 || !releaseAllowed ||
                frame - _lastShadowCasterDemotionBatchFrame < ShadowCasterDemotionBatchSpacingFrames)
            {
                return;
            }

            int demoted = 0;
            for (int i = _motionDynamicShadowCasters.Count - 1; i >= 0; i--)
            {
                RenderInfo3D info = _motionDynamicShadowCasters[i];
                if (frame - info.ShadowCasterLastChangeFrame < ShadowCasterQuietFrames)
                    continue;

                RemoveMotionDynamicShadowCasterAt(i);
                info.SetShadowCasterMotionDynamic(false);
                info.ReportShadowCasterDemotion();
                demoted++;
            }

            _lastShadowCasterDemotionBatchFrame = frame;
            DirectionalShadowStaticCacheDiagnostics.RecordDemotionBatch(demoted);
            DirectionalShadowStaticCacheDiagnostics.SetPendingDemotions(0);
        }
    }

    private void RemoveMotionDynamicShadowCasterAt(int index)
    {
        int last = _motionDynamicShadowCasters.Count - 1;
        _motionDynamicShadowCasters[index] = _motionDynamicShadowCasters[last];
        _motionDynamicShadowCasters.RemoveAt(last);
    }
}
