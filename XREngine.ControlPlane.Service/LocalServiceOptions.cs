using System.Net;

namespace XREngine.ControlPlane.Service;

/// <summary>Trusted operator configuration for the single-host development service.</summary>
public sealed class LocalServiceOptions
{
    public LocalServiceMode Mode { get; set; } = LocalServiceMode.HostAgent;
    public string AgentUrl { get; set; } = "http://127.0.0.1:5089";
    public string ListenUrl { get; set; } = "http://127.0.0.1:5088";
    public string HostId { get; set; } = "local-host";
    public string ServerExecutable { get; set; } = string.Empty;
    public string WorkingRoot { get; set; } = string.Empty;
    public string HostAgentDataRoot { get; set; } = string.Empty;
    public bool PreserveWorkersOnAgentCrash { get; set; } = true;
    public ManagedWorkerTlsConfiguration? RealtimeTls { get; set; }
    public AdmissionSigningOptions? AdmissionSigning { get; set; }
    /// <summary>Optional public HTTPS gateway policy. A missing policy keeps the local loopback-only service mode.</summary>
    public PublicApiOptions? PublicApi { get; set; }
    public string AdvertisedHost { get; set; } = "127.0.0.1";
    public string BindAddress { get; set; } = "127.0.0.1";
    public int FirstUdpPort { get; set; } = 5200;
    public int LastUdpPort { get; set; } = 5299;
    public int MaxInstances { get; set; } = 4;
    public int MaxPlayerSlots { get; set; } = 32;
    public int StartupTimeoutSeconds { get; set; } = 90;
    public int ShutdownTimeoutSeconds { get; set; } = 15;
    public int DrainTimeoutSeconds { get; set; } = 60;
    public int AdmissionTimeoutSeconds { get; set; } = 60;
    public int WorkerLeaseSeconds { get; set; } = 15;
    public int ResumeWindowSeconds { get; set; } = 30;
    public int WorkerCpuPercent { get; set; } = 25;
    public long WorkerMemoryLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long HostMemoryBudgetBytes { get; set; } = 8L * 1024 * 1024 * 1024;
    /// <summary>Optional same-user native game launcher for the local sample UI.</summary>
    public LocalNativeLauncherOptions NativeLauncher { get; set; } = new();
    public List<LocalApiUser> Users { get; set; } = [];
    public Dictionary<string, string> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the local service options, ensuring that the configuration is correct and consistent.
    /// </summary>
    /// <param name="configurationDirectory">The directory containing the configuration files for the local service.</param>
    /// <exception cref="InvalidOperationException">Thrown if the local service options are invalid or inconsistent.</exception>
    public void Validate(string configurationDirectory)
    {
        if (PublicApi is not null)
            PublicApi.Validate(this);
        else if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out IPAddress? listenIp)
            || !IPAddress.IsLoopback(listenIp) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("The local service requires an explicit loopback HTTP listen URL.");
        
        ListenUrl = ListenUrl.TrimEnd('/');
        if (!Uri.TryCreate(AgentUrl, UriKind.Absolute, out Uri? agentUri) || agentUri.Scheme != "http" || !IPAddress.TryParse(agentUri.Host, out IPAddress? agentIp) || !IPAddress.IsLoopback(agentIp)
            || agentUri.AbsolutePath != "/" || !string.IsNullOrEmpty(agentUri.Query) || !string.IsNullOrEmpty(agentUri.Fragment) || !string.IsNullOrEmpty(agentUri.UserInfo))
            throw new InvalidOperationException("AgentUrl must be a loopback HTTP URL.");
        
        AgentUrl = AgentUrl.TrimEnd('/');
        if (!IPAddress.TryParse(BindAddress, out IPAddress? bindIp) || 
            !IPAddress.IsLoopback(bindIp) || 
            bindIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || 
            RealtimeTls is null && (
                !IPAddress.TryParse(AdvertisedHost, out IPAddress? advertisedIp) || 
                !IPAddress.IsLoopback(advertisedIp) || 
                advertisedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new InvalidOperationException("This development service advertises only literal loopback endpoints.");
        
        if (RealtimeTls is not null && (Uri.CheckHostName(AdvertisedHost) == UriHostNameType.Unknown
            || !IPAddress.TryParse(RealtimeTls.ListenAddress, out IPAddress? tlsListen)
            || !IPAddress.IsLoopback(tlsListen) && AdmissionSigning is null))
            throw new InvalidOperationException("Remote TLS requires an advertised DNS/IP name and explicit admission signing trust.");
        
        if (FirstUdpPort < 1024 || LastUdpPort > 65535 || LastUdpPort < FirstUdpPort
            || MaxInstances < 1 || MaxInstances > 64 || MaxPlayerSlots < 1
            || MaxInstances > LastUdpPort - FirstUdpPort + 1)
            throw new InvalidOperationException("Invalid local host capacity or UDP port range.");
        
        if (StartupTimeoutSeconds is < 5 or > 600 || ShutdownTimeoutSeconds is < 1 or > 120
            || DrainTimeoutSeconds is < 1 or > 3600 || WorkerLeaseSeconds is < 5 or > 120
            || AdmissionTimeoutSeconds is < 5 or > 600 || ResumeWindowSeconds is < 0 or > 300
            || WorkerCpuPercent is < 1 or > 100 || WorkerMemoryLimitBytes < 256L * 1024 * 1024
            || HostMemoryBudgetBytes < WorkerMemoryLimitBytes)
            throw new InvalidOperationException("Invalid worker deadline or resource limit.");
        
        ArgumentException.ThrowIfNullOrWhiteSpace(HostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ServerExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkingRoot);

        ServerExecutable = Path.GetFullPath(ServerExecutable, configurationDirectory);
        WorkingRoot = Path.GetFullPath(WorkingRoot, configurationDirectory);
        HostAgentDataRoot = string.IsNullOrWhiteSpace(HostAgentDataRoot)
            ? Path.Combine(WorkingRoot, "host-agent")
            : Path.GetFullPath(HostAgentDataRoot, configurationDirectory);
        
        if (!File.Exists(ServerExecutable) || !ServerExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ServerExecutable must name a built dedicated server executable.");
        
        foreach (string key in Packages.Keys.ToArray())
            Packages[key] = Path.GetFullPath(Packages[key], configurationDirectory);
        
        if (Users.Count == 0 || Users.Select(user => user.UserId).Distinct(StringComparer.Ordinal).Count() != Users.Count)
            throw new InvalidOperationException("Configure at least one uniquely named local API user.");
        
        foreach (LocalApiUser user in Users)
            user.Initialize();
        
        if (Users.Select(user => user.TokenFingerprint).Distinct(StringComparer.Ordinal).Count() != Users.Count)
            throw new InvalidOperationException("Each local user must have a distinct API token.");
        
        NativeLauncher.Validate(configurationDirectory);
    }
}
