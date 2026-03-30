using Extensions;
using XREngine.Core.Files;
using XREngine.Rendering.Info;

namespace XREngine.Components.Scene.Mesh
{
    [Serializable]
    public abstract class RenderableComponent : XRComponent, IRenderable, IPostCookedBinaryDeserialize
    {
        private int _meshLayer = -1;
        private bool? _meshCastsShadows = null;

        /// <summary>
        /// When >= 0, overrides the render layer for all meshes owned by this component.
        /// Set before or after assigning a Model; the setter propagates to existing meshes.
        /// </summary>
        public int MeshLayer
        {
            get => _meshLayer;
            set
            {
                _meshLayer = value;
                if (value >= 0)
                    foreach (var m in Meshes)
                        m.RenderInfo.Layer = value;
            }
        }

        /// <summary>
        /// When non-null, overrides shadow-casting for all meshes owned by this component.
        /// Set before or after assigning a Model; the setter propagates to existing meshes.
        /// </summary>
        public bool? MeshCastsShadows
        {
            get => _meshCastsShadows;
            set
            {
                _meshCastsShadows = value;
                if (value.HasValue)
                    foreach (var m in Meshes)
                        m.RenderInfo.CastsShadows = value.Value;
            }
        }

        public RenderableComponent()
        {
            Meshes.PostAnythingAdded += Meshes_PostAnythingAdded;
            Meshes.PostAnythingRemoved += Meshes_PostAnythingRemoved;
        }

        protected virtual void Meshes_PostAnythingRemoved(RenderableMesh item)
        {
            using var t = Engine.Profiler.Start("RenderableComponent.Meshes_PostAnythingRemoved");

            var ri = item.RenderInfo;
            int i = RenderedObjects.IndexOf(ri);
            if (i < 0)
                return;
            
            RenderedObjects = [.. RenderedObjects.Where((_, x) => x != i)];

            if (IsActive && ri.WorldInstance == World)
                ri.WorldInstance = null;
        }

        protected virtual void Meshes_PostAnythingAdded(RenderableMesh item)
        {
            using var t = Engine.Profiler.Start("RenderableComponent.Meshes_PostAnythingAdded");

            var ri = item.RenderInfo;
            int i = RenderedObjects.IndexOf(ri);
            if (i >= 0)
                return;

            if (_meshLayer >= 0)
                ri.Layer = _meshLayer;
            if (_meshCastsShadows.HasValue)
                ri.CastsShadows = _meshCastsShadows.Value;

            RenderedObjects = [.. RenderedObjects, ri];

            if (IsActive)
                ri.WorldInstance = WorldAs<XREngine.Rendering.XRWorldInstance>();
        }

        public EventList<RenderableMesh> Meshes { get; private set; } = new EventList<RenderableMesh>() { ThreadSafe = true };
        public RenderInfo[] RenderedObjects { get; private set; } = [];

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            SyncRenderedObjectsWithWorld();
        }

        protected override void OnComponentDeactivated()
        {
            // Unregister all render infos from the world so they stop drawing.
            foreach (var ri in RenderedObjects)
                ri.WorldInstance = null;
            base.OnComponentDeactivated();
        }

        /// <summary>
        /// Ensures every <see cref="RenderedObjects"/> entry is registered (or unregistered)
        /// with the current world instance to match the component's active state.
        /// </summary>
        private void SyncRenderedObjectsWithWorld()
        {
            var world = WorldAs<XREngine.Rendering.XRWorldInstance>();
            foreach (var ri in RenderedObjects)
            {
                if (IsActive && world is not null)
                {
                    if (!ReferenceEquals(ri.WorldInstance, world))
                        ri.WorldInstance = world;
                }
                else
                {
                    ri.WorldInstance = null;
                }
            }
        }

        void IPostCookedBinaryDeserialize.OnPostCookedBinaryDeserialize()
        {
            // Cooked-binary restore can replace the Meshes EventList instance (setter/deserializer),
            // which drops the constructor-installed event subscriptions.
            Meshes.PostAnythingAdded -= Meshes_PostAnythingAdded;
            Meshes.PostAnythingRemoved -= Meshes_PostAnythingRemoved;
            Meshes.PostAnythingAdded += Meshes_PostAnythingAdded;
            Meshes.PostAnythingRemoved += Meshes_PostAnythingRemoved;

            // Rebuild RenderedObjects from current meshes.
            RenderedObjects = Meshes.Select(static m => (RenderInfo)m.RenderInfo).ToArray();

            // Ensure render infos are registered with the current world instance when active.
            // Without this, the VisualScene can end up tracking zero renderables after snapshot restore.
            SyncRenderedObjectsWithWorld();

            //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_DUMP") == "1")
            {
                Debug.RenderingEvery(
                    $"RenderableComponent.PostCooked.{GetHashCode()}",
                    TimeSpan.FromDays(1),
                    "[RenderDiag] RenderableComponent post-cooked. Node={0} Active={1} Meshes={2} RenderedObjects={3} WorldNull={4}",
                    SceneNode?.Name ?? "<null>",
                    IsActive,
                    Meshes.Count,
                    RenderedObjects.Length,
                    WorldAs<XREngine.Rendering.XRWorldInstance>() is null);
            }
        }
    }
}
