using Extensions;
using XREngine.Core.Files;
using XREngine.Rendering.Info;

namespace XREngine.Components.Scene.Mesh
{
    [Serializable]
    public abstract class RenderableComponent : XRComponent, IRenderable, IPostCookedBinaryDeserialize
    {
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

            RenderedObjects = [.. RenderedObjects, ri];

            if (IsActive)
                ri.WorldInstance = World;
        }

        public EventList<RenderableMesh> Meshes { get; private set; } = new EventList<RenderableMesh>() { ThreadSafe = true };
        public RenderInfo[] RenderedObjects { get; private set; } = [];

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
            var world = World;
            foreach (var mesh in Meshes)
            {
                var ri = mesh.RenderInfo;
                if (IsActive && world is not null)
                {
                    if (!ReferenceEquals(ri.WorldInstance, world))
                        ri.WorldInstance = world;
                }
                else
                {
                    if (ReferenceEquals(ri.WorldInstance, world))
                        ri.WorldInstance = null;
                }
            }

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
                    world is null);
            }
        }
    }
}
