namespace XREngine.ControlPlane;

public sealed class ControlPlaneResult<T>
{
    public ControlPlaneFailureReason FailureReason { get; init; }
    public string? Message { get; init; }
    public T? Value { get; init; }

    public bool Success => FailureReason == ControlPlaneFailureReason.None;

    public static ControlPlaneResult<T> Ok(T value)
        => new() { Value = value };

    public static ControlPlaneResult<T> Fail(ControlPlaneFailureReason reason, string message)
        => new() { FailureReason = reason, Message = message };
}
