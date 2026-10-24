using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.UI;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    // Counts of the collect pass in progress. Main-view collections can run on more than one thread, so the writes use
    // interlocked operations.
    private int _shadowOnlyCasterCandidates;
    private int _mainViewGpuCollections;

    // Counts of the previous collect pass. The census moves the counts here at the start of each pass.
    private int _previousShadowOnlyCasterCandidates;
    private int _previousMainViewGpuCollections;
    private ulong _shadowCasterMembershipRevisionAtPassStart;

    // A diagnostic capture that waits for the next main-view GPU collection. Each main-view collection reads this field
    // once, and does nothing more while it is null.
    private ShadowOnlyCasterCensusCapture? _shadowOnlyCasterCapture;

    /// <summary>
    /// Returns whether the scene can have shadow-casting renderables that no main view collected. For these
    /// renderables, a CPU shadow view collection is the only per-frame CPU collection: it runs their pre-collection
    /// update, and its swap refreshes their GPU scene rows. The GPU-driven cascades read only these rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every main-view collection under GPU dispatch visits each renderable that canonical GPU collection does not own.
    /// It counts a candidate for each shadow-casting renderable with a mesh command when one of these conditions is
    /// true:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="RenderInfo3D.AllowRender"/> rejects it for the main view, by layer, mirror filter, volume or a
    /// subclass rule, and no disposal or retirement was requested.
    /// </description></item>
    /// <item><description>
    /// Its pre-collection callback rejects the main camera with the result
    /// <see cref="ERenderInfoCollectResult.CallbackRejected"/>, so the callback can accept a different camera. For
    /// example, an HLOD source that a far camera hides, a light probe preview or a mirror display.
    /// </description></item>
    /// <item><description>
    /// The main view collects it without the primary command, because the material enables the shadow pass but not the
    /// base pass.
    /// </description></item>
    /// </list>
    /// <para>
    /// The census does not count a renderable that no camera collects, because the shadow cameras reject it too: a
    /// render info whose disposal was requested, the render info of a <see cref="RenderableMesh"/> that requested
    /// retirement, and a render info whose callback, declared through
    /// <see cref="RenderInfo.SetCameraIndependentPreCollectCommandsCallback"/>, rejects the main camera. Examples of
    /// the last case are an item on a screen-space UI canvas, which has a 3D render info that no 3D camera collects, and
    /// the proxy of an HLOD group that is disabled or has no built proxy. The main view sees a callback result only
    /// when <see cref="RenderInfo3D.AllowRender"/> accepts the render info, so a declared callback does not remove a
    /// render info that <see cref="RenderInfo3D.AllowRender"/> rejects.
    /// </para>
    /// <para>
    /// The count is conservative: a candidate that another main view collects still counts, and so does a renderable
    /// whose camera-dependent callback also rejects the shadow cameras. A renderable that the canonical GPU collection
    /// owns is not a candidate, because the covered-source refresh maintains it for all views. Thus each visited
    /// shadow-casting renderable with a mesh command that a camera can collect is either collected with its primary
    /// command by a main view, or counted.
    /// </para>
    /// <para>
    /// The census reads the counts of the previous pass and the counts that the current pass recorded so far, because
    /// the light collection can run before or after the main views in a pass. The method returns true when either pass
    /// counted a candidate, when no main-view GPU collection ran in either pass, when shadow-casting renderables joined
    /// or left the scene in the current pass, or when the scene does not use GPU dispatch. A layer, material or callback
    /// state change on a renderable that occurs in the same pass, before the main views collect it, shows in the next
    /// pass. In that case the GPU scene rows of that renderable can be one frame old.
    /// </para>
    /// </remarks>
    internal bool MayHaveShadowOnlyCasters()
    {
        // CPU dispatch collects the main views through the CPU tree, which does not visit off-view renderables. The
        // census cannot see them there, so it reports that they can exist.
        if (!_isGpuDispatchActive)
            return true;

        if (ShadowCasterMembershipRevision != _shadowCasterMembershipRevisionAtPassStart)
            return true;

        if (Volatile.Read(ref _previousMainViewGpuCollections) == 0 &&
            Volatile.Read(ref _mainViewGpuCollections) == 0)
            return true;

        return Volatile.Read(ref _previousShadowOnlyCasterCandidates) > 0 ||
            Volatile.Read(ref _shadowOnlyCasterCandidates) > 0;
    }

    /// <summary>
    /// Reads the shadow-only caster census for diagnostics. The read has no side effect on the census.
    /// </summary>
    public ShadowOnlyCasterCensusSnapshot GetShadowOnlyCasterCensusSnapshot()
        => new(
            _isGpuDispatchActive,
            Volatile.Read(ref _previousShadowOnlyCasterCandidates),
            Volatile.Read(ref _shadowOnlyCasterCandidates),
            Volatile.Read(ref _previousMainViewGpuCollections),
            Volatile.Read(ref _mainViewGpuCollections),
            ShadowCasterMembershipRevision,
            Volatile.Read(ref _shadowCasterMembershipRevisionAtPassStart),
            MayHaveShadowOnlyCasters(),
            Volatile.Read(ref _shadowOnlyCasterCapture) is not null);

    /// <summary>
    /// Requests a diagnostic capture of the candidates that the next completed main-view GPU collection counts.
    /// </summary>
    /// <remarks>
    /// The call allocates the record buffer on the calling thread. The collect threads do not allocate for the capture.
    /// A new request replaces a capture that no collection claimed yet, and the replaced capture does not complete.
    /// Poll <see cref="ShadowOnlyCasterCensusCapture.IsCompleted"/>, and call
    /// <see cref="CancelShadowOnlyCasterCensusCapture"/> when the wait stops before completion. Under CPU dispatch no
    /// main-view GPU collection runs, so the capture does not complete.
    /// </remarks>
    /// <param name="maxCandidates">The maximum number of candidates to record. Must be 1 or more.</param>
    public ShadowOnlyCasterCensusCapture RequestShadowOnlyCasterCensusCapture(int maxCandidates)
    {
        ShadowOnlyCasterCensusCapture capture = new(maxCandidates);
        Volatile.Write(ref _shadowOnlyCasterCapture, capture);
        return capture;
    }

    /// <summary>
    /// Removes <paramref name="capture"/> when it still waits for a collection. A capture that a collection claimed
    /// can still complete.
    /// </summary>
    public void CancelShadowOnlyCasterCensusCapture(ShadowOnlyCasterCensusCapture capture)
        => Interlocked.CompareExchange(ref _shadowOnlyCasterCapture, null, capture);

    /// <summary>
    /// Moves the census counts of the finished pass to the previous-pass fields. Call it once at the start of each
    /// collect pass, before pending renderable operations change the scene membership.
    /// </summary>
    private void BeginShadowOnlyCasterCensusPass()
    {
        Volatile.Write(
            ref _previousShadowOnlyCasterCandidates,
            Interlocked.Exchange(ref _shadowOnlyCasterCandidates, 0));
        Volatile.Write(
            ref _previousMainViewGpuCollections,
            Interlocked.Exchange(ref _mainViewGpuCollections, 0));
        Volatile.Write(ref _shadowCasterMembershipRevisionAtPassStart, ShadowCasterMembershipRevision);
    }

    /// <summary>
    /// Claims the pending diagnostic capture for one main-view collection. When no capture is pending, the cost is one
    /// volatile read.
    /// </summary>
    /// <returns>The claimed capture, or null. The caller must complete or abandon a claimed capture.</returns>
    private ShadowOnlyCasterCensusCapture? BeginShadowOnlyCasterCapture(IRuntimeCullingCamera? camera, bool collectMirrors)
    {
        ShadowOnlyCasterCensusCapture? capture = Volatile.Read(ref _shadowOnlyCasterCapture);
        return capture is not null && capture.TryBeginRecording(camera, collectMirrors) ? capture : null;
    }

    /// <summary>Records one completed main-view GPU collection and its shadow-only caster candidates.</summary>
    private void RecordMainViewShadowOnlyCasterCensus(int candidates, ShadowOnlyCasterCensusCapture? capture)
    {
        if (candidates > 0)
            Interlocked.Add(ref _shadowOnlyCasterCandidates, candidates);
        Interlocked.Increment(ref _mainViewGpuCollections);

        if (capture is null)
            return;

        capture.Complete(candidates);
        Interlocked.CompareExchange(ref _shadowOnlyCasterCapture, null, capture);
    }

    /// <summary>
    /// Returns whether a renderable that <see cref="RenderInfo3D.AllowRender"/> rejected for a main view is a
    /// shadow-only caster candidate. A shadow camera does not collect a render info whose disposal or retirement was
    /// requested, so such a render info is not a candidate.
    /// </summary>
    private bool IsRejectedShadowOnlyCasterCandidate(RenderInfo3D renderable,
        UIWorldCollectPacketSet? worldPacketSet)
        => IsShadowOnlyCasterCandidate(renderable, worldPacketSet) &&
            !renderable.IsDisposalRequested &&
            renderable.OwnerRenderableMesh?.IsRetirementRequested != true;

    /// <summary>
    /// Returns whether a renderable that a main view passed to <see cref="RenderInfo.TryCollectCommands"/> is a
    /// shadow-only caster candidate, and why.
    /// </summary>
    /// <remarks>
    /// Only a collection without the primary command and a camera-dependent callback rejection count. The results
    /// <see cref="ERenderInfoCollectResult.Disposed"/>, <see cref="ERenderInfoCollectResult.Retired"/> and
    /// <see cref="ERenderInfoCollectResult.RejectedForEveryCamera"/> apply to the shadow cameras too.
    /// </remarks>
    private bool IsCollectedShadowOnlyCasterCandidate(
        RenderInfo3D renderable,
        ERenderInfoCollectResult result,
        UIWorldCollectPacketSet? worldPacketSet,
        out EShadowOnlyCasterCandidateReason reason)
    {
        switch (result)
        {
            case ERenderInfoCollectResult.Collected:
                reason = EShadowOnlyCasterCandidateReason.ShadowPassOnly;
                return IsShadowPassOnlyRenderable(renderable);
            case ERenderInfoCollectResult.CallbackRejected:
                reason = EShadowOnlyCasterCandidateReason.CallbackRejected;
                return IsShadowOnlyCasterCandidate(renderable, worldPacketSet);
            default:
                reason = default;
                return false;
        }
    }

    /// <summary>
    /// Returns whether a renderable is a possible shadow-only caster: it casts shadows, and it has a mesh command, which
    /// owns a GPU scene row.
    /// </summary>
    private bool IsShadowOnlyCasterCandidate(RenderInfo3D renderable,
        UIWorldCollectPacketSet? worldPacketSet)
    {
        if (!renderable.CastsShadows)
            return false;

        if (renderable.FrozenWorldCommandSource?.HasFrozenShadowCasters(
                worldPacketSet) == true)
            return true;

        var commands = renderable.RenderCommands;
        for (int i = 0; i < commands.Count; i++)
            if (commands[i] is IRenderCommandMesh)
                return true;

        return false;
    }

    /// <summary>
    /// Returns whether a main view collected <paramref name="renderable"/> without its primary command, because the
    /// material enables the shadow pass but not the base pass.
    /// </summary>
    private static bool IsShadowPassOnlyRenderable(RenderInfo3D renderable)
        => renderable.CastsShadows &&
            renderable.OwnerRenderableMesh is RenderableMesh mesh &&
            !mesh.ShouldCollectPrimaryCommand(isShadowCollection: false) &&
            mesh.ShouldCollectPrimaryCommand(isShadowCollection: true);

    /// <summary>
    /// Finds the check of the base <see cref="RenderInfo3D.AllowRender"/> method that rejected a main view. Only a
    /// diagnostic capture calls this method, after the rejection.
    /// </summary>
    private static EShadowOnlyCasterCandidateReason ClassifyAllowRenderRejection(
        RenderInfo3D renderable,
        IVolume? volume,
        IRuntimeCullingCamera? camera,
        bool collectMirrors)
    {
        if (!collectMirrors && renderable.Owner is MirrorCaptureComponent)
            return EShadowOnlyCasterCandidateReason.MirrorRejected;
        if (camera is not null && !camera.RendersLayer(renderable.Layer))
            return EShadowOnlyCasterCandidateReason.LayerRejected;
        if (!renderable.Intersects(volume, containsOnly: false))
            return EShadowOnlyCasterCandidateReason.VolumeRejected;
        return EShadowOnlyCasterCandidateReason.AllowRenderRejected;
    }
}
