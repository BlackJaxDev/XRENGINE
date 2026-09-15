namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents an ownership record of a local host agent, capturing details about the worker process and its execution state.
/// </summary>
/// <param name="InstanceId">The unique identifier of the worker instance.</param>
/// <param name="Generation">The generation of the worker instance.</param>
/// <param name="ProcessId">The ID of the worker process.</param>
/// <param name="ProcessStartTimeUtc">The UTC start time of the worker process.</param>
/// <param name="RecordedUtc">The UTC time when this record was created.</param>
/// <param name="Exited">Indicates whether the worker process has exited.</param>
/// <param name="ExitCode">The exit code of the worker process, if available.</param>
/// <param name="ExecutableHash">The SHA-256 hash of the worker process executable, if available.</param>
/// <param name="Directory">The working directory of the worker process, if available.</param>
internal sealed record LocalHostAgentOwnershipRecord(
    string InstanceId, 
    Guid Generation, 
    int ProcessId, 
    DateTime ProcessStartTimeUtc, 
    DateTimeOffset RecordedUtc, 
    bool Exited, 
    int? ExitCode, 
    string? ExecutableHash = null, 
    string? Directory = null);
