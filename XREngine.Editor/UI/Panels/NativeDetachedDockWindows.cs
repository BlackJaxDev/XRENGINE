using System.Numerics;
using System.Text.Json;
using XREngine.Components;
using XREngine.Editor.Settings;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Creates an isolated input/canvas host for a detached native tool panel.</summary>
internal static class NativeDetachedDockWindows
{
    private sealed record SavedWindow(string Key, int Panel);
    private sealed record OpenWindow(XRWindow Window, RuntimeWorld World, SceneNode Root,
        LocalPlayerController Controller, string Key, int Panel);

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
            foreach (SavedWindow saved in ReadSavedWindows())
                if (!OpenWindows.ContainsKey(saved.Key))
                    OpenCore(source, saved.Panel, saved.Key, restoring: true);
        }, "Restore detached native dock windows");
    }

    public static bool Open(XRWindow source, int panel)
        => OpenCore(source, panel, "detached:" + Guid.NewGuid().ToString("N"), restoring: false);

    private static bool OpenCore(XRWindow source, int panel, string key, bool restoring)
    {
        if (panel is < 0 or > 3 || source.TargetWorldInstance?.WorldContext is not RuntimeWorld sourceWorld ||
            sourceWorld.TargetWorld is not { } authoredWorld)
        {
            Debug.UI("[NativeDock] Cannot detach a panel without a source window and authored world.");
            return false;
        }

        XRWindow? window = null;
        LocalPlayerController? controller = null;
        SceneNode? root = null;
        RuntimeWorld? world = null;
        try
        {
            window = Engine.CreateWindow(new GameWindowStartupSettings
            {
                WindowTitle = $"Native {PanelName(panel)}",
                Width = 960,
                Height = 680,
                X = 160 + OpenWindows.Count * 28,
                Y = 120 + OpenWindows.Count * 28,
                WindowState = EWindowState.Windowed,
                LocalPlayers = 0,
                TargetWorld = authoredWorld,
                VSync = Engine.EffectiveSettings.VSync != EVSyncMode.Off,
            });
            world = window.TargetWorldInstance?.WorldContext as RuntimeWorld
                ?? throw new InvalidOperationException("The detached window has no runtime world.");

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
            canvas.CanvasTransform.SetSize(new Vector2(960, 680));
            PawnComponent pawn = root.AddComponent<PawnComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            input.OwningPawn = pawn;
            controller.ControlledPawn = pawn;
            viewport.ScreenSpaceUserInterfaceOverride = canvas;
            CameraComponent? camera = source.Viewports.FirstOrDefault()?.CameraComponent;
            if (camera is not null)
                pawn.CameraComponent = camera;

            SceneNode content = root.NewChildWithTransform<UIBoundableTransform>(out var bounds, "DockContent");
            bounds.MinAnchor = Vector2.Zero;
            bounds.MaxAnchor = Vector2.One;
            EditorPanelHost.BuildDetached(content, window, key, panel);
            EditorWorldIntegrationRegistry.GetOrAttach(world).AddToEditorScene(root);
            if (root.IsActiveSelf)
                root.OnActivated();

            OpenWindows.Add(key, new OpenWindow(window, world, root, controller, key, panel));
            window.FramebufferResized += (resizedWindow, size) =>
                _ = Engine.InvokeOnMainThread(() =>
                    canvas.CanvasTransform.SetSize(new Vector2(Math.Max(1, size.X), Math.Max(1, size.Y))),
                    "Native dock window resized");
            window.ClosingRequested += closedWindow =>
                _ = Engine.InvokeOnMainThread(() => Close(key), "Native dock window closed");
            if (!restoring)
                SaveWindow(key, panel);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"[NativeDock] Failed to detach {PanelName(panel)}.");
            if (root is not null && world is not null)
                EditorWorldIntegrationRegistry.GetOrAttach(world).RemoveFromEditorScene(root);
            controller?.Destroy();
            window?.RequestClose();
            return false;
        }
    }

    private static void Close(string key)
    {
        if (!OpenWindows.Remove(key, out OpenWindow? entry))
            return;
        entry.Root.IsActiveSelf = false;
        if (EditorWorldIntegrationRegistry.TryGet(entry.World, out EditorWorldIntegration? integration))
            integration?.RemoveFromEditorScene(entry.Root);
        entry.Controller.Destroy();
        if (!Engine.ShuttingDown)
            ForgetWindow(key);
    }

    private static List<SavedWindow> ReadSavedWindows()
    {
        string json = EditorPreferencesService.Current.GlobalPreferences.NativeDetachedWindowsJson;
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<SavedWindow>>(json)?.Where(saved =>
                saved.Key.StartsWith("detached:", StringComparison.Ordinal) && saved.Panel is >= 0 and < 4).ToList() ?? [];
        }
        catch (JsonException exception)
        {
            Debug.UI($"[NativeDock] Saved windows are invalid: {exception.Message}");
            return [];
        }
    }

    private static void SaveWindow(string key, int panel)
    {
        List<SavedWindow> saved = ReadSavedWindows();
        saved.RemoveAll(entry => entry.Key == key);
        saved.Add(new SavedWindow(key, panel));
        WriteSavedWindows(saved);
    }

    private static void ForgetWindow(string key)
    {
        List<SavedWindow> saved = ReadSavedWindows();
        if (saved.RemoveAll(entry => entry.Key == key) > 0)
            WriteSavedWindows(saved);
    }

    private static void WriteSavedWindows(List<SavedWindow> saved)
    {
        EditorPreferencesService service = EditorPreferencesService.Current;
        service.GlobalPreferences.NativeDetachedWindowsJson = JsonSerializer.Serialize(saved);
        service.SaveGlobalPreferences();
    }

    private static string PanelName(int panel) => panel switch
    {
        0 => "Assets",
        1 => "Console",
        2 => "Profiler",
        3 => "Animation",
        _ => "Panel",
    };
}
