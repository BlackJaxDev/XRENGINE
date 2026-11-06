namespace XREngine.Cutscenes;

/// <summary>
/// Loads and releases the levels of detail of assets for the cutscene residency planner.
/// The planner calls these members on the update thread. The backend reports complete work
/// through <see cref="CutsceneResidencyPlanner.NotifyCompleted"/>, which accepts calls from any thread.
/// </summary>
public interface IResidencyBackend
{
    /// <summary>
    /// Starts the work for a request. The planner calls this method in priority order.
    /// Returns <see cref="ResidencyTicket.None"/> if the backend does not accept the request.
    /// </summary>
    /// <param name="request">The request.</param>
    ResidencyTicket Request(in ResidencyRequest request);

    /// <summary>
    /// Stops the work of a ticket. If the work is complete, the ticket no longer holds its level,
    /// and the backend can release that data. A complete result can stay in a cache.
    /// </summary>
    /// <param name="ticket">The ticket to cancel.</param>
    void Cancel(ResidencyTicket ticket);

    /// <summary>Gets the highest level of an asset that is resident now.</summary>
    /// <param name="assetId">The asset.</param>
    EResidencyLevel GetLevel(Guid assetId);
}
