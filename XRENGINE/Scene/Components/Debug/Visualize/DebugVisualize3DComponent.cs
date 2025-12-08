using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Components
{
    public class DebugVisualize3DComponent : XRComponent, IRenderable
    {
        private readonly RenderInfo3D _renderInfo;
        private readonly RenderCommandMethod3D _rc;

        public RenderInfo3D RenderInfo => _renderInfo;
        public RenderCommandMethod3D RenderCommand => _rc;

        public delegate void DelDebugRenderCallback(DebugVisualize3DComponent comp);
        public event DelDebugRenderCallback? DebugRender;

        public delegate void DelPreRenderCallback(DebugVisualize3DComponent comp, RenderInfo info, RenderCommand command, XRCamera? camera);
        public event DelPreRenderCallback? PreRenderCallback;

        public delegate void DelSwapBuffersCallback(DebugVisualize3DComponent comp, RenderInfo info, RenderCommand command);
        public event DelSwapBuffersCallback? SwapBuffersCallback;

        protected virtual void Render()
            => DebugRender?.Invoke(this);

        public DebugVisualize3DComponent()
        {
            RenderedObjects = [_renderInfo = RenderInfo3D.New(this, _rc = new((int)EDefaultRenderPass.OnTopForward, Render))];
            _renderInfo.Layer = DefaultLayers.GizmosIndex;
            _renderInfo.CollectedForRenderCallback += RenderInfo_PreRenderCallback;
            _renderInfo.SwapBuffersCallback += RenderInfo_SwapBuffersCallback;
        }
        ~DebugVisualize3DComponent()
        {
            _renderInfo.CollectedForRenderCallback -= RenderInfo_PreRenderCallback;
            _renderInfo.SwapBuffersCallback -= RenderInfo_SwapBuffersCallback;
        }

        protected virtual void RenderInfo_SwapBuffersCallback(RenderInfo info, RenderCommand command)
            => SwapBuffersCallback?.Invoke(this, info, command);

        protected virtual void RenderInfo_PreRenderCallback(RenderInfo info, RenderCommand command, XRCamera? camera)
            => PreRenderCallback?.Invoke(this, info, command, camera);

        public RenderInfo[] RenderedObjects { get; }
    }
}
