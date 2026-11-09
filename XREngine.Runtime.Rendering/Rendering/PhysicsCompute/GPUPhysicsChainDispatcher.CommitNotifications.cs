using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    // The bounds build compares each row with the published page that it reads. The commit
    // uses this result while that page is still the published page; otherwise it compares
    // the pages again.
    private readonly List<XRMeshRenderer> _commitNotificationChangedRenderers = [];
    private PhysicsChainOutputPage? _commitNotificationPriorPage;
    private uint _commitNotificationPriorGeneration;
    private uint _commitNotificationPriorProducerEpoch;
    private int _commitNotificationRowCount;
    private bool _commitNotificationQueryStateChanged;

    /// <summary>Selects the published page that this bounds build compares its rows with.</summary>
    private void BeginCommitNotificationBuild()
    {
        int priorIndex = _publishedOutputPageIndex;
        PhysicsChainOutputPage? prior = priorIndex >= 0 ? _outputPages[priorIndex] : null;
        _commitNotificationPriorPage = prior;
        _commitNotificationPriorGeneration = prior?.Generation ?? 0u;
        _commitNotificationPriorProducerEpoch = prior?.ProducerEpoch ?? 0u;
        _commitNotificationChangedRenderers.Clear();
        _commitNotificationRowCount = 0;
        _commitNotificationQueryStateChanged = false;
    }

    /// <summary>
    /// Records whether one row's committed bound or committed query state differs from the
    /// prior published page.
    /// </summary>
    /// <param name="prior">The renderer's state on the prior page, or a null reference.</param>
    private void RecordCommitNotification(XRMeshRenderer renderer,
        ref readonly PhysicsChainGpuRendererOutputState prior, in PhysicsChainGpuRendererOutputState state)
    {
        ++_commitNotificationRowCount;
        bool boundsChanged = Unsafe.IsNullRef(in prior)
            || prior.CpuSpatialBoundsValid != state.CpuSpatialBoundsValid
            || prior.CpuSpatialBoundsVersion != state.CpuSpatialBoundsVersion;
        if (boundsChanged)
        {
            _commitNotificationChangedRenderers.Add(renderer);
            _commitNotificationQueryStateChanged = true;
        }
        else if (!HasSameCommittedQueryState(in prior, in state))
            _commitNotificationQueryStateChanged = true;
    }

    /// <summary>
    /// Adds a notification for each renderer on <paramref name="page"/> whose committed bound
    /// changed. The caller holds <see cref="_outputPageSync"/>. Returns true when any value that a
    /// committed spatial query reads differs from <paramref name="priorPage"/>.
    /// </summary>
    private bool CollectCommitNotifications(PhysicsChainOutputPage page, PhysicsChainOutputPage? priorPage)
    {
        bool precomputed = priorPage is not null
            && ReferenceEquals(priorPage, _commitNotificationPriorPage)
            && priorPage.Generation == _commitNotificationPriorGeneration
            && priorPage.ProducerEpoch == _commitNotificationPriorProducerEpoch
            && page.RendererStates.Count == _commitNotificationRowCount;
        if (!precomputed)
            return CollectCommittedSpatialNotifications(page, priorPage, _commitNotifications);

        for (int index = 0; index < _commitNotificationChangedRenderers.Count; ++index)
            _commitNotifications.Add((_commitNotificationChangedRenderers[index], true));
        return _commitNotificationQueryStateChanged
            || priorPage!.RendererStates.Count != page.RendererStates.Count;
    }

    /// <summary>
    /// Advances the covered shadow caster output revision of each scene of this publication once.
    /// A covered caster's GPU pose changes with each output, also when its committed bound does not.
    /// A scene with no covered caster does not read the revision.
    /// </summary>
    private void NotifyCoveredShadowCasterScenes(uint producerEpoch)
    {
        for (int index = 0; index < _gpuBoundsVisualScenes.Count; ++index)
            _gpuBoundsVisualScenes[index].NotifyCoveredShadowCasterOutputChanged(producerEpoch, published: true);
    }

    /// <summary>Releases the page and renderer references of the last notification build.</summary>
    private void ClearCommitNotificationBuild()
    {
        _commitNotificationChangedRenderers.Clear();
        _commitNotificationPriorPage = null;
        _commitNotificationRowCount = 0;
    }
}
