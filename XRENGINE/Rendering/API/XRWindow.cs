using Extensions;
using Newtonsoft.Json;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering
{
    /// <summary>
    /// Links a Silk.NET generated window to an API-specific engine renderer.
    /// </summary>
    [RuntimeOnly]
    public sealed class XRWindow : XRBase
    {
        public static event Action<XRWindow, bool>? AnyWindowFocusChanged;
        public event Action<XRWindow, bool>? FocusChanged;

        private XRWorldInstance? _targetWorldInstance;
        public XRWorldInstance? TargetWorldInstance
        {
            get => _targetWorldInstance;
            set => SetField(ref _targetWorldInstance, value);
        }

        /// <summary>
        /// Indicates whether this window prefers HDR output; renderers can override swap-chain/context setup accordingly.
        /// </summary>
        public bool PreferHDROutput { get; internal set; }

        /// <summary>
        /// True when the platform's native chrome should remain visible. False means the engine is expected to render its own title bar.
        /// </summary>
        public bool UseNativeTitleBar { get; }

        private readonly EventList<XRViewport> _viewports = [];
        public EventList<XRViewport> Viewports => _viewports;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(TargetWorldInstance):
                    VerifyTick();
                    if (!(Engine.Networking?.IsClient ?? false))
                        Engine.Networking?.ReplicateStateChange(
                            new Engine.StateChangeInfo(
                                Engine.EStateChangeType.WorldChange,
                                JsonConvert.SerializeObject(EncodeWorldHierarchy())),
                            true,
                            true);
                    break;
            }
        }

        private class NodeRepresentation
        {
            public Guid ServerGUID { get; set; }
            public (string? FullTypeDef, Guid ServerGUID) TransformType { get; set; } = (null, Guid.Empty);
            public (string FullTypeDef, Guid ServerGUID)[] ComponentTypes { get; set; } = [];
            public NodeRepresentation[] Children { get; set; } = [];
        }
        private class WorldHierarchy
        {
            public string? GameModeFullTypeDef { get; set; }
            public NodeRepresentation?[]? RootNodes { get; set; } = [];
        }

        private WorldHierarchy? EncodeWorldHierarchy()
        {
            var world = TargetWorldInstance;
            if (world is null)
                return null;

            // Filter out editor-only nodes (nodes in the hidden editor scene)
            var rootNodes = world.RootNodes
                .Where(node => !world.IsInEditorScene(node))
                .Select(EncodeNode)
                .ToArray();
            return new WorldHierarchy
            {
                GameModeFullTypeDef = world.GameMode?.GetType().FullName,
                RootNodes = rootNodes,
            };
        }

        private NodeRepresentation EncodeNode(SceneNode node)
        {
            var transform = node.Transform;
            NodeRepresentation[] children = [.. transform.Children.Where(x => x.SceneNode is not null).Select(x => EncodeNode(x.SceneNode!))];
            (string FullTypeDef, Guid ServerGUID)[] components = [.. node.Components.Select(x => (x.GetType().FullName ?? string.Empty, x.ID))];
            return new NodeRepresentation
            {
                ServerGUID = node.ID,
                TransformType = (transform.GetType().FullName, transform.ID),
                ComponentTypes = components,
                Children = children,
            };
        }

        private void ViewportsChanged(object sender, TCollectionChangedEventArgs<XRViewport> e)
        {
            switch (e.Action)
            {
                case ECollectionChangedAction.Remove:
                    foreach (var viewport in e.OldItems)
                        viewport.Destroy();
                    break;
                case ECollectionChangedAction.Clear:
                    foreach (var viewport in e.OldItems)
                        viewport.Destroy();
                    break;
            }
            VerifyTick();
        }

        public bool IsTickLinked { get; private set; } = false;
        private void VerifyTick()
        {
            using var sample = Engine.Profiler.Start("XRWindow.VerifyTick");

            if (ShouldBeRendering())
            {
                if (IsTickLinked)
                    return;

                IsTickLinked = true;
                BeginTick();
            }
            else
            {
                if (!IsTickLinked)
                    return;

                IsTickLinked = false;
                EndTick();
            }
        }

        public void RenderViewports()
        {
            using var sample = Engine.Profiler.Start("XRWindow.RenderViewports");
            foreach (var viewport in Viewports)
            {
                using var viewportSample = Engine.Profiler.Start($"XRViewport.Render[{viewport.Index}]");
                viewport.Render();
            }
        }

        private void UnlinkWindow()
        {
            var w = Window;
            if (w is null)
                return;

            w.FramebufferResize -= FramebufferResizeCallback;
            w.Render -= RenderCallback;
            w.FocusChanged -= OnFocusChanged;
            w.Closing -= Window_Closing;
            w.Load -= Window_Load;
        }

        private void LinkWindow()
        {
            IWindow? w = Window;
            if (w is null)
                return;

            w.FramebufferResize += FramebufferResizeCallback;
            w.Render += RenderCallback;
            w.FocusChanged += OnFocusChanged;
            w.Closing += Window_Closing;
            w.Load += Window_Load;
        }

        private bool _isFocused = false;
        public bool IsFocused
        {
            get => _isFocused;
            private set => SetField(ref _isFocused, value);
        }

        private void OnFocusChanged(bool focused)
        {
            IsFocused = focused;
            FocusChanged?.Invoke(this, focused);
            AnyWindowFocusChanged?.Invoke(this, focused);
        }

        private void EndTick()
        {
            using var sample = Engine.Profiler.Start("XRWindow.EndTick");

            Engine.Time.Timer.SwapBuffers -= SwapBuffers;
            Engine.Time.Timer.RenderFrame -= RenderFrame;
            //Engine.Rendering.DestroyObjectsForRenderer(Renderer);
            Renderer.CleanUp();
            Window.DoEvents();
        }

        private void BeginTick()
        {
            using var sample = Engine.Profiler.Start("XRWindow.BeginTick");

            Renderer.Initialize();
            Engine.Time.Timer.SwapBuffers += SwapBuffers;
            Engine.Time.Timer.RenderFrame += RenderFrame;
        }

        private void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("XRWindow.SwapBuffers");
        }

        private void RenderFrame()
        {
            using var sample = Engine.Profiler.Start("XRWindow.Timer.RenderFrame");

            {
                using var eventsSample = Engine.Profiler.Start("XRWindow.Timer.DoEvents");
                Window.DoEvents();
            }

            {
                using var doRenderSample = Engine.Profiler.Start("XRWindow.Timer.DoRender");
                Window.DoRender();
            }
        }

        public event Action? RenderViewportsCallback;

        private bool _viewportPanelSizingActive;
        private int _lastViewportPanelX;
        private int _lastViewportPanelY;
        private int _lastViewportPanelWidth;
        private int _lastViewportPanelHeight;

        // FBO for viewport panel mode - renders scene to texture for ImGui display
        private XRTexture2D? _viewportPanelTexture;
        private XRFrameBuffer? _viewportPanelFBO;

        /// <summary>
        /// Gets the texture containing the rendered scene for viewport panel mode.
        /// Returns null if not in viewport panel mode or if the FBO hasn't been created yet.
        /// </summary>
        public XRTexture2D? ViewportPanelTexture => _viewportPanelTexture;

        /// <summary>
        /// Gets the FBO used for rendering in viewport panel mode.
        /// </summary>
        public XRFrameBuffer? ViewportPanelFBO => _viewportPanelFBO;

        private void EnsureViewportPanelFBO(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            bool needsResize = _viewportPanelTexture is not null &&
                (_viewportPanelTexture.Width != (uint)width || _viewportPanelTexture.Height != (uint)height);

            if (_viewportPanelFBO is null || needsResize)
            {
                // Create or recreate texture with the correct size
                _viewportPanelTexture = XRTexture2D.CreateFrameBufferTexture(
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.Rgba8,
                    EPixelFormat.Rgba,
                    EPixelType.UnsignedByte,
                    EFrameBufferAttachment.ColorAttachment0);
                _viewportPanelTexture.Resizable = true;
                _viewportPanelTexture.MinFilter = ETexMinFilter.Linear;
                _viewportPanelTexture.MagFilter = ETexMagFilter.Linear;
                _viewportPanelTexture.UWrap = ETexWrapMode.ClampToEdge;
                _viewportPanelTexture.VWrap = ETexWrapMode.ClampToEdge;
                _viewportPanelTexture.Name = "ViewportPanelTexture";

                _viewportPanelFBO = new XRFrameBuffer((_viewportPanelTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = "ViewportPanelFBO"
                };
            }
        }

        private void DestroyViewportPanelFBO()
        {
            _viewportPanelFBO?.Destroy();
            _viewportPanelFBO = null;
            _viewportPanelTexture?.Destroy();
            _viewportPanelTexture = null;
        }

        //private float _lastFrameTime = 0.0f;
        private void RenderCallback(double delta)
        {
            using var frameSample = Engine.Profiler.Start("XRWindow.RenderFrame");

            // Reset per-frame rendering statistics at the start of each frame
            Engine.Rendering.Stats.BeginFrame();

            try
            {
                Renderer.Active = true;
                AbstractRenderer.Current = Renderer;
            
                bool useViewportPanelMode =
                    Engine.IsEditor &&
                    Engine.Rendering.Settings.ViewportPresentationMode == Engine.Rendering.EngineSettings.EViewportPresentationMode.UseViewportPanel;
                bool canRenderWindowViewports = !Engine.VRState.IsInVR || Engine.Rendering.Settings.RenderWindowsWhileInVR;

                // High-signal render diagnostics (rate-limited)
                {
                    string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";
                    if (!canRenderWindowViewports)
                    {
                        Debug.RenderingEvery(
                            keyBase + ".VRGated",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] Window gated by VR. IsInVR={0}, RenderWindowsWhileInVR={1}",
                            Engine.VRState.IsInVR,
                            Engine.Rendering.Settings.RenderWindowsWhileInVR);
                    }

                    if (!ShouldBeRendering())
                    {
                        Debug.RenderingWarningEvery(
                            keyBase + ".NotRendering",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] Window not rendering: Viewports={0}, TargetWorldInstanceNull={1}, PresentationMode={2}, CanRenderWindowViewports={3}",
                            Viewports.Count,
                            TargetWorldInstance is null,
                            Engine.Rendering.Settings.ViewportPresentationMode,
                            canRenderWindowViewports);
                    }

                    // Per-viewport state snapshot (only a few times a second)
                    Debug.RenderingEvery(
                        keyBase + ".ViewportSummary",
                        TimeSpan.FromSeconds(2),
                        "[RenderDiag] Window tick. Delta={0:F4}s, Viewports={1}, TargetWorld={2}, PanelMode={3}",
                        delta,
                        Viewports.Count,
                        TargetWorldInstance?.TargetWorld?.Name ?? "<null>",
                        useViewportPanelMode);

                    foreach (var vp in Viewports)
                    {
                        var activeCamera = vp.ActiveCamera;
                        var world = vp.World;
                        bool hasPipeline = vp.RenderPipelineInstance.Pipeline is not null;
                        Debug.RenderingEvery(
                            keyBase + $".VP.{vp.Index}",
                            TimeSpan.FromSeconds(2),
                            "[RenderDiag] VP[{0}] Region={1}x{2}@({3},{4}) Internal={5}x{6} ActiveCameraNull={7} WorldNull={8} PipelineNull={9} AssocPlayer={10}",
                            vp.Index,
                            vp.Width,
                            vp.Height,
                            vp.X,
                            vp.Y,
                            vp.InternalWidth,
                            vp.InternalHeight,
                            activeCamera is null,
                            world is null,
                            !hasPipeline,
                            vp.AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");
                    }
                }

                using (var preRenderSample = Engine.Profiler.Start("XRWindow.GlobalPreRender"))
                {
                    TargetWorldInstance?.GlobalPreRender();
                }
                
                using (var renderCallbackSample = Engine.Profiler.Start("XRWindow.RenderViewportsCallback"))
                {
                    RenderViewportsCallback?.Invoke();
                }

                if (canRenderWindowViewports)
                {
                    if (useViewportPanelMode)
                    {
                        BoundingRectangle? viewportPanelRegion = Engine.Rendering.ViewportPanelRenderRegionProvider?.Invoke(this);
                        if (viewportPanelRegion.HasValue && viewportPanelRegion.Value.Width > 0 && viewportPanelRegion.Value.Height > 0)
                        {
                            // ImGui rendering typically leaves scissor/cropping enabled.
                            // Ensure world rendering starts from a clean state so clears and passes aren't clipped/offset.
                            Renderer.SetCroppingEnabled(false);

                            // Ensure FBO exists and is the correct size
                            EnsureViewportPanelFBO(viewportPanelRegion.Value.Width, viewportPanelRegion.Value.Height);

                            // Apply viewport sizing (but not position - we render at 0,0 in the FBO)
                            ApplyViewportPanelRegionForFBO(viewportPanelRegion.Value);

                            // Render viewports to the FBO
                            RenderViewportsToFBO(_viewportPanelFBO);
                        }
                        else
                        {
                            Debug.RenderingEvery(
                                $"XRWindow.RenderCallback.{GetHashCode()}.PanelRegionMissing",
                                TimeSpan.FromSeconds(1),
                                "[RenderDiag] Viewport panel mode active but region missing/invalid. Region={0}",
                                viewportPanelRegion.HasValue ? $"{viewportPanelRegion.Value.Width}x{viewportPanelRegion.Value.Height}" : "<null>");
                            RestoreViewportPanelSizingIfNeeded();
                            DestroyViewportPanelFBO();
                        }
                    }
                    else
                    {
                        RestoreViewportPanelSizingIfNeeded();
                        DestroyViewportPanelFBO();

                        // RenderViewportsCallback (e.g., ImGui) can leave scissor/cropping enabled.
                        // Ensure world rendering starts from a clean state so clears and passes aren't clipped/offset.
                        Renderer.SetCroppingEnabled(false);

                        RenderViewports();
                    }
                }
                else
                {
                    RestoreViewportPanelSizingIfNeeded();
                    DestroyViewportPanelFBO();
                }
                using (var postRenderSample = Engine.Profiler.Start("XRWindow.GlobalPostRender"))
                {
                    TargetWorldInstance?.GlobalPostRender();
                }
            
            }
            finally
            {
                Renderer.Active = false;
                AbstractRenderer.Current = null;
            }
        }

        private bool ShouldBeRendering()
            => Viewports.Count > 0 && TargetWorldInstance is not null;

        private void FramebufferResizeCallback(Vector2D<int> obj)
        {
            using var sample = Engine.Profiler.Start("XRWindow.FramebufferResize");

            //Debug.Out("Window resized to {0}x{1}", obj.X, obj.Y);
            Viewports.ForEach(vp => vp.Resize((uint)obj.X, (uint)obj.Y, true));
            Renderer.FrameBufferInvalidated();

            //var timer = Engine.Time.Timer;
            //await timer.DispatchCollectVisible();
            //await timer.DispatchSwapBuffers();
            //timer.DispatchRender();
        }

        private void ApplyViewportPanelRegion(BoundingRectangle region)
        {
            if (Viewports.Count == 0)
                return;

            bool sizeChanged =
                !_viewportPanelSizingActive ||
                region.Width != _lastViewportPanelWidth ||
                region.Height != _lastViewportPanelHeight;

            // Always resize viewports to match the panel size.
            // Resize() sets X=0, Y=0 from percentage fields; we then set the actual offset.
            if (sizeChanged)
            {
                Viewports.ForEach(vp => vp.Resize((uint)region.Width, (uint)region.Height, setInternalResolution: true));
            }

            // Set viewport position to match the panel bounds.
            // This affects Region which is used by VPRC_PushViewportRenderArea(UseInternalResolution=false)
            // for the final output pass only. Internal passes use InternalResolutionRegion (always 0,0).
            foreach (var vp in Viewports)
            {
                vp.X = region.X;
                vp.Y = region.Y;
            }

            _viewportPanelSizingActive = true;
            _lastViewportPanelX = region.X;
            _lastViewportPanelY = region.Y;
            _lastViewportPanelWidth = region.Width;
            _lastViewportPanelHeight = region.Height;
        }

        /// <summary>
        /// Applies viewport sizing for FBO mode. Unlike ApplyViewportPanelRegion, this sets
        /// viewport position to (0,0) since we're rendering to an FBO that will be displayed by ImGui.
        /// </summary>
        private void ApplyViewportPanelRegionForFBO(BoundingRectangle region)
        {
            if (Viewports.Count == 0)
                return;

            bool sizeChanged =
                !_viewportPanelSizingActive ||
                region.Width != _lastViewportPanelWidth ||
                region.Height != _lastViewportPanelHeight;

            // Always resize viewports to match the panel size.
            if (sizeChanged)
            {
                Viewports.ForEach(vp => vp.Resize((uint)region.Width, (uint)region.Height, setInternalResolution: true));
            }

            // For FBO mode, viewport position is always (0,0) since the FBO is the same size as the content area.
            // ImGui will position the rendered image at the correct location.
            foreach (var vp in Viewports)
            {
                vp.X = 0;
                vp.Y = 0;
            }

            _viewportPanelSizingActive = true;
            _lastViewportPanelX = 0;
            _lastViewportPanelY = 0;
            _lastViewportPanelWidth = region.Width;
            _lastViewportPanelHeight = region.Height;
        }

        /// <summary>
        /// Renders all viewports to the specified FBO instead of the window framebuffer.
        /// </summary>
        public void RenderViewportsToFBO(XRFrameBuffer? targetFBO)
        {
            if (targetFBO is null)
            {
                RenderViewports();
                return;
            }

            using var sample = Engine.Profiler.Start("XRWindow.RenderViewportsToFBO");
            foreach (var viewport in Viewports)
            {
                using var viewportSample = Engine.Profiler.Start($"XRViewport.RenderToFBO[{viewport.Index}]");
                viewport.Render(targetFBO);
            }
        }

        private void RestoreViewportPanelSizingIfNeeded()
        {
            if (!_viewportPanelSizingActive)
                return;

            var fb = Window.FramebufferSize;
            if (fb.X > 0 && fb.Y > 0)
            {
                Viewports.ForEach(vp => vp.Resize((uint)fb.X, (uint)fb.Y, setInternalResolution: true));
            }

            _viewportPanelSizingActive = false;
            _lastViewportPanelX = 0;
            _lastViewportPanelY = 0;
            _lastViewportPanelWidth = 0;
            _lastViewportPanelHeight = 0;
        }

        public XRViewport GetOrAddViewportForPlayer(LocalPlayerController controller, bool autoSizeAllViewports)
            => controller.Viewport ??= AddViewportForPlayer(controller, autoSizeAllViewports);

        private XRViewport AddViewportForPlayer(LocalPlayerController? controller, bool autoSizeAllViewports)
        {
            using var sample = Engine.Profiler.Start("XRWindow.AddViewportForPlayer");

            XRViewport newViewport = XRViewport.ForTotalViewportCount(this, Viewports.Count);
            newViewport.AssociatedPlayer = controller;
            Viewports.Add(newViewport);

            Debug.Out("Added new viewport to {0}: {1}", GetType().GetFriendlyName(), newViewport.Index);

            if (autoSizeAllViewports)
                ResizeAllViewportsAccordingToPlayers();

            return newViewport;
        }

        /// <summary>
        /// Remakes all viewports in order of active local player indices.
        /// </summary>
        public void ResizeAllViewportsAccordingToPlayers()
        {
            using var sample = Engine.Profiler.Start("XRWindow.ResizeAllViewportsAccordingToPlayers");

            LocalPlayerController[] players = [.. Viewports
                .Select(x => x.AssociatedPlayer)
                .OfType<LocalPlayerController>()
                .Distinct()
                .OrderBy(x => (int)x.LocalPlayerIndex)];
            foreach (var viewport in Viewports)
                viewport.Destroy();
            Viewports.Clear();
            for (int i = 0; i < players.Length; i++)
                AddViewportForPlayer(players[i], false);
        }


        public void RegisterLocalPlayer(ELocalPlayerIndex playerIndex, bool autoSizeAllViewports)
            => RegisterController(Engine.State.GetOrCreateLocalPlayer(playerIndex), autoSizeAllViewports);

        public void RegisterController(LocalPlayerController controller, bool autoSizeAllViewports)
            => GetOrAddViewportForPlayer(controller, autoSizeAllViewports).AssociatedPlayer = controller;

        public void UnregisterLocalPlayer(ELocalPlayerIndex playerIndex)
        {
            LocalPlayerController? controller = Engine.State.GetLocalPlayer(playerIndex);
            if (controller is not null)
                UnregisterController(controller);
        }

        public void UnregisterController(LocalPlayerController controller)
        {
            if (controller.Viewport != null && Viewports.Contains(controller.Viewport))
                controller.Viewport = null;
        }

        /// <summary>
        /// Silk.NET window instance.
        /// </summary>
        public IWindow Window { get; }

        /// <summary>
        /// Interface to render a scene for this window using the requested graphics API.
        /// </summary>
        public AbstractRenderer Renderer { get; }

        public IInputContext? Input { get; private set; }

        public XRWindow(WindowOptions options, bool useNativeTitleBar)
        {
            _viewports.CollectionChanged += ViewportsChanged;
            Silk.NET.Windowing.Window.PrioritizeGlfw();
            Window = Silk.NET.Windowing.Window.Create(options);
            UseNativeTitleBar = useNativeTitleBar;
            LinkWindow();
            Window.Initialize();
            //Window.IsEventDriven = true;
            Renderer = Window.API.API switch
            {
                ContextAPI.OpenGL => new OpenGLRenderer(this, true),
                ContextAPI.Vulkan => new VulkanRenderer(this, true),
                _ => throw new Exception($"Unsupported API: {Window.API.API}"),
            };
        }

        private void Window_Load()
        {
            //Task.Run(() =>
            //{
                Input = Window.CreateInput();
                Input.ConnectionChanged += Input_ConnectionChanged;
            //});
        }

        private void Input_ConnectionChanged(IInputDevice device, bool connected)
        {
            switch (device)
            {
                case IKeyboard keyboard:
                    
                    break;
                case IMouse mouse:

                    break;
                case IGamepad gamepad:

                    break;
            }
        }

        private void Window_Closing()
        {
            //Renderer.Dispose();
            //Window.Dispose();
            UnlinkWindow();
            Engine.RemoveWindow(this);
        }

        private void ResizeViewports(Vector2D<int> obj)
        {
            using var sample = Engine.Profiler.Start("XRWindow.ResizeViewports");

            void SetSize(XRViewport vp)
            {
                vp.Resize((uint)obj.X, (uint)obj.Y, true);
                //vp.SetInternalResolution((int)(obj.X * 0.5f), (int)(obj.X * 0.5f), false);
                //vp.SetInternalResolutionPercentage(0.5f, 0.5f);
            }
            Viewports.ForEach(SetSize);
        }

        public void UpdateViewportSizes()
        {
            using var sample = Engine.Profiler.Start("XRWindow.UpdateViewportSizes");
            ResizeViewports(Window.Size);
        }

        public void SetWorld(XRWorld? targetWorld)
        {
            if (targetWorld is not null)
                TargetWorldInstance = XRWorldInstance.GetOrInitWorld(targetWorld);
        }
    }
}
