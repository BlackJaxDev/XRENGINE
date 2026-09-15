using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using XREngine.ControlPlane;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine.Networking;

/// <summary>
/// Private managed-worker adapter. Its network calls run away from the UDP/tick path; admission
/// only reads the last complete directive snapshot held in memory.
/// </summary>
internal sealed partial class ManagedServerWorker : IDisposable
{
    internal const string ConfigurationEnvironmentVariable = XREngineEnvironmentVariables.ManagedWorkerConfigFile;
    private readonly ManagedWorkerLaunch _launch;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _grantLock = new();
    private Dictionary<string, ManagedAdmissionGrant> _grants = new(StringComparer.Ordinal);
    // This cache supports idempotent retransmits after directives stop repeating Connected grants.
    // It is never roster or capacity authority.
    private readonly Dictionary<string, CachedAdmission> _acceptedAdmissionRetries = new(StringComparer.Ordinal);
    // Per principal, this prevents an old Join epoch from recreating a player after a
    // later Resume. Entries contain no bearer material and expire with the credential.
    private readonly Dictionary<ManagedCredentialPrincipal, CredentialEpochHighWater> _credentialEpochHighWater = [];
    private HashSet<string> _revoked = new(StringComparer.Ordinal);
    private long _lastDirectiveSequence;
    private long _reportSequence;
    private DateTimeOffset _freshUntilUtc;
    private volatile bool _draining;
    private volatile bool _stopping;
    private ManagedWorkerFailure? _failure;
    private Task? _pollTask;
    private XRWorld? _loadedWorld;

    private ManagedServerWorker(ManagedWorkerLaunch launch)
    {
        _launch = launch;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", launch.ManagementToken);
        _freshUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1_000, launch.Startup.ManagementLeaseMilliseconds));
    }

    public int MaxPlayers => _launch.MaxPlayers;
    public Guid SessionId => _launch.SessionId;
    public int BindPort => _launch.BindPort;
    public IPAddress BindAddress => IPAddress.Parse(_launch.BindAddress);
    public float TickRate => _launch.Startup.TickRate;
    public int FixedDeltaMilliseconds => _launch.Startup.FixedDeltaMilliseconds;
    public XRWorld LoadedWorld => _loadedWorld ?? throw new InvalidOperationException("Managed world has not loaded.");
    public WorldAssetIdentity WorldAsset => _launch.WorldPackage.Asset;

    public static ManagedServerWorker? TryLoadFromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Managed worker configuration file does not exist.", fullPath);

        ManagedWorkerLaunch launch = JsonSerializer.Deserialize(File.ReadAllText(fullPath), XreControlPlaneJsonContext.Default.ManagedWorkerLaunch)
            ?? throw new InvalidOperationException("Managed worker configuration file was empty.");
        ValidateLaunch(launch);
        return new ManagedServerWorker(launch);
    }

    public XRWorld LoadVerifiedWorld()
    {
        WorldPackageVerificationResult verification = WorldPackageManifestBuilder.Verify(_launch.WorldPackage, _launch.PackageRootPath, requireAssetContentHashMatch: true);
        if (!verification.Success)
            throw new InvalidOperationException("Managed world package verification failed.");

        string root = Path.GetFullPath(_launch.PackageRootPath);
        string worldPath = Path.GetFullPath(Path.Combine(root, _launch.WorldEntryPoint));
        if (!worldPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(worldPath), ".asset", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(worldPath))
        {
            throw new InvalidOperationException("Managed world entry point must be a verified native .asset file inside its package root.");
        }

        XRWorld world = Engine.Assets.Load<XRWorld>(worldPath, bypassJobThread: true)
            ?? throw new InvalidOperationException($"Managed world entry point '{_launch.WorldEntryPoint}' did not load an XRWorld.");
        ApplyGameBootstrap(world);
        world.Settings.PhysicsSubsteps = _launch.Startup.PhysicsSubsteps;
        WorldAssetIdentityProvider.RegisterVerifiedIdentity(world, _launch.WorldPackage.Asset);
        WorldAssetIdentityProvider.RegisterVerifiedAssetPaths(world, _launch.WorldPackage.Files.Select(static file => file.RelativePath));

        _loadedWorld = world;
        return world;
    }

    public void StartPolling()
    {
        if (_pollTask is not null)
            return;
        StartEncryptedIngress();
        _pollTask = Task.Run(PollAsync);
    }

    public ServerJoinAdmissionResult ResolveJoin(PlayerJoinRequest request, ServerSessionContext? session)
    {
        if (_draining || _stopping || DateTimeOffset.UtcNow > _freshUntilUtc)
            return new ServerJoinAdmissionResult(null, AdmissionFailureReason.Unauthorized, "Managed admission is temporarily unavailable.");

        if (string.IsNullOrWhiteSpace(request.ReservationId) || string.IsNullOrWhiteSpace(request.AdmissionSecret))
            return new ServerJoinAdmissionResult(null, AdmissionFailureReason.Unauthorized, "A player-specific managed admission grant is required.");

        lock (_grantLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_draining || _stopping || now > _freshUntilUtc)
                return new ServerJoinAdmissionResult(null, AdmissionFailureReason.Unauthorized, "Managed admission is temporarily unavailable.");
            if (_revoked.Contains(request.ReservationId))
                return new ServerJoinAdmissionResult(null, AdmissionFailureReason.Unauthorized, "The managed admission grant is not installed.");
            _grants.TryGetValue(request.ReservationId, out ManagedAdmissionGrant? grant);
            _acceptedAdmissionRetries.TryGetValue(request.ReservationId, out CachedAdmission? retry);
            if (grant is null && retry is not null)
                grant = retry.Grant;
            if (grant is null)
                return new ServerJoinAdmissionResult(null, AdmissionFailureReason.Unauthorized, "The managed admission grant is not installed.");
            bool sameCachedCredential = retry is not null
                && retry.Grant.Purpose == grant.Purpose
                && retry.Grant.CredentialEpoch == grant.CredentialEpoch;
            if (session is null || grant.ExpiresUtc <= now || grant.Generation != _launch.Generation || request.WorkerGeneration != _launch.Generation || request.SessionId != _launch.SessionId || grant.SessionId != _launch.SessionId
                || request.ResumeRequested != (grant.Purpose == ManagedAdmissionGrantPurpose.Resume)
                || request.CredentialEpoch != grant.CredentialEpoch
                || !string.Equals(grant.BuildVersion, _launch.BuildVersion, StringComparison.Ordinal)
                || !string.Equals(grant.WorldId, _launch.WorldPackage.Asset.WorldId, StringComparison.Ordinal)
                || !string.Equals(grant.WorldRevision, _launch.WorldPackage.Asset.RevisionId, StringComparison.Ordinal)
                || !string.Equals(WorldAssetIdentity.NormalizeHash(grant.ContentHash), WorldAssetIdentity.NormalizeHash(_launch.WorldPackage.Asset.ContentHash), StringComparison.Ordinal)
                || !SecretsEqual(grant.Secret, request.AdmissionSecret)
                || !string.Equals(grant.ClientId, request.ClientId, StringComparison.Ordinal)
                || !string.Equals(grant.AccountId, request.AccountId, StringComparison.Ordinal))
            {
                return new ServerJoinAdmissionResult(null, AdmissionFailureReason.Unauthorized, "The managed admission grant is invalid or expired.");
            }

            DateTimeOffset acceptedUtc = sameCachedCredential ? retry!.AcceptedUtc : now;
            _acceptedAdmissionRetries[grant.ReservationId] = new CachedAdmission(grant, acceptedUtc);

            return new ServerJoinAdmissionResult(
                session,
                AccountId: grant.AccountId,
                ReservationId: grant.ReservationId,
                CredentialPurpose: (int)grant.Purpose,
                CredentialEpoch: grant.CredentialEpoch,
                AcceptedUtc: acceptedUtc);
        }
    }

    /// <summary>Called only after server pawn/controller creation has completed.</summary>
    public void RecordConnected(ServerSessionPlayerEvent player)
    {
        if (string.IsNullOrWhiteSpace(player.ReservationId))
            return;
        Debug.Networking(
            "[ManagedWorker] Connected instance={0}; generation={1}; reservation={2}; playerIndex={3}.",
            _launch.InstanceId,
            _launch.Generation,
            player.ReservationId,
            player.ServerPlayerIndex);
    }

    public void RecordDisconnected(ServerSessionPlayerEvent player)
    {
        if (!string.IsNullOrWhiteSpace(player.ReservationId))
            lock (_grantLock)
            {
                _acceptedAdmissionRetries.Remove(player.ReservationId);
            }
    }

    public void Dispose()
    {
        _tlsGateway?.Dispose();
        _tlsCertificate?.Dispose();
        _stop.Cancel();
        try { _pollTask?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _http.Dispose();
        _stop.Dispose();
        lock (_grantLock)
        {
            foreach (ManagedAdmissionVerifier verifier in _grantVerifiers.Values)
                CryptographicOperations.ZeroMemory(verifier.RootKey);
            _grantVerifiers.Clear();
            _credentialEpochHighWater.Clear();
        }
    }

    private async Task PollAsync()
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(Math.Clamp(_launch.Startup.ManagementPollMilliseconds, 100, 30_000));
        while (!_stop.IsCancellationRequested)
        {
            if (!_stopping && !EncryptedIngressReady)
            {
                _failure = new ManagedWorkerFailure { Code = "encrypted_ingress_lost", Message = "The encrypted realtime listener stopped.", Retryable = false };
                RequestShutdown("encrypted realtime listener stopped");
            }
            try
            {
                ManagedWorkerDirective? directive = await PostReportAsync(_stop.Token).ConfigureAwait(false);
                if (directive is not null)
                    ApplyDirective(directive);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _failure = new ManagedWorkerFailure { Code = "management_poll_failed", Message = ex.Message, Retryable = true };
            }

            if (DateTimeOffset.UtcNow > _freshUntilUtc && _freshUntilUtc != default)
            {
                _draining = true;
                RequestShutdown("management lease expired");
                break;
            }

            await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
        }
    }

    private async Task<ManagedWorkerDirective?> PostReportAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(new Uri(_launch.ManagementUrl.TrimEnd('/') + "/"), $"v1/workers/{Uri.EscapeDataString(_launch.InstanceId)}/reports"));
        request.Content = JsonContent.Create(CreateReport(), XreControlPlaneJsonContext.Default.ManagedWorkerReport);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync(XreControlPlaneJsonContext.Default.ManagedWorkerDirective, cancellationToken).ConfigureAwait(false);
    }

    private ManagedWorkerReport CreateReport()
    {
        ServerNetworkingManager? server = Engine.Networking as ServerNetworkingManager;
        List<ServerSessionPlayerEvent> players = server is null ? [] : [.. server.GetConnectedPlayers()];
        return new ManagedWorkerReport
        {
            InstanceId = _launch.InstanceId,
            Generation = _launch.Generation,
            Sequence = Interlocked.Increment(ref _reportSequence),
            ObservedState = _stopping ? ManagedWorkerState.Stopping : _draining ? ManagedWorkerState.Draining : server is not null && _loadedWorld is not null && server.TickProgress > 0 && EncryptedIngressReady ? ManagedWorkerState.Ready : ManagedWorkerState.Starting,
            Failure = _failure,
            LoadedWorldAsset = _loadedWorld is null ? null : _launch.WorldPackage.Asset,
            BoundUdpPort = _launch.BindPort,
            TickProgress = server?.TickProgress ?? 0,
            InstalledReservationIds = GetInstalledReservationIds(),
            InstalledGrantEpochs = GetInstalledGrantEpochs(),
            Roster = CreateRoster(players),
            Metrics = CreateMetrics(players),
            ReportedUtc = DateTimeOffset.UtcNow,
        };
    }

    private ManagedWorkerMetrics CreateMetrics(IReadOnlyList<ServerSessionPlayerEvent> players)
    {
        AuthoritativeSimulationMetrics simulation = Engine.Networking is ServerNetworkingManager server
            ? server.GetAuthoritativeSimulationMetrics()
            : default;
        return new ManagedWorkerMetrics
        {
            ConnectedPlayers = players.Count,
            SynchronizedPlayers = players.Count(static player => player.SynchronizedUtc.HasValue),
            TickRate = _launch.Startup.TickRate,
            WorkingSetBytes = Environment.WorkingSet,
            LastSimulationTickMilliseconds = simulation.LastTickDurationMilliseconds,
            ConfiguredFixedTickMilliseconds = simulation.ConfiguredFixedTickMilliseconds,
            LastTickBudgetHeadroomMilliseconds = simulation.LastTickBudgetHeadroomMilliseconds,
            LastSimulationTickAllocatedBytes = simulation.LastTickAllocatedBytes,
            TotalSimulationTickCount = simulation.TotalSimulationTickCount,
            SimulationMetricWindowTickCount = simulation.SampleWindowTickCount,
            SimulationMetricWindowAverageTickMilliseconds = simulation.SampleWindowAverageTickMilliseconds,
            SimulationMetricWindowPeakTickMilliseconds = simulation.SampleWindowPeakTickMilliseconds,
            SimulationMetricWindowAllocatedBytes = simulation.SampleWindowAllocatedBytes,
            InputQueueDepth = simulation.InputQueueDepth,
            OldestInputAgeMilliseconds = simulation.OldestInputAgeMilliseconds,
            SimulatedInputCount = simulation.SimulatedInputCount,
            RejectedInputCount = simulation.RejectedInputCount,
            AuthoritativeTransformBytes = simulation.AuthoritativeTransformBytes,
            EncryptedIngressListening = _tlsGateway?.IsListening ?? false,
            EncryptedConnections = _tlsGateway?.ConnectionCount ?? 0,
            RejectedEncryptedConnections = _tlsGateway?.RejectedConnections ?? 0,
            EncryptedReceivedBytes = _tlsGateway?.ReceivedBytes ?? 0,
            EncryptedSentBytes = _tlsGateway?.SentBytes ?? 0,
        };
    }

    private void ApplyDirective(ManagedWorkerDirective directive)
    {
        if (directive.ContractVersion != _launch.ContractVersion || directive.InstanceId != _launch.InstanceId || directive.Generation != _launch.Generation || directive.DirectiveSequence <= Interlocked.Read(ref _lastDirectiveSequence))
            return;

        ServerNetworkingManager? server = Engine.Networking as ServerNetworkingManager;
        var installedGrants = new Dictionary<string, ManagedAdmissionGrant>(StringComparer.Ordinal);
        foreach (ManagedAdmissionGrant grant in directive.AdmissionGrants)
        {
            if (_launch.AdmissionSigningKeys.Count > 0 && !ManagedAdmissionSigning.Verify(grant, _launch, DateTimeOffset.UtcNow))
                throw new InvalidDataException("The manager supplied an invalid signed admission credential.");
            if (IsCredentialEpochRetired(grant))
                continue;
            if (grant.Purpose == ManagedAdmissionGrantPurpose.Resume
                && (server is null || !server.TrySetReservationResumeDeadline(grant.ReservationId, grant.ClientId, grant.SessionId, grant.Generation, grant.ExpiresUtc)))
            {
                continue;
            }

            installedGrants[grant.ReservationId] = grant;
        }

        lock (_grantLock)
        {
            PruneCredentialEpochHighWater(DateTimeOffset.UtcNow);
            foreach (ManagedAdmissionGrant grant in installedGrants.Values)
            {
                var identity = CreateAdmissionIdentity(grant);
                byte[] key = ManagedUdpAuthentication.DeriveRootKey(grant.Secret, identity);
                grant.Secret = string.Empty;
                if (_grantVerifiers.Remove(grant.ReservationId, out ManagedAdmissionVerifier? previous))
                    CryptographicOperations.ZeroMemory(previous.RootKey);
                _grantVerifiers.Add(grant.ReservationId, new ManagedAdmissionVerifier { Identity = identity, RootKey = key, ExpiresUtc = grant.ExpiresUtc });
            }
            _grants = installedGrants;
            _revoked = directive.RevokedReservationIds.ToHashSet(StringComparer.Ordinal);
            foreach (string reservationId in _acceptedAdmissionRetries
                .Where(pair => pair.Value.Grant.ExpiresUtc <= DateTimeOffset.UtcNow || _revoked.Contains(pair.Key))
                .Select(static pair => pair.Key)
                .ToArray())
            {
                _acceptedAdmissionRetries.Remove(reservationId);
            }
            foreach (string reservationId in _grantVerifiers.Where(pair => pair.Value.ExpiresUtc <= DateTimeOffset.UtcNow || _revoked.Contains(pair.Key)
                || !_grants.ContainsKey(pair.Key) && !_acceptedAdmissionRetries.ContainsKey(pair.Key)).Select(static pair => pair.Key).ToArray())
            {
                CryptographicOperations.ZeroMemory(_grantVerifiers[reservationId].RootKey);
                _grantVerifiers.Remove(reservationId);
            }
            Interlocked.Exchange(ref _lastDirectiveSequence, directive.DirectiveSequence);
            _freshUntilUtc = directive.FreshUntilUtc;
            _draining = directive.Draining;
        }

        if (server is not null)
        {
            foreach (string reservationId in directive.KickReservationIds)
            {
                Engine.EnqueueMainThreadTask(() => server.KickReservation(reservationId, "Managed admission revoked"), "managed worker kick");
            }
        }

        if (directive.Stop)
            RequestShutdown("manager requested stop");
    }

    private List<string> GetInstalledReservationIds()
    {
        lock (_grantLock)
            return [.. _grants.Keys];
    }

    private Dictionary<string, long> GetInstalledGrantEpochs()
    {
        lock (_grantLock)
            return _grants.ToDictionary(static pair => pair.Key, static pair => pair.Value.CredentialEpoch, StringComparer.Ordinal);
    }

    private List<ManagedWorkerRosterEntry> CreateRoster(IEnumerable<ServerSessionPlayerEvent> players)
    {
        lock (_grantLock)
        {
            var roster = new List<ManagedWorkerRosterEntry>();
            foreach (ServerSessionPlayerEvent player in players)
            {
                if (string.IsNullOrWhiteSpace(player.ReservationId)
                    || player.CredentialPurpose is not int purpose
                    || !Enum.IsDefined((ManagedAdmissionGrantPurpose)purpose)
                    || player.ConnectedUtc == default)
                {
                    continue;
                }

                roster.Add(new ManagedWorkerRosterEntry
                {
                    ReservationId = player.ReservationId,
                    ClientId = player.ClientId,
                    AccountId = player.AccountId ?? string.Empty,
                    CredentialPurpose = (ManagedAdmissionGrantPurpose)purpose,
                    CredentialEpoch = player.CredentialEpoch,
                    State = player.SynchronizedUtc.HasValue ? ManagedPlayerConnectionState.Synchronized : ManagedPlayerConnectionState.Connected,
                    ConnectedUtc = player.ConnectedUtc,
                    SynchronizedUtc = player.SynchronizedUtc,
                });
            }
            return roster;
        }
    }

    private static bool SecretsEqual(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied))
            return false;
        byte[] expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        byte[] suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private sealed record CachedAdmission(ManagedAdmissionGrant Grant, DateTimeOffset AcceptedUtc);
    private readonly record struct ManagedCredentialPrincipal(Guid SessionId, string ClientId, string AccountId);
    private readonly record struct CredentialEpochHighWater(long Epoch, DateTimeOffset ExpiresUtc);

    private bool IsCredentialEpochRetired(ManagedAdmissionGrant grant)
    {
        lock (_grantLock)
        {
            PruneCredentialEpochHighWater(DateTimeOffset.UtcNow);
            return _credentialEpochHighWater.TryGetValue(CreateCredentialPrincipal(grant), out CredentialEpochHighWater highWater)
                && grant.CredentialEpoch <= highWater.Epoch;
        }
    }

    private bool CanAdvanceCredentialEpoch(ManagedAdmissionGrant grant)
    {
        ManagedCredentialPrincipal principal = CreateCredentialPrincipal(grant);
        PruneCredentialEpochHighWater(DateTimeOffset.UtcNow);
        if (_credentialEpochHighWater.TryGetValue(principal, out CredentialEpochHighWater highWater))
            return grant.CredentialEpoch > highWater.Epoch;
        return _credentialEpochHighWater.Count < CredentialEpochHighWaterCapacity;
    }

    private void RecordCredentialEpoch(ManagedAdmissionGrant grant)
    {
        ManagedCredentialPrincipal principal = CreateCredentialPrincipal(grant);
        PruneCredentialEpochHighWater(DateTimeOffset.UtcNow);
        _credentialEpochHighWater[principal] = new CredentialEpochHighWater(grant.CredentialEpoch, grant.ExpiresUtc);
    }

    private void PruneCredentialEpochHighWater(DateTimeOffset now)
    {
        foreach (ManagedCredentialPrincipal principal in _credentialEpochHighWater
            .Where(pair => pair.Value.ExpiresUtc <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _credentialEpochHighWater.Remove(principal);
        }
    }

    private static ManagedCredentialPrincipal CreateCredentialPrincipal(ManagedAdmissionGrant grant)
        => new(grant.SessionId, grant.ClientId, grant.AccountId);

    private int CredentialEpochHighWaterCapacity => Math.Clamp(_launch.MaxPlayers, 1, 512) * 8;


    private void ApplyGameBootstrap(XRWorld world)
    {
        // world-v1 is the declared built-in bootstrap for package-authored worlds. It intentionally
        // avoids local devices; remote pawns are created only by ServerNetworkingManager admission.
        if (!string.Equals(_launch.GameBootstrapId, "world-v1", StringComparison.Ordinal))
            throw new NotSupportedException($"Managed game bootstrap '{_launch.GameBootstrapId}' is not registered by this server build.");

        world.DefaultGameMode ??= new CustomGameMode { DefaultPlayerPawnClass = null };
    }

    private void RequestShutdown(string reason)
    {
        if (_stopping)
            return;
        _stopping = true;
        Engine.EnqueueMainThreadTask(Engine.ShutDown, $"managed worker shutdown: {reason}");
    }

    private static void ValidateLaunch(ManagedWorkerLaunch launch)
    {
        ValidateEncryptedIngress(launch);
        if (launch.ContractVersion != 1 || string.IsNullOrWhiteSpace(launch.InstanceId) || launch.Generation == Guid.Empty || launch.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(launch.ManagementUrl) || string.IsNullOrWhiteSpace(launch.ManagementToken) || !IPAddress.TryParse(launch.BindAddress, out _) || launch.BindPort is < 1 or > 65535
            || launch.MaxPlayers <= 0 || string.IsNullOrWhiteSpace(launch.PackageRootPath) || string.IsNullOrWhiteSpace(launch.WorldEntryPoint)
            || !string.Equals(launch.GameBootstrapId, "world-v1", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(launch.WorldPackage.WorldEntryPoint) && !string.Equals(launch.WorldEntryPoint, launch.WorldPackage.WorldEntryPoint, StringComparison.Ordinal)
            || !string.Equals(launch.BuildVersion, launch.WorldPackage.Asset.RequiredBuildVersion, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(launch.WorldPackage.BuildVersion) && !string.Equals(launch.BuildVersion, launch.WorldPackage.BuildVersion, StringComparison.Ordinal)
            || launch.Startup.TickRate <= 0 || launch.Startup.FixedDeltaMilliseconds <= 0 || launch.Startup.PhysicsSubsteps <= 0)
        {
            throw new InvalidOperationException("Managed worker configuration is incomplete or invalid.");
        }
    }
}
