using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands
{
    public abstract class RenderCommand : XRBase, IComparable<RenderCommand>, IComparable
    {
        public RenderCommand() { }
        public RenderCommand(int renderPass) => RenderPass = renderPass;

        public delegate void DelPreRender(RenderCommand command, XRCamera? camera);
        public event DelPreRender? OnCollectedForRender;

        public delegate void DelSwapBuffers(RenderCommand command);
        public event DelSwapBuffers? OnSwapBuffers;

        private int _renderPass = (int)EDefaultRenderPass.OpaqueForward;
        /// <summary>
        /// Used by the engine for proper order of rendering.
        /// </summary>
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        private bool _enabled = true;
        private bool _renderEnabled = true;
        private bool _hasSwappedBuffers = false;

        /// <summary>
        /// Whether this command should be collected for rendering.
        /// Updated during Tick/Update, swapped to RenderEnabled during SwapBuffers.
        /// Before the first SwapBuffers call, RenderEnabled is synced immediately to avoid race conditions on startup.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (SetField(ref _enabled, value))
                {
                    // Before the first SwapBuffers, sync RenderEnabled immediately
                    // to ensure correct initial state
                    if (!_hasSwappedBuffers)
                        _renderEnabled = value;
                }
            }
        }

        /// <summary>
        /// The enabled state that was active during the last SwapBuffers.
        /// Used by the render thread to determine if the command should actually render.
        /// </summary>
        public bool RenderEnabled => _renderEnabled;

        public abstract int CompareTo(RenderCommand? other);
        public int CompareTo(object? obj) => CompareTo(obj as RenderCommand);

        public event Action? PreRender;
        public event Action? PostRender;

        private StateObject? _renderState;

        protected void OnPreRender()
        {
            _renderState = Engine.Profiler.Start("RenderCommand.Render");
            
            PreRender?.Invoke();
        }
        protected void OnPostRender()
        {
            PostRender?.Invoke();

            _renderState?.Dispose();
            _renderState = null;
        }

        public abstract void Render();

        /// <summary>
        /// Called in the collect visible thread.
        /// </summary>
        /// <param name="camera"></param>
        public virtual void CollectedForRender(XRCamera? camera)
            => OnCollectedForRender?.Invoke(this, camera);

        /// <summary>
        /// Called when the engine is swapping buffers - both the collect and render threads are waiting.
        /// </summary>
        /// <param name="shadowPass"></param>
        public virtual void SwapBuffers()
        {
            _hasSwappedBuffers = true;
            _renderEnabled = _enabled;
            OnSwapBuffers?.Invoke(this);
        }
    }
}