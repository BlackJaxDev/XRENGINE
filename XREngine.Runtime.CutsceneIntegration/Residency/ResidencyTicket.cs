namespace XREngine.Cutscenes;

/// <summary>
/// Identifies one request that a residency backend accepted.
/// While the work runs, the ticket identifies the work. After the work is complete,
/// the ticket holds the loaded level until the planner cancels it.
/// </summary>
/// <param name="Id">The backend identifier of the request. Zero is not a valid ticket.</param>
public readonly record struct ResidencyTicket(long Id)
{
    /// <summary>The ticket that identifies no request.</summary>
    public static ResidencyTicket None => default;

    /// <summary>True if the ticket identifies a request.</summary>
    public bool IsValid => Id != 0;
}
