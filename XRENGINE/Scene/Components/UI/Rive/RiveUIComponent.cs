using RiveSharp;
using Silk.NET.OpenGL;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;
using Renderer = RiveSharp.Renderer;

namespace XREngine.Scene.Components.UI;

[RequireComponents(typeof(UIMaterialComponent))]
public class RiveUIComponent : UIInteractableComponent
{
    public UIMaterialComponent MaterialComponent => GetSiblingComponent<UIMaterialComponent>(true)!;

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
        NeedsMouseMove = true; // This component needs mouse move events to handle pointer events.
        //Render the FBO in 3D pre-render pass - this is before ANYTHING gets rendered
        RenderInfo3D.RenderCommands.Add(new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, RenderFBO));
    }

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(XREngine.Components.ETickGroup.Normal, XREngine.Components.ETickOrder.Scene, AdvanceScene);
    }

    private float GetRenderDelta()
    {
        _deltaLock.Enter();
        float accumDt = _renderDeltaAccumulator;
        _renderDeltaAccumulator = 0.0f; // Reset the accumulator after reading it.
        _deltaLock.Exit();
        return accumDt;
    }

    private readonly Lock _deltaLock = new();
    private float _renderDeltaAccumulator = 0.0f;
    private void AdvanceScene()
    {
        _deltaLock.Enter();
        _renderDeltaAccumulator += Engine.SmoothedDelta;
        _deltaLock.Exit();
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

        _surfaceTexture?.Destroy();
        _surfaceTexture = null;
    }

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
                name = name.Replace('/', Path.DirectorySeparatorChar);
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

    private Vector2 _mousePosition = Vector2.Zero;

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
    {
        base.MouseMoved(lastPosLocal, posLocal);
        _mousePosition = posLocal; // Update the mouse position for the next pointer event.
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
        var pointerPos = _mousePosition;
        pointerPos.Y = viewSize.Y - pointerPos.Y; // Invert Y coordinate for Rive's coordinate system.

        // Forward the pointer event to the render thread.
        void Action()
        {
            Mat2D mat = ComputeAlignment(viewSize.X, viewSize.Y);
            if (mat.Invert(out var inverse))
                handler(inverse * new Vec2D(pointerPos.X, pointerPos.Y));
        }
        _sceneActionsQueue.Enqueue(Action);
    }

    private GRGlInterface? _interface;
    private GRContext? _context;
    private SKSurface? _surface;
    private XRTexture2D? _surfaceTexture;
    private XRFrameBuffer? _renderFBO;
    private GRBackendRenderTarget? _renderTarget;

    private void RenderFBO()
    {
        var viewSize = BoundableTransform.ActualSize;
        if (viewSize.X <= float.Epsilon || viewSize.Y <= float.Epsilon)
        {
            Debug.LogWarning("Rive UI component has invalid size. Cannot render.");
            return;
        }

        SKSizeI sKSizeI = new((int)viewSize.X, (int)viewSize.Y);
        VerifyTexture(sKSizeI);
        VerifyFBO(sKSizeI);

        using var bind = _renderFBO!.BindForWritingState();
        //AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(0, 0, sKSizeI.Width, sKSizeI.Height));
        Engine.Rendering.State.ClearColor(ColorF4.Transparent);
        Engine.Rendering.State.ClearByBoundFBO(true, true, true);

        if (!VerifyContext() || !VerifyRenderTarget(sKSizeI))
            return;

        RenderToCanvas();

        _context?.Flush();
    }

    private void VerifyFBO(SKSizeI sKSizeI)
    {
        if (_renderFBO is not null && _renderFBO.Width == (uint)sKSizeI.Width && _renderFBO.Height == (uint)sKSizeI.Height)
            return;
        
        _renderFBO = new((_surfaceTexture!, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        _renderFBO.Generate();

        //Recreate render target
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    private bool VerifyContext()
    {
        if (_context is not null)
            return true;
        
        _interface = GRGlInterface.Create();
        if (_interface is null || !_interface.Validate())
        {
            Debug.LogWarning("Failed to create a valid Skia GL interface.");
            return false;
        }

        _context = GRContext.CreateGl(_interface);
        return true;
    }

    private Renderer? _renderer;

    private void RenderToCanvas()
    {
        if (_renderTarget is null || _renderFBO is null || _context is null)
        {
            Debug.LogWarning("Render target or surface texture is not initialized. Cannot render Rive UI component.");
            return;
        }

        if (_surface is null)
        {
            var surf = SKSurface.Create(
                _context,
                _renderTarget,
                GRSurfaceOrigin.BottomLeft,
                SKColorType.Rgba8888
            );
            if (surf is null)
            {
                Debug.LogWarning("Failed to create a valid SKSurface for Rive rendering.");
                return;
            }
            _surface = surf;
            _renderer = new Renderer(_surface.Canvas);
        }

        using (new SKAutoCanvasRestore(_surface.Canvas, doSave: true))
        {
            // Handle pending scene actions from the main thread.
            while (_sceneActionsQueue.TryDequeue(out var action))
                action();

            _surface.Canvas.Clear();
            _renderer!.Save();
            _renderer.Transform(ComputeAlignment(_renderTarget.Width, _renderTarget.Height));

            float accumDt = GetRenderDelta();
            if (accumDt > 0.0f)
                _scene.AdvanceAndApply(accumDt);

            _scene.Draw(_renderer);

            _renderer.Restore();
        }

        _surface.Canvas.Flush();
    }

    private bool VerifyRenderTarget(SKSizeI sKSizeI)
    {
        if (_renderTarget is not null && _renderTarget.Size == sKSizeI && _renderTarget.IsValid)
            return true;

        var current = AbstractRenderer.Current;
        if (current is null || current is not OpenGLRenderer ogl)
        {
            Debug.LogWarning("No current renderer found. Cannot create backend texture.");
            return false;
        }

        _renderTarget?.Dispose();
        _renderTarget = new GRBackendRenderTarget(
            sKSizeI.Width,
            sKSizeI.Height,
            GetSampleCount(ogl),
            GetSurfaceStencilBits(_renderFBO!),
            GetFBOInfo());

        //Recreate surface
        _surface = null;

        return true;
    }

    private GRGlFramebufferInfo GetFBOInfo()
    {
        var bindingID = (_renderFBO?.APIWrappers?.FirstOrDefault() as GLFrameBuffer)?.BindingId ?? 0u;
        if (bindingID == 0)
            Debug.LogWarning("Framebuffer binding ID is invalid");
        return new GRGlFramebufferInfo(bindingID, SKColorType.Rgba8888.ToGlSizedFormat());
    }

    private static int GetSurfaceStencilBits(XRFrameBuffer fbo)
    {
        if (fbo.APIWrappers.FirstOrDefault() is not GLFrameBuffer glFBO)
        {
            Debug.LogWarning("Framebuffer is not a GL framebuffer. Cannot get stencil bits.");
            return 0;
        }
        return glFBO?.GetAttachmentParameter(GLEnum.ColorAttachment0, GLEnum.FramebufferAttachmentStencilSize) ?? 0;
    }

    private int GetSampleCount(OpenGLRenderer ogl)
    {
        int sampleCount = ogl!.GetInteger(GLEnum.Samples);
        int maxSurfaceSampleCount = _context!.GetMaxSurfaceSampleCount(SKColorType.Rgba8888);
        if (sampleCount > maxSurfaceSampleCount)
            sampleCount = maxSurfaceSampleCount;
        return sampleCount;
    }

    private void VerifyTexture(SKSizeI sKSizeI)
    {
        if (_surfaceTexture is not null && 
            _surfaceTexture.Width == sKSizeI.Width &&
            _surfaceTexture.Height == sKSizeI.Height)
            return;
        
        if (_surfaceTexture?.Resizable ?? false)
        {
            //If the texture is resizable, we can just resize it.
            _surfaceTexture.Resize((uint)sKSizeI.Width, (uint)sKSizeI.Height);
        }
        else
        {
            //If the texture is not resizable, we need to destroy it and create a new one.
            _surfaceTexture?.Destroy();
            _surfaceTexture = XRTexture2D.CreateFrameBufferTexture(
                (uint)sKSizeI.Width,
                (uint)sKSizeI.Height,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte
            );
            _surfaceTexture.Resizable = false;
            _surfaceTexture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            _surfaceTexture.Generate();

            var mat = MaterialComponent.Material;
            if (mat is not null && mat is not null && mat.Textures.Count > 0)
                mat.Textures[0] = _surfaceTexture;
            else
                SetMaterial();
        }

        //The backend texture needs to be fully recreated if the size has changed.
        //_backendTexture?.Dispose();
        //_backendTexture = null;
        _renderFBO?.Destroy();
        _renderFBO = null;
    }

    private void SetMaterial()
    {
        XRMaterial mat = XRMaterial.CreateUnlitTextureMaterialForward(_surfaceTexture!);
        //mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        mat.EnableTransparency();
        mat.Name = "RiveUIComponentMaterial";
        mat.RenderOptions.CullMode = ECullMode.Back;
        mat.RenderOptions.DepthTest = new DepthTest
        {
            Enabled = ERenderParamUsage.Enabled,
            Function = EComparison.Always,
            UpdateDepth = true,
        };
        var matComp = MaterialComponent;
        matComp.Material = mat;
        matComp.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        //matComp.FlipVerticalUVCoord = false;
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
        => ComputeAlignment(new RiveSharp.AABB(0, 0, (float)width, (float)height));

    // Called from the render thread.
    // Computes alignment based on the size of _scene.
    private Mat2D ComputeAlignment(RiveSharp.AABB frame)
        => Renderer.ComputeAlignment(Fit.Contain, Alignment.Center, frame, new RiveSharp.AABB(0, 0, _scene.Width, _scene.Height));
}
