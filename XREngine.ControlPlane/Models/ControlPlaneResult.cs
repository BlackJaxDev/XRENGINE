namespace XREngine.ControlPlane;

/// <summary>
/// Represents the result of an operation in the control plane, including success or failure information.
/// </summary>
/// <typeparam name="T">The type of the value returned by the operation if it succeeds.</typeparam>
public sealed class ControlPlaneResult<T>
{
    /// <summary>
    /// Gets the reason for the failure of the operation.
    /// </summary>
    public ControlPlaneFailureReason FailureReason { get; init; }

    /// <summary>
    /// Gets the message associated with the result of the operation.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the value returned by the operation if it succeeds.
    /// </summary>
    public T? Value { get; init; }

    /// <summary>
    /// Gets a value indicating whether the operation was successful.
    /// </summary>
    public bool Success => FailureReason == ControlPlaneFailureReason.None;

    /// <summary>
    /// Creates a successful result with the specified value.
    /// </summary>
    /// <param name="value">The value returned by the operation.</param>
    /// <returns>A successful control plane result containing the specified value.</returns>
    public static ControlPlaneResult<T> Ok(T value)
        => new() { Value = value };

    /// <summary>
    /// Creates a failed result with the specified failure reason and message.
    /// </summary>
    /// <param name="reason">The reason for the failure.</param>
    /// <param name="message">The message associated with the failure.</param>
    /// <returns>A failed control plane result containing the specified failure reason and message.</returns>
    public static ControlPlaneResult<T> Fail(ControlPlaneFailureReason reason, string message)
        => new() { FailureReason = reason, Message = message };
}
