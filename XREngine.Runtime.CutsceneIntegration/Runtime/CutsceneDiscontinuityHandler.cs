using XREngine.Rendering;
using XREngine.Rendering.Residency;

namespace XREngine.Cutscenes;

/// <summary>
/// Resets the temporal render state of the cutscene viewport once for each <see cref="CutscenePlayer.Discontinuity"/>.
/// <see cref="CutsceneCameraRouter"/> owns one handler and calls it from its discontinuity listener, after the router
/// claimed the camera of the new shot. The player raises one discontinuity in a tick, also when a cut and a jump occur in
/// the same tick, so each reset below occurs once for each discontinuity. A scrub raises a discontinuity in each step, so
/// <see cref="Handle"/> does not allocate. A <see cref="ECutsceneDiscontinuityKind.ClockResync"/> (a hard resync to the
/// audio inside one shot) does only the temporal history reset.
/// </summary>
/// <remarks>
/// <para>The resets that the handler does, with the state that each one clears in the Default, Advanced, and RVC pipelines
/// (RVC derives from Default and uses its passes):</para>
/// <list type="bullet">
/// <item><description><see cref="XRViewport.InvalidateTemporalHistory(bool)"/> with no property notifications. It advances
/// <see cref="XRCamera.TemporalHistoryEpoch"/> of the active camera. The epoch clears the previous unjittered view-projection
/// of the camera (camera motion vectors), and the frame view history ledger marks the view as having no valid history.
/// <c>VPRC_TemporalAccumulationPass</c> then resets its TAA and TSR history (all three pipelines). Spatial hash AO, the only
/// temporal AO pass, resets its history when the temporal data of that pass has no ready history (Default and RVC, mono
/// views only; the desktop viewport of the router is mono). The
/// Advanced preparation extractor does not use the depth pyramid of the previous frame for a view with no valid history.</description></item>
/// <item><description><see cref="XRCamera.ResetAutoExposureHistory"/> on the active camera, for a full discontinuity only.
/// Auto exposure does not read the epoch: <c>VPRC_ExposureUpdate</c> blends from the exposure of the previous frame in all
/// three pipelines. The reset makes the next GPU update use a blend of one (OpenGL, Vulkan, and WebGPU) and makes the CPU
/// path set the metered value at once. The reset records the new camera epoch, and <c>VPRC_ExposureUpdate</c> passes the
/// epoch that the view history ledger recorded at the capture of the rendered frame. So a frame from before the reset
/// that still renders does not use it, and the first new frame uses it once. This guard applies only where the desktop
/// view history ledger authors history for the frame. A frame with no ledger epoch (output sequence 0, view history
/// authoring off, a stereo pass, or the front-buffer CPU path) counts as a new frame, so on those paths a frame from before
/// the reset can still use it. The reset never stays blocked.</description></item>
/// </list>
/// <para>State with no reset hook, which the handler does not reset:</para>
/// <list type="bullet">
/// <item><description>GI history. Radiance cascade history, ReSTIR temporal reservoirs, Surfel GI surfels, and DDGI probe
/// hysteresis have no reset for a camera discontinuity in any pipeline. Surfels and DDGI probes are in world space.</description></item>
/// <item><description>Skinning and object history. The Default pipeline keeps the previous model matrix and the previous
/// skin palette for each mesh, and the Advanced deformed vertex arena keeps the previous deformation for each owner. No public
/// API resets them for a view discontinuity. <c>AdvancedDeformedVertexArena.InvalidateAllHistoryForSceneEpoch</c> exists only
/// for a scene change at a frame boundary in the extractor. The TAA reset hides most of this, because no history is used in
/// the frame of the reset.</description></item>
/// <item><description>The RVC temporal shadelet cache. It does not check the epoch. Its reuse is off by default.</description></item>
/// </list>
/// <para>The simulations do not use this handler. <see cref="SimulationControlEvaluator"/> applies their policies in the
/// player tick, because a <see cref="CutscenePlayer.Stop"/> raises the discontinuity outside the tick.</para>
/// </remarks>
public sealed class CutsceneDiscontinuityHandler
{
    /// <summary>The number of discontinuities that reset a viewport.</summary>
    public long HandledCount { get; private set; }

    /// <summary>Resets the temporal render state of a viewport for one discontinuity. A null viewport does nothing.</summary>
    /// <param name="viewport">The viewport that shows the cutscene camera.</param>
    /// <param name="kind">The kind of the discontinuity. A clock resync resets only the temporal history.</param>
    public void Handle(XRViewport? viewport, ECutsceneDiscontinuityKind kind = ECutsceneDiscontinuityKind.Full)
    {
        // A cut or a jump changes the scene cost at once. Streaming relearns its normal frame time, so the new shot does
        // not count as upload or decode cost. A clock resync stays in the same shot.
        if (kind == ECutsceneDiscontinuityKind.Full)
            TextureResidencyRequests.Shared.NotifySceneDiscontinuity();

        if (viewport is null)
            return;

        HandledCount++;

        // A scrub raises a discontinuity in each frame. A change event would allocate its arguments when another object
        // listens to the camera. The history consumers read the epoch, so they do not need the event.
        viewport.InvalidateTemporalHistory(publishNotifications: false);
        if (kind == ECutsceneDiscontinuityKind.Full)
            viewport.ActiveCamera?.ResetAutoExposureHistory();
    }
}
