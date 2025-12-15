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
            Engine.EnqueueSwapTask(() => MeshesRemoved_Task(item));
        }

        protected virtual void Meshes_PostAnythingAdded(RenderableMesh item)
        {
            Engine.EnqueueSwapTask(() => MeshesAdded_Task(item));
        }

        private void MeshesRemoved_Task(RenderableMesh item)
        {
            using var t = Engine.Profiler.Start("RenderableComponent.MeshesRemoved_Task");
            
            var ri = item.RenderInfo;
            int i = RenderedObjects.IndexOf(ri);
            if (i < 0)
                return;
            
            RenderedObjects = [.. RenderedObjects.Where((_, x) => x != i)];

            if (IsActive && ri.WorldInstance == World)
                ri.WorldInstance = null;
        }

        private void MeshesAdded_Task(RenderableMesh item)
        {
            using var t = Engine.Profiler.Start("RenderableComponent.MeshesAdded_Task");

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
    }
}
