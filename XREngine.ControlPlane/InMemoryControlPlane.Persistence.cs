namespace XREngine.ControlPlane;

public sealed partial class InMemoryControlPlane
{
    public DurableControlPlaneSnapshot ExportDurableSnapshot()
    {
        lock (_sync)
            return new DurableControlPlaneSnapshot { Instances = _instances.Values.Select(state => new DurableControlPlaneInstance
            {
                Info = CloneInstanceInfo(state.Info, includeToken: true), IsManaged = state.IsManaged, CreateFingerprint = state.CreateFingerprint,
                Reservations = state.Reservations.ToDictionary(pair => pair.Key, pair => CloneReservation(pair.Value), StringComparer.OrdinalIgnoreCase),
                Grants = state.Grants.ToDictionary(pair => pair.Key, pair => CloneGrant(pair.Value), StringComparer.OrdinalIgnoreCase),
                ReservationIdempotency = new(state.ReservationIdempotency, StringComparer.OrdinalIgnoreCase), ReservationFingerprints = new(state.ReservationFingerprints, StringComparer.OrdinalIgnoreCase),
                RevokedReservationIds = new(state.RevokedReservationIds, StringComparer.OrdinalIgnoreCase), KickReservationIds = new(state.KickReservationIds, StringComparer.OrdinalIgnoreCase),
                InstalledGrantEpochs = new(state.InstalledGrantEpochs, StringComparer.OrdinalIgnoreCase), Launch = state.Launch is null ? null : CloneLaunch(state.Launch),
                RequestedState = state.RequestedState, ObservedState = state.ObservedState, LastWorkerReportSequence = state.LastWorkerReportSequence,
                LastWorkerReportedUtc = state.LastWorkerReportedUtc, DirectiveSequence = state.DirectiveSequence, ProcessExitConfirmed = state.ProcessExitConfirmed,
            }).ToList() };
    }

    public void ImportDurableSnapshot(DurableControlPlaneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version != 1 || snapshot.Instances.Count > 10_000)
            throw new InvalidOperationException("Checkpoint version or instance count is invalid.");
        lock (_sync)
        {
            if (_instances.Count != 0) throw new InvalidOperationException("Import requires an empty registry.");
            foreach (DurableControlPlaneInstance persisted in snapshot.Instances)
            {
                if (string.IsNullOrWhiteSpace(persisted.Info.InstanceId) || _instances.ContainsKey(persisted.Info.InstanceId))
                    throw new InvalidOperationException("Checkpoint contains an invalid or duplicate instance.");
                if (persisted.Reservations.Count > persisted.Info.MaxPlayers * 2 || persisted.Grants.Count > persisted.Info.MaxPlayers * 2)
                    throw new InvalidOperationException("Checkpoint contains an invalid admission allocation.");
                var state = new InstanceState { IsManaged = persisted.IsManaged, CreateFingerprint = persisted.CreateFingerprint, Info = CloneInstanceInfo(persisted.Info, includeToken: true) };
                foreach (var pair in persisted.Reservations) state.Reservations.Add(pair.Key, CloneReservation(pair.Value));
                foreach (var pair in persisted.Grants) state.Grants.Add(pair.Key, CloneGrant(pair.Value));
                foreach (var pair in persisted.ReservationIdempotency) state.ReservationIdempotency.Add(pair.Key, pair.Value);
                foreach (var pair in persisted.ReservationFingerprints) state.ReservationFingerprints.Add(pair.Key, pair.Value);
                state.RevokedReservationIds.UnionWith(persisted.RevokedReservationIds); state.KickReservationIds.UnionWith(persisted.KickReservationIds);
                foreach (var pair in persisted.InstalledGrantEpochs) state.InstalledGrantEpochs.Add(pair.Key, pair.Value);
                state.Launch = persisted.Launch is null ? null : CloneLaunch(persisted.Launch); state.RequestedState = persisted.RequestedState; state.ObservedState = persisted.ObservedState;
                state.LastWorkerReportSequence = persisted.LastWorkerReportSequence; state.LastWorkerReportedUtc = persisted.LastWorkerReportedUtc;
                state.DirectiveSequence = persisted.DirectiveSequence; state.ProcessExitConfirmed = persisted.ProcessExitConfirmed;
                _instances.Add(state.Info.InstanceId, state);
            }
        }
    }
}
