using System.Numerics;
using System.Text.Json;
using XREngine.Components;
using XREngine.Editor.Settings;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Creates an isolated input/canvas host for a detached native tool panel.</summary>
internal static class NativeDetachedDockWindows
{
    private sealed record OpenWindow(XRWindow Window, RuntimeWorld World, SceneNode Root, SceneNode CameraRoot,
        LocalPlayerController Controller, string Key, string Panel)
    {
        internal NativeDockCloseApprovalGate CloseGate { get; } = new(Window);
    }

    private static readonly Dictionary<string, OpenWindow> OpenWindows = new(StringComparer.Ordinal);
    private static bool _restoreQueued;

    public static void Restore(XRWindow source)
    {
        if (_restoreQueued || ReadSavedWindows().Count == 0)
            return;
        _restoreQueued = true;
        _ = Engine.InvokeOnMainThread(() =>
        {
            _restoreQueued = false;
            foreach (NativeDockSavedWindow saved in ReadSavedWindows())
                if (!OpenWindows.ContainsKey(saved.Key) && ResolvePanel(saved) is { } panel)
                {
                    bool legacy = !NativeDockPanel.All.Contains(saved.PanelId ?? string.Empty) &&
                        saved.Panel is >= 0 and < 4;
                    if (legacy && NativeDockLayoutStore.HasLayout(saved.Key))
                        NativeDockLayoutStore.MigrateLegacyDetached(saved.Key, saved.Panel!.Value);
                    if (OpenCore(source, panel, saved.Key, restoring: true, saved) && legacy)
                        SaveWindow(saved.Key, panel);
                }
        }, "Restore detached native dock windows", executeNowIfAlreadyMainThread: true);
    }

    public static bool Open(XRWindow source, int panel)
        => (uint)panel < (uint)NativeDockPanel.LegacyTools.Length &&
           Open(source, NativeDockPanel.LegacyTools[panel]);

    public static bool Open(XRWindow source, string panel)
        => OpenCore(source, panel, "detached:" + Guid.NewGuid().ToString("N"), restoring: false);

    private static bool OpenCore(XRWindow source, string panel, string key, bool restoring,
        NativeDockSavedWindow? saved = null)
    {
        if (!NativeDockPanel.All.Contains(panel) || source.TargetWorldInstance?.WorldContext is not RuntimeWorld sourceWorld ||
            sourceWorld.TargetWorld is not { } authoredWorld)
        {
            Debug.UI("[NativeDock] Cannot detach a panel without a source window and authored world.");
            return false;
        }

        XRWindow? window = null;
        LocalPlayerController? controller = null;
        SceneNode? root = null;
        SceneNode? cameraRoot = null;
        RuntimeWorld? world = null;
        try
        {
            NativeDockWindowPlacement fallback = new(
                160 + OpenWindows.Count * 28, 120 + OpenWindows.Count * 28, 960, 680);
            ReadOnlySpan<RuntimeDesktopMonitor> monitors = source.DesktopWindowBackend is { } sourceBackend
                ? sourceBackend.Monitors.Span : ReadOnlySpan<RuntimeDesktopMonitor>.Empty;
            NativeDockWindowPlacement placement = NativeDockWindowPlacement.Resolve(
                saved?.X, saved?.Y, saved?.Width, saved?.Height,
                monitors, fallback);
            window = Engine.CreateWindow(new GameWindowStartupSettings
            {
                WindowTitle = $"Native {PanelName(panel)}",
                Width = placement.Width,
                Height = placement.Height,
                X = placement.X,
                Y = placement.Y,
                WindowState = EWindowState.Windowed,
                LocalPlayers = 0,
                TargetWorld = authoredWorld,
                VSync = Engine.EffectiveSettings.VSync != EVSyncMode.Off,
            });
            world = window.TargetWorldInstance?.WorldContext as RuntimeWorld
                ?? throw new InvalidOperationException("The detached window has no runtime world.");
            if (!ReferenceEquals(world, sourceWorld))
                throw new InvalidOperationException("The detached window did not reuse the source runtime world.");

            // This controller is not registered in Engine.State.LocalPlayers, so it cannot
            // steal the main editor pawn's viewport or device snapshot.
            controller = new LocalPlayerController(ELocalPlayerIndex.One);
            window.RegisterController(controller, autoSizeAllViewports: false);
            XRViewport viewport = controller.Viewport as XRViewport
                ?? throw new InvalidOperationException("The detached window has no viewport.");
            viewport.WorldInstanceOverride = window.TargetWorldInstance;
            viewport.AllowUIRender = true;
            window.UpdateViewportSizes();

            root = new SceneNode(world, $"NativeDock_{key}") { IsEditorOnly = true };
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            canvas.CanvasTransform.DrawSpace = ECanvasDrawSpace.Screen;
            canvas.CanvasTransform.SetSize(new Vector2(placement.Width, placement.Height));
            CameraComponent? sourceCamera = source.Viewports.FirstOrDefault()?.CameraComponent;
            Transform cameraTransform = sourceCamera is null
                ? new Transform()
                : new Transform(sourceCamera.Transform.WorldTranslation, sourceCamera.Transform.WorldRotation);
            cameraRoot = new SceneNode(world, $"NativeDockCamera_{key}", cameraTransform) { IsEditorOnly = true };
            CameraComponent detachedCamera = cameraRoot.AddComponent<CameraComponent>()!;
            if (sourceCamera is not null)
            {
                detachedCamera.CameraParameters = sourceCamera.CameraParameters.CreateFromPrevious(sourceCamera.CameraParameters);
                if (sourceCamera.Camera.TryGetAssignedRenderPipeline(out RenderPipeline? pipeline) && pipeline is not null)
                    detachedCamera.Camera.RenderPipeline = pipeline;
            }
            EditorFlyingCameraPawnComponent pawn = cameraRoot.AddComponent(
                static () => new EditorFlyingCameraPawnComponent { OwnsGlobalSelectionGizmo = false })!;
            pawn.CameraComponent = detachedCamera;
            if (sourceCamera is not null)
                pawn.InitializeDetachedCameraPose(sourceCamera.Transform.WorldRotation);
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            input.OwningPawn = pawn;
            controller.ControlledPawn = pawn;
            viewport.CameraComponent = detachedCamera;
            viewport.ScreenSpaceUserInterfaceOverride = canvas;

            SceneNode content = root.NewChildWithTransform<UIBoundableTransform>(out var bounds, "DockContent");
            bounds.MinAnchor = Vector2.Zero;
            bounds.MaxAnchor = Vector2.One;
            NativeDockTreeHost.BuildDetached(content, world, window, key, panel, restoring);
            EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
            integration.AddToEditorScene(cameraRoot);
            integration.AddToEditorScene(root);
            if (cameraRoot.IsActiveSelf)
                cameraRoot.OnActivated();
            if (root.IsActiveSelf)
                root.OnActivated();

            OpenWindow open = new(window, world, root, cameraRoot, controller, key, panel);
            OpenWindows.Add(key, open);
            window.FramebufferResized += (resizedWindow, size) =>
                _ = Engine.InvokeOnMainThread(() =>
                {
                    if (!OpenWindows.TryGetValue(key, out OpenWindow? current) ||
                        !ReferenceEquals(current, open) ||
                        !ReferenceEquals(current.Window, resizedWindow) ||
                        current.Root.IsDestroyed || !current.Root.IsActiveInHierarchy)
                        return;
                    canvas.CanvasTransform.SetSize(
                        new Vector2(Math.Max(1, size.X), Math.Max(1, size.Y)));
                },
                    "Native dock window resized", executeNowIfAlreadyMainThread: true);
            window.CloseApproved += (closedWindow, approval) =>
            {
                if (!ReferenceEquals(open.Window, closedWindow))
                    return;
                NativeDockWindowPlacement? approvedPlacement = null;
                try
                {
                    IRuntimeWindowBackend? backend = closedWindow.DesktopWindowBackend;
                    if (backend is not null)
                    {
                        // This callback runs on the native owner thread, before the backend retires.
                        WindowSurfaceSnapshot surface = backend.Surface;
                        if (surface.HasValidClientExtent)
                        {
                            var position = backend.Position;
                            approvedPlacement = new NativeDockWindowPlacement(position.X, position.Y,
                                surface.ClientWidth, surface.ClientHeight);
                        }
                    }
                }
                catch (Exception error)
                {
                    Debug.LogException(error, "[NativeDock] Could not capture detached window bounds.");
                }
                if (!open.CloseGate.Register(closedWindow, approval))
                    return;
                _ = Engine.InvokeOnMainThread(
                    () => Close(key, open, closedWindow, approval, approvedPlacement),
                    "Native dock window closed", executeNowIfAlreadyMainThread: true);
            };
            if (!restoring)
                SaveWindow(key, panel, placement);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"[NativeDock] Failed to detach {PanelName(panel)}.");
            if (world is not null)
            {
                EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
                if (root is not null)
                    integration.RemoveFromEditorScene(root);
                if (cameraRoot is not null)
                    integration.RemoveFromEditorScene(cameraRoot);
            }
            root?.Destroy(true);
            cameraRoot?.Destroy(true);
            controller?.Destroy();
            window?.RequestClose();
            return false;
        }
    }

    private static void Close(string key, OpenWindow approvedEntry, XRWindow closedWindow,
        WindowCloseApproval approval, NativeDockWindowPlacement? placement)
    {
        if (!OpenWindows.TryGetValue(key, out OpenWindow? entry) ||
            !ReferenceEquals(entry, approvedEntry) ||
            !ReferenceEquals(entry.Window, closedWindow) ||
            !entry.CloseGate.TryConsume(closedWindow, approval) ||
            !OpenWindows.Remove(key))
            return;
        List<Exception>? failures = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        Attempt(() => entry.Root.IsActiveSelf = false);
        Attempt(() => entry.CameraRoot.IsActiveSelf = false);
        EditorWorldIntegration? integration = null;
        Attempt(() => { EditorWorldIntegrationRegistry.TryGet(entry.World, out integration); });
        if (integration is not null)
        {
            Attempt(() => integration.RemoveFromEditorScene(entry.Root));
            Attempt(() => integration.RemoveFromEditorScene(entry.CameraRoot));
        }
        Attempt(() => entry.Controller.Destroy());
        Attempt(() => entry.Root.Destroy(true));
        Attempt(() => entry.CameraRoot.Destroy(true));
        if (approval.ApplicationShutdownIntent)
            Attempt(() => SaveWindow(key, entry.Panel, placement));
        else
            Attempt(() => ForgetWindow(key));
        if (failures is not null)
            throw new AggregateException("Detached dock window cleanup failed.", failures);
    }

    private static List<NativeDockSavedWindow> ReadSavedWindows()
    {
        string json = EditorPreferencesService.Current.GlobalPreferences.NativeDetachedWindowsJson;
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<NativeDockSavedWindow>>(json)?.Where(saved =>
                saved is not null && saved.Key?.StartsWith("detached:", StringComparison.Ordinal) == true &&
                ResolvePanel(saved) is not null).ToList() ?? [];
        }
        catch (JsonException exception)
        {
            Debug.UI($"[NativeDock] Saved windows are invalid: {exception.Message}");
            return [];
        }
    }

    private static string? ResolvePanel(NativeDockSavedWindow saved)
        => saved.PanelId is { } panelId && NativeDockPanel.All.Contains(panelId)
            ? panelId : saved.Panel is >= 0 and < 4
                ? NativeDockPanel.LegacyTools[saved.Panel.Value] : null;

    private static void SaveWindow(string key, string panel,
        NativeDockWindowPlacement? placement = null)
    {
        List<NativeDockSavedWindow> saved = ReadSavedWindows();
        NativeDockSavedWindow? previous = saved.Find(entry => entry.Key == key);
        saved.RemoveAll(entry => entry.Key == key);
        saved.Add(new NativeDockSavedWindow(key, PanelId: panel,
            X: placement?.X ?? previous?.X,
            Y: placement?.Y ?? previous?.Y,
            Width: placement?.Width ?? previous?.Width,
            Height: placement?.Height ?? previous?.Height));
        WriteSavedWindows(saved);
    }

    private static void ForgetWindow(string key)
    {
        List<NativeDockSavedWindow> saved = ReadSavedWindows();
        if (saved.RemoveAll(entry => entry.Key == key) > 0)
            WriteSavedWindows(saved);
    }

    private static void WriteSavedWindows(List<NativeDockSavedWindow> saved)
    {
        EditorPreferencesService service = EditorPreferencesService.Current;
        service.GlobalPreferences.NativeDetachedWindowsJson = JsonSerializer.Serialize(saved);
        service.SaveGlobalPreferences();
    }

    private static string PanelName(string panel)
        => panel.Length == 0 ? "Panel" : char.ToUpperInvariant(panel[0]) + panel[1..];
}
