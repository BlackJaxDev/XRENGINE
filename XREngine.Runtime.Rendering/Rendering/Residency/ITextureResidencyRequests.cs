namespace XREngine.Rendering.Residency;

/// <summary>
/// The public seam for explicit texture residency requests and the texture streaming budget profile.
/// A request is a demand signal separate from visibility: it raises the policy target of one streamed texture
/// to a requested mip until the caller cancels it. It does not count as visibility and it does not pin the texture.
/// </summary>
/// <remarks>
/// Get the shared instance from <see cref="TextureResidencyRequests.Shared"/>. The imported texture streaming
/// manager implements this interface. The per-frame limits, the VRAM budget fit, and the publish rule apply to each request.
/// </remarks>
public interface ITextureResidencyRequests
{
    /// <summary>
    /// Occurs one time for each request when its mip becomes resident. The state of the request is
    /// <see cref="ETextureResidencyRequestState.Resident"/> before the event occurs. The event runs on the evaluation
    /// thread, outside the streaming locks. Keep handlers short. A handler can receive an event for a ticket that it
    /// cancelled already, and it must ignore such tickets.
    /// </summary>
    event Action<TextureResidencyTicket>? ResidencyRequestCompleted;

    /// <summary>The current collect frame. Use it to compute the deadline frame of a request.</summary>
    long CurrentFrameId { get; }

    /// <summary>
    /// True if the last streaming evaluation found VRAM budget pressure. The per-frame transition limits of a budget
    /// profile do not count. Any thread can read it.
    /// </summary>
    bool IsUnderBudgetPressure { get; }

    /// <summary>
    /// The frame of the evaluation that wrote <see cref="IsUnderBudgetPressure"/>. A value that does not advance
    /// means that no evaluation ran, so the pressure flag is stale.
    /// </summary>
    long BudgetPressureFrameId { get; }

    /// <summary>True while model imports that register textures with streaming are active.</summary>
    bool HasActiveImportedModelImports { get; }

    /// <summary>
    /// Requests that a streamed texture holds at least <paramref name="targetMip"/> until <see cref="CancelResidency"/>.
    /// </summary>
    /// <param name="texture">A texture that imported texture streaming tracks.</param>
    /// <param name="targetMip">The mip to make resident. 0 is the full source size. A large value requests the preview only.</param>
    /// <param name="deadlineFrame">The collect frame at which the caller needs the mip. Use <see cref="long.MaxValue"/> for no deadline.</param>
    /// <param name="priority">The job priority of the promotion.</param>
    /// <param name="generation">The caller generation. The logs show it. Streaming does not compare it.</param>
    /// <returns>The ticket, or <see cref="TextureResidencyTicket.None"/> if streaming does not track the texture.</returns>
    TextureResidencyTicket RequestResidency(XRTexture2D texture, int targetMip, long deadlineFrame, JobPriority priority, int generation);

    /// <summary>
    /// Cancels a request. A resident request releases its hold and the texture goes back to visibility-driven residency.
    /// </summary>
    /// <param name="ticket">The ticket of the request.</param>
    /// <returns>True if the ticket identified an active request.</returns>
    bool CancelResidency(TextureResidencyTicket ticket);

    /// <summary>Gets the state of a request. A cancelled or unknown ticket returns <see cref="ETextureResidencyRequestState.None"/>.</summary>
    /// <param name="ticket">The ticket of the request.</param>
    ETextureResidencyRequestState GetResidencyRequestState(TextureResidencyTicket ticket);

    /// <summary>Gets the largest side of the source of a tracked texture.</summary>
    /// <param name="texture">A texture that imported texture streaming tracks.</param>
    /// <param name="sourceMaxDimension">The largest side, in texels, or 0.</param>
    /// <returns>False if streaming does not track the texture or the source size is not known yet.</returns>
    bool TryGetSourceMaxDimension(XRTexture2D texture, out uint sourceMaxDimension);

    /// <summary>
    /// Selects the per-frame streaming limits. The limits fade to the new profile over some evaluations.
    /// The VRAM budget fit and the publish rule do not change. The profile is global, so callers that share it must
    /// count their holds. Any thread can call it.
    /// </summary>
    /// <param name="profile">The profile to use.</param>
    void SetBudgetProfile(ETextureStreamingBudgetProfile profile);
}
