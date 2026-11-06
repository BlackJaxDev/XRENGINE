using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// Owns the one cutscene that the editor controls. The sequencer panel and the MCP tools use the same session.
/// In edit mode the session runs a <see cref="CutscenePreviewHost"/>. In play mode it controls the player of a
/// <see cref="CutsceneDirectorComponent"/>, whose own tick moves the position.
/// The methods that change state must run on the update thread.
/// </summary>
public static class CutscenePreviewSession
{
    private static readonly Action _updateFrameHandler = OnUpdateFrame;
    private static readonly List<CutsceneCompileDiagnostic> _sessionDiagnostics = [];

    private static RuntimeWorld? _previewWorld;
    private static XRWorld? _previewTargetWorld;
    private static CutscenePreviewHost? _host;
    private static CutsceneDirectorComponent? _director;
    private static bool _updateHooked;
    private static bool _initialized;

    /// <summary>The active preview, or null if the session does not run a preview.</summary>
    public static CutscenePreviewHost? Host => _host;

    /// <summary>The director that the session controls in play mode, or null.</summary>
    public static CutsceneDirectorComponent? Director => _director;

    /// <summary>True when a preview or a director is open.</summary>
    public static bool IsOpen => _host is { IsActive: true } || _director is { IsOpen: true };

    /// <summary>The asset that is open, or null.</summary>
    public static CutsceneAsset? Asset => _host?.Asset ?? (_director is { IsOpen: true } director ? director.Asset : null);

    /// <summary>The current position.</summary>
    public static CutsceneTime Position => _host?.Position ?? DirectorPlayer?.Position ?? CutsceneTime.Zero;

    /// <summary>The transport state.</summary>
    public static ECutsceneTransportState State => _host?.State ?? DirectorPlayer?.State ?? ECutsceneTransportState.Stopped;

    /// <summary>The playback rate.</summary>
    public static double Rate => _host?.Rate ?? DirectorPlayer?.Rate ?? 1.0;

    /// <summary>The player of the preview or of the director, or null if nothing is open.</summary>
    public static CutscenePlayer? Player => _host?.Player ?? DirectorPlayer;

    /// <summary>The residency session of the preview or of the director, or null if residency does not run.</summary>
    public static CutsceneResidencySession? Residency => _host?.Residency ?? (_director is { IsOpen: true } director ? director.Residency : null);

    private static CutscenePlayer? DirectorPlayer => _director is { IsOpen: true } director ? director.Player : null;

    /// <summary>
    /// Registers the default binding resolver of directors, and ends a preview before play mode captures the world.
    /// A preview value in the capture would become part of the restored scene.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        CutsceneDirectorComponent.DefaultBindingResolverFactory = CreateResolverForDirector;
        CutsceneDirectorComponent.DefaultResidencyBackendFactory = static _ => CreateResidencyBackend();
        Engine.Assets.SaveGuard = GetSaveRejection;
        Engine.PlayMode.PreEnterPlay += Close;

        // Play can start without transitions, and that path does not raise PreEnterPlay. It does change the state.
        Engine.PlayMode.StateChanged += OnPlayModeStateChanged;
    }

    /// <summary>
    /// Gets the reason why a save is refused. While a preview is open, the live scene holds preview values,
    /// so a scene or a world must not be saved. A cutscene asset holds no preview values, so its save is allowed.
    /// </summary>
    /// <param name="asset">The asset to save.</param>
    /// <returns>The reason, or null if the save is allowed.</returns>
    public static string? GetSaveRejection(XRAsset asset)
        => _host is not null && asset is XRScene or XRWorld
            ? $"Cannot save '{asset.Name}' while a cutscene preview is open, because the scene holds preview values. End the preview, then save."
            : null;

    /// <summary>Creates a binding resolver from the root nodes of a world and the asset manager of the engine.</summary>
    /// <param name="world">The world to search.</param>
    /// <returns>The resolver.</returns>
    public static ICutsceneBindingResolver CreateResolver(RuntimeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new CutsceneSceneBindingResolver(Engine.Assets, world.RootNodes, world);
    }

    /// <summary>Creates the texture residency backend of the editor. It finds the textures in the asset manager of the engine.</summary>
    public static IResidencyBackend CreateResidencyBackend()
        => new TextureResidencyBackend(Engine.Assets);

    /// <summary>Finds the scene viewport of a world. The sequencer panel draws over this viewport.</summary>
    /// <param name="world">The world.</param>
    /// <returns>The first viewport of the window that shows the world, or null.</returns>
    public static XRViewport? FindSceneViewport(RuntimeWorld? world)
    {
        XRViewport? fallback = null;
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            if (window.Viewports.Count == 0)
                continue;

            XRViewport viewport = window.Viewports[0];
            if (window.TargetWorldInstance is IRuntimeRenderWorld renderWorld && ReferenceEquals(renderWorld.WorldContext, world))
                return viewport;

            fallback ??= viewport;
        }

        if (fallback is not null)
            AddSessionDiagnostic("No window shows the world of the cutscene. The cutscene camera shows on the viewport of another window.");
        return fallback;
    }

    /// <summary>Starts an edit-mode preview of an asset. An open cutscene closes first.</summary>
    /// <param name="asset">The cutscene to preview.</param>
    /// <param name="world">The world that holds the bound nodes.</param>
    public static void OpenPreview(CutsceneAsset asset, RuntimeWorld world)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(world);
        Close();
        _sessionDiagnostics.Clear();

        CutscenePreviewHost host = new(CreateResolver(world), FindSceneViewport(world), CreateResidencyBackend);
        host.Begin(asset);
        _host = host;
        _previewWorld = world;
        _previewTargetWorld = world.TargetWorld;
        HookUpdate();
    }

    /// <summary>
    /// Opens a director in play mode and shows its camera on the scene viewport. An open cutscene closes first.
    /// </summary>
    /// <param name="director">The director to open.</param>
    /// <returns>True if the director opened. If not, <see cref="CopyDiagnostics"/> gives the reason.</returns>
    public static bool OpenDirector(CutsceneDirectorComponent director)
    {
        ArgumentNullException.ThrowIfNull(director);
        Close();
        _sessionDiagnostics.Clear();

        director.TargetViewport ??= FindSceneViewport(director.World as RuntimeWorld);
        director.OpenAsync().GetAwaiter().GetResult();
        _director = director;
        return director.IsOpen;
    }

    /// <summary>Ends the preview and restores the scene, or closes the director. A call with nothing open does nothing.</summary>
    public static void Close()
    {
        UnhookUpdate();

        CutscenePreviewHost? host = _host;
        CutsceneDirectorComponent? director = _director;
        _host = null;
        _director = null;
        _previewWorld = null;
        _previewTargetWorld = null;
        host?.End();
        director?.Close();
    }

    /// <summary>Asks for a new position.</summary>
    /// <param name="target">The new position.</param>
    /// <param name="intent">Tells if more seeks are likely to follow.</param>
    public static void Seek(CutsceneTime target, ESeekIntent intent)
    {
        if (_host is { } host)
            host.Seek(target, intent);
        else
            DirectorPlayer?.Seek(target, intent);
    }

    /// <summary>Asks for a new position and evaluates it now, so that the result is visible to the caller.</summary>
    /// <param name="target">The new position.</param>
    /// <param name="intent">Tells if more seeks are likely to follow.</param>
    public static void SeekNow(CutsceneTime target, ESeekIntent intent)
    {
        Seek(target, intent);
        if (_host is { } host)
            host.Tick(0.0);
        else if (_director is { IsOpen: true } director)
            director.AdvanceFrame(0.0);
    }

    /// <summary>Starts or resumes playback at a rate.</summary>
    /// <param name="rate">The rate. A negative value plays backward.</param>
    public static void Play(double rate)
    {
        SetRate(rate);
        if (_host is { } host)
            host.Play();
        else
            DirectorPlayer?.Play();
    }

    /// <summary>Holds the position.</summary>
    public static void Pause()
    {
        if (_host is { } host)
            host.Pause();
        else
            DirectorPlayer?.Pause();
    }

    /// <summary>Sets the rate without a change to the transport state.</summary>
    /// <param name="rate">The rate. A negative value plays backward.</param>
    public static void SetRate(double rate)
    {
        if (_host is { } host)
            host.SetRate(rate);
        else
            DirectorPlayer?.SetRate(rate);
    }

    /// <summary>Starts a scrub.</summary>
    public static void BeginScrub()
    {
        if (_host is { } host)
            host.BeginScrub();
        else
            DirectorPlayer?.BeginScrub();
    }

    /// <summary>Ends a scrub.</summary>
    public static void EndScrub()
    {
        if (_host is { } host)
            host.EndScrub();
        else
            DirectorPlayer?.EndScrub();
    }

    /// <summary>Gets the node that a binding slot of the preview holds now.</summary>
    /// <param name="slot">The binding slot.</param>
    /// <returns>The node, or null if no preview is open or the slot has no live node.</returns>
    public static SceneNode? GetBoundNode(int slot)
        => _host?.GetBoundNode(slot);

    /// <summary>Tells the preview that the asset changed. Any thread can call this method.</summary>
    public static void NotifyAssetEdited()
        => _host?.NotifyAssetEdited();

    /// <summary>Copies the current problems to a list. After a failure the list holds the reason.</summary>
    /// <param name="destination">The list that receives the problems. The method does not clear it.</param>
    public static void CopyDiagnostics(List<CutsceneCompileDiagnostic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.AddRange(_sessionDiagnostics);
        if (_host is { } host)
        {
            host.CopyDiagnostics(destination);
            return;
        }

        if (_director is { } director)
        {
            destination.AddRange(director.Diagnostics);
            if (director.Player is { } player)
                destination.AddRange(player.Diagnostics);
        }
    }

    private static ICutsceneBindingResolver? CreateResolverForDirector(CutsceneDirectorComponent director)
        => director.World is RuntimeWorld world ? CreateResolver(world) : null;

    private static void OnUpdateFrame()
        => AdvanceFrame(Engine.UndilatedDelta);

    private static void OnPlayModeStateChanged(EPlayModeState state)
    {
        if (state is EPlayModeState.EnteringPlay or EPlayModeState.Play && _host is not null)
            Close();
    }

    /// <summary>Runs one frame of the preview. It closes a preview whose world is gone.</summary>
    /// <param name="deltaSeconds">The wall time since the last frame, in seconds.</param>
    internal static void AdvanceFrame(double deltaSeconds)
    {
        CutscenePreviewHost? host = _host;
        if (host is null)
            return;

        // An exception in a timer callback stops the update loop, so a failed preview closes here instead.
        // The world check is inside, because a window can close on another thread during the check.
        try
        {
            if (!IsPreviewWorldCurrent())
            {
                AddSessionDiagnostic("The world changed, so the cutscene preview closed.");
                CloseAfterFailure();
                return;
            }

            host.Tick(deltaSeconds);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "The cutscene preview failed and closed.");
            AddSessionDiagnostic($"The preview failed and closed: {ex.Message}");
            CloseAfterFailure();
        }
    }

    private static bool IsPreviewWorldCurrent()
    {
        RuntimeWorld? world = _previewWorld;
        if (world is null || !ReferenceEquals(world.TargetWorld, _previewTargetWorld))
            return false;

        // The runtime world can also leave the windows. A session without windows has nothing to compare.
        var windows = RuntimeEngine.Windows;
        for (int i = 0; i < windows.Count; i++)
            if (windows[i].TargetWorldInstance is IRuntimeRenderWorld renderWorld && ReferenceEquals(renderWorld.WorldContext, world))
                return true;
        return windows.Count == 0;
    }

    /// <summary>Closes the session from the update callback. A second failure here must not stop the update loop.</summary>
    private static void CloseAfterFailure()
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "The cutscene preview did not close cleanly.");
            AddSessionDiagnostic($"The preview did not close cleanly: {ex.Message}");
        }
    }

    private static void AddSessionDiagnostic(string message)
        => _sessionDiagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, message));

    private static void HookUpdate()
    {
        if (_updateHooked)
            return;
        _updateHooked = true;
        Engine.Time.Timer.UpdateFrame += _updateFrameHandler;
    }

    private static void UnhookUpdate()
    {
        if (!_updateHooked)
            return;
        _updateHooked = false;
        Engine.Time.Timer.UpdateFrame -= _updateFrameHandler;
    }
}
