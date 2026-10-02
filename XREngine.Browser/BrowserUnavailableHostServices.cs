using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Browser;

/// <summary>
/// Scopes unavailable native host operations to one browser engine session. Composition and
/// provider replacement are serialized on the browser event thread.
/// </summary>
internal sealed partial class BrowserUnavailableHostServices : IDisposable
{
    private static readonly object Sync = new();
    private static BrowserUnavailableHostServices? _active;

    private readonly IRuntimeWindowApplicationServices _previousWindows = RuntimeWindowApplicationServices.Current;
    private readonly IRuntimeVrRenderingServices _previousVrRendering = RuntimeVrRenderingServices.Current;
    private readonly IRuntimeVrInputServices _previousVrInput = RuntimeVrInputServices.Current;
    private readonly IRuntimeVrStateServices _previousVrState = RuntimeVrStateServices.Current;
    private readonly IRuntimeVideoStreamingServices? _previousVideo = RuntimeVideoStreamingServices.Current;
    private readonly IRuntimeNetworkDiscoveryHostServices _previousDiscovery = RuntimeNetworkDiscoveryHostServices.Current;
    private readonly IRuntimeClipboardServices? _previousClipboard = RuntimeClipboardServices.Current;
    private readonly IRuntimeOpenVrStateProvider? _previousOpenVrState = RuntimeOpenVrStateServices.Current;
    private readonly IRuntimeOpenVrCompositor? _previousOpenVrCompositor = RuntimeOpenVrCompositorServices.Current;
    private readonly IRuntimeVrLifecycleServices _previousVrLifecycle = RuntimeEngine.VRState.LifecycleServices;
    private readonly BrowserWindowServices _windows = new();
    private readonly BrowserVrRenderingServices _vrRendering = new();
    private readonly BrowserVrInputServices _vrInput = new();
    private readonly BrowserVrStateServices _vrState = new();
    private readonly BrowserVideoServices _video = new();
    private readonly BrowserNetworkDiscoveryServices _discovery = new();
    private readonly BrowserClipboardServices _clipboard = new();
    private readonly BrowserOpenVrStateProvider _openVrState = new();
    private readonly BrowserOpenVrCompositor _openVrCompositor = new();
    private readonly BrowserVrLifecycleServices _vrLifecycle = new();
    private IDisposable? _thirdPartyAssets;
    private IDisposable? _sceneImport;
    private IDisposable? _modelSceneLoading;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _installing;
    private bool _restoring;
    private bool _installed;
    private DirectSlot _pending;

    [Flags]
    private enum DirectSlot
    {
        Windows = 1 << 0,
        VrRendering = 1 << 1,
        VrInput = 1 << 2,
        VrState = 1 << 3,
        Video = 1 << 4,
        Discovery = 1 << 5,
        Clipboard = 1 << 6,
        OpenVrState = 1 << 7,
        OpenVrCompositor = 1 << 8,
        VrLifecycle = 1 << 9,
    }

    private BrowserUnavailableHostServices() { }

    /// <summary>Reserves the process-wide browser slot before any provider is changed.</summary>
    public static BrowserUnavailableHostServices Reserve()
    {
        lock (Sync)
        {
            if (_active is not null)
                throw new InvalidOperationException("Browser.HostServices.SessionAlreadyInstalled: browser host services have one owner at a time.");
            if (!RuntimeVrInputServices.IsDefaultProvider || !RuntimeVrStateServices.IsDefaultProvider)
                throw new InvalidOperationException(
                    "Browser.VrServices.DefaultProviderRequired: browser composition cannot overlay an installed VR runtime.");

            BrowserUnavailableHostServices lease = new();
            _active = lease;
            return lease;
        }
    }

    /// <summary>Installs browser providers after the caller retains this lease.</summary>
    public void Install()
    {
        lock (Sync)
        {
            EnsureOwnerThread();
            if (!ReferenceEquals(_active, this) || _installing || _restoring || _installed)
                throw new InvalidOperationException("Browser.HostServices.InvalidInstallState: browser host ownership changed.");
            if (!RuntimeVrInputServices.IsDefaultProvider || !RuntimeVrStateServices.IsDefaultProvider)
                throw new InvalidOperationException(
                    "Browser.VrServices.DefaultProviderRequired: browser composition cannot overlay an installed VR runtime.");
            _installing = true;
            try
            {
                _pending |= DirectSlot.Windows;
                RuntimeWindowApplicationServices.Current = _windows;
                _pending |= DirectSlot.VrRendering;
                RuntimeVrRenderingServices.Current = _vrRendering;
                _pending |= DirectSlot.VrInput;
                RuntimeVrInputServices.Current = _vrInput;
                _pending |= DirectSlot.VrState;
                RuntimeVrStateServices.Current = _vrState;
                _pending |= DirectSlot.Video;
                RuntimeVideoStreamingServices.Current = _video;
                _pending |= DirectSlot.Discovery;
                RuntimeNetworkDiscoveryHostServices.Current = _discovery;
                _pending |= DirectSlot.Clipboard;
                RuntimeClipboardServices.Current = _clipboard;
                _pending |= DirectSlot.OpenVrState;
                RuntimeOpenVrStateServices.Current = _openVrState;
                _pending |= DirectSlot.OpenVrCompositor;
                RuntimeOpenVrCompositorServices.Current = _openVrCompositor;
                _pending |= DirectSlot.VrLifecycle;
                RuntimeEngine.VRState.LifecycleServices = _vrLifecycle;
                _thirdPartyAssets = RuntimeThirdPartyAssetLoadingServices.Install(new BrowserThirdPartyAssetServices());
                _sceneImport = RuntimeSceneImportServices.Install(new BrowserSceneImportServices());
                _modelSceneLoading = RuntimeModelSceneLoadingServices.Install(new BrowserModelSceneLoadingServices());
                _installed = true;
            }
            finally
            {
                _installing = false;
            }
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            EnsureOwnerThread();
            if (!ReferenceEquals(_active, this))
                return;
            if (_installing || _restoring)
                throw new InvalidOperationException("Browser.HostServices.ReentrantTeardown: provider installation or restoration is active.");
            EnsureRestorable();
            _restoring = true;
            try
            {
                Restore();
                _active = null;
                _installed = false;
            }
            finally
            {
                _restoring = false;
            }
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Browser.HostServices.CallerThreadRequired: browser service composition must use its owning event thread.");
    }

    private void EnsureRestorable()
    {
        if (Foreign(DirectSlot.Windows, RuntimeWindowApplicationServices.Current, _windows) ||
            Foreign(DirectSlot.VrRendering, RuntimeVrRenderingServices.Current, _vrRendering) ||
            Foreign(DirectSlot.VrInput, RuntimeVrInputServices.Current, _vrInput) ||
            Foreign(DirectSlot.VrState, RuntimeVrStateServices.Current, _vrState) ||
            Foreign(DirectSlot.Video, RuntimeVideoStreamingServices.Current, _video) ||
            Foreign(DirectSlot.Discovery, RuntimeNetworkDiscoveryHostServices.Current, _discovery) ||
            Foreign(DirectSlot.Clipboard, RuntimeClipboardServices.Current, _clipboard) ||
            Foreign(DirectSlot.OpenVrState, RuntimeOpenVrStateServices.Current, _openVrState) ||
            Foreign(DirectSlot.OpenVrCompositor, RuntimeOpenVrCompositorServices.Current, _openVrCompositor) ||
            Foreign(DirectSlot.VrLifecycle, RuntimeEngine.VRState.LifecycleServices, _vrLifecycle))
            throw new InvalidOperationException(
                "Browser.HostServices.ForeignProviderActive: retire the later host-service override before stopping the browser session.");
    }

    private bool Foreign<T>(DirectSlot slot, T? current, T installed) where T : class
        => (_pending & slot) != 0 && !ReferenceEquals(current, installed);

    private void Restore()
    {
        List<Exception>? failures = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        void RestoreSlot(DirectSlot slot, Action restore)
        {
            if ((_pending & slot) == 0)
                return;
            Attempt(() =>
            {
                restore();
                _pending &= ~slot;
            });
        }

        RestoreSlot(DirectSlot.VrLifecycle,
            () => RuntimeEngine.VRState.LifecycleServices = _previousVrLifecycle);
        Attempt(() => { _modelSceneLoading?.Dispose(); _modelSceneLoading = null; });
        Attempt(() => { _sceneImport?.Dispose(); _sceneImport = null; });
        Attempt(() => { _thirdPartyAssets?.Dispose(); _thirdPartyAssets = null; });
        RestoreSlot(DirectSlot.OpenVrCompositor,
            () => RuntimeOpenVrCompositorServices.Current = _previousOpenVrCompositor);
        RestoreSlot(DirectSlot.OpenVrState,
            () => RuntimeOpenVrStateServices.Current = _previousOpenVrState);
        RestoreSlot(DirectSlot.Clipboard,
            () => RuntimeClipboardServices.Current = _previousClipboard);
        RestoreSlot(DirectSlot.Discovery,
            () => RuntimeNetworkDiscoveryHostServices.Current = _previousDiscovery);
        RestoreSlot(DirectSlot.Video,
            () => RuntimeVideoStreamingServices.Current = _previousVideo);
        RestoreSlot(DirectSlot.VrState,
            () => RuntimeVrStateServices.Current = _previousVrState);
        RestoreSlot(DirectSlot.VrInput,
            () => RuntimeVrInputServices.Current = _previousVrInput);
        RestoreSlot(DirectSlot.VrRendering,
            () => RuntimeVrRenderingServices.Current = _previousVrRendering);
        RestoreSlot(DirectSlot.Windows,
            () => RuntimeWindowApplicationServices.Current = _previousWindows);

        if (failures is [Exception failure])
            throw failure;
        if (failures is { Count: > 1 })
            throw new AggregateException("Browser host-service restoration failed.", failures);
    }

    private static NotSupportedException Unsupported(string operation)
        => new($"Browser.{operation}.Unsupported: this host operation has no browser implementation.");

    private sealed class BrowserWindowServices : IRuntimeWindowApplicationServices
    {
        public bool IsRunning => false;
        public WindowMailboxDiagnostics Diagnostics => default;
        public bool TryStartForStartupWindows(IReadOnlyList<WindowStartupValues> windows)
            => windows.Count == 0 ? false : throw Unsupported("NativeWindow.Start");
        public bool ShouldCreateWindowOnHost(WindowStartupValues window) => throw Unsupported("NativeWindow.Create");
        public XRWindow CreateWindow(Func<XRWindow> factory, string reason) => throw Unsupported("NativeWindow.Create");
        public void UnregisterWindow(XRWindow window) { }
        public void EnqueueWindowTask(IRuntimeRenderWindowHost window, Action task, string reason)
            => throw Unsupported("NativeWindow.EnqueueTask");
        public T InvokeWindowTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason)
            => throw Unsupported("NativeWindow.InvokeTask");
        public void Stop() { }
    }

    private sealed class BrowserVrRenderingServices : IRuntimeVrRenderingServices
    {
        public IRuntimeVrRenderModelProvider RenderModelProvider { get; } = new BrowserVrModels();
        public IRuntimeVrEyeCamera CreateEyeCamera(TransformBase transform, bool leftEye, float nearPlane, float farPlane)
            => throw Unsupported("VrRendering.CreateEyeCamera");
        public void SetHeadsetViewInformation(IRuntimeVrEyeCamera? leftEyeCamera, IRuntimeVrEyeCamera? rightEyeCamera,
            IRuntimeWorldContext? world, SceneNode? hmdNode)
        {
            if (leftEyeCamera is not null || rightEyeCamera is not null || world is not null || hmdNode is not null)
                throw Unsupported("VrRendering.SetHeadsetViewInformation");
        }
        public bool TryEnsureHeadsetViewInformation(IRuntimeWorldContext? world, SceneNode? hmdNode, float nearPlane, float farPlane)
            => false;
        public IRuntimeVrRenderModelHandle CreateRenderModelHandle(SceneNode node, string? childName = null)
            => throw Unsupported("VrRendering.CreateRenderModelHandle");
    }

    private sealed class BrowserVrModels : IRuntimeVrRenderModelProvider
    {
        public event Action? ModelsChanged { add { } remove { } }
        public bool TryGetControllerRenderModel(bool leftHand,
            [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        { renderModel = null; return false; }
        public bool TryGetTrackerRenderModel(string? openXrTrackerUserPath, uint? openVrDeviceIndex,
            [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
        { renderModel = null; return false; }
        public string DescribeAvailability() => "Browser.VrRendering.Unsupported: no browser VR render-model provider is installed.";
    }

    private sealed class BrowserVideoServices : IRuntimeVideoStreamingServices
    {
        public IVideoFrameGpuActions CreateVideoFrameGpuActions(object renderer)
            => throw Unsupported("VideoFrameGpuActions.Create");
    }

    private sealed class BrowserNetworkDiscoveryServices : IRuntimeNetworkDiscoveryHostServices
    {
        public object? ConfigureNetworking(NetworkDiscoveryConnectionSettings settings)
            => throw Unsupported("LanDiscovery.ConfigureNetworking");
    }

    private sealed class BrowserClipboardServices : IRuntimeClipboardServices
    {
        public string? GetText() => throw Unsupported("Clipboard.GetText");
        public void SetText(string text) => throw Unsupported("Clipboard.SetText");
    }

    private sealed class BrowserOpenVrStateProvider : IRuntimeOpenVrStateProvider
    {
        public float RealWorldIpd => 0;
        public bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane,
            out System.Numerics.Matrix4x4 projection)
        { projection = System.Numerics.Matrix4x4.Identity; return false; }
    }

    private sealed class BrowserOpenVrCompositor : IRuntimeOpenVrCompositor
    {
        public RuntimeOpenVrSubmitResult SubmitEyes(nint leftEyeHandle, nint rightEyeHandle,
            RuntimeOpenVrTextureType textureType, RuntimeOpenVrColorSpace colorSpace,
            RuntimeOpenVrSubmitFlags flags) => throw Unsupported("OpenVrCompositor.SubmitEyes");
    }

    private sealed class BrowserThirdPartyAssetServices : IRuntimeThirdPartyAssetLoadingServices
    {
        public XRAsset? Load(string filePath, string extension, Type assetType, object? importOptions = null,
            AssetImportContext? importContext = null, XRAsset? targetAsset = null)
            => throw Unsupported("ThirdPartyAsset.Load");
    }

    private sealed class BrowserSceneImportServices : IRuntimeSceneImportServices
    {
        public IReadOnlyList<SceneNode> ImportScene(string filePath) => throw Unsupported("SceneImport.ImportScene");
    }

    private sealed class BrowserModelSceneLoadingServices : IRuntimeModelSceneLoadingServices
    {
        public Task<SceneNode?> LoadAsync(string sourcePath, SceneNode parent,
            CancellationToken cancellationToken = default)
            => Task.FromException<SceneNode?>(Unsupported("ModelScene.LoadAsync"));
    }
}
