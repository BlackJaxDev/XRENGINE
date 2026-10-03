using OpenVR.NET.Devices;
using System.Threading;
using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Input;
using XREngine.Scene;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// Handles the connection and management of VR trackers in the scene.
    /// </summary>
    public class VRTrackerCollectionComponent : XRComponent
    {
        private DateTime _nextTrackerReverifyUtc = DateTime.MinValue;
        private int _reverifyRequested = 1;
        private readonly List<SceneNode> _ownedTrackerNodes = [];
        private readonly Dictionary<string, VRTrackerTransform> _openVrTrackers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _openVrIndices = new(StringComparer.Ordinal);

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RuntimeVrStateServices.DeviceDetected += OnDeviceDetected;
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, ReverifyOnSceneThread);
            Interlocked.Exchange(ref _reverifyRequested, 1);
        }

        protected override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, ReverifyOnSceneThread);
            RuntimeVrStateServices.DeviceDetected -= OnDeviceDetected;
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            for (int i = 0; i < _ownedTrackerNodes.Count; i++)
                _ownedTrackerNodes[i].Destroy(true);
            _ownedTrackerNodes.Clear();
            Trackers.Clear();
            OpenXrTrackers.Clear();
            _openVrTrackers.Clear();
            _openVrIndices.Clear();
            base.OnDestroying();
        }

        public Dictionary<uint, (VrDevice?, VRTrackerTransform)> Trackers { get; } = [];
        public Dictionary<string, VRTrackerTransform> OpenXrTrackers { get; } = new(StringComparer.Ordinal);

        private void OnDeviceDetected(VrDevice device)
            => RequestReverify();

        private void RequestReverify()
            => Interlocked.Exchange(ref _reverifyRequested, 1);

        private void ReverifyOnSceneThread()
        {
            DateTime now = DateTime.UtcNow;
            if (Volatile.Read(ref _reverifyRequested) == 0 && now < _nextTrackerReverifyUtc)
                return;
            Interlocked.Exchange(ref _reverifyRequested, 0);
            _nextTrackerReverifyUtc = now + TimeSpan.FromSeconds(1);
            ReverifyTrackedDevices();
        }

        private void ReverifyTrackedDevices()
        {
            if (RuntimeVrStateServices.IsOpenXRActive)
            {
                ReverifyOpenXrTrackers();
                return;
            }

            foreach (VrDevice device in RuntimeVrStateServices.TrackedDevices)
            {
                if (!RuntimeVrStateServices.IsGenericTracker(device.DeviceIndex))
                    continue;

                string? identity;
                try { identity = device.GetString(Valve.VR.ETrackedDeviceProperty.Prop_SerialNumber_String); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(identity))
                    continue;

                if (!_openVrTrackers.TryGetValue(identity, out VRTrackerTransform? transform))
                {
                    transform = AddRealTracker(device);
                    _openVrTrackers.Add(identity, transform);
                }
                else
                    transform.Tracker = device;

                if (_openVrIndices.TryGetValue(identity, out uint oldIndex) && oldIndex != device.DeviceIndex)
                {
                    if (Trackers.TryGetValue(oldIndex, out var oldEntry) && ReferenceEquals(oldEntry.Item2, transform))
                        Trackers.Remove(oldIndex);
                }
                _openVrIndices[identity] = device.DeviceIndex;
                Trackers[device.DeviceIndex] = (device, transform);
            }
        }

        private void ReverifyOpenXrTrackers()
        {
            RuntimeVrTrackerInfo[] trackers = RuntimeVrStateServices.GetKnownOpenXrTrackers();
            for (int i = 0; i < trackers.Length; i++)
            {
                RuntimeVrTrackerInfo tracker = trackers[i];
                string? identity = tracker.PersistentPath;
                if (string.IsNullOrWhiteSpace(identity))
                    continue;

                if (OpenXrTrackers.TryGetValue(identity, out VRTrackerTransform? existing))
                    existing.ApplyOpenXrTrackerInfo(tracker);
                else
                    AddOpenXrTracker(tracker);
            }
        }

        /// <summary>
        /// Adds a real VR tracker discovered from the VR API to the collection.
        /// </summary>
        /// <param name="device"></param>
        private VRTrackerTransform AddRealTracker(VrDevice device)
        {
            SceneNode trackerNode = SceneNode.NewChild();
            _ownedTrackerNodes.Add(trackerNode);
            trackerNode.Name = $"Tracker {device.DeviceIndex}";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();
            tfm.Tracker = device;

            VRTrackerModelComponent modelComponent = trackerNode.AddComponent<VRTrackerModelComponent>()!;
            modelComponent.DeviceIndex = device.DeviceIndex;

            return tfm;
        }

        private void AddOpenXrTracker(RuntimeVrTrackerInfo tracker)
        {
            SceneNode trackerNode = SceneNode.NewChild();
            _ownedTrackerNodes.Add(trackerNode);
            trackerNode.Name = $"OpenXR Tracker {GetOpenXrTrackerDisplayName(tracker)}";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();
            tfm.ApplyOpenXrTrackerInfo(tracker);

            VRTrackerModelComponent modelComponent = trackerNode.AddComponent<VRTrackerModelComponent>()!;
            modelComponent.OpenXrTrackerUserPath = tracker.PersistentPath;

            uint syntheticDeviceIndex = uint.MaxValue - (uint)Trackers.Count;
            while (Trackers.ContainsKey(syntheticDeviceIndex))
                syntheticDeviceIndex--;

            Trackers.Add(syntheticDeviceIndex, (null, tfm));
            OpenXrTrackers.Add(tracker.PersistentPath!, tfm);
        }

        /// <summary>
        /// Adds a manual tracker to the collection that does not correspond to any real VR device.
        /// This tracker can be used for custom tracking or testing.
        /// </summary>
        public VRTrackerTransform AddManualTracker(string? name = null)
        {
            SceneNode trackerNode = SceneNode.NewChild();
            _ownedTrackerNodes.Add(trackerNode);
            trackerNode.Name = name ?? "Manual Tracker";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();
            tfm.SyntheticIdentity = "manual:" + Guid.NewGuid().ToString("N");

            uint manualTrackerDeviceIndex = uint.MaxValue;
            while (Trackers.ContainsKey(manualTrackerDeviceIndex))
                manualTrackerDeviceIndex--;
            Trackers.Add(manualTrackerDeviceIndex, (null, tfm));
            return tfm;
        }

        public VRTrackerTransform? GetTrackerByNodeName(string name, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
        {
            foreach (var tracker in Trackers.Values)
            {
                VRTrackerTransform nodeTransform = tracker.Item2;
                SceneNode? node = nodeTransform.SceneNode;
                if (node is not null && string.Equals(node.Name, name, comp))
                    return nodeTransform;
            }
            return null;
        }

        private static string GetOpenXrTrackerDisplayName(RuntimeVrTrackerInfo tracker)
        {
            string userPath = tracker.PersistentPath ?? tracker.UserPath;
            int slash = userPath.LastIndexOf('/');
            return slash >= 0 && slash + 1 < userPath.Length
                ? userPath[(slash + 1)..]
                : userPath;
        }
    }
}
