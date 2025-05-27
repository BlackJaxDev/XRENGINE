using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering.Commands;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Info
{
    public delegate float DelGetSortOrder(bool shadowPass);
    public delegate void DelCullingVolumeChanged(IVolume oldVolume, IVolume newVolume);
    /// <summary>
    /// Render info defines how a renderable object should be rendered and contains state information for the object.
    /// The culling volume is used to determine if the object is visible to the camera.
    /// If the culling volume is null, the object is always rendered.
    /// When this render info is visible, all render commands are added to the rendering passes.
    /// </summary>
    public abstract class RenderInfo : XRBase, ITreeItem
    {
        public abstract ITreeNode? TreeNode { get; }

        public IRenderable? Owner { get; set; }

        public override string ToString()
            => $"{Owner?.ToString() ?? "Unknown"}";

        public delegate void DelPreRenderCallback(RenderInfo info, RenderCommand command, XRCamera? camera);
        /// <summary>
        /// This callback is called when the engine is collecting render commands for the render pass, and this render info is added.
        /// </summary>
        public event DelPreRenderCallback? CollectedForRenderCallback;

        public delegate void DelSwapBuffersCallback(RenderInfo info, RenderCommand command);
        /// <summary>
        /// This callback is called when the engine is swapping buffers - both the collect and render threads are currently in sync and waiting.
        /// </summary>
        public event DelSwapBuffersCallback? SwapBuffersCallback;

        public delegate bool DelAddRenderCommandsCallback(RenderInfo info, RenderCommandCollection passes, XRCamera? camera);
        /// <summary>
        /// This callback is called before render commands are added to the render pass.
        /// Return false to skip adding render commands.
        /// </summary>
        [YamlIgnore]
        public DelAddRenderCommandsCallback? PreCollectCommandsCallback;

        protected RenderInfo(IRenderable? owner, params RenderCommand[] renderCommands)
        {
            Owner = owner;
            RenderCommands.PostAnythingAdded += Added;
            RenderCommands.PostAnythingRemoved += Removed;
            RenderCommands.AddRange(renderCommands);
        }

        private void Removed(RenderCommand item)
        {
            item.OnCollectedForRender -= CollectedForRender;
            item.OnSwapBuffers -= SwapBuffers;
        }

        private void Added(RenderCommand item)
        {
            item.OnCollectedForRender += CollectedForRender;
            item.OnSwapBuffers += SwapBuffers;
        }

        private void SwapBuffers(RenderCommand command)
            => SwapBuffersCallback?.Invoke(this, command);

        private void CollectedForRender(RenderCommand command, XRCamera? camera)
            => CollectedForRenderCallback?.Invoke(this, command, camera);

        protected abstract void RenderCullingVolume();

        private EventList<RenderCommand> _renderCommands = [];
        public EventList<RenderCommand> RenderCommands
        {
            get => _renderCommands;
            set => SetField(ref _renderCommands, value);
        }

        private bool _isVisible = true;
        /// <summary>
        /// IsVisible determines if the object exists in the visual scene tree at all.
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => SetField(ref _isVisible, value);
        }

        /// <summary>
        /// ShouldRender determines if the object is rendered. If false, the object still exists in the visual scene tree, but is not rendered.
        /// </summary>
        public virtual bool ShouldRender => true;

        private XRWorldInstance? _worldInstance;
        /// <summary>
        /// This is the world instance that this render info is part of.
        /// It is set automatically when the render info is added to a 3D visual scene.
        /// </summary>
        [YamlIgnore]
        public XRWorldInstance? WorldInstance
        {
            get => _worldInstance;
            internal set => SetField(ref _worldInstance, value);
        }

        private UICanvasComponent? _userInterfaceCanvas;
        /// <summary>
        /// This is the user interface canvas that this render info is part of.
        /// It is set automatically when the render info is added to a 2D visual scene.
        /// </summary>
        public UICanvasComponent? UserInterfaceCanvas
        {
            get => _userInterfaceCanvas;
            set => SetField(ref _userInterfaceCanvas, value);
        }

        IRenderableBase? ITreeItem.Owner => Owner;

        public void CollectCommands(RenderCommandCollection passes, XRCamera? camera)
        {
            if (!(PreCollectCommandsCallback?.Invoke(this, passes, camera) ?? true))
                return;

            for (int i = 0; i < RenderCommands.Count; i++)
            {
                RenderCommand cmd = RenderCommands[i];
                if (!cmd.Enabled)
                    continue;

                cmd.CollectedForRender(camera);
                passes.Add(cmd);
            }

            if (Engine.Rendering.Settings.RenderCullingVolumes)
                RenderCullingVolume();
        }
    }
}