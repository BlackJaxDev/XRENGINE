using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
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
    public abstract class RenderInfo : XRBase, ITreeItem, IDisposable
    {
        private static long s_nextStableInstanceId;
        private bool _disposeRequested;
        private bool _disposeInProgress;
        private bool _disposed;
        private readonly EventList<RenderCommand> _ownedRenderCommands;
        private EventList<RenderCommand>? _attachedRenderCommands;
        private bool _renderCommandHooksComplete;
        private int _renderCommandsSetDepth;

        /// <summary>
        /// Process-stable identity used by frame visibility candidates and diagnostics.
        /// </summary>
        [YamlIgnore]
        public ulong StableInstanceId { get; } =
            unchecked((ulong)System.Threading.Interlocked.Increment(ref s_nextStableInstanceId));
        public abstract ITreeNode? TreeNode { get; }

        public IRenderable? Owner { get; set; }
        protected bool IsDisposalRequested => _disposeRequested;

        public override string ToString()
            => $"{Owner?.ToString() ?? "Unknown"}";

        public delegate void DelPreRenderCallback(RenderInfo info, RenderCommand command, IRuntimeRenderCamera? camera);
        /// <summary>
        /// This callback is called when the engine is collecting render commands for the render pass, and this render info is added.
        /// </summary>
        public event DelPreRenderCallback? CollectedForRenderCallback;

        public delegate void DelSwapBuffersCallback(RenderInfo info, RenderCommand command);
        /// <summary>
        /// This callback is called when the engine is swapping buffers - both the collect and render threads are currently in sync and waiting.
        /// </summary>
        public event DelSwapBuffersCallback? SwapBuffersCallback;

        private RenderCommand.DelPreRender? _defaultCollectedHandler;
        private RenderCommand.DelSwapBuffers? _defaultSwapHandler;

        internal bool HasDefaultCommandCallbacks(RenderCommand command, DelSwapBuffersCallback? sceneSwapHandler)
        {
            if (CollectedForRenderCallback is not null ||
                (SwapBuffersCallback is not null && SwapBuffersCallback != sceneSwapHandler)) return false;
            if (_defaultCollectedHandler is null) SetField(ref _defaultCollectedHandler, CollectedForRender, publishNotifications: false);
            if (_defaultSwapHandler is null) SetField(ref _defaultSwapHandler, SwapBuffers, publishNotifications: false);
            return command.HasOnlyOwnerLifecycleCallbacks(_defaultCollectedHandler!, _defaultSwapHandler!);
        }

        public delegate bool DelAddRenderCommandsCallback(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera);
        /// <summary>
        /// This callback is called before render commands are added to the render pass.
        /// Return false to skip adding render commands.
        /// </summary>
        [YamlIgnore]
        public DelAddRenderCommandsCallback? PreCollectCommandsCallback;

        protected RenderInfo(IRenderable? owner, params RenderCommand[] renderCommands)
        {
            Owner = owner;
            _ownedRenderCommands = _renderCommands;
            AttachRenderCommands(RenderCommands);
            _attachedRenderCommands = RenderCommands;
            _renderCommandHooksComplete = true;
            RenderCommands.AddRange(renderCommands);
        }

        private void AttachRenderCommands(EventList<RenderCommand> commands)
        {
            commands.PostAnythingAdded += Added;
            commands.PostAnythingRemoved += Removed;
            for (int i = 0; i < commands.Count; i++)
                Added(commands[i]);
        }

        private void DetachRenderCommands(EventList<RenderCommand> commands)
        {
            commands.PostAnythingAdded -= Added;
            commands.PostAnythingRemoved -= Removed;
            List<Exception>? failures = null;
            for (int i = 0; i < commands.Count; i++)
            {
                try
                {
                    Removed(commands[i]);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
            if (failures is not null)
                throw new AggregateException("Failed to detach render commands.", failures);
        }

        private void Removed(RenderCommand item)
        {
            item.OnCollectedForRender -= CollectedForRender;
            item.OnSwapBuffers -= SwapBuffers;
            if (ReferenceEquals(item.OwnerRenderInfo, this))
            {
                if (item is RenderCommandMesh3D meshCommand)
                    meshCommand.DetachRendererMutationTracking();
                item.OwnerRenderInfo = null;
            }
        }

        private void Added(RenderCommand item)
        {
            item.OwnerRenderInfo = this;
            if (item is RenderCommandMesh3D meshCommand)
                meshCommand.AttachRendererMutationTracking();
            item.OnCollectedForRender += CollectedForRender;
            item.OnSwapBuffers += SwapBuffers;
        }

        private void SwapBuffers(RenderCommand command)
            => SwapBuffersCallback?.Invoke(this, command);

        private void CollectedForRender(RenderCommand command, IRuntimeRenderCamera? camera)
            => CollectedForRenderCallback?.Invoke(this, command, camera);

        protected abstract void RenderCullingVolume();

        public delegate bool DelRenderCullingVolumeDebug(RenderInfo info);
        /// <summary>
        /// Optional culling-volume debug override. Return true when the override handled
        /// debug drawing, even if it intentionally drew nothing.
        /// </summary>
        [YamlIgnore]
        public DelRenderCullingVolumeDebug? RenderCullingVolumeDebugOverride;

        private EventList<RenderCommand> _renderCommands = [];
        /// <summary>
        /// Commands attached to this render info. Replacement lists remain owned by
        /// their callers; the list created with this render info is released at disposal.
        /// </summary>
        public EventList<RenderCommand> RenderCommands
        {
            get => _renderCommands;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                _renderCommandsSetDepth++;
                try
                {
                    SetField(ref _renderCommands, value);
                }
                finally
                {
                    // Nested property notifications may replace the list again. Only
                    // the outer setter reconciles the final field with actual hooks.
                    if (--_renderCommandsSetDepth == 0 && !_disposeRequested)
                        ReconcileRenderCommands();
                }
            }
        }

        private void ReconcileRenderCommands()
        {
            EventList<RenderCommand>? previous = _attachedRenderCommands;
            EventList<RenderCommand> current = _renderCommands;
            if (ReferenceEquals(previous, current) && _renderCommandHooksComplete)
                return;
            if (previous is not null)
            {
                DetachRenderCommands(previous);
                _attachedRenderCommands = null;
                _renderCommandHooksComplete = false;
            }
            _attachedRenderCommands = current;
            try
            {
                AttachRenderCommands(current);
                _renderCommandHooksComplete = true;
            }
            catch
            {
                // Track a partial attachment until every command is detached.
                // A later setter or terminal disposal can retry the cleanup.
                try
                {
                    DetachRenderCommands(current);
                    _attachedRenderCommands = null;
                }
                catch { }
                throw;
            }
        }

        /// <summary>
        /// Releases the command list created by this render info and detaches commands
        /// from any caller-supplied replacement list without destroying that list.
        /// </summary>
        public void Dispose()
        {
            if (_disposed || _disposeInProgress)
                return;
            _disposeRequested = true;
            _disposeInProgress = true;

            try
            {
                List<Exception>? failures = null;
                try { ReleaseWorldRegistration(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
                try { ReleaseCanvasRegistration(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
                if (_attachedRenderCommands is { } attached)
                {
                    try
                    {
                        DetachRenderCommands(attached);
                        _attachedRenderCommands = null;
                    }
                    catch (Exception ex) { (failures ??= []).Add(ex); }
                }
                if (!_ownedRenderCommands.IsDestroyed)
                {
                    try
                    {
                        _ownedRenderCommands.Destroy(true);
                        if (!_ownedRenderCommands.IsDestroyed)
                            throw new InvalidOperationException("Render command list destruction was vetoed.");
                    }
                    catch (Exception ex) { (failures ??= []).Add(ex); }
                }
                CollectedForRenderCallback = null;
                SwapBuffersCallback = null;
                PreCollectCommandsCallback = null;
                RenderCullingVolumeDebugOverride = null;
                Owner = null;
                if (failures is not null)
                    throw new AggregateException("Failed to dispose render info.", failures);
                _disposed = true;
            }
            finally
            {
                _disposeInProgress = false;
            }
        }

        protected virtual void ReleaseWorldRegistration()
        {
            if (_worldInstance is not { } world)
                return;
            if (this is IRuntimeRenderInfo3DRegistrationItem item)
                world.RemoveRenderable3D(item);
            ClearWorldRegistrationField();
        }

        protected void ClearWorldRegistrationField()
        {
            SetField(ref _worldInstance, null, publishNotifications: false, nameof(WorldInstance));
        }

        protected virtual void ReleaseCanvasRegistration()
        {
            if (_userInterfaceCanvas is not { } canvas)
                return;
            if (this is IRuntimeRenderInfo2DRegistrationItem item)
                canvas.RemoveRenderable2D(item);
            ClearCanvasRegistrationField();
        }

        protected void ClearCanvasRegistrationField()
        {
            SetField(ref _userInterfaceCanvas, null, publishNotifications: false, nameof(UserInterfaceCanvas));
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

        private IRuntimeRenderInfo3DRegistrationTarget? _worldInstance;
        /// <summary>
        /// This is the world instance that this render info is part of.
        /// It is set automatically when the render info is added to a 3D visual scene.
        /// </summary>
        [YamlIgnore]
        public IRuntimeRenderInfo3DRegistrationTarget? WorldInstance
        {
            get => _worldInstance;
            internal set
            {
                if (_disposeRequested && value is not null)
                    throw new ObjectDisposedException(nameof(RenderInfo));
                SetField(ref _worldInstance, value);
            }
        }

        private IRuntimeRenderInfo2DRegistrationTarget? _userInterfaceCanvas;
        /// <summary>
        /// This is the user interface canvas that this render info is part of.
        /// It is set automatically when the render info is added to a 2D visual scene.
        /// </summary>
        [YamlIgnore]
        public IRuntimeRenderInfo2DRegistrationTarget? UserInterfaceCanvas
        {
            get => _userInterfaceCanvas;
            set
            {
                if (_disposeRequested && value is not null)
                    throw new ObjectDisposedException(nameof(RenderInfo));
                SetField(ref _userInterfaceCanvas, value);
            }
        }

        IRenderableBase? ITreeItem.Owner => Owner;

        public void CollectCommands(RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            if (_disposeRequested)
                return;
            if (!(PreCollectCommandsCallback?.Invoke(this, passes, camera) ?? true))
                return;

            for (int i = 0; i < RenderCommands.Count; i++)
            {
                RenderCommand cmd = RenderCommands[i];
                // Use the live Enabled flag so hover stencil responds immediately.
                // This keeps CPU-rendered debug overlays responsive even if it means
                // a small risk of cross-thread visibility lag.
                if (!cmd.Enabled)
                    continue;

                cmd.CollectedForRender(camera);
                passes.AddCPU(cmd, camera);
            }

            if (RuntimeRenderingHostServices.FrameTiming.RenderCullingVolumesEnabled &&
                !(RenderCullingVolumeDebugOverride?.Invoke(this) ?? false))
            {
                RenderCullingVolume();
            }
        }
    }
}
