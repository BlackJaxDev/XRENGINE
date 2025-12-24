using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;

namespace XREngine.Components.Scene.Volumes
{
    public delegate void DelOnOverlapEnter(XRComponent component);
    public delegate void DelOnOverlapLeave(XRComponent component);

    /// <summary>
    /// A volume that triggers events when other components enter or leave it.
    /// </summary>
    [Description("A volume that triggers events when other components enter or leave it.")]
    public class TriggerVolumeComponent : XRComponent
    {
        public event DelOnOverlapEnter? Entered;
        public event DelOnOverlapLeave? Left;

        private Vector3 _halfExtents = new(0.5f);
        public Vector3 HalfExtents
        {
            get => _halfExtents;
            set => SetField(ref _halfExtents, value);
        }

        private LayerMask _overlapMask = LayerMask.Everything;
        public LayerMask OverlapMask
        {
            get => _overlapMask;
            set => SetField(ref _overlapMask, value);
        }

        public bool TrackContacts { get; set; } = true;

        protected virtual void OnEntered(XRComponent component)
            => Entered?.Invoke(component);
        protected virtual void OnLeft(XRComponent component)
            => Left?.Invoke(component);

        public HashSet<XRComponent> OverlappingComponents { get; } = [];

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, Tick);
        }
        protected internal override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, Tick);
            base.OnComponentDeactivated();
        }

        private readonly SortedDictionary<float, List<(XRComponent? item, object? data)>> _overlapResults = [];
        private void Tick()
        {
            if (!TrackContacts)
                return;

            var scene = World?.PhysicsScene;
            if (scene is null)
                return;

            _overlapResults.Clear();
            var geometry = new IPhysicsGeometry.Box(HalfExtents);
            var pose = GetWorldPose();
            scene.OverlapMultiple(geometry, pose, OverlapMask, filter: null, _overlapResults);

            HashSet<XRComponent> newOverlaps = [];
            foreach (var kvp in _overlapResults)
            {
                var list = kvp.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i].item;
                    if (item is null || item == this)
                        continue;
                    newOverlaps.Add(item);
                }
            }

            if (newOverlaps.Count == 0 && OverlappingComponents.Count == 0)
                return;

            // Leave events
            if (OverlappingComponents.Count > 0)
            {
                List<XRComponent> toRemove = [];
                foreach (var existing in OverlappingComponents)
                {
                    if (!newOverlaps.Contains(existing))
                        toRemove.Add(existing);
                }

                for (int i = 0; i < toRemove.Count; i++)
                {
                    var comp = toRemove[i];
                    OverlappingComponents.Remove(comp);
                    OnLeft(comp);
                }
            }

            // Enter events
            foreach (var comp in newOverlaps)
            {
                if (OverlappingComponents.Add(comp))
                    OnEntered(comp);
            }
        }

        private (Vector3 position, Quaternion rotation) GetWorldPose()
        {
            var matrix = Transform.WorldMatrix;
            Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 translation);
            return (translation, rotation);
        }
    }
}
