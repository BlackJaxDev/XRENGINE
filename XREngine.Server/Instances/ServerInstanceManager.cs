using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Rendering;

namespace XREngine.Server.Instances
{
    internal sealed class ServerInstanceManager
    {
        private readonly WorldDownloadService _worldDownloader;
        private readonly ConcurrentDictionary<Guid, ServerInstance> _instances = new();

        public event Action? InstancesChanged;

        public ServerInstanceManager(WorldDownloadService worldDownloader)
        {
            _worldDownloader = worldDownloader;
        }

        public IReadOnlyCollection<ServerInstance> Instances => _instances.Values.ToArray();

        public bool TryGetInstance(Guid id, out ServerInstance? instance)
            => _instances.TryGetValue(id, out instance);

        public ServerInstance GetOrCreateInstance(Guid? instanceId, WorldLocator locator, bool enableDevRendering, CancellationToken cancellationToken = default)
        {
            var id = instanceId ?? (locator.WorldId != Guid.Empty ? locator.WorldId : Guid.NewGuid());

            if (_instances.TryGetValue(id, out var existing))
                return existing;

            XRWorld world = _worldDownloader.FetchWorldAsync(locator, cancellationToken).GetAwaiter().GetResult();
            XRWorldInstance worldInstance = XRWorldInstance.GetOrInitWorld(world);

            // Disable rendering by default unless explicitly requested for development.
            if (!enableDevRendering)
                DisableRenderingForWorld(worldInstance);

            var created = new ServerInstance(id, locator, worldInstance, enableDevRendering);
            if (_instances.TryAdd(id, created))
                InstancesChanged?.Invoke();

            return created;
        }

        public void TouchInstance(ServerInstance instance)
        {
            instance.LastActivityUtc = DateTime.UtcNow;
        }

        private static void DisableRenderingForWorld(XRWorldInstance worldInstance)
        {
            if (worldInstance?.TargetWorld is null)
                return;

            foreach (var scene in worldInstance.TargetWorld.Scenes)
            {
                if (scene.VisualScene?.Renderer is null)
                    continue;
                scene.VisualScene.Renderer.Enabled = false;
            }
        }
    }

    internal sealed class ServerInstance
    {
        public ServerInstance(Guid id, WorldLocator locator, XRWorldInstance worldInstance, bool devRendering)
        {
            InstanceId = id;
            Locator = locator;
            WorldInstance = worldInstance;
            DevRenderingEnabled = devRendering;
        }

        public Guid InstanceId { get; }
        public WorldLocator Locator { get; }
        public XRWorldInstance WorldInstance { get; }
        public bool DevRenderingEnabled { get; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

        private readonly Dictionary<int, ServerPlayerBinding> _players = new();

        public IReadOnlyDictionary<int, ServerPlayerBinding> Players => _players;

        public void TrackPlayer(int serverPlayerIndex, ServerPlayerBinding binding)
        {
            _players[serverPlayerIndex] = binding;
            LastActivityUtc = DateTime.UtcNow;
        }

        public void RemovePlayer(int serverPlayerIndex)
        {
            if (_players.Remove(serverPlayerIndex))
                LastActivityUtc = DateTime.UtcNow;
        }
    }

    internal sealed record ServerPlayerBinding(string ClientId, int ServerPlayerIndex, PawnComponent? Pawn, Guid TransformId);
}
