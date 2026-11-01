using XREngine.Components;
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
    private static readonly List<CutsceneCompileDiagnostic> _lastFailure = [];

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
        Engine.PlayMode.PreEnterPlay += Close;
    }

    /// <summary>Creates a binding resolver from the root nodes of a world and the asset manager of the engine.</summary>
    /// <param name="world">The world to search.</param>
    /// <returns>The resolver.</returns>
    public static ICutsceneBindingResolver CreateResolver(RuntimeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new CutsceneSceneBindingResolver(Engine.Assets, world.RootNodes, world);
    }

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

        CutscenePreviewHost host = new(CreateResolver(world), FindSceneViewport(world));
        host.Begin(asset);
        _host = host;
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
        else
            DirectorPlayer?.Tick(0.0);
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

    /// <summary>Tells the preview that the asset changed. Any thread can call this method.</summary>
    public static void NotifyAssetEdited()
        => _host?.NotifyAssetEdited();

    /// <summary>Copies the current problems to a list. After a failure the list holds the reason.</summary>
    /// <param name="destination">The list that receives the problems. The method does not clear it.</param>
    public static void CopyDiagnostics(List<CutsceneCompileDiagnostic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
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
            return;
        }

        destination.AddRange(_lastFailure);
    }

    private static ICutsceneBindingResolver? CreateResolverForDirector(CutsceneDirectorComponent director)
        => director.World is RuntimeWorld world ? CreateResolver(world) : null;

    private static void OnUpdateFrame()
    {
        CutscenePreviewHost? host = _host;
        if (host is null)
            return;

        // An exception in a timer callback stops the update loop, so a failed preview closes here instead.
        try
        {
            host.Tick(Engine.UndilatedDelta);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "The cutscene preview failed and closed.");
            _lastFailure.Clear();
            _lastFailure.Add(new CutsceneCompileDiagnostic(-1, -1, $"The preview failed and closed: {ex.Message}"));
            Close();
        }
    }

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
