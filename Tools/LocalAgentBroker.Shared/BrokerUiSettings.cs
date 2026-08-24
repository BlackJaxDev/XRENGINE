namespace XREngine.LocalAgentBroker.Shared;

/// <summary>
/// User-managed lifecycle and history-retention preferences for the tray companion.
/// </summary>
public sealed record BrokerUiSettings
{
    public const int MaximumIdleExitMinutes = 10_080;

    public const int MaximumRecordRetentionHours = 87_600;

    /// <summary>Shows a Windows notification when a broker accepts a new prompt.</summary>
    public bool NotificationsEnabled { get; init; } = true;

    /// <summary>
    /// Minutes without a queued or running prompt before the tray process exits.
    /// A null value keeps the process open until the user exits it.
    /// </summary>
    public int? IdleExitMinutes { get; init; }

    /// <summary>
    /// Age in hours after which terminal prompt records are deleted.
    /// A null value retains records until the user deletes them.
    /// </summary>
    public int? RecordRetentionHours { get; init; }

    public BrokerUiSettings Normalize()
        => this with
        {
            IdleExitMinutes = NormalizeValue(IdleExitMinutes, MaximumIdleExitMinutes),
            RecordRetentionHours = NormalizeValue(
                RecordRetentionHours,
                MaximumRecordRetentionHours),
        };

    private static int? NormalizeValue(int? value, int maximum)
        => value is null ? null : Math.Clamp(value.Value, 1, maximum);
}
