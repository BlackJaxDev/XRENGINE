using System.Linq;
using XREngine;
using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Editor.HotReload;

namespace XREngine.Editor;

internal static class EditorOpenXrPawnSwitcher
{
    private static bool _initialized;
    private static bool _configured;
    private static bool _startsOnLaunch;
    private static bool _requested;
    private static volatile bool _preparing;
    private static string? _preparedRuntimeJson;
    private static AbstractRenderer? _preparedRenderer;
    private static IRuntimeRenderWorld? _desktopPawnWorld;
    private static PawnComponent? _desktopPawn;
    private static PawnComponent? _vrPawn;
    private static SceneNode? _ownedVrRoot;
    private static IRuntimeRenderWorld? _vrPawnWorld;
    private static EditorWorldIntegration? _vrEditorIntegration;

    public static bool IsRequested => _requested;
    public static Guid? OwnedVrRigNodeId => _ownedVrRoot?.ID;
    public static bool OwnedVrRigIsInEditorScene => _ownedVrRoot is { } root &&
        _vrEditorIntegration?.IsInEditorScene(root) == true;
    public static string? LastError { get; private set; }

    public static bool CanToggle
    {
        get
        {
            if (!_configured || _preparing)
                return false;

            OpenXRAPI? api = RuntimeEngine.VRState.OpenXRApi;
            if (_requested && _startsOnLaunch && api is null)
                return false;

            return _requested || api is null || api.RuntimeState == OpenXRAPI.OpenXrRuntimeState.DesktopOnly;
        }
    }

    public static string Status
    {
        get
        {
            if (!_configured)
                return "Enable VR.AllowDesktopEditing in Unit Testing settings.";

            if (_preparing)
                return "Preparing selected VR runtime";

            OpenXRAPI? api = RuntimeEngine.VRState.OpenXRApi;
            if (api is null)
                return _requested ? "OpenXR startup pending" : "Desktop";

            return api.RuntimeState switch
            {
                OpenXRAPI.OpenXrRuntimeState.SessionRunning => _requested ? "VR session running" : "Stopping VR",
                OpenXRAPI.OpenXrRuntimeState.SessionStopping or OpenXRAPI.OpenXrRuntimeState.SessionLost => "Stopping VR",
                OpenXRAPI.OpenXrRuntimeState.Unavailable => "OpenXR unavailable; see diagnostics",
                OpenXRAPI.OpenXrRuntimeState.DesktopOnly => _requested ? "Starting VR" : "Desktop",
                _ => _requested ? "Starting VR" : "Stopping VR",
            };
        }
    }

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        RuntimeEngine.VRState.OpenXRSessionRunningChanged += OnOpenXRSessionRunningChanged;
    }

    public static void Configure(UnitTestingWorldSettings settings)
    {
        _configured = settings.VR.AllowDesktopEditing && settings.VR.Mode != UnitTestingVrLaunchMode.OpenVR;
        _startsOnLaunch = settings.VR.Mode is UnitTestingVrLaunchMode.MonadoOpenXR or UnitTestingVrLaunchMode.OpenXR;
        _requested = _configured && _startsOnLaunch;
        _desktopPawnWorld = null;
        _desktopPawn = null;
        _preparedRuntimeJson = null;
        _preparedRenderer = null;
        LastError = null;
    }

    public static bool TrySetEnabled(bool enabled, EditorOpenXrRuntimeChoice? runtime = null)
    {
        LastError = null;
        if (!_configured)
        {
            LastError = "Enable VR.AllowDesktopEditing to switch between desktop and VR.";
            return false;
        }

        if (_preparing)
        {
            LastError = "Wait for the selected VR runtime to finish preparing.";
            return false;
        }

        if (enabled && ResolveWorld()?.WorldContext is not RuntimeWorld)
        {
            LastError = "Open a world before starting VR.";
            return false;
        }

        if (enabled)
        {
            if (runtime is null)
            {
                LastError = "Choose Monado for testing or SteamVR for a headset.";
                return false;
            }

            OpenXRAPI? api = RuntimeEngine.VRState.OpenXRApi;
            if (api is not null && api.RuntimeState != OpenXRAPI.OpenXrRuntimeState.DesktopOnly)
            {
                LastError = "Wait for OpenXR session teardown before starting it again.";
                return false;
            }

            _requested = true;
            _preparing = true;
            _ = StartSelectedRuntimeAsync(runtime.Value);
            return true;
        }
        else
        {
            if (!RuntimeEngine.VRState.StopOpenXR())
            {
                LastError = "No OpenXR runtime is available to stop.";
                return false;
            }
        }

        _requested = enabled;
        Engine.EnqueueUpdateThreadTask(SynchronizePawnControl);
        return true;
    }

    private static async Task StartSelectedRuntimeAsync(EditorOpenXrRuntimeChoice choice)
    {
        try
        {
            var prepared = await EditorOpenXrRuntimeSelection.PrepareAsync(
                choice, RuntimeBootstrapState.Settings, CancellationToken.None).ConfigureAwait(false);
            XRWindow window = RuntimeEngine.Windows.FirstOrDefault()
                ?? throw new InvalidOperationException("Open a window before starting VR.");

            if (!ReferenceEquals(_preparedRenderer, window.Renderer) ||
                !string.Equals(_preparedRuntimeJson, prepared.RuntimeJsonPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Environment.GetEnvironmentVariable("XR_RUNTIME_JSON"), prepared.RuntimeJsonPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Environment.GetEnvironmentVariable("XRE_UNIT_TEST_OPENXR_RUNTIME_JSON"), prepared.RuntimeJsonPath, StringComparison.OrdinalIgnoreCase))
            {
                var configuration = new EditorOpenXrRendererConfiguration(prepared);
                var result = await RendererHotReloadService.Current.RestartCurrentGenerationWithConfigurationAsync(
                    window.Renderer.BackendId, configuration, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (!result.Succeeded)
                    throw new InvalidOperationException(result.Error ?? "Could not prepare the renderer for the selected runtime.");
                _preparedRuntimeJson = prepared.RuntimeJsonPath;
                _preparedRenderer = window.Renderer;
            }

            Engine.EnqueueUpdateThreadTask(() =>
            {
                try
                {
                    if (ResolveWorld() is { } world)
                        _ = ResolveDesktopPawn(world);
                    if (!RuntimeEngine.VRState.InitializeOpenXR(window))
                        throw new InvalidOperationException("OpenXR startup failed; inspect the runtime diagnostics.");
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _requested = false;
                }
                finally
                {
                    _preparing = false;
                }
            });
        }
        catch (Exception ex)
        {
            _preparedRuntimeJson = null;
            _preparedRenderer = null;
            LastError = ex.Message;
            _requested = false;
            _preparing = false;
            Debug.LogWarning($"Editor OpenXR runtime preparation failed: {ex.Message}");
        }
    }

    private static void OnOpenXRSessionRunningChanged(bool running)
    {
        if (!_configured || (running && !_requested))
            return;

        Engine.EnqueueUpdateThreadTask(SynchronizePawnControl);
    }

    /// <summary>Creates and retires temporary scene objects only on the update thread.</summary>
    private static void SynchronizePawnControl()
    {
        bool running = RuntimeEngine.VRState.OpenXRApi?.IsSessionRunning == true;
        // A stop request can precede the runtime's final eye frame. Keep its
        // cameras alive until the session has actually stopped rendering.
        if (running && !_requested)
            return;

        IRuntimeRenderWorld? world = ResolveWorld();
        if (world?.WorldContext is not RuntimeWorld runtimeWorld)
        {
            if (!running)
                DestroyOwnedVrPawn();
            return;
        }

        try
        {
            if (running)
            {
                _ = ResolveDesktopPawn(world);
                if (_vrPawn is null || !IsUsablePawn(_vrPawn, world) || !ReferenceEquals(_vrPawnWorld, world))
                {
                    DestroyOwnedVrPawn();
                    _vrPawnWorld = world;
                    _vrPawn = FindVrPawn(world);
                    if (_vrPawn is null)
                    {
                        _ownedVrRoot = new SceneNode { Name = "Editor OpenXR Rig" };
                        (_, _vrPawn) = BootstrapPawnFactory.CreateVrPawn(_ownedVrRoot);
                        _vrEditorIntegration = EditorWorldIntegrationRegistry.GetOrAttach(runtimeWorld);
                        _vrEditorIntegration.AddToEditorScene(_ownedVrRoot);
                    }
                }

                _vrPawn.PossessByLocalPlayer(ELocalPlayerIndex.One);
                return;
            }

            if (_vrPawn is null && _ownedVrRoot is null)
                return;

            PawnComponent? desktopPawn = ResolveDesktopPawn(world);
            if (desktopPawn is null)
            {
                SceneNode root = new() { Name = "Editor Desktop Rig" };
                try
                {
                    (_, desktopPawn) = BootstrapPawnFactory.CreateDesktopEditorPawn(root);
                    EditorWorldIntegrationRegistry.GetOrAttach(runtimeWorld).AddToEditorScene(root);
                }
                catch
                {
                    root.Destroy();
                    throw;
                }
                _desktopPawnWorld = world;
                _desktopPawn = desktopPawn;
            }

            desktopPawn.PossessByLocalPlayer(ELocalPlayerIndex.One);
            DestroyOwnedVrPawn();
        }
        catch (Exception ex)
        {
            LastError = $"Could not switch editor pawn: {ex.Message}";
            _requested = false;
            RuntimeEngine.VRState.StopOpenXR();
            Debug.LogWarning(LastError);
        }
    }

    private static void DestroyOwnedVrPawn()
    {
        if (_ownedVrRoot is { } root)
        {
            _vrEditorIntegration?.RemoveFromEditorScene(root);
            root.Destroy();
            _ownedVrRoot = null;
        }
        _vrPawn = null;
        _vrPawnWorld = null;
        _vrEditorIntegration = null;
    }

    private static IRuntimeRenderWorld? ResolveWorld()
        => (Engine.State.MainPlayer.Viewport as XRViewport)?.World
            ?? Engine.WorldInstances.FirstOrDefault()?.GetRenderWorld();

    private static PawnComponent? ResolveDesktopPawn(IRuntimeRenderWorld world)
    {
        if (Engine.State.MainPlayer.ControlledPawnComponent is PawnComponent current &&
            !ReferenceEquals(current, _vrPawn) && IsDesktopPawn(current, world))
        {
            _desktopPawnWorld = world;
            _desktopPawn = current;
            return current;
        }

        if (ReferenceEquals(_desktopPawnWorld, world) && _desktopPawn is { } cached && IsDesktopPawn(cached, world))
            return _desktopPawn;

        PawnComponent? found = FindDesktopPawn(world);
        if (found is not null)
        {
            _desktopPawnWorld = world;
            _desktopPawn = found;
        }

        return found;
    }

    private static PawnComponent? FindVrPawn(IRuntimeRenderWorld world)
    {
        foreach (var root in world.RootNodes)
        {
            foreach (var node in SceneNodePrefabUtility.EnumerateHierarchy(root))
            {
                if (node.GetComponent<VRPlayerInputSet>() is not null &&
                    node.GetComponent<PawnComponent>() is PawnComponent pawn && IsUsablePawn(pawn, world))
                {
                    return pawn;
                }
            }
        }

        return null;
    }

    private static bool IsUsablePawn(PawnComponent pawn, IRuntimeRenderWorld world)
        => !pawn.IsDestroyed && !pawn.IsDestroyQueued &&
            ReferenceEquals(pawn.World, world.WorldContext) && pawn.IsActiveInHierarchy;

    private static bool IsDesktopPawn(PawnComponent pawn, IRuntimeRenderWorld world)
        => IsUsablePawn(pawn, world) && pawn.SceneNode is { } node && node.GetComponent<VRPlayerInputSet>() is null;

    private static PawnComponent? FindDesktopPawn(IRuntimeRenderWorld world)
    {
        foreach (var root in world.RootNodes)
        {
            foreach (var node in SceneNodePrefabUtility.EnumerateHierarchy(root))
            {
                if (node.GetComponent<PawnComponent>() is PawnComponent pawn && IsDesktopPawn(pawn, world))
                    return pawn;
            }
        }

        return null;
    }
}
