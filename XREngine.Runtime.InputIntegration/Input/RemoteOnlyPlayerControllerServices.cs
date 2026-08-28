using XREngine.Input;

namespace XREngine.Runtime.InputIntegration;

/// <summary>Controller roster for a server profile: remote peers only, with no local device creation.</summary>
public sealed class RemoteOnlyPlayerControllerServices : IRuntimePlayerControllerServices, IDisposable
{
    private static readonly IPawnController?[] EmptyLocalPlayers = new IPawnController?[4];
    private readonly List<IPawnController> _remotePlayers = [];
    private bool _disposed;

    public event Action<IPawnController>? LocalPlayerAdded { add { } remove { } }
    public event Action<IPawnController>? LocalPlayerRemoved { add { } remove { } }
    public IPawnController? GetLocalPlayer(ELocalPlayerIndex index) => null;
    public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex index, Type? controllerTypeOverride = null)
        => throw new InvalidOperationException("The active application profile forbids local player controllers.");
    public bool RemoveLocalPlayer(ELocalPlayerIndex index) => false;
    public IPawnController MainPlayer => throw new InvalidOperationException("The active application profile has no local player.");
    public int LocalPlayerCount => 0;
    public IReadOnlyList<IPawnController?> AllLocalPlayers => EmptyLocalPlayers;

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
        if (_disposed)
            return;
        foreach (IPawnController player in _remotePlayers)
            if (player is IDisposable disposable)
                disposable.Dispose();
        _remotePlayers.Clear();
        _disposed = true;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
