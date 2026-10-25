using XREngine.Data.Core;

namespace XREngine.UnitTests.Scene;

internal sealed class QueueObjectProbe : XRObjectBase
{
    protected override bool ParticipatesInObjectCache => false;

    public int DestroyNowCalls { get; private set; }
    public bool ReturnBeforeBase { get; set; }
    public bool DeferFromOverride { get; set; }
    public bool ThrowBeforeBase { get; set; }
    public ManualResetEventSlim? GenerateEntered { get; set; }
    public ManualResetEventSlim? ReleaseGenerate { get; set; }
    public ManualResetEventSlim? DestroyEntered { get; set; }
    public ManualResetEventSlim? ReleaseDestroy { get; set; }
    public bool DrainInsideDestroy { get; set; }
    public Action? DestroyingAction { get; set; }
    public bool ThrowOnDispatchRelease { get; set; }

    protected override void OnLifetimeMutationBeginning(EObjectLifetimeMutation kind)
    {
        if (kind != EObjectLifetimeMutation.Generate || GenerateEntered is not { } entered)
            return;
        entered.Set();
        if (ReleaseGenerate?.Wait(TimeSpan.FromSeconds(5)) != true)
            throw new TimeoutException("The blocked Generate call did not resume.");
    }

    protected override void OnLifetimeMutationEnded(EObjectLifetimeMutation kind)
    {
        if (kind == EObjectLifetimeMutation.DispatchDestruction && ThrowOnDispatchRelease)
            throw new InvalidOperationException("Expected dispatch release failure.");
    }

    public override void Destroy(bool now = false)
    {
        if (now) DestroyNowCalls++;
        if (now && DestroyEntered is { } entered)
        {
            entered.Set();
            if (ReleaseDestroy?.Wait(TimeSpan.FromSeconds(5)) != true)
                throw new TimeoutException("The blocked Destroy call did not resume.");
        }
        if (now && DrainInsideDestroy)
            XRObjectBase.ProcessPendingDestructions();
        if (now && ThrowBeforeBase)
            throw new InvalidOperationException("Expected pre-base destruction failure.");
        if (now && ReturnBeforeBase)
            return;
        if (now && DeferFromOverride)
        {
            base.Destroy(false);
            return;
        }
        base.Destroy(now);
    }

    protected override void OnDestroying() => DestroyingAction?.Invoke();
}
