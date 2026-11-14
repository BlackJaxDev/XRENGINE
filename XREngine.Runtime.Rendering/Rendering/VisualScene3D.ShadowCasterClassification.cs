using System.Collections.Generic;
using System.Diagnostics;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Shadows;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    /// <summary>A dynamic caster without a change for this time is due for demotion.</summary>
    private static readonly long ShadowCasterQuietTicks = Stopwatch.Frequency * 5L;

    /// <summary>
    /// A caster that has been due for this time without a planned static draw that takes it
    /// makes the next demotion batch a forced one (see <see cref="ProcessShadowCasterDemotions"/>).
    /// </summary>
    private static readonly long ShadowCasterForcedDemotionDelayTicks = Stopwatch.Frequency * 5L;

    /// <summary>Minimum time between two forced demotion batches that the delay rule starts.</summary>
    private static readonly long ShadowCasterForcedDemotionSpacingTicks = Stopwatch.Frequency * 10L;

    /// <summary>
    /// When this many casters are due, one demotion batch runs at once without the gate: many
    /// quiet casters in the overlay cost more in each refresh than one static draw per cascade.
    /// </summary>
    private const int ShadowCasterForcedDemotionCount = 32;

    /// <summary>Content changes in <see cref="ShadowCasterContentWindowTicks"/> that promote a caster.</summary>
    private const int ShadowCasterContentPromotionCount = 3;

    /// <summary>Window of the content change repeat rule.</summary>
    private static readonly long ShadowCasterContentWindowTicks = Stopwatch.Frequency;

    // Casters that motion or repeated content changes made dynamic. Renderables with committed
    // deformation bounds are dynamic by type and never enter this list, so they never demote.
    private readonly List<RenderInfo3D> _motionDynamicShadowCasters = new(64);
    private readonly object _shadowCasterClassificationLock = new();
    private long _lastForcedShadowCasterDemotionTimestamp;

    /// <summary>
    /// Records a transform or bounds change of <paramref name="info"/> and promotes it to the
    /// dynamic set at once, also during scene load. The promotion costs one static draw of the
    /// tiles that the caster meets (the tiles are drawn again without it), the same as a one-off
    /// move of a static caster; each later move is free. A caster that a load moves once leaves
    /// the overlay again through demotion. Called from the thread that reports the change. It
    /// allocates only when the moving-caster list grows.
    /// </summary>
    internal void NoteShadowCasterMotion(RenderInfo3D info)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_shadowCasterClassificationLock)
        {
            info.ShadowCasterLastChangeTimestamp = now;
            info.ShadowCasterDueSinceTimestamp = 0L;
            if (!info.IsShadowCasterDynamic)
                PromoteShadowCasterNoLock(info);
        }
    }

    /// <summary>
    /// Records a content change of <paramref name="info"/> (mesh, material, instance count,
    /// enabled state). A single content change does not promote: it is a one-time static change.
    /// <see cref="ShadowCasterContentPromotionCount"/> content changes within
    /// <see cref="ShadowCasterContentWindowTicks"/> promote the caster, so a renderable that
    /// changes its content in many frames is drawn in the overlay and does not make the static
    /// tiles stale in each frame.
    /// </summary>
    internal void NoteShadowCasterContentChange(RenderInfo3D info)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_shadowCasterClassificationLock)
        {
            if (info.IsShadowCasterDynamic)
            {
                info.ShadowCasterLastChangeTimestamp = now;
                info.ShadowCasterDueSinceTimestamp = 0L;
                return;
            }

            if (now - info.ShadowCasterContentWindowStart > ShadowCasterContentWindowTicks)
            {
                info.ShadowCasterContentWindowStart = now;
                info.ShadowCasterContentChanges = 0;
            }
            if (++info.ShadowCasterContentChanges < ShadowCasterContentPromotionCount)
                return;

            info.ShadowCasterLastChangeTimestamp = now;
            info.ShadowCasterDueSinceTimestamp = 0L;
            PromoteShadowCasterNoLock(info);
        }
    }

    private void PromoteShadowCasterNoLock(RenderInfo3D info)
    {
        _motionDynamicShadowCasters.Add(info);
        info.SetShadowCasterMotionDynamic(true);
        DirectionalShadowStaticCacheDiagnostics.RecordPromotion();
    }

    /// <summary>
    /// Demotes the dynamic casters that have been quiet for 5 seconds. Normally a due caster
    /// demotes only when <paramref name="gate"/> confirms that every cascade whose tile it meets
    /// draws its static tile in this plan pass for another cause (a refit or a static caster
    /// change), so the demotion adds no static draw: the caster enters the static tile in that
    /// draw. While it is quiet, a due caster causes no refresh, and in a refresh its overlay draw
    /// is small. Two rules force a batch of all due casters without the gate, because the
    /// overlay must not keep quiet casters forever: <see cref="ShadowCasterForcedDemotionCount"/>
    /// or more due casters (many overlay draws cost more than one static draw per cascade), and
    /// a caster that has been due for <see cref="ShadowCasterForcedDemotionDelayTicks"/>, at most
    /// once per <see cref="ShadowCasterForcedDemotionSpacingTicks"/> (a static camera can go
    /// without planned static draws for a long time). The static draws that a forced batch causes
    /// go through the normal refresh rules. Each demoted caster reports a static change and
    /// leaves the dynamic list in the same pass, so the swap of this frame carries the static
    /// draw flag. Casters that left this scene leave the list. Runs on the collect thread after
    /// the cascade requests of the pass, and does not allocate.
    /// </summary>
    internal void ProcessShadowCasterDemotions(IShadowCasterDemotionGate gate)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_shadowCasterClassificationLock)
        {
            int dueCount = 0;
            bool overdue = false;
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

                dueCount++;
                if (info.ShadowCasterDueSinceTimestamp == 0L)
                    info.ShadowCasterDueSinceTimestamp = now;
                else if (now - info.ShadowCasterDueSinceTimestamp >= ShadowCasterForcedDemotionDelayTicks)
                    overdue = true;
            }

            bool forced = dueCount >= ShadowCasterForcedDemotionCount ||
                (overdue && now - _lastForcedShadowCasterDemotionTimestamp >= ShadowCasterForcedDemotionSpacingTicks);
            int demoted = 0;
            int stillDue = 0;
            for (int i = _motionDynamicShadowCasters.Count - 1; i >= 0 && dueCount > 0; i--)
            {
                RenderInfo3D info = _motionDynamicShadowCasters[i];
                if (now - info.ShadowCasterLastChangeTimestamp < ShadowCasterQuietTicks)
                    continue;

                if (!forced &&
                    (((IOctreeItem)info).WorldCullingVolume is not { } box ||
                     !gate.CanDemote(new AABB(box.WorldMinimum, box.WorldMaximum))))
                {
                    stillDue++;
                    continue;
                }

                RemoveMotionDynamicShadowCasterAt(i);
                info.ShadowCasterDueSinceTimestamp = 0L;
                info.SetShadowCasterMotionDynamic(false);
                info.ReportShadowCasterDemotion();
                demoted++;
            }

            if (demoted > 0)
            {
                if (forced)
                    _lastForcedShadowCasterDemotionTimestamp = now;
                DirectionalShadowStaticCacheDiagnostics.RecordDemotionBatch(demoted, forced);
            }
            DirectionalShadowStaticCacheDiagnostics.SetPendingDemotions(stillDue);
        }
    }

    private void RemoveMotionDynamicShadowCasterAt(int index)
    {
        int last = _motionDynamicShadowCasters.Count - 1;
        _motionDynamicShadowCasters[index] = _motionDynamicShadowCasters[last];
        _motionDynamicShadowCasters.RemoveAt(last);
    }
}
