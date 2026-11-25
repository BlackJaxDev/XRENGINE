using XREngine.Editor;

namespace XREngine.UnitTests.Rendering;

/// <summary>Provides observable commits and callback conflicts for the batch tests.</summary>
internal sealed class ControlledFloatCurveEditMember(string name, ulong delta,
    List<string> writes, bool hasChange = true) : IInspectorFloatCurveEditMember
{
    public bool HasChange { get; } = hasChange;
    public ulong ExactGeneration { get; private set; }
    public ulong ExactGenerationDelta => HasChange ? delta : 0;
    public ulong NotificationRevision { get; private set; }
    public bool CanNotify => true;
    public bool BudgetAvailable { get; set; } = true;
    public int Value { get; private set; }
    public int Attempts { get; private set; }
    public Action? AfterCommit { get; set; }

    public bool CanReserveTransitions(int count) => !HasChange || BudgetAvailable;
    public bool Matches(bool after) => Value == (after && HasChange ? 1 : 0);

    public void Transition(bool forward, ulong expectedExactGeneration, ulong expectedNotificationRevision)
    {
        Attempts++;
        if (!Matches(!forward) || ExactGeneration != expectedExactGeneration ||
            NotificationRevision != expectedNotificationRevision || !CanReserveTransitions(1))
            throw new InvalidOperationException("The controlled member changed before commit.");
        if (!HasChange) return;
        Value = forward ? 1 : 0;
        ExactGeneration += delta;
        NotificationRevision++;
        writes.Add(name + (forward ? "+" : "-"));
        AfterCommit?.Invoke();
    }

    internal void ExternalWrite(int value, bool structural = false)
    {
        Value = value;
        if (structural) ExactGeneration++;
        NotificationRevision++;
    }
}
