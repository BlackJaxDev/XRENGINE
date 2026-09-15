using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed partial class InMemoryControlPlane
{
    public ControlPlaneResult<MultiplayerInstanceInfo> CreateManagedInstance(CreateMultiplayerInstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.DevelopmentMode = false;
        ControlPlaneResult<MultiplayerInstanceInfo> result = CreateInstanceCore(request, managed: true);
        if (result.Value is not null)
            result.Value.SessionToken = string.Empty;
        return result;
    }

    public ControlPlaneResult<ManagedWorkerLaunch> ConfigureManagedWorkerLaunch(ManagedWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (string.IsNullOrWhiteSpace(launch.InstanceId) || launch.Generation == Guid.Empty || string.IsNullOrWhiteSpace(launch.ManagementToken)
            || launch.BindPort is < 1 or > 65535 || launch.WorldPackage is null)
            return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InvalidRequest, "Managed worker launch configuration is incomplete.");
        if (!string.Equals(WorldAssetIdentity.NormalizeHash(launch.WorldPackage.Asset.ContentHash), WorldAssetIdentity.NormalizeHash(launch.WorldPackage.ManifestHash), StringComparison.OrdinalIgnoreCase))
            return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InvalidRequest, "Managed package asset identity must bind to its manifest hash.");
        if (string.IsNullOrWhiteSpace(launch.WorldEntryPoint) || string.IsNullOrWhiteSpace(launch.GameBootstrapId)
            || !string.Equals(launch.WorldEntryPoint, launch.WorldPackage.WorldEntryPoint, StringComparison.Ordinal)
            || !string.Equals(launch.GameBootstrapId, launch.WorldPackage.GameBootstrapId, StringComparison.Ordinal)
            || !string.Equals(launch.BuildVersion, launch.WorldPackage.BuildVersion, StringComparison.Ordinal)
            || !launch.WorldPackage.Files.Any(file => string.Equals(file.RelativePath, launch.WorldEntryPoint.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InvalidRequest, "Managed launch must exactly name a packaged world entry point, game bootstrap, and build.");

        lock (_sync)
        {
            if (!_instances.TryGetValue(launch.InstanceId.Trim(), out InstanceState? instance))
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{launch.InstanceId}' was not found.");
            if (!instance.IsManaged || instance.ProcessExitConfirmed || instance.RequestedState == ManagedWorkerState.Stopping)
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InvalidRequest, "The managed worker launch can no longer be configured.");
            if (instance.Info.WorkerGeneration != launch.Generation || instance.Info.SessionId != launch.SessionId)
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.StaleWorkerGeneration, "Launch configuration does not match the allocated worker generation.");
            if (!string.Equals(instance.Info.OperationId, launch.OperationId, StringComparison.Ordinal))
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.OperationConflict, "Launch configuration does not match the allocation operation.");
            if (!instance.Info.WorldAsset.IsSameAssetAs(launch.WorldPackage.Asset) || launch.MaxPlayers != instance.Info.MaxPlayers)
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InvalidRequest, "Launch configuration does not match the allocated instance content or capacity.");
            if (!string.Equals(instance.Info.WorldAsset.RequiredBuildVersion, launch.WorldPackage.Asset.RequiredBuildVersion, StringComparison.Ordinal)
                || !string.Equals(launch.WorldPackage.Asset.RequiredBuildVersion, launch.WorldPackage.BuildVersion, StringComparison.Ordinal)
                || !string.Equals(launch.WorldPackage.BuildVersion, launch.BuildVersion, StringComparison.Ordinal))
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.BuildVersionMismatch, "Managed worker build identities conflict.");
            if (instance.Launch is not null && !IsEquivalentLaunch(instance.Launch, launch))
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.OperationConflict, "Worker launch configuration is immutable after its first assignment.");

            instance.Launch = CloneLaunch(launch);
            if (instance.ObservedState == ManagedWorkerState.Allocated)
                instance.Info.State = MultiplayerInstanceState.Allocating;
            return ControlPlaneResult<ManagedWorkerLaunch>.Ok(CloneLaunch(instance.Launch));
        }
    }

    public ControlPlaneResult<ManagedWorkerLaunch> GetManagedWorkerLaunch(string instanceId, Guid generation)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || generation == Guid.Empty)
            return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InvalidRequest, "Instance id and worker generation are required.");

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance))
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{instanceId}' was not found.");
            if (instance.Info.WorkerGeneration != generation || instance.Launch is null)
                return ControlPlaneResult<ManagedWorkerLaunch>.Fail(ControlPlaneFailureReason.StaleWorkerGeneration, "No matching worker launch is available.");
            return ControlPlaneResult<ManagedWorkerLaunch>.Ok(CloneLaunch(instance.Launch));
        }
    }

    public bool HeartbeatHost(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            return false;

        lock (_sync)
        {
            if (!_hosts.TryGetValue(hostId.Trim(), out HostState? host))
                return false;

            host.LeaseExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.HostHeartbeatLeaseSeconds));
            return true;
        }
    }

    public ControlPlaneResult<ReserveManagedAdmissionResult> ReserveAdmission(ReserveManagedAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.AccountId))
            return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InvalidRequest, "Instance, client, and account ids are required.");

        lock (_sync)
        {
            if (!_instances.TryGetValue(request.InstanceId.Trim(), out InstanceState? instance))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{request.InstanceId}' was not found.");
            if (!instance.IsManaged)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InvalidRequest, "Admission reservations are only valid for managed instances.");

            ExpireReservations(instance, DateTimeOffset.UtcNow);
            if (instance.Info.State == MultiplayerInstanceState.Draining)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceDraining, "The instance is draining.");
            if (instance.Info.State != MultiplayerInstanceState.Ready)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceNotReady, "The instance has not reported readiness.");
            if (!string.Equals(instance.Info.WorldAsset.RequiredBuildVersion, request.BuildVersion, StringComparison.Ordinal))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.BuildVersionMismatch, "Client build version is not compatible with the instance.");

            string idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? string.Empty
                : request.IdempotencyKey.Trim();
            string reservationFingerprint = string.Join('|', request.InstanceId, request.ClientId, request.AccountId, request.BuildVersion, request.LifetimeSeconds);
            if (!string.IsNullOrEmpty(idempotencyKey) && instance.ReservationIdempotency.TryGetValue(idempotencyKey, out string? priorId))
            {
                if (!instance.ReservationFingerprints.TryGetValue(idempotencyKey, out string? priorFingerprint) || !string.Equals(priorFingerprint, reservationFingerprint, StringComparison.Ordinal))
                    return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.OperationConflict, "The idempotency key was retried with different parameters.");
                if (!instance.Reservations.TryGetValue(priorId, out ManagedAdmissionReservation? prior))
                    return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.ReservationNotFound, "The idempotent reservation operation is terminal.");
                if (!string.Equals(prior.ClientId, request.ClientId.Trim(), StringComparison.Ordinal) || !string.Equals(prior.AccountId, request.AccountId.Trim(), StringComparison.Ordinal))
                    return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.OperationConflict, "The idempotency key belongs to a different client or account.");
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Ok(CreateReservationResult(instance, prior));
            }

            if (instance.Reservations.Values.Any(reservation =>
                string.Equals(reservation.ClientId, request.ClientId.Trim(), StringComparison.Ordinal)
                && string.Equals(reservation.AccountId, request.AccountId.Trim(), StringComparison.Ordinal)
                && !instance.RevokedReservationIds.Contains(reservation.ReservationId)))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.OperationConflict, "The client already has an active admission reservation.");

            if (instance.Reservations.Count >= instance.Info.MaxPlayers)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceFull, "All admission slots are reserved.");

            DateTimeOffset expiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(request.LifetimeSeconds ?? _options.AdmissionReservationLifetimeSeconds, 1, 3600));
            string reservationId = Guid.NewGuid().ToString("N");
            var reservation = new ManagedAdmissionReservation
            {
                ReservationId = reservationId,
                InstanceId = instance.Info.InstanceId,
                ClientId = request.ClientId.Trim(),
                AccountId = request.AccountId.Trim(),
                State = ManagedPlayerConnectionState.PendingDelivery,
                ExpiresUtc = expiresUtc,
            };
            instance.Reservations.Add(reservationId, reservation);
            instance.Grants.Add(reservationId, new ManagedAdmissionGrant
            {
                ReservationId = reservationId,
                Secret = CreateOpaqueToken(_options.TokenByteLength),
                ClientId = reservation.ClientId,
                AccountId = reservation.AccountId,
                SessionId = instance.Info.SessionId,
                Generation = instance.Info.WorkerGeneration,
                WorldId = instance.Info.WorldAsset.WorldId,
                WorldRevision = instance.Info.WorldAsset.RevisionId,
                ContentHash = instance.Info.WorldAsset.ContentHash,
                BuildVersion = request.BuildVersion,
                ExpiresUtc = expiresUtc,
            });
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                instance.ReservationIdempotency[idempotencyKey] = reservationId;
                instance.ReservationFingerprints[idempotencyKey] = reservationFingerprint;
            }

            UpdateOccupancy(instance);
            return ControlPlaneResult<ReserveManagedAdmissionResult>.Ok(CreateReservationResult(instance, reservation));
        }
    }

    public ControlPlaneResult<ManagedAdmissionReservation> GetReservation(string instanceId, string reservationId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(reservationId))
            return ControlPlaneResult<ManagedAdmissionReservation>.Fail(ControlPlaneFailureReason.InvalidRequest, "Instance and reservation ids are required.");

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance))
                return ControlPlaneResult<ManagedAdmissionReservation>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{instanceId}' was not found.");
            return instance.Reservations.TryGetValue(reservationId.Trim(), out ManagedAdmissionReservation? reservation)
                ? ControlPlaneResult<ManagedAdmissionReservation>.Ok(CloneReservation(reservation))
                : ControlPlaneResult<ManagedAdmissionReservation>.Fail(ControlPlaneFailureReason.ReservationNotFound, $"Reservation '{reservationId}' was not found.");
        }
    }

    public ControlPlaneResult<ReserveManagedAdmissionResult> GetDeliveredAdmission(string instanceId, string reservationId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(reservationId))
            return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InvalidRequest, "Instance and reservation ids are required.");

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{instanceId}' was not found.");
            if (!instance.Reservations.TryGetValue(reservationId.Trim(), out ManagedAdmissionReservation? reservation))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.ReservationNotFound, $"Reservation '{reservationId}' was not found.");
            if (instance.RevokedReservationIds.Contains(reservation.ReservationId))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.ReservationRevoked, "The admission reservation was revoked.");
            if (instance.Info.State == MultiplayerInstanceState.Draining)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceDraining, "The instance is draining.");
            if (instance.Info.State != MultiplayerInstanceState.Ready)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.InstanceNotReady, "The instance is not ready for admission.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (reservation.State is ManagedPlayerConnectionState.Connected or ManagedPlayerConnectionState.Synchronized)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.ReservationConsumed, "The admission credential has already been consumed.");
            if (reservation.State == ManagedPlayerConnectionState.PendingDelivery && reservation.ExpiresUtc <= now
                || reservation.State == ManagedPlayerConnectionState.Installed && reservation.ExpiresUtc <= now
                || reservation.State == ManagedPlayerConnectionState.ResumeHeld && reservation.ResumeUntilUtc <= now)
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.ReservationExpired, "The admission credential has expired.");
            if (!IsCurrentGrantInstalled(instance, reservation.ReservationId))
                return ControlPlaneResult<ReserveManagedAdmissionResult>.Fail(ControlPlaneFailureReason.ReservationPendingDelivery, "The worker has not installed the current reservation credential.");
            return ControlPlaneResult<ReserveManagedAdmissionResult>.Ok(CreateReservationResult(instance, reservation));
        }
    }

    public bool RevokeAdmission(string instanceId, string reservationId, bool kick = false)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(reservationId))
            return false;

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance) || !instance.Reservations.ContainsKey(reservationId.Trim()))
                return false;

            instance.RevokedReservationIds.Add(reservationId.Trim());
            if (kick)
                instance.KickReservationIds.Add(reservationId.Trim());
            // Preserve the slot until the next authoritative roster confirms the worker has removed it.
            UpdateOccupancy(instance);
            return true;
        }
    }

    public bool SetRequestedWorkerState(string instanceId, ManagedWorkerState requestedState)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || requestedState is not (ManagedWorkerState.Draining or ManagedWorkerState.Stopping))
            return false;

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance))
                return false;
            if (!instance.IsManaged || instance.ProcessExitConfirmed)
                return false;

            // Stop is terminal from the manager's perspective and cannot be weakened by a later request.
            if (instance.RequestedState == ManagedWorkerState.Stopping)
                return true;

            instance.RequestedState = requestedState;
            if (requestedState == ManagedWorkerState.Stopping)
                instance.Info.State = MultiplayerInstanceState.Stopping;
            else if (requestedState == ManagedWorkerState.Draining && instance.Info.State is MultiplayerInstanceState.Ready or MultiplayerInstanceState.Draining)
                instance.Info.State = MultiplayerInstanceState.Draining;
            return true;
        }
    }

    public bool RecordLaunchProgress(string instanceId, Guid generation, ManagedWorkerState state)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || generation == Guid.Empty || state is not (ManagedWorkerState.Allocated or ManagedWorkerState.Staging or ManagedWorkerState.Starting))
            return false;

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance) || !instance.IsManaged || instance.Info.WorkerGeneration != generation || instance.ProcessExitConfirmed
                || state < instance.ObservedState)
                return false;
            instance.ObservedState = state;
            ApplyWorkerState(instance, state);
            return true;
        }
    }

    public bool RecordOwnedProcessExit(string instanceId, Guid generation, int? exitCode, ManagedWorkerFailure? failure = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || generation == Guid.Empty)
            return false;

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance) || instance.Info.WorkerGeneration != generation)
                return false;

            if (instance.ProcessExitConfirmed)
                return true;
            instance.ProcessExitConfirmed = true;
            instance.ObservedState = failure is null && (exitCode is 0 or null) ? ManagedWorkerState.Stopped : ManagedWorkerState.Failed;
            instance.Info.State = instance.ObservedState == ManagedWorkerState.Stopped ? MultiplayerInstanceState.Stopped : MultiplayerInstanceState.Failed;
            instance.Reservations.Clear();
            instance.Grants.Clear();
            instance.RevokedReservationIds.Clear();
            instance.KickReservationIds.Clear();
            instance.InstalledGrantEpochs.Clear();
            UpdateOccupancy(instance);
            return true;
        }
    }

    public ControlPlaneResult<ManagedWorkerDirective> RecordWorkerReport(ManagedWorkerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(report.InstanceId) || report.Generation == Guid.Empty)
            return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Instance id and generation are required.");

        lock (_sync)
        {
            if (!_instances.TryGetValue(report.InstanceId.Trim(), out InstanceState? instance))
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{report.InstanceId}' was not found.");
            if (instance.Info.WorkerGeneration != report.Generation)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.StaleWorkerGeneration, "Worker generation is stale.");
            if (instance.ProcessExitConfirmed)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.StaleWorkerGeneration, "The worker process has already exited.");
            if (report.ContractVersion != 1)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Unsupported worker report contract version.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (report.ReportedUtc == default || report.ReportedUtc > now.AddMinutes(1) || report.ReportedUtc < now.AddSeconds(-Math.Max(1, _options.WorkerReportMaximumAgeSeconds))
                || report.ReportedUtc <= instance.LastWorkerReportedUtc)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Worker report timestamp is invalid.");
            if (report.Sequence <= instance.LastWorkerReportSequence)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.StaleWorkerReport, "Worker report sequence is stale.");
            if (report.ObservedState < instance.ObservedState && instance.ObservedState is not ManagedWorkerState.Failed)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Worker lifecycle state regressed.");

            instance.LastWorkerReportSequence = report.Sequence;
            instance.LastWorkerReportedUtc = report.ReportedUtc;
            if (report.ObservedState == ManagedWorkerState.Ready
                && (instance.Launch is null || report.LoadedWorldAsset is null || !instance.Info.WorldAsset.IsSameAssetAs(report.LoadedWorldAsset)
                    || report.BoundUdpPort == 0 || report.BoundUdpPort != instance.Launch.BindPort))
            {
                instance.Info.State = MultiplayerInstanceState.Failed;
                instance.RequestedState = ManagedWorkerState.Stopping;
                instance.ObservedState = ManagedWorkerState.Failed;
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.WorldAssetMismatch, "Worker readiness did not prove the allocated content and endpoint.");
            }
            instance.ObservedState = report.ObservedState;
            ApplyWorkerState(instance, report.ObservedState);

            foreach (string reservationId in report.InstalledReservationIds)
            {
                if (instance.Reservations.TryGetValue(reservationId, out ManagedAdmissionReservation? reservation)
                    && reservation.State == ManagedPlayerConnectionState.PendingDelivery)
                    reservation.State = ManagedPlayerConnectionState.Installed;
            }
            foreach ((string reservationId, long credentialEpoch) in report.InstalledGrantEpochs)
            {
                if (!instance.Grants.TryGetValue(reservationId, out ManagedAdmissionGrant? grant) || grant.CredentialEpoch != credentialEpoch)
                    continue;
                instance.InstalledGrantEpochs[reservationId] = credentialEpoch;
                if (instance.Reservations.TryGetValue(reservationId, out ManagedAdmissionReservation? reservation)
                    && reservation.State == ManagedPlayerConnectionState.PendingDelivery)
                    reservation.State = ManagedPlayerConnectionState.Installed;
            }

            if (!IsAuthoritativeRosterValid(instance, report.Roster))
            {
                instance.RequestedState = ManagedWorkerState.Stopping;
                instance.Info.State = MultiplayerInstanceState.Failed;
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Worker roster violates the admission contract.");
            }
            ReconcileRoster(instance, report.Roster);
            ExpireReservations(instance, DateTimeOffset.UtcNow, includeInstalledAndResume: true);
            UpdateOccupancy(instance);
            return ControlPlaneResult<ManagedWorkerDirective>.Ok(CreateDirective(instance));
        }
    }

    private ReserveManagedAdmissionResult CreateReservationResult(InstanceState instance, ManagedAdmissionReservation reservation)
    {
        bool delivered = IsCurrentGrantInstalled(instance, reservation.ReservationId);
        return new ReserveManagedAdmissionResult
        {
            Reservation = CloneReservation(reservation),
            Grant = delivered && reservation.State is (ManagedPlayerConnectionState.Installed or ManagedPlayerConnectionState.ResumeHeld)
                ? CloneGrant(instance.Grants[reservation.ReservationId])
                : null,
            DeliveredToWorker = delivered,
        };
    }

    private void ReconcileRoster(InstanceState instance, IEnumerable<ManagedWorkerRosterEntry> roster)
    {
        HashSet<string> reportedReservationIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedWorkerRosterEntry entry in roster)
        {
            if (!instance.Reservations.TryGetValue(entry.ReservationId, out ManagedAdmissionReservation? reservation))
                continue;
            if (!string.Equals(reservation.ClientId, entry.ClientId, StringComparison.Ordinal) || !string.Equals(reservation.AccountId, entry.AccountId, StringComparison.Ordinal))
                continue;
            if (!instance.Grants.TryGetValue(entry.ReservationId, out ManagedAdmissionGrant? grant)
                || grant.Purpose != entry.CredentialPurpose || grant.CredentialEpoch != entry.CredentialEpoch
                || entry.ConnectedUtc == default
                || entry.State is not (ManagedPlayerConnectionState.Connected or ManagedPlayerConnectionState.Synchronized or ManagedPlayerConnectionState.ResumeHeld)
                || grant.Purpose == ManagedAdmissionGrantPurpose.Join && entry.ConnectedUtc > reservation.ExpiresUtc
                || grant.Purpose == ManagedAdmissionGrantPurpose.Resume && (reservation.ResumeUntilUtc is null || entry.ConnectedUtc > reservation.ResumeUntilUtc))
                continue;
            if (!reportedReservationIds.Add(entry.ReservationId))
                continue;
            // Revocation disables admission immediately, but occupancy remains an observation:
            // an accepted handshake may finish before the worker applies its queued kick.
            // Keep reporting that live player until a later roster confirms actual removal.
            reservation.State = entry.State switch
            {
                ManagedPlayerConnectionState.Synchronized => ManagedPlayerConnectionState.Synchronized,
                ManagedPlayerConnectionState.Connected => ManagedPlayerConnectionState.Connected,
                ManagedPlayerConnectionState.ResumeHeld => ManagedPlayerConnectionState.ResumeHeld,
                _ => reservation.State,
            };
        }

        foreach (string revokedId in instance.RevokedReservationIds.ToArray())
        {
            if (reportedReservationIds.Contains(revokedId))
                continue;
            instance.Reservations.Remove(revokedId);
            instance.Grants.Remove(revokedId);
            instance.KickReservationIds.Remove(revokedId);
            instance.InstalledGrantEpochs.Remove(revokedId);
        }

        DateTimeOffset resumeUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.AdmissionResumeLeaseSeconds));
        foreach (ManagedAdmissionReservation reservation in instance.Reservations.Values)
        {
            if (reservation.State == ManagedPlayerConnectionState.ResumeHeld && reservation.ResumeUntilUtc is null)
            {
                reservation.ResumeUntilUtc = resumeUntil;
                RotateResumeGrant(instance, reservation);
            }
            if (reservation.State is ManagedPlayerConnectionState.Connected or ManagedPlayerConnectionState.Synchronized
                && !reportedReservationIds.Contains(reservation.ReservationId))
            {
                reservation.State = ManagedPlayerConnectionState.ResumeHeld;
                reservation.ResumeUntilUtc = resumeUntil;
                RotateResumeGrant(instance, reservation);
            }
        }
    }

    private static bool IsAuthoritativeRosterValid(InstanceState instance, IReadOnlyCollection<ManagedWorkerRosterEntry> roster)
    {
        if (roster.Count > instance.Info.MaxPlayers)
            return false;

        HashSet<string> reservationIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedWorkerRosterEntry entry in roster)
        {
            if (!reservationIds.Add(entry.ReservationId)
                || entry.ConnectedUtc == default
                || entry.State is not (ManagedPlayerConnectionState.Connected or ManagedPlayerConnectionState.Synchronized or ManagedPlayerConnectionState.ResumeHeld)
                || !instance.Reservations.TryGetValue(entry.ReservationId, out ManagedAdmissionReservation? reservation)
                || !instance.Grants.TryGetValue(entry.ReservationId, out ManagedAdmissionGrant? grant)
                || !string.Equals(reservation.ClientId, entry.ClientId, StringComparison.Ordinal)
                || !string.Equals(reservation.AccountId, entry.AccountId, StringComparison.Ordinal)
                || grant.Purpose != entry.CredentialPurpose
                || grant.CredentialEpoch != entry.CredentialEpoch
                || grant.Purpose == ManagedAdmissionGrantPurpose.Join && entry.ConnectedUtc > reservation.ExpiresUtc
                || grant.Purpose == ManagedAdmissionGrantPurpose.Resume && (reservation.ResumeUntilUtc is null || entry.ConnectedUtc > reservation.ResumeUntilUtc))
                return false;
        }
        return true;
    }

    private void ApplyWorkerState(InstanceState instance, ManagedWorkerState observed)
    {
        if (observed == ManagedWorkerState.Stopped)
        {
            // Only the host supervisor's OS-process observation releases capacity and reservations.
            instance.Info.State = MultiplayerInstanceState.Stopping;
            return;
        }

        if (observed == ManagedWorkerState.Failed)
        {
            instance.Info.State = MultiplayerInstanceState.Failed;
            return;
        }

        if (instance.RequestedState == ManagedWorkerState.Stopping)
        {
            instance.Info.State = MultiplayerInstanceState.Stopping;
            return;
        }

        instance.Info.State = observed switch
        {
            ManagedWorkerState.Allocated => MultiplayerInstanceState.Allocating,
            ManagedWorkerState.Staging => MultiplayerInstanceState.Staging,
            ManagedWorkerState.Starting => MultiplayerInstanceState.Starting,
            ManagedWorkerState.Ready => instance.RequestedState == ManagedWorkerState.Draining ? MultiplayerInstanceState.Draining : MultiplayerInstanceState.Ready,
            ManagedWorkerState.Draining => MultiplayerInstanceState.Draining,
            ManagedWorkerState.Stopping => MultiplayerInstanceState.Stopping,
            _ => instance.Info.State,
        };
    }

    private ManagedWorkerDirective CreateDirective(InstanceState instance)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ManagedWorkerDirective
        {
            InstanceId = instance.Info.InstanceId,
            Generation = instance.Info.WorkerGeneration,
            DirectiveSequence = ++instance.DirectiveSequence,
            Draining = instance.RequestedState is ManagedWorkerState.Draining or ManagedWorkerState.Stopping,
            Stop = instance.RequestedState == ManagedWorkerState.Stopping,
            AdmissionGrants = [.. instance.Reservations.Values.Where(reservation => !instance.RevokedReservationIds.Contains(reservation.ReservationId)
                && reservation.State is (ManagedPlayerConnectionState.PendingDelivery or ManagedPlayerConnectionState.Installed or ManagedPlayerConnectionState.ResumeHeld)).Select(reservation => CreateDirectiveGrant(instance, reservation, now))],
            RevokedReservationIds = [.. instance.RevokedReservationIds],
            KickReservationIds = instance.RequestedState == ManagedWorkerState.Stopping ? [.. instance.Reservations.Keys] : [.. instance.KickReservationIds],
            IssuedUtc = now,
            FreshUntilUtc = now.AddSeconds(Math.Max(1, _options.AdmissionDirectiveLeaseSeconds)),
        };
    }

    private void ExpireReservations(InstanceState instance, DateTimeOffset now, bool includeInstalledAndResume = false)
    {
        foreach (string id in instance.Reservations.Values.Where(reservation =>
            reservation.State == ManagedPlayerConnectionState.PendingDelivery && reservation.ExpiresUtc <= now
            || includeInstalledAndResume && reservation.State == ManagedPlayerConnectionState.Installed && reservation.ExpiresUtc <= now
            || includeInstalledAndResume && reservation.State == ManagedPlayerConnectionState.ResumeHeld && reservation.ResumeUntilUtc <= now).Select(reservation => reservation.ReservationId).ToArray())
        {
            instance.Reservations.Remove(id);
            instance.Grants.Remove(id);
            instance.RevokedReservationIds.Add(id);
        }
        UpdateOccupancy(instance);
    }

    private static void UpdateOccupancy(InstanceState instance)
    {
        instance.Info.ReservedPlayers = instance.Reservations.Count;
        instance.Info.ConnectedPlayers = instance.Reservations.Values.Count(reservation => reservation.State is ManagedPlayerConnectionState.Connected or ManagedPlayerConnectionState.Synchronized);
        instance.Info.SynchronizedPlayers = instance.Reservations.Values.Count(reservation => reservation.State == ManagedPlayerConnectionState.Synchronized);
        instance.Info.ResumeHeldPlayers = instance.Reservations.Values.Count(reservation => reservation.State == ManagedPlayerConnectionState.ResumeHeld);
        instance.Info.CurrentPlayers = instance.Info.ConnectedPlayers;
    }

    private static ManagedAdmissionReservation CloneReservation(ManagedAdmissionReservation reservation)
        => new()
        {
            ReservationId = reservation.ReservationId,
            InstanceId = reservation.InstanceId,
            ClientId = reservation.ClientId,
            AccountId = reservation.AccountId,
            State = reservation.State,
            ExpiresUtc = reservation.ExpiresUtc,
            ResumeUntilUtc = reservation.ResumeUntilUtc,
        };

    private static ManagedAdmissionGrant CloneGrant(ManagedAdmissionGrant grant)
        => new()
        {
            ReservationId = grant.ReservationId,
            Secret = grant.Secret,
            Purpose = grant.Purpose,
            CredentialEpoch = grant.CredentialEpoch,
            ClientId = grant.ClientId,
            AccountId = grant.AccountId,
            SessionId = grant.SessionId,
            Generation = grant.Generation,
            WorldId = grant.WorldId,
            WorldRevision = grant.WorldRevision,
            ContentHash = grant.ContentHash,
            BuildVersion = grant.BuildVersion,
            ExpiresUtc = grant.ExpiresUtc,
            Signature = grant.Signature is null ? null : new ManagedAdmissionSignature
            {
                KeyId = grant.Signature.KeyId,
                Issuer = grant.Signature.Issuer,
                IssuedUtc = grant.Signature.IssuedUtc,
                Value = grant.Signature.Value,
            },
        };

    private ManagedAdmissionGrant CreateDirectiveGrant(InstanceState instance, ManagedAdmissionReservation reservation, DateTimeOffset now)
    {
        ManagedAdmissionGrant grant = CloneGrant(instance.Grants[reservation.ReservationId]);
        if (reservation.State == ManagedPlayerConnectionState.ResumeHeld && reservation.ResumeUntilUtc is DateTimeOffset resumeUntil)
            grant.ExpiresUtc = resumeUntil;
        return grant;
    }

    private void RotateResumeGrant(InstanceState instance, ManagedAdmissionReservation reservation)
    {
        ManagedAdmissionGrant previous = instance.Grants[reservation.ReservationId];
        instance.Grants[reservation.ReservationId] = new ManagedAdmissionGrant
        {
            ReservationId = previous.ReservationId,
            Secret = CreateOpaqueToken(_options.TokenByteLength),
            Purpose = ManagedAdmissionGrantPurpose.Resume,
            CredentialEpoch = previous.CredentialEpoch + 1,
            ClientId = previous.ClientId,
            AccountId = previous.AccountId,
            SessionId = previous.SessionId,
            Generation = previous.Generation,
            WorldId = previous.WorldId,
            WorldRevision = previous.WorldRevision,
            ContentHash = previous.ContentHash,
            BuildVersion = previous.BuildVersion,
            ExpiresUtc = reservation.ResumeUntilUtc ?? previous.ExpiresUtc,
        };
        instance.InstalledGrantEpochs.Remove(reservation.ReservationId);
    }

    private static bool IsCurrentGrantInstalled(InstanceState instance, string reservationId)
        => instance.Grants.TryGetValue(reservationId, out ManagedAdmissionGrant? grant)
            && instance.InstalledGrantEpochs.TryGetValue(reservationId, out long installedEpoch)
            && installedEpoch == grant.CredentialEpoch;

    private static bool IsEquivalentLaunch(ManagedWorkerLaunch current, ManagedWorkerLaunch replacement)
        => current.ContractVersion == replacement.ContractVersion
            && string.Equals(current.OperationId, replacement.OperationId, StringComparison.Ordinal)
            && current.InstanceId == replacement.InstanceId
            && current.SessionId == replacement.SessionId
            && current.Generation == replacement.Generation
            && string.Equals(current.ManagementUrl, replacement.ManagementUrl, StringComparison.Ordinal)
            && string.Equals(current.ManagementToken, replacement.ManagementToken, StringComparison.Ordinal)
            && string.Equals(current.BindAddress, replacement.BindAddress, StringComparison.Ordinal)
            && current.BindPort == replacement.BindPort
            && string.Equals(current.AdvertisedEndpoint.Host, replacement.AdvertisedEndpoint.Host, StringComparison.Ordinal)
            && current.AdvertisedEndpoint.Port == replacement.AdvertisedEndpoint.Port
            && string.Equals(current.AdvertisedEndpoint.ProtocolVersion, replacement.AdvertisedEndpoint.ProtocolVersion, StringComparison.Ordinal)
            && current.MaxPlayers == replacement.MaxPlayers
            && string.Equals(current.WorldPackage.ManifestHash, replacement.WorldPackage.ManifestHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.WorldEntryPoint, replacement.WorldEntryPoint, StringComparison.Ordinal)
            && string.Equals(current.GameBootstrapId, replacement.GameBootstrapId, StringComparison.Ordinal)
            && string.Equals(current.BuildVersion, replacement.BuildVersion, StringComparison.Ordinal)
            && current.Startup.StartupTimeoutMilliseconds == replacement.Startup.StartupTimeoutMilliseconds
            && current.Startup.ShutdownTimeoutMilliseconds == replacement.Startup.ShutdownTimeoutMilliseconds
            && current.Startup.TickRate == replacement.Startup.TickRate
            && current.Startup.PhysicsSubsteps == replacement.Startup.PhysicsSubsteps
            && current.Startup.FixedDeltaMilliseconds == replacement.Startup.FixedDeltaMilliseconds
            && current.Startup.ManagementPollMilliseconds == replacement.Startup.ManagementPollMilliseconds
            && current.Startup.ManagementLeaseMilliseconds == replacement.Startup.ManagementLeaseMilliseconds
            && current.ResourceLimits.CpuPercentLimit == replacement.ResourceLimits.CpuPercentLimit
            && current.ResourceLimits.MemoryBytesLimit == replacement.ResourceLimits.MemoryBytesLimit
            && current.ResourceLimits.MaxOpenHandles == replacement.ResourceLimits.MaxOpenHandles;

    private static ManagedWorkerLaunch CloneLaunch(ManagedWorkerLaunch launch)
        => new()
        {
            ContractVersion = launch.ContractVersion,
            OperationId = launch.OperationId,
            InstanceId = launch.InstanceId,
            SessionId = launch.SessionId,
            Generation = launch.Generation,
            ManagementUrl = launch.ManagementUrl,
            ManagementToken = launch.ManagementToken,
            BindAddress = launch.BindAddress,
            BindPort = launch.BindPort,
            AdvertisedEndpoint = CloneEndpoint(launch.AdvertisedEndpoint),
            MaxPlayers = launch.MaxPlayers,
            WorldPackage = CloneWorldPackage(launch.WorldPackage),
            PackageRootPath = launch.PackageRootPath,
            WorldEntryPoint = launch.WorldEntryPoint,
            GameBootstrapId = launch.GameBootstrapId,
            BuildVersion = launch.BuildVersion,
            Startup = new ManagedWorkerStartupConfiguration
            {
                StartupTimeoutMilliseconds = launch.Startup.StartupTimeoutMilliseconds,
                ShutdownTimeoutMilliseconds = launch.Startup.ShutdownTimeoutMilliseconds,
                TickRate = launch.Startup.TickRate,
                PhysicsSubsteps = launch.Startup.PhysicsSubsteps,
                FixedDeltaMilliseconds = launch.Startup.FixedDeltaMilliseconds,
                ManagementPollMilliseconds = launch.Startup.ManagementPollMilliseconds,
                ManagementLeaseMilliseconds = launch.Startup.ManagementLeaseMilliseconds,
            },
            ResourceLimits = new ManagedWorkerResourceLimits
            {
                CpuPercentLimit = launch.ResourceLimits.CpuPercentLimit,
                MemoryBytesLimit = launch.ResourceLimits.MemoryBytesLimit,
                MaxOpenHandles = launch.ResourceLimits.MaxOpenHandles,
            },
            Tls = launch.Tls is null ? null : new ManagedWorkerTlsConfiguration
            {
                ListenAddress = launch.Tls.ListenAddress,
                ListenPort = launch.Tls.ListenPort,
                CertificateThumbprint = launch.Tls.CertificateThumbprint,
                UseMachineCertificateStore = launch.Tls.UseMachineCertificateStore,
                MaximumConnections = launch.Tls.MaximumConnections,
                MaximumConnectionsPerAddress = launch.Tls.MaximumConnectionsPerAddress,
            },
            AdmissionIssuer = launch.AdmissionIssuer,
            AdmissionSigningKeys = new Dictionary<string, string>(launch.AdmissionSigningKeys, StringComparer.Ordinal),
        };
}
