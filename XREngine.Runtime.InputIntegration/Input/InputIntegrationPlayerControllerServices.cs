using System.Diagnostics.CodeAnalysis;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

/// <summary>
/// Owns the process-local controller roster for an input-enabled runtime profile.
/// The registry is deliberately independent of windows and worlds so that host
/// composition can install it for desktop/VR clients and omit it for headless servers.
/// </summary>
public sealed class InputIntegrationPlayerControllerServices : IRuntimePlayerControllerServices, IDisposable
{
    private readonly object _sync = new();
    private readonly IPawnController?[] _localPlayers = new IPawnController[4];
    private readonly List<IPawnController> _remotePlayers = [];
    private bool _disposed;

    public event Action<IPawnController>? LocalPlayerAdded;
    public event Action<IPawnController>? LocalPlayerRemoved;

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex index) => _localPlayers[(int)index];

    public IPawnController GetOrCreateLocalPlayer(
        ELocalPlayerIndex index,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type? controllerTypeOverride = null)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            Type desiredType = controllerTypeOverride ?? typeof(LocalPlayerController);
            IPawnController? existing = _localPlayers[(int)index];
            if (existing is not null && desiredType.IsInstanceOfType(existing))
                return existing;

            XRComponent? controlledPawn = existing?.ControlledPawnComponent;
            XRWindow[] boundWindows = existing is null
                ? []
                : InputIntegrationViewportBindingRebinder.SnapshotWindowsBoundTo(existing);
            IPawnController player = CreateLocalController(desiredType, index);
            player.ControlledPawnComponent = controlledPawn;
            if (existing is not null)
            {
                InputIntegrationViewportBindingRebinder.Rebind(boundWindows, existing, player);
                _localPlayers[(int)index] = player;
                LocalPlayerRemoved?.Invoke(existing);
                if (existing is XRObjectBase existingObject)
                    existingObject.Destroy();
            }
            else
            {
                _localPlayers[(int)index] = player;
            }

            player.OnPawnCameraChanged();
            LocalPlayerAdded?.Invoke(player);
            return player;
        }
    }

    public bool RemoveLocalPlayer(ELocalPlayerIndex index)
    {
        lock (_sync)
        {
            IPawnController? player = _localPlayers[(int)index];
            if (player is null)
                return false;

            _localPlayers[(int)index] = null;
            LocalPlayerRemoved?.Invoke(player);
            if (player is XRObjectBase objectBase)
                objectBase.Destroy();
            return true;
        }
    }

    public IPawnController MainPlayer => GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
    public int LocalPlayerCount => _localPlayers.Count(static player => player is not null);
    public IReadOnlyList<IPawnController?> AllLocalPlayers => _localPlayers;

    public IPawnController CreateRemotePlayer(int serverPlayerIndex)
    {
        ThrowIfDisposed();
        return new RemotePlayerController(serverPlayerIndex);
    }

    public IReadOnlyList<IPawnController> RemotePlayers => _remotePlayers;

    public void AddRemotePlayer(IPawnController player)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(player);
        if (!_remotePlayers.Contains(player))
            _remotePlayers.Add(player);
    }

    public bool RemoveRemotePlayer(IPawnController player)
    {
        ThrowIfDisposed();
        return _remotePlayers.Remove(player);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;

            for (int index = 0; index < _localPlayers.Length; index++)
                RemoveLocalPlayer((ELocalPlayerIndex)index);

            foreach (XRObjectBase player in _remotePlayers.OfType<XRObjectBase>())
                player.Destroy();
            _remotePlayers.Clear();
            LocalPlayerAdded = null;
            LocalPlayerRemoved = null;
        }
    }

    private static IPawnController CreateLocalController(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type controllerType,
        ELocalPlayerIndex index)
    {
        if (!typeof(IPawnController).IsAssignableFrom(controllerType))
            throw new ArgumentException($"Controller type {controllerType.FullName} must implement {nameof(IPawnController)}.", nameof(controllerType));

        if (controllerType == typeof(LocalPlayerController))
            return new LocalPlayerController(index);

        if (RuntimePlayerControllerServices.TryCreateLocalController(controllerType, index, out IPawnController? registered))
            return registered!;

        if (XRRuntimeEnvironment.IsAotRuntimeBuild)
            throw new InvalidOperationException($"No registered local-player factory exists for {controllerType.FullName}.");

        return (controllerType.GetConstructor([typeof(ELocalPlayerIndex)])?.Invoke([index])
            ?? Activator.CreateInstance(controllerType)) as IPawnController
            ?? throw new InvalidOperationException($"Failed to instantiate {controllerType.FullName}.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InputIntegrationPlayerControllerServices));
    }
}
