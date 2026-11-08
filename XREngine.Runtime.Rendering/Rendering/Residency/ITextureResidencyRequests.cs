namespace XREngine.Rendering.Residency;

/// <summary>
/// The public seam for explicit texture residency requests and the texture streaming budget profile.
/// A request is a demand signal separate from visibility: it raises the policy target of one streamed texture
/// to a requested mip while the caller holds its handle. It does not count as visibility and it does not pin the texture.
/// </summary>
/// <remarks>
/// <para>
/// Get the shared instance from <see cref="TextureResidencyRequests.Shared"/>. The imported texture streaming
/// manager implements this interface. The per-frame limits, the VRAM budget fit, and the publish rule apply to each request.
/// </para>
/// <para>
/// Any thread can call each member. The members use internal locks and atomic reads, and they do not wait for
/// the render thread or the streaming evaluation.
/// </para>
/// </remarks>
public interface ITextureResidencyRequests
{
    /// <summary>
    /// Occurs one time for each request when its mip becomes resident. Compare the ticket with
    /// <see cref="TextureResidencyHandle.Ticket"/>. The state of the request is <see cref="ETextureResidencyRequestState.Resident"/>
    /// before the event occurs.
    /// </summary>
    /// <remarks>
    /// The event runs on the streaming evaluation thread, outside the streaming locks. Keep handlers short. A handler can
    /// receive an event for a handle that it disposed already, and it must ignore such tickets. Streaming calls each handler
    /// in its own try/catch: a handler that throws does not stop the evaluation, the other handlers, or the events of the
    /// other tickets. Streaming writes each exception, with the handler and the texture named, to the
    /// <c>texture-residency-handler-failures.log</c> auxiliary log, which Release builds keep. Any thread can add or remove a
    /// handler; a handler that is removed while the event runs can still receive that event.
    /// </remarks>
    event Action<TextureResidencyTicket>? ResidencyRequestCompleted;

    /// <summary>
    /// The current collect frame of texture streaming. Use it to compute the deadline frame of a request.
    /// It is a streaming clock, not the engine frame counter: it advances one time for each streaming evaluation.
    /// Any thread can read it. It does not throw.
    /// </summary>
    long CurrentFrameId { get; }

    /// <summary>
    /// True if the last streaming evaluation found VRAM budget pressure. The per-frame transition limits of a budget
    /// profile do not count. Any thread can read it. It does not throw.
    /// </summary>
    bool IsUnderBudgetPressure { get; }

    /// <summary>
    /// The collect frame (see <see cref="CurrentFrameId"/>) of the evaluation that wrote <see cref="IsUnderBudgetPressure"/>.
    /// A value that does not advance means that no evaluation ran, so the pressure flag is stale.
    /// Any thread can read it. It does not throw.
    /// </summary>
    long BudgetPressureFrameId { get; }

    /// <summary>
    /// True while imports that register textures with streaming are active. During an import, streaming queues no
    /// promotion of a texture that is not visible, so a request waits. Any thread can read it. It does not throw.
    /// </summary>
    bool AreStreamedTexturesImporting { get; }

    /// <summary>
    /// Requests that a streamed texture holds at least <paramref name="targetMip"/> while the caller holds the returned handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller owns the handle. Dispose the handle to cancel the request. A handle that is never disposed keeps the
    /// mip resident until the process ends or the texture is destroyed: streaming drops the request of a destroyed or
    /// collected texture at its next evaluation. Streaming does not keep the handle alive, and the handle has no finalizer,
    /// so a lost handle cannot cancel its request.
    /// </para>
    /// <para>
    /// Any thread can call it. The call can register the streaming source of a native imported texture, as a first
    /// material use does. It logs a warning when it returns null.
    /// </para>
    /// </remarks>
    /// <param name="texture">A texture that imported texture streaming tracks.</param>
    /// <param name="targetMip">The mip to make resident. 0 is the full source size. A large value requests the preview only.</param>
    /// <param name="deadlineFrame">The collect frame (see <see cref="CurrentFrameId"/>) at which the caller needs the mip. Use <see cref="long.MaxValue"/> for no deadline.</param>
    /// <param name="priority">The job priority of the promotion.</param>
    /// <param name="generation">The caller generation. The logs show it. Streaming does not compare it.</param>
    /// <returns>The owned request handle, or null if streaming does not track the texture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texture"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetMip"/> is negative.</exception>
    TextureResidencyHandle? RequestResidency(XRTexture2D texture, int targetMip, long deadlineFrame, JobPriority priority, int generation);

    /// <summary>Gets the largest side of the source of a tracked texture.</summary>
    /// <remarks>Any thread can call it. It does not throw; a null texture returns false.</remarks>
    /// <param name="texture">A texture that imported texture streaming tracks.</param>
    /// <param name="sourceMaxDimension">The largest side, in texels, or 0.</param>
    /// <returns>False if streaming does not track the texture or the source size is not known yet.</returns>
    bool TryGetSourceMaxDimension(XRTexture2D texture, out uint sourceMaxDimension);

    /// <summary>
    /// Takes a lease on a budget profile. The streaming limits use the profile while at least one lease is live.
    /// When the last lease is disposed, the limits go back to <see cref="ETextureStreamingBudgetProfile.Default"/>.
    /// </summary>
    /// <remarks>
    /// The seam counts the leases, so callers that do not know each other cannot release the profile of another caller.
    /// The limits fade to the new profile over some evaluations. The VRAM budget fit and the publish rule do not change.
    /// Dispose of a lease is idempotent. A lease that is never disposed keeps the profile until the process ends.
    /// Any thread can call it and can dispose the lease.
    /// </remarks>
    /// <param name="profile">The profile to hold. It must not be <see cref="ETextureStreamingBudgetProfile.Default"/>, which is the profile with no lease.</param>
    /// <returns>The lease. Dispose it to release the profile.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="profile"/> is <see cref="ETextureStreamingBudgetProfile.Default"/> or not a known profile.</exception>
    IDisposable AcquireBudgetProfile(ETextureStreamingBudgetProfile profile);

    /// <summary>
    /// Tells streaming that the scene cost changed at once, for example at a camera cut. The upload and decode controllers
    /// then relearn the normal frame time, so the cost of the new shot does not count as streaming cost.
    /// </summary>
    /// <remarks>Any thread can call it. It allocates nothing. The default implementation does nothing.</remarks>
    void NotifySceneDiscontinuity()
    {
    }
}
