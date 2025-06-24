using Extensions;
using Newtonsoft.Json;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering
{
    /// <summary>
    /// Links a Silk.NET generated window to an API-specific engine renderer.
    /// </summary>
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

            var rootNodes = world.RootNodes.Select(EncodeNode).ToArray();
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
            foreach (var viewport in Viewports)
                viewport.Render();
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
            Engine.Time.Timer.SwapBuffers -= SwapBuffers;
            Engine.Time.Timer.RenderFrame -= RenderFrame;
            //Engine.Rendering.DestroyObjectsForRenderer(Renderer);
            Renderer.CleanUp();
            Window.DoEvents();
        }

        private void BeginTick()
        {
            Renderer.Initialize();
            Engine.Time.Timer.SwapBuffers += SwapBuffers;
            Engine.Time.Timer.RenderFrame += RenderFrame;
        }

        private void SwapBuffers()
        {

        }

        private void RenderFrame()
        {
            Window.DoEvents();
            Window.DoRender();
        }

        public event Action? RenderViewportsCallback;

        //private float _lastFrameTime = 0.0f;
        private void RenderCallback(double delta)
        {
            //using var d = Profiler.Start();

            try
            {
                Renderer.Active = true;
                AbstractRenderer.Current = Renderer;

                TargetWorldInstance?.GlobalPreRender();
                RenderViewportsCallback?.Invoke();
                if (!Engine.VRState.IsInVR || Engine.Rendering.Settings.RenderWindowsWhileInVR)
                    RenderViewports();
                TargetWorldInstance?.GlobalPostRender();
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
            //Debug.Out("Window resized to {0}x{1}", obj.X, obj.Y);
            Viewports.ForEach(vp => vp.Resize((uint)obj.X, (uint)obj.Y, true));
            Renderer.FrameBufferInvalidated();

            //var timer = Engine.Time.Timer;
            //await timer.DispatchCollectVisible();
            //await timer.DispatchSwapBuffers();
            //timer.DispatchRender();
        }

        public XRViewport GetOrAddViewportForPlayer(LocalPlayerController controller, bool autoSizeAllViewports)
            => controller.Viewport ??= AddViewportForPlayer(controller, autoSizeAllViewports);

        private XRViewport AddViewportForPlayer(LocalPlayerController? controller, bool autoSizeAllViewports)
        {
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
            LocalPlayerController[] players = [.. Viewports.Select(x => x.AssociatedPlayer).Where(x => x is not null).Distinct().OrderBy(x => (int)x!.LocalPlayerIndex)];
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

        public XRWindow(WindowOptions options)
        {
            _viewports.CollectionChanged += ViewportsChanged;
            Silk.NET.Windowing.Window.PrioritizeGlfw();
            Window = Silk.NET.Windowing.Window.Create(options);
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
            void SetSize(XRViewport vp)
            {
                vp.Resize((uint)obj.X, (uint)obj.Y, true);
                //vp.SetInternalResolution((int)(obj.X * 0.5f), (int)(obj.X * 0.5f), false);
                //vp.SetInternalResolutionPercentage(0.5f, 0.5f);
            }
            Viewports.ForEach(SetSize);
        }

        public void UpdateViewportSizes()
            => ResizeViewports(Window.Size);

        public void SetWorld(XRWorld? targetWorld)
        {
            if (targetWorld is not null)
                TargetWorldInstance = XRWorldInstance.GetOrInitWorld(targetWorld);
        }
    }
}
