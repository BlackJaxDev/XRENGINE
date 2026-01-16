using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Components
{
    /// <summary>
    /// Renders a debug visualization of the scene's octree.
    /// Not intended for production use.
    /// </summary>
    public class DebugVisualizeOctreeComponent : DebugVisualize3DComponent
    {
        //private readonly Dictionary<XRCamera, CameraNodes> _cameraNodes = [];
        //private class CameraNodes
        //{
        //    public List<(OctreeNodeBase node, bool intersects)> _octreeNodesUpdating = [];
        //    public List<(OctreeNodeBase node, bool intersects)> _octreeNodesRendering = [];

        //    public void SwapBuffers()
        //    {
        //        (_octreeNodesUpdating, _octreeNodesRendering) = (_octreeNodesRendering, _octreeNodesUpdating);
        //        _octreeNodesUpdating.Clear();
        //    }
        //}

        protected override void RenderInfo_SwapBuffersCallback(RenderInfo info, RenderCommand command)
        {
            //if (Engine.Rendering.State.IsShadowPass)
            //    return;

            //base.RenderInfo_SwapBuffersCallback(info, command);

            //foreach (CameraNodes cameraNodes in _cameraNodes.Values)
            //    cameraNodes.SwapBuffers();
        }

        protected override void RenderInfo_PreRenderCallback(RenderInfo info, RenderCommand command, XRCamera? camera)
        {
            //if (Engine.Rendering.State.IsShadowPass)
            //    return;

            //if (camera is null)
            //    return;

            //if (!_cameraNodes.TryGetValue(camera, out CameraNodes? cameraNodes))
            //    _cameraNodes[camera] = cameraNodes = new CameraNodes();

            //base.RenderInfo_PreRenderCallback(info, command, camera);

            //void AddNodes((OctreeNodeBase node, bool intersects) d)
            //    => cameraNodes._octreeNodesUpdating.Add(d);

            //World?.VisualScene?.RenderTree?.CollectVisibleNodes(camera?.WorldFrustum(), false, AddNodes);
        }

        protected override void Render()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            //base.Render();

            var camera = Engine.Rendering.State.RenderingCamera;
            //if (camera is null || !_cameraNodes.TryGetValue(camera, out CameraNodes? cameraNodes))
            //    return;

            List<(OctreeNodeBase node, bool intersects)> list = [];
            void AddNodes((OctreeNodeBase node, bool intersects) d)
                => list.Add(d);

            World?.VisualScene?.RenderTree?.CollectVisibleNodes(camera?.WorldFrustum(), false, AddNodes);

            foreach ((OctreeNodeBase node, bool intersects) in list)
                Engine.Rendering.Debug.RenderAABB(
                    node.Bounds.HalfExtents,
                    node.Center,
                    false,
                    intersects
                        ? Engine.EditorPreferences.Theme.OctreeIntersectedBoundsColor
                        : Engine.EditorPreferences.Theme.OctreeContainedBoundsColor);
        }
    }
}
