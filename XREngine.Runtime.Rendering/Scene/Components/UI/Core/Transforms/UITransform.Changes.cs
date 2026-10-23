using System.ComponentModel;
using System.Threading;
using XREngine.Rendering.UI.State;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UITransform
{
    private static long _nextElementId;
    // Runtime sequence counters use atomics; they are not authored properties.
    private readonly long _elementId = Interlocked.Increment(ref _nextElementId);
    private long _incarnation = 1;
    private long _measureRevision, _arrangeRevision, _transformRevision, _visualRevision;
    private long _clipRevision, _hitRevision, _topologyRevision, _textRevision;
    private bool _retired;
    private UIChangeJournal? _attachmentJournal;

    /// <summary>Identifies this element and its current canvas attachment. Never serialized or reused.</summary>
    [RuntimeOnly, YamlIgnore, Browsable(false)]
    public UIElementIdentity ElementIdentity => new(_elementId, Interlocked.Read(ref _incarnation));

    /// <summary>Gets independent mutation revisions without copying a tree.</summary>
    [RuntimeOnly, YamlIgnore, Browsable(false)]
    public UIChangeRevisions ChangeRevisions => new(
        Interlocked.Read(ref _measureRevision), Interlocked.Read(ref _arrangeRevision),
        Interlocked.Read(ref _transformRevision), Interlocked.Read(ref _visualRevision),
        Interlocked.Read(ref _clipRevision), Interlocked.Read(ref _hitRevision),
        Interlocked.Read(ref _topologyRevision), Interlocked.Read(ref _textRevision));

    /// <summary>Finds the layout-owning canvas using only ancestor canvas links.</summary>
    public UICanvasTransform? GetMutationCanvas()
    {
        UICanvasTransform? canvas = this as UICanvasTransform ?? ParentCanvas;
        while (canvas?.ParentCanvas is { } parent && !ReferenceEquals(parent, canvas))
            canvas = parent;
        return canvas;
    }

    /// <summary>
    /// Records an authored or derived change. Callers must serialize live tree writes.
    /// Layout invalidation is separate; this method does not traverse descendants.
    /// </summary>
    public void RecordUIChange(UIChangeDomain domains)
    {
        if (_retired || domains == UIChangeDomain.None)
            return;
        if ((domains & ~UIChangeDomain.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(domains));
        UICanvasTransform? canvas = GetMutationCanvas();
        VerifyAttachmentMutationAccess();
        if ((domains & UIChangeDomain.Measure) != 0) Interlocked.Increment(ref _measureRevision);
        if ((domains & UIChangeDomain.Arrange) != 0) Interlocked.Increment(ref _arrangeRevision);
        if ((domains & UIChangeDomain.Transform) != 0) Interlocked.Increment(ref _transformRevision);
        if ((domains & UIChangeDomain.Visual) != 0) Interlocked.Increment(ref _visualRevision);
        if ((domains & UIChangeDomain.Clip) != 0) Interlocked.Increment(ref _clipRevision);
        if ((domains & UIChangeDomain.HitTest) != 0) Interlocked.Increment(ref _hitRevision);
        if ((domains & UIChangeDomain.Topology) != 0) Interlocked.Increment(ref _topologyRevision);
        if ((domains & UIChangeDomain.Text) != 0) Interlocked.Increment(ref _textRevision);
        UIChangeJournal? journal = canvas?.ChangeJournal;
        journal?.Record(ElementIdentity, (Parent as UITransform)?.ElementIdentity ?? default, domains);
        _attachmentJournal = journal;
        UIRuntimeCounters.Add(UIWorkCounter.MutationsRecorded);
    }

    private void RetireAttachment()
    {
        // A parent canvas can already point at its new root when children receive this call.
        UIChangeJournal? previousJournal = _attachmentJournal ?? (this as UICanvasTransform)?.ChangeJournal;
        previousJournal?.RecordRemoval(ElementIdentity);
        _attachmentJournal = null;
        Interlocked.Increment(ref _incarnation);
    }

    /// <summary>Checks both the last attachment and current canvas before a tree write.</summary>
    private void VerifyAttachmentMutationAccess()
    {
        _attachmentJournal?.VerifyMutationAccess();
        GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
    }

    /// <summary>Checks both sides of a child list edit before the list changes.</summary>
    protected override bool ValidateChildListMutation(TransformBase child)
    {
        VerifyAttachmentMutationAccess();
        if (child is UITransform uiChild)
            uiChild.VerifyAttachmentMutationAccess();
        return base.ValidateChildListMutation(child);
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T value)
    {
        VerifyAttachmentMutationAccess();
        if (propName == nameof(Children) && value is EventList<TransformBase> newChildren)
            foreach (TransformBase child in newChildren)
                if (child is UITransform uiChild)
                    uiChild.VerifyAttachmentMutationAccess();
        if (propName == nameof(ParentCanvas) && value is UICanvasTransform newCanvas)
            newCanvas.GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
        if (propName == nameof(Parent) && value is UITransform newParent)
            newParent.GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
        return base.OnPropertyChanging(propName, field, value);
    }
}
