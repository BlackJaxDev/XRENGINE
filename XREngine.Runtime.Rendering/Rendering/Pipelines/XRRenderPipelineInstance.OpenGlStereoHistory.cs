namespace XREngine.Rendering;

public partial class XRRenderPipelineInstance
{
    private readonly RenderFrameViewHistoryLedger[] _nativeStereoHistory = [new(), new()];
    private readonly RenderFrameViewHistoryCandidateToken[] _nativeStereoCandidates = new RenderFrameViewHistoryCandidateToken[2];
    private readonly RenderFrameViewDescriptor[] _nativeStereoViews = new RenderFrameViewDescriptor[2];
    private ulong _nativeStereoSequence;
    private ulong _nativeStereoHistoryFrame;
    private int _nativeStereoHistoryGeneration;

    /// <summary>
    /// Resolves each GL eye against its last successfully authored native HDR
    /// output. OpenXR supplies its own accepted-submission view history.
    /// </summary>
    internal RenderFrameViewSet CaptureOpenGlStereoHistory(
        in RenderFrameViewSet current, IRuntimeRenderCamera left, IRuntimeRenderCamera right)
    {
        CompleteOpenGlStereoHistory(succeeded: false);
        ulong sequence = checked(++_nativeStereoSequence);
        _nativeStereoHistoryFrame = RuntimeEngine.Rendering.State.RenderFrameId;
        _nativeStereoHistoryGeneration = ResourceGeneration;
        for (int eye = 0; eye < 2; eye++)
        {
            _nativeStereoViews[eye] = _nativeStereoHistory[eye].Capture(
                sequence, _nativeStereoHistoryFrame, eye == 0 ? left : right,
                TemporalHistoryPipelineIdentity, unchecked((ulong)ResourceGeneration),
                AdvancedOutputBinding.Request.OutputId, authoring: true,
                current.GetView(eye), out _, out _nativeStereoCandidates[eye]);
        }
        return RenderFrameViewSet.Create(current.RenderMode, current.VisibilityPolicy,
            current.VisibilityGroupCount, _nativeStereoViews, current.DebugName);
    }

    /// <summary>
    /// GL's ordered context has issued full native background/shading writes
    /// for both layers before accepting the family. Advancing these matrices
    /// does not claim GPU completion or headset presentation.
    /// </summary>
    internal void CompleteOpenGlStereoHistory(bool succeeded)
    {
        bool commit = succeeded &&
            _nativeStereoHistoryFrame == RuntimeEngine.Rendering.State.RenderFrameId &&
            _nativeStereoHistoryGeneration == ResourceGeneration;
        for (int eye = 0; eye < _nativeStereoCandidates.Length; eye++)
        {
            if (commit)
                _nativeStereoCandidates[eye].Commit();
            else
                _nativeStereoCandidates[eye].Discard();
            _nativeStereoCandidates[eye] = default;
        }
    }
}
