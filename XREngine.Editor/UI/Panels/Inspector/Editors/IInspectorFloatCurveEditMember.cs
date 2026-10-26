namespace XREngine.Editor;

/// <summary>Defines exact state and revision checks for one selected curve edit.</summary>
internal interface IInspectorFloatCurveEditMember
{
    bool HasChange { get; }
    ulong ExactGeneration { get; }
    ulong ExactGenerationDelta { get; }
    ulong NotificationRevision { get; }
    bool CanNotify { get; }
    bool CanReserveTransitions(int count);
    bool Matches(bool after);
    void Transition(bool forward, ulong expectedExactGeneration, ulong expectedNotificationRevision);
}
