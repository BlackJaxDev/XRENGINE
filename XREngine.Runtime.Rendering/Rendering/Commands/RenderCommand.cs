using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Commands
{
    public abstract class RenderCommand : XRBase, IComparable<RenderCommand>, IComparable
    {
        public RenderCommand()
        {
            StableQueryKey = (uint)System.Threading.Interlocked.Increment(ref s_nextStableQueryKey);
        }
        public RenderCommand(int renderPass)
        {
            StableQueryKey = (uint)System.Threading.Interlocked.Increment(ref s_nextStableQueryKey);
            RenderPass = renderPass;
        }

        private static int s_nextStableQueryKey;

        /// <summary>
        /// Stable per-command identity assigned at construction time. Used as the key for
        /// secondary state that must survive scene mutations (insert/remove of unrelated
        /// commands), notably the CPU occlusion coordinator's per-mesh query state. The
        /// foreach position in the render-command list (cpuCmdIndex) is *not* stable across
        /// mutations and must not be used for this purpose. Monotonically increasing within
        /// the process; wraps every 4G commands which is fine for the coordinator's
        /// dictionary keying and stale-eviction TTL.
        /// </summary>
        [YamlIgnore]
        public uint StableQueryKey { get; }

        /// <summary>
        /// Optional world-space culling volume for this command, used by per-command
        /// visibility systems that need a cheap proxy geometry without involving the
        /// command's full mesh + material. Notably consumed by
        /// <c>CpuRenderOcclusionCoordinator</c>'s periodic-retest path: when an occluded
        /// mesh is force-requeried, it draws this AABB (depth-only, color writes off)
        /// instead of redrawing the full mesh, which avoids visible flicker.
        /// Default null; mesh-bearing commands override to return their transformed
        /// mesh bounds.
        /// </summary>
        [YamlIgnore]
        public virtual AABB? CullingVolume => null;

        public delegate void DelPreRender(RenderCommand command, IRuntimeRenderCamera? camera);
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

        // Dirty-delta swap support.
        //
        // _dirty: any property change marks this true via OnPropertyChanged. Cleared at the end of
        // SwapBuffers() once the per-command publish has run. The render-side snapshot fields
        // (RenderEnabled / per-derived-class snapshot copies) are owned by the command instance,
        // so once a clean publish has run, subsequent collections that share this command can skip
        // their per-command SwapBuffers without losing data.
        //
        // _swapQueued: dedup bit for RenderCommandCollection._updatingSwapQueue. AddCPU may be
        // called multiple times per frame for the same command (e.g. multiple cameras or shadow
        // viewports). The queue uses this bit to add each dirty command at most once per swap.
        // Cleared inside SwapBuffers() after the publish runs.
        //
        // Both fields are mutated under the RenderCommandCollection._lock during AddCPU and during
        // the synchronized SwapBuffers window. They are read without locking by the gate inside
        // SwapBuffers, so they are marked volatile to keep that read coherent.
        internal volatile bool _dirty = true;
        internal volatile bool _swapQueued = false;

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

        [YamlIgnore]
        internal long SortOrderKey { get; set; }

        public abstract int CompareTo(RenderCommand? other);
        public int CompareTo(object? obj) => CompareTo(obj as RenderCommand);

        public event Action? PreRender;
        public event Action? PostRender;

        private IDisposable? _renderState;

        protected void OnPreRender()
        {
            _renderState = RuntimeRenderingHostServices.Current.StartProfileScope("RenderCommand.Render");
            
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
        public virtual void CollectedForRender(IRuntimeRenderCamera? camera)
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
            // Clear after publish so subsequent collections that share this command in the
            // same frame can short-circuit, and so the next frame only re-publishes if a
            // property actually mutated in the interim.
            _dirty = false;
            _swapQueued = false;
        }

        /// <summary>
        /// Marks this command dirty for the next CPU swap. Any property change on a
        /// <see cref="XRBase"/>-derived setter that uses <c>SetField</c> routes through here, so the
        /// dirty-delta queue picks the command up automatically. Subclasses that mutate state via
        /// direct field assignment (rare; <see cref="XRBase"/> mutation contract discourages it)
        /// should call <see cref="MarkDirty"/> explicitly.
        /// </summary>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            _dirty = true;
            base.OnPropertyChanged(propName, prev, field);
        }

        /// <summary>
        /// Manual dirty hook for paths that mutate render-command state without using
        /// <c>SetField</c>. Safe to call from any thread.
        /// </summary>
        public void MarkDirty() => _dirty = true;
    }
}
