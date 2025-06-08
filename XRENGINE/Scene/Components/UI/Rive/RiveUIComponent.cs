using RiveSharp;
using Silk.NET.GLFW;
using Silk.NET.OpenGL;
using Silk.NET.SDL;
using Silk.NET.Vulkan;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Numerics;
using UltralightNet;
using XREngine.Data.Rendering;
using XREngine.Input.Devices;
using XREngine.Native;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;

namespace XREngine.Scene.Components.UI
{
    public class RiveUIComponent : UIInteractableComponent
    {
        private CancellationTokenSource? _activeSourceFileLoader = null;

        private string? _artboardName;
        private string? _animationName;
        private string? _stateMachineName;

        /// <summary>
        /// The name of the artboard to load.
        /// </summary>
        public string? ArtboardName
        {
            get => _artboardName;
            set => SetField(ref _artboardName, value);
        }

        /// <summary>
        /// The name of the animation to load.
        /// This will be ignored if StateMachineName is set.
        /// </summary>
        public string? AnimationName
        {
            get => _animationName;
            set => SetField(ref _animationName, value);
        }

        /// <summary>
        /// The name of the state machine to load.
        /// This will override AnimationName if set.
        /// </summary>
        public string? StateMachineName
        {
            get => _stateMachineName;
            set => SetField(ref _stateMachineName, value);
        }

        public void SetSource(string newSourceName)
        {
            // Clear the current Scene while we wait for the new one to load.
            _sceneActionsQueue.Enqueue(() => _scene = new RiveSharp.Scene());
            _activeSourceFileLoader?.Cancel();
            _activeSourceFileLoader = new CancellationTokenSource();
            // Defer state machine inputs here until the new file is loaded.
            _deferredSMInputsDuringFileLoad = [];
            LoadSourceFileDataAsync(newSourceName, _activeSourceFileLoader.Token);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ArtboardName):
                    _sceneActionsQueue.Enqueue(() => UpdateScene(SceneUpdates.Artboard));
                    break;
                case nameof(AnimationName):
                case nameof(StateMachineName):
                    _sceneActionsQueue.Enqueue(() => UpdateScene(SceneUpdates.AnimationOrStateMachine));
                    break;
            }
        }

        // _scene is used on the render thread exclusively.
        private RiveSharp.Scene _scene = new();

        // Source actions originating from other threads must be funneled through this queue.
        readonly ConcurrentQueue<Action> _sceneActionsQueue = new();

        private enum SceneUpdates
        {
            File = 3,
            Artboard = 2,
            AnimationOrStateMachine = 1,
        };

        DateTime? _lastPaintTime;

        public EventList<StateMachineInput> Inputs { get; }

        void SetPlayer(StateMachineInput input)
            => input.SetRivePlayer(new WeakReference<RiveUIComponent?>(this));
        void RemovePlayer(StateMachineInput input)
            => input.SetRivePlayer(new WeakReference<RiveUIComponent?>(null));

        public RiveUIComponent()
        {
            Inputs = [];
            Inputs.PostAnythingAdded += SetPlayer;
            Inputs.PostAnythingRemoved += RemovePlayer;
            RenderInfo2D.RenderCommands.Add(new RenderCommandMethod2D((int)EDefaultRenderPass.OpaqueForward, Render));
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            //InitializeSurface();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            DestroySurface();
        }

        private void DestroySurface()
        {
            _surface?.Dispose();
            _surface = null;

            _renderTarget?.Dispose();
            _renderTarget = null;

            _context?.Dispose();
            _context = null;

            _interface?.Dispose();
            _interface = null;

            _texture?.Destroy();
            _texture = null;
        }

        //private void ResizeSurface(object? sender, Data.Core.IXRPropertyChangedEventArgs e)
        //{
        //    if (e.PropertyName != nameof(UIBoundableTransform.ActualSize))
        //        return;

        //    //DestroySurface();
        //    //InitializeSurface();
        //}
        //private void InitializeSurface()
        //{
        //    var bTfm = BoundableTransform;
        //    bTfm.PropertyChanged += ResizeSurface;
        //    var bounds = bTfm.ActualSize;
        //    uint width = (uint)bounds.X;
        //    uint height = (uint)bounds.Y;

        //    _interface = GRGlInterface.Create();
        //    if (!_interface.Validate())
        //        throw new Exception("Could not initialize Skia GL interface!");
        //    _context = GRContext.CreateGl(_interface);
        //    _texture = XRTexture2D.CreateFrameBufferTexture(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        //    uint bindingId = _texture.APIWrappers.FirstOrDefault() is IGLTexture tex ? tex.BindingId : 0u;
        //    var glTexInfo = new GRGlTextureInfo
        //    {
        //        Target = (uint)GLEnum.Texture2D,
        //        Id = bindingId,
        //        Format = (uint)GLEnum.Rgba8
        //    };
        //    //var vkTexInfo = new GRVkImageInfo
        //    //{
        //    //    Image = 0,
        //    //    Format = (uint)Format.R8G8B8A8Uint,
        //    //    ImageLayout = (uint)ImageLayout.PresentSrcKhr,
        //    //    SampleCount = 1,
        //    //};
        //    _backendTexture = new GRBackendTexture(
        //        (int)width,
        //        (int)height,
        //        false, // no mipmaps
        //        glTexInfo
        //    );
        //    _surface = SKSurface.Create(
        //        _context,
        //        _backendTexture,
        //        GRSurfaceOrigin.BottomLeft,
        //        SKColorType.Rgba8888);
        //}

        public async void LoadSourceFileDataAsync(string name, CancellationToken cancellationToken)
        {
            byte[]? data = null;
            if (Uri.TryCreate(name, UriKind.Absolute, out var uri))
            {
                using var client = new HttpClient();
                data = await client.GetByteArrayAsync(uri, cancellationToken);
            }
            else
            {
                // Assume the name is a local file path.
                try
                {
                    string fullPath = Path.Combine(Environment.CurrentDirectory, name);
                    data = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load Rive file '{name}': {ex.Message}");
                    return;
                }
            }
            if (data != null && !cancellationToken.IsCancellationRequested)
            {
                _sceneActionsQueue.Enqueue(() => UpdateScene(SceneUpdates.File, data));

                // Apply deferred state machine inputs once the scene is fully loaded.
                if (_deferredSMInputsDuringFileLoad != null)
                    foreach (Action stateMachineInput in _deferredSMInputsDuringFileLoad)
                        _sceneActionsQueue.Enqueue(stateMachineInput);
            }
            _deferredSMInputsDuringFileLoad = null;
            _activeSourceFileLoader = null;
        }

        // State machine inputs to set once the current async file load finishes.
        private List<Action>? _deferredSMInputsDuringFileLoad = null;

        private void EnqueueStateMachineInput(Action stateMachineInput)
        {
            if (_deferredSMInputsDuringFileLoad != null)
                _deferredSMInputsDuringFileLoad.Add(stateMachineInput); // A source file is currently loading async. Don't set this input until it completes.
            else
                _sceneActionsQueue.Enqueue(stateMachineInput);
        }

        public void SetBool(string name, bool value)
            => EnqueueStateMachineInput(() => _scene.SetBool(name, value));
        public void SetNumber(string name, float value)
            => EnqueueStateMachineInput(() => _scene.SetNumber(name, value));
        public void FireTrigger(string name)
            => EnqueueStateMachineInput(() => _scene.FireTrigger(name));

        private delegate void PointerHandler(Vec2D pos);

        public override void RegisterInput(InputInterface input)
        {
            base.RegisterInput(input);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, MouseDown);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Released, MouseUp);
        }

        private Vector2 _mousePos = Vector2.Zero;

        public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
        {
            base.MouseMoved(lastPosLocal, posLocal);
            _mousePos = posLocal; // Update the mouse position for the next pointer event.
            HandlePointerEvent(_scene.PointerMove);
        }

        private void MouseUp()
            => HandlePointerEvent(_scene.PointerUp);
        private void MouseDown()
            => HandlePointerEvent(_scene.PointerDown);

        private void HandlePointerEvent(Action<Vec2D> handler)
        {
            // Ignore pointer events while a new scene is loading.
            if (_activeSourceFileLoader != null)
                return;
            
            // Capture the viewSize and pointerPos at the time of the event.
            var viewSize = BoundableTransform.ActualSize;
            var pointerPos = _mousePos;

            // Forward the pointer event to the render thread.
            void Action()
            {
                Mat2D mat = ComputeAlignment(viewSize.X, viewSize.Y);
                if (mat.Invert(out var inverse))
                    handler(inverse * new Vec2D(pointerPos.X, pointerPos.Y));
            }
            _sceneActionsQueue.Enqueue(Action);
        }

        private SKSizeI _lastSize;

        private GRGlInterface? _interface;
        private GRContext? _context;
        private SKSurface? _surface;
        private XRTexture2D? _texture;
        //private GRBackendTexture? _backendTexture;
        private GRGlFramebufferInfo _glInfo;
        private GRBackendRenderTarget? _renderTarget;

        private const uint GL_STENCIL_BITS = 0x0D57; // GL_STENCIL_BITS

        private void Render()
        {
            //GlesContext obj = glesContext;
            //if (obj == null || !obj.HasSurface)
            //{
            //    return;
            //}

            //glesContext.MakeCurrent();
            //if (pendingSizeChange)
            //{
            //    pendingSizeChange = false;
            //    if (!EnableRenderLoop)
            //    {
            //        glesContext.SwapBuffers();
            //    }
            //}

            //glesContext.GetSurfaceDimensions(out var width, out var height);
            //glesContext.SetViewportSize(width, height);

            AbstractRenderer.Current?.Clear(true, true, true);

            if (_context is null)
            {
                _interface = GRGlInterface.Create();
                if (_interface is null || !_interface.Validate())
                {
                    Debug.LogWarning("Failed to create a valid Skia GL interface.");
                    return;
                }
                _context = GRContext.CreateGl(_interface);
            }

            var viewSize = BoundableTransform.ActualSize;
            SKSizeI sKSizeI = new((int)viewSize.X, (int)viewSize.Y);
            if (_renderTarget is null || _lastSize != sKSizeI || !_renderTarget.IsValid)
            {
                _lastSize = sKSizeI;

                var current = AbstractRenderer.Current;
                if (current is null || current is not OpenGLRenderer glRend)
                {
                    Debug.LogWarning("No current renderer found. Cannot create render target.");
                    return;
                }
                int fboID = glRend.GetInteger(GLEnum.FramebufferBinding);
                int stencilBits = 8;//glRend.GetInteger(GLEnum.StencilBits);
                int sampleCount = glRend.GetInteger(GLEnum.Samples);

                int maxSurfaceSampleCount = _context.GetMaxSurfaceSampleCount(SKColorType.Rgba8888);
                if (sampleCount > maxSurfaceSampleCount)
                {
                    sampleCount = maxSurfaceSampleCount;
                }

                _glInfo = new GRGlFramebufferInfo((uint)fboID, SKColorType.Rgba8888.ToGlSizedFormat());
                _surface?.Dispose();
                _surface = null;
                _renderTarget?.Dispose();
                _renderTarget = new GRBackendRenderTarget(sKSizeI.Width, sKSizeI.Height, sampleCount, stencilBits, _glInfo);
            }

            _surface ??= SKSurface.Create(_context, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
            
            using (new SKAutoCanvasRestore(_surface.Canvas, doSave: true))
            {
                // Handle pending scene actions from the main thread.
                while (_sceneActionsQueue.TryDequeue(out var action))
                    action();

                if (_scene.IsLoaded)
                {
                    var now = DateTime.Now;
                    if (_lastPaintTime != null)
                        _scene.AdvanceAndApply((now - _lastPaintTime.Value).TotalSeconds);
                    _lastPaintTime = now;

                    _surface.Canvas.Clear();
                    var renderer = new RiveSharp.Renderer(_surface.Canvas);
                    renderer.Save();
                    renderer.Transform(ComputeAlignment(_renderTarget.Width, _renderTarget.Height));
                    _scene.Draw(renderer);
                    renderer.Restore();
                }
            }

            _surface.Canvas.Flush();
            _context.Flush();

            //glesContext.SwapBuffers();
        }

        // Called from the render thread. Updates _scene according to updates.
        private void UpdateScene(SceneUpdates updates, byte[]? sourceFileData = null)
        {
            if (updates >= SceneUpdates.File)
                _scene.LoadFile(sourceFileData);
            
            if (updates >= SceneUpdates.Artboard)
                _scene.LoadArtboard(_artboardName);
            
            if (updates >= SceneUpdates.AnimationOrStateMachine)
            {
                if (!string.IsNullOrEmpty(_stateMachineName))
                    _scene.LoadStateMachine(_stateMachineName);
                else if (!string.IsNullOrEmpty(_animationName))
                    _scene.LoadAnimation(_animationName);
                else if (!_scene.LoadStateMachine(null))
                    _scene.LoadAnimation(null);
            }
        }

        // Called from the render thread. Computes alignment based on the size of _scene.
        private Mat2D ComputeAlignment(double width, double height)
            => ComputeAlignment(new AABB(0, 0, (float)width, (float)height));

        // Called from the render thread.
        // Computes alignment based on the size of _scene.
        private Mat2D ComputeAlignment(AABB frame)
            => RiveSharp.Renderer.ComputeAlignment(Fit.Contain, Alignment.Center, frame, new AABB(0, 0, _scene.Width, _scene.Height));
    }
}
