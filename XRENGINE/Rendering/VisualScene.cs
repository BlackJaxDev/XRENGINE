using System.Collections;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Scene
{
    public delegate void DelRender(RenderCommandCollection renderingPasses, XRCamera camera, XRViewport viewport, XRFrameBuffer? target);
    public abstract class VisualScene : XRBase, IEnumerable<RenderInfo>
    {
        public abstract IRenderTree GenericRenderTree { get; }
        public GPUScene GPUCommands { get; } = new();
        public XRMeshRenderer? IndirectDrawBuffer { get; set; } = null;
        
        /// <summary>
        /// Collects render commands for all renderables in the scene that intersect with the given volume.
        /// If the volume is null, all renderables are collected.
        /// Typically, the collectionVolume is the camera's frustum.
        /// </summary>
        public abstract void CollectRenderedItems(
            RenderCommandCollection meshRenderCommands,
            XRCamera? activeCamera,
            bool cullWithFrustum,
            Func<XRCamera>? cullingCameraOverride,
            IVolume? collectionVolumeOverride,
            bool collectMirrors);

        public virtual void DebugRender(XRCamera? camera, bool onlyContainingItems = false)
        {

        }

        public virtual void GlobalCollectVisible()
        {

        }

        /// <summary>
        /// Occurs before rendering any viewports.
        /// </summary>
        public virtual void GlobalPreRender()
        {

        }

        /// <summary>
        /// Occurs after rendering all viewports.
        /// </summary>
        public virtual void GlobalPostRender()
        {

        }

        /// <summary>
        /// Swaps the update/render buffers for the scene.
        /// </summary>
        public virtual void GlobalSwapBuffers()
        {
            using var sample = Engine.Profiler.Start("VisualScene.GlobalSwapBuffers");
            GenericRenderTree.Swap();
        }

        public void Initialize()
        {
            GPUCommands.Initialize();
        }

        public void Destroy()
        {
            GPUCommands.Destroy();
        }

        public abstract IEnumerator<RenderInfo> GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}