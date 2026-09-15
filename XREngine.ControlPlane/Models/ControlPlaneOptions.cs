namespace XREngine.ControlPlane;

/// <summary>
/// Represents the configuration options for the control plane.
/// </summary>
public sealed class ControlPlaneOptions
{
    /// <summary>
    /// Gets or sets the default protocol version for the control plane.
    /// </summary>
    public string DefaultProtocolVersion { get; set; } = "dev";

    /// <summary>
    /// Gets or sets the default maximum number of players for the control plane.
    /// </summary>
    public int DefaultMaxPlayers { get; set; } = 8;

    /// <summary>
    /// Gets or sets the length, in bytes, of the token used by the control plane.
    /// </summary>
    public int TokenByteLength { get; set; } = 32;

    /// <summary>
    /// Gets or sets the default multicast group for the control plane.
    /// </summary>
    public string DefaultMulticastGroup { get; set; } = "239.0.0.222";

    /// <summary>
    /// Gets or sets the default multicast port for the control plane.
    /// </summary>
    public int DefaultMulticastPort { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the lease duration, in seconds, for the host heartbeat.
    /// </summary>
    public int HostHeartbeatLeaseSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the lifetime, in seconds, of an admission reservation.
    /// </summary>
    public int AdmissionReservationLifetimeSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the lease duration, in seconds, for an admission directive.
    /// </summary>
    public int AdmissionDirectiveLeaseSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the lease duration, in seconds, for an admission resume.
    /// </summary>
    public int AdmissionResumeLeaseSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum age, in seconds, of a worker report.
    /// </summary>
    public int WorkerReportMaximumAgeSeconds { get; set; } = 30;
}
