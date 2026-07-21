using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public sealed partial class GPURenderPassCollection
{
    private XRDataBuffer? _viewBatchClassificationBuffer;
    private bool _viewBatchClassificationCapabilityEnabled;
    private bool _viewBatchClassificationValidationPassed;
    private EGpuViewBatchSubmissionPolicy _requestedViewBatchSubmissionPolicy;
    private ViewBatchContentPolicy _viewBatchContentPolicy = ViewBatchContentPolicy.Exact;
    private bool _culledCommandViewMasksAreExact;
    private ulong _culledCommandViewMasksFrameId;
    private ulong _viewBatchClassificationFrameId;
    private bool _viewBatchClassificationPublished;

    private bool HasCurrentFrameExactCommandViewMasks
        => _culledCommandViewMasksAreExact &&
            _culledCommandViewMasksFrameId == RuntimeEngine.Rendering.State.RenderFrameId;

    private bool HasCurrentFrameViewBatchClassification
        => _viewBatchClassificationPublished &&
            _viewBatchClassificationFrameId == RuntimeEngine.Rendering.State.RenderFrameId;

    private void InvalidateExactCommandViewMasks()
    {
        _culledCommandViewMasksAreExact = false;
        _culledCommandViewMasksFrameId = 0u;
    }

    private void PublishExactCommandViewMasks()
    {
        _culledCommandViewMasksAreExact = true;
        _culledCommandViewMasksFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
    }

    public XRDataBuffer? ViewBatchClassificationBuffer => _viewBatchClassificationBuffer;

    public bool RequiresPerViewTransparentSubmission
        => _activeViewCount > 1u && RenderPass == (int)EDefaultRenderPass.TransparentForward;

    public bool RequiresExactTransparentCandidateRejection
        => RequiresPerViewTransparentSubmission &&
            _viewBatchContentPolicy.TransparentPolicy is ETransparentMultiviewPolicy.PerViewSorted
                or ETransparentMultiviewPolicy.ForceSplit;

    private bool _exactTransparentMultiviewRejectedThisFrame;
    private bool _reportedExactTransparentMultiviewRejection;
    private uint _reportedExactTransparentMultiviewViewCount;
    private ETransparentMultiviewPolicy _reportedExactTransparentMultiviewPolicy;

    public bool ExactTransparentMultiviewRejectedThisFrame
    {
        get => _exactTransparentMultiviewRejectedThisFrame;
        private set => SetField(ref _exactTransparentMultiviewRejectedThisFrame, value);
    }

    private void ReportExactTransparentMultiviewRejection()
    {
        ExactTransparentMultiviewRejectedThisFrame = true;

        ETransparentMultiviewPolicy policy = _viewBatchContentPolicy.TransparentPolicy;
        if (_reportedExactTransparentMultiviewRejection &&
            _reportedExactTransparentMultiviewViewCount == _activeViewCount &&
            _reportedExactTransparentMultiviewPolicy == policy)
        {
            return;
        }

        _reportedExactTransparentMultiviewRejection = true;
        _reportedExactTransparentMultiviewViewCount = _activeViewCount;
        _reportedExactTransparentMultiviewPolicy = policy;
        XREngine.Debug.RenderingWarning(
            $"[GPU-PIPELINE] Rejected exact-sorted candidates from multiview transparent pass {RenderPass}: policy {policy} requires per-view sorted submission, but the active view batch has {_activeViewCount} views and no split rendering scope. Approximate and OIT-compatible candidates continue; no source-view filtering or CPU fallback is used. ConservativeSharedOrder is the only allowed union policy.");
    }

    private void ClearExactTransparentMultiviewRejection()
    {
        ExactTransparentMultiviewRejectedThisFrame = false;
        _reportedExactTransparentMultiviewRejection = false;
    }

    /// <summary>
    /// Resolves to conservative union submission until the backend capability
    /// and validation gates are both explicitly enabled.
    /// </summary>
    public EGpuViewBatchSubmissionPolicy EffectiveViewBatchSubmissionPolicy
    {
        get
        {
            if (!_viewBatchClassificationCapabilityEnabled ||
                !_viewBatchClassificationValidationPassed ||
                !HasCurrentFrameExactCommandViewMasks)
            {
                return EGpuViewBatchSubmissionPolicy.ConservativeUnion;
            }
            if (_requestedViewBatchSubmissionPolicy == EGpuViewBatchSubmissionPolicy.TraditionalLayerSuppression &&
                RequiresPerViewClassificationForCurrentPass)
            {
                return EGpuViewBatchSubmissionPolicy.ConservativeUnion;
            }
            return _requestedViewBatchSubmissionPolicy;
        }
    }

    public EMultiviewLodPolicy EffectiveMultiviewLodPolicy
        => _activeViewCount > 1u
            ? EMultiviewLodPolicy.ConservativeHighestDetail
            : EMultiviewLodPolicy.PerViewExact;

    public ViewBatchContentPolicy EffectiveViewBatchContentPolicy
        => _viewBatchContentPolicy with { LodPolicy = EffectiveMultiviewLodPolicy };

    private bool RequiresPerViewClassificationForCurrentPass
        => (_activeViewCount > 1u &&
            EffectiveMultiviewLodPolicy == EMultiviewLodPolicy.PerViewExact) ||
            (RequiresPerViewTransparentSubmission &&
             EffectiveViewBatchContentPolicy.TransparentPolicy is ETransparentMultiviewPolicy.PerViewSorted
                 or ETransparentMultiviewPolicy.ForceSplit);

    /// <summary>
    /// Selects how exact view masks are consumed after classification. Callers
    /// must not set <paramref name="validationPassed"/> before backend-specific
    /// layer/task suppression has completed its acceptance validation.
    /// </summary>
    public void ConfigureViewBatchSubmissionPolicy(
        EGpuViewBatchSubmissionPolicy requestedPolicy,
        bool backendCapabilityEnabled,
        bool validationPassed)
        => ConfigureViewBatchSubmissionPolicy(
            requestedPolicy,
            backendCapabilityEnabled,
            validationPassed,
            ViewBatchContentPolicy.Exact);

    public void ConfigureViewBatchSubmissionPolicy(
        EGpuViewBatchSubmissionPolicy requestedPolicy,
        bool backendCapabilityEnabled,
        bool validationPassed,
        in ViewBatchContentPolicy contentPolicy)
    {
        _requestedViewBatchSubmissionPolicy = requestedPolicy;
        _viewBatchClassificationCapabilityEnabled = backendCapabilityEnabled;
        _viewBatchClassificationValidationPassed = validationPassed;
        _viewBatchContentPolicy = contentPolicy;
    }

    private void EnsureViewBatchClassificationBuffer(uint capacity)
    {
        if (_viewBatchClassificationBuffer is null ||
            _viewBatchClassificationBuffer.ComponentType != EComponentType.UInt ||
            _viewBatchClassificationBuffer.ComponentCount != GPUViewBatchClassificationLayout.UIntCount)
        {
            _viewBatchClassificationBuffer?.Destroy();
            _viewBatchClassificationBuffer = new XRDataBuffer(
                "GPUViewBatchClassification",
                EBufferTarget.ShaderStorageBuffer,
                capacity,
                EComponentType.UInt,
                GPUViewBatchClassificationLayout.UIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
                BindingIndexOverride = (uint)GPUBatchingBindings.BuildKeysClassification,
            };
            _viewBatchClassificationBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
            _viewBatchClassificationBuffer.Generate();
            return;
        }

        if (_viewBatchClassificationBuffer.ElementCount < capacity)
            _viewBatchClassificationBuffer.Resize(capacity);
    }
}
