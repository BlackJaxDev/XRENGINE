using System.Collections.Generic;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Shadows;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    /// <summary>A dynamic caster without a change for this time is due for demotion.</summary>
    private static readonly long ShadowCasterQuietTicks = System.Diagnostics.Stopwatch.Frequency * 5L;

    // Casters that motion made dynamic. Renderables with committed deformation bounds are
    // dynamic by type and never enter this list, so they never demote.
    private readonly List<RenderInfo3D> _motionDynamicShadowCasters = new(64);
    private readonly object _shadowCasterClassificationLock = new();

    /// <summary>
    /// Records a change of <paramref name="info"/> and promotes it to the dynamic set at its first
    /// change. The promotion costs one static draw of the tiles that the caster meets (the
    /// tiles are drawn again without it), the same as a one-off move of a static caster; each
    /// later move is free. Called from the thread that reports the change. It allocates only
    /// when the moving-caster list grows.
    /// </summary>
    internal void NoteShadowCasterMotion(RenderInfo3D info)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_shadowCasterClassificationLock)
        {
            info.ShadowCasterLastChangeTimestamp = now;
            if (info.IsShadowCasterDynamic)
                return;

            _motionDynamicShadowCasters.Add(info);
            info.SetShadowCasterMotionDynamic(true);
            DirectionalShadowStaticCacheDiagnostics.RecordPromotion();
        }
    }

    /// <summary>
    /// Demotes the moving casters that have been quiet for 5 seconds, but only when
    /// <paramref name="gate"/> confirms that every cascade whose tile a caster meets draws its
    /// static tile in this plan pass for another cause (a refit or a static caster change). The
    /// demotion then adds no static draw: the caster enters the static tile in that draw. A due
    /// caster stays in the overlay until such a pass; while it is quiet, it causes no refresh,
    /// and in a refresh its overlay draw is small. Each demoted caster reports a static change
    /// and leaves the moving list in the same pass, so the swap of this frame carries the static
    /// draw flag. Casters that left this scene leave the list. Runs on the collect thread after
    /// the cascade requests of the pass, and does not allocate.
    /// </summary>
    internal void ProcessShadowCasterDemotions(IShadowCasterDemotionGate gate)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_shadowCasterClassificationLock)
        {
            int due = 0;
            int demoted = 0;
            for (int i = _motionDynamicShadowCasters.Count - 1; i >= 0; i--)
            {
                RenderInfo3D info = _motionDynamicShadowCasters[i];
                if (!ReferenceEquals(info.RegisteredVisualScene, this))
                {
                    // The caster left the scene. Its draw rows leave with it.
                    RemoveMotionDynamicShadowCasterAt(i);
                    info.SetShadowCasterMotionDynamic(false);
                    continue;
                }

                if (now - info.ShadowCasterLastChangeTimestamp < ShadowCasterQuietTicks)
                    continue;

                if (((IOctreeItem)info).WorldCullingVolume is not { } box ||
                    !gate.CanDemote(new AABB(box.WorldMinimum, box.WorldMaximum)))
                {
                    due++;
                    continue;
                }

                RemoveMotionDynamicShadowCasterAt(i);
                info.SetShadowCasterMotionDynamic(false);
                info.ReportShadowCasterDemotion();
                demoted++;
            }

            if (demoted > 0)
                DirectionalShadowStaticCacheDiagnostics.RecordDemotionBatch(demoted);
            DirectionalShadowStaticCacheDiagnostics.SetPendingDemotions(due);
        }
    }

    private void RemoveMotionDynamicShadowCasterAt(int index)
    {
        int last = _motionDynamicShadowCasters.Count - 1;
        _motionDynamicShadowCasters[index] = _motionDynamicShadowCasters[last];
        _motionDynamicShadowCasters.RemoveAt(last);
    }
}
