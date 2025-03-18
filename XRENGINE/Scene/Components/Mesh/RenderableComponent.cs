using Extensions;
using XREngine.Rendering.Info;

namespace XREngine.Components.Scene.Mesh
{
    [Serializable]
    public abstract class RenderableComponent : XRComponent, IRenderable
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

        public EventList<RenderableMesh> Meshes { get; private set; } = [];
        public RenderInfo[] RenderedObjects { get; private set; } = [];
    }
}
