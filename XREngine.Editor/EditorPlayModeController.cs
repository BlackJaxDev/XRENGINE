using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Editor-specific controller for play mode that handles:
/// - Undo system integration (suppress during play, clear after)
/// - Editor tool state (disable gizmos during play)
/// - Keyboard shortcuts (F5 to play, Shift+F5 to stop)
/// - Visual indicators for play mode
/// - Editor pawn possession tracking and restoration
/// 
/// NOTE: Snapshot capture/restore is handled by Engine.PlayMode.
/// This controller only tracks editor-specific state like pawn possessions.
/// </summary>
public static class EditorPlayModeController
{
    private static bool _initialized;
    private static readonly Dictionary<LocalInputInterface, ShortcutHandlers> _shortcutHandlers = new(ReferenceEqualityComparer.Instance);

    private sealed record PlayerPossessionSnapshot(
        Type ControllerType,
        Guid? PawnId,
        Guid? PawnNodeId,
        Type? PawnType,
        string? PawnName);

    /// <summary>
    /// Stores the editor possession state for each local player before entering play mode.
    /// Must survive snapshot restore (which can replace scene nodes/components).
    /// </summary>
    private static readonly Dictionary<ELocalPlayerIndex, PlayerPossessionSnapshot> _editorPossessionSnapshot = [];

    private record ShortcutHandlers(Action PlayPause, Action Stop, Action StepFrame);

    /// <summary>
    /// Initializes the editor play mode controller.
    /// Call this once during editor startup.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        // Subscribe to play mode events
        Engine.PlayMode.PreEnterPlay += OnPreEnterPlay;
        Engine.PlayMode.PostEnterPlay += OnPostEnterPlay;
        Engine.PlayMode.PreExitPlay += OnPreExitPlay;
        Engine.PlayMode.PostExitPlay += OnPostExitPlay;
        Engine.PlayMode.PostSnapshotRestore += OnPostSnapshotRestore;
        Engine.PlayMode.Paused += OnPaused;
        Engine.PlayMode.Resumed += OnResumed;

        // Register keyboard shortcuts
        LocalInputInterface.GlobalRegisters.Add(RegisterPlayModeInput);

        _initialized = true;
        Debug.Out("EditorPlayModeController initialized");
    }

    /// <summary>
    /// Shuts down the editor play mode controller.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized)
            return;

        Engine.PlayMode.PreEnterPlay -= OnPreEnterPlay;
        Engine.PlayMode.PostEnterPlay -= OnPostEnterPlay;
        Engine.PlayMode.PreExitPlay -= OnPreExitPlay;
        Engine.PlayMode.PostExitPlay -= OnPostExitPlay;
        Engine.PlayMode.PostSnapshotRestore -= OnPostSnapshotRestore;
        Engine.PlayMode.Paused -= OnPaused;
        Engine.PlayMode.Resumed -= OnResumed;

        _initialized = false;
    }

    #region Event Handlers

    private static void OnPreEnterPlay()
    {
        Debug.Out("EditorPlayModeController: PreEnterPlay");

        // Disable undo recording during play
        // Undo.SuppressRecording(); // TODO: Implement in Undo class

        // Save current editor pawn possessions for restoration when exiting play mode.
        // This is saved BEFORE any snapshot operations so we have the original editor pawn references.
        SaveEditorPawnSnapshot();

        // Clear selection to avoid editing during play
        Selection.Clear();

        // Disable transform gizmos
        // TransformTool.Disable(); // TODO: Implement

        // Enable input
        Engine.Input.SetUIInputCaptured(false);

        // Notify UI to update
        PlayModeUIChanged?.Invoke(true);
    }

    private static void OnPostEnterPlay()
    {
        Debug.Out("EditorPlayModeController: PostEnterPlay - Now in play mode");
    }

    private static void OnPreExitPlay()
    {
        Debug.Out("EditorPlayModeController: PreExitPlay");
    }

    private static void OnPostExitPlay()
    {
        Debug.Out("EditorPlayModeController: PostExitPlay");

        // Re-enable undo recording
        // Undo.ResumeRecording(); // TODO: Implement in Undo class

        // NOTE: World state restore is already handled by Engine.PlayMode.
        // We just need to restore editor-specific state here.

        // Re-enable transform gizmos
        // TransformTool.Enable(); // TODO: Implement

        // Clear undo history from play session
        Undo.ClearHistory();

        // Restore editor pawn possessions.
        // Prefer restoring during PostSnapshotRestore when exiting play (so rendering/input rebinding
        // sees the correct editor pawn), but keep a fallback here for restoration modes that don't
        // invoke PostSnapshotRestore.
        if (_editorPossessionSnapshot.Count > 0)
            RestoreEditorPawnSnapshot();

        // Notify UI to update
        PlayModeUIChanged?.Invoke(false);
    }

    /// <summary>
    /// Called after Engine.PlayMode restores a snapshot (SerializeAndRestore mode).
    /// This is the right time to rebuild light caches after deserialization.
    /// </summary>
    private static void OnPostSnapshotRestore(XRWorld world)
    {
        Debug.Out($"EditorPlayModeController: PostSnapshotRestore for world {world?.Name ?? "<null>"} State={Engine.PlayMode.State}");

        // Rebuild light caches after deserialization
        if (world is not null && XRWorldInstance.WorldInstances.TryGetValue(world, out var instance))
            instance.Lights.RebuildCachesFromWorld();

        // Only rebind camera transforms when EXITING play mode, not when ENTERING.
        // PostSnapshotRestore fires both when entering (after deserializing the play-mode copy)
        // and when exiting (after restoring the edit-mode snapshot).
        if (Engine.PlayMode.State != EPlayModeState.ExitingPlay)
        {
            Debug.Out($"[EditorPlayModeController] Skipping camera rebind (not ExitingPlay, State={Engine.PlayMode.State})");
            return;
        }

        // CRITICAL: After snapshot restore, XRCamera instances inside CameraComponents may have
        // stale Transform references. The Lazy<XRCamera> captures the Transform at creation time,
        // but deserialization may have replaced the scene node's transform with a new instance.
        // Rebind all camera transforms to ensure rendering uses the correct (restored) transforms.
        if (world is not null)
            RebindAllCameraTransforms(world);

        // CRITICAL: When exiting play, snapshot restore happens before PlayMode state flips back to Edit.
        // If we wait until PostExitPlay to restore the editor pawn possession, systems that rebind
        // runtime-only wiring during PostSnapshotRestore can still observe the (transient) play pawn.
        // Restore the editor pawn possession here so LocalPlayerController.ControlledPawn is correct
        // for subsequent edit-mode rebinding.
        if (Engine.IsEditor && _editorPossessionSnapshot.Count > 0)
        {
            Debug.Out("[EditorPlayModeController] ExitingPlay PostSnapshotRestore: restoring editor pawn possession now.");
            RestoreEditorPawnSnapshot();
        }
    }

    /// <summary>
    /// Rebinds XRCamera.Transform to CameraComponent.Transform for all camera components in the world.
    /// This fixes stale transform references that can occur after snapshot restore.
    /// </summary>
    private static void RebindAllCameraTransforms(XRWorld world)
    {
        int reboundCount = 0;
        foreach (var scene in world.Scenes)
        {
            if (scene.RootNodes is null)
                continue;

            foreach (var root in scene.RootNodes)
            {
                RebindCameraTransformsRecursive(root, ref reboundCount);
            }
        }

        if (reboundCount > 0)
            Debug.Out($"[EditorPlayModeController] Rebound {reboundCount} camera transform(s) after snapshot restore.");
    }

    private static void RebindCameraTransformsRecursive(SceneNode node, ref int reboundCount)
    {
        if (node is null)
            return;

        foreach (var cameraComponent in node.Components.OfType<CameraComponent>())
        {
            // Only rebind if the Lazy<XRCamera> has already been created
            // (accessing .Camera would create it unnecessarily)
            var xrCam = cameraComponent.Camera;
            var componentTransform = cameraComponent.Transform;

            if (xrCam is not null && componentTransform is not null && !ReferenceEquals(xrCam.Transform, componentTransform))
            {
                Debug.Out($"[EditorPlayModeController] Rebinding camera transform for '{cameraComponent.Name ?? cameraComponent.GetType().Name}': " +
                    $"OldTfm={xrCam.Transform?.GetHashCode().ToString() ?? "NULL"} NewTfm={componentTransform.GetHashCode()}");
                xrCam.Transform = componentTransform;
                componentTransform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
                reboundCount++;
            }
        }

        foreach (var child in node.Transform.Children)
        {
            var childNode = child.SceneNode;
            if (childNode is not null)
                RebindCameraTransformsRecursive(childNode, ref reboundCount);
        }
    }

    private static void OnPaused()
    {
        Debug.Out("EditorPlayModeController: Game paused");
        PlayModePausedChanged?.Invoke(true);
    }

    private static void OnResumed()
    {
        Debug.Out("EditorPlayModeController: Game resumed");
        PlayModePausedChanged?.Invoke(false);
    }

    #endregion

    #region Keyboard Shortcuts

    private static void RegisterPlayModeInput(InputInterface inputInterface)
    {
        if (inputInterface is not LocalInputInterface local)
            return;

        if (inputInterface.Unregister)
        {
            // Unregistration is handled automatically by the input system
            // when Unregister is true, just remove our handlers reference
            _shortcutHandlers.Remove(local);
            return;
        }

        if (_shortcutHandlers.ContainsKey(local))
            return;

        Action playPauseHandler = () => HandlePlayPauseShortcut(local);
        Action stopHandler = () => HandleStopShortcut(local);
        Action stepFrameHandler = () => HandleStepFrameShortcut(local);

        _shortcutHandlers[local] = new ShortcutHandlers(playPauseHandler, stopHandler, stepFrameHandler);

        // F5 = Play/Pause toggle, Shift+F5 = Stop
        local.RegisterKeyEvent(EKey.F5, EButtonInputType.Pressed, playPauseHandler);
        // F6 = Step frame (when paused)
        local.RegisterKeyEvent(EKey.F6, EButtonInputType.Pressed, stepFrameHandler);
    }

    private static void HandlePlayPauseShortcut(LocalInputInterface input)
    {
        bool shiftHeld = IsShiftDown(input);

        if (shiftHeld)
        {
            // Shift+F5 = Stop
            if (Engine.PlayMode.IsPlaying || Engine.PlayMode.IsPaused)
            {
                EditorState.ExitPlayMode();
            }
        }
        else
        {
            // F5 = Toggle play/pause
            if (Engine.PlayMode.IsEditing)
            {
                EditorState.EnterPlayMode();
            }
            else if (Engine.PlayMode.IsPlaying)
            {
                EditorState.Pause();
            }
            else if (Engine.PlayMode.IsPaused)
            {
                EditorState.Resume();
            }
        }
    }

    private static void HandleStopShortcut(LocalInputInterface input)
    {
        if (Engine.PlayMode.IsPlaying || Engine.PlayMode.IsPaused)
        {
            EditorState.ExitPlayMode();
        }
    }

    private static void HandleStepFrameShortcut(LocalInputInterface input)
    {
        if (Engine.PlayMode.IsPaused)
        {
            EditorState.StepFrame();
        }
    }

    private static bool IsShiftDown(LocalInputInterface input)
        => input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed);

    #endregion

    #region Helpers

    private static XRWorld? GetCurrentEditorWorld()
    {
        // Get the world currently being viewed in the editor
        // First try the first window's target world
        var window = Engine.Windows.FirstOrDefault();
        return window?.TargetWorldInstance?.TargetWorld;
    }

    /// <summary>
    /// Saves the current editor pawn possessions for all local players.
    /// Called before entering play mode to allow restoration when exiting.
    /// </summary>
    private static void SaveEditorPawnSnapshot()
    {
        _editorPossessionSnapshot.Clear();
        
        foreach (var player in Engine.State.LocalPlayers)
        {
            if (player is LocalPlayerController localPlayer)
            {
                var pawn = localPlayer.ControlledPawn;
                _editorPossessionSnapshot[localPlayer.LocalPlayerIndex] = new PlayerPossessionSnapshot(
                    localPlayer.GetType(),
                    pawn?.ID,
                    pawn?.SceneNode?.ID,
                    pawn?.GetType(),
                    pawn?.Name);

                Debug.Out($"Saved editor possession for player {localPlayer.LocalPlayerIndex}: Pawn={pawn?.Name ?? "null"} PawnId={pawn?.ID.ToString() ?? "null"}");
            }
        }
    }

    /// <summary>
    /// Restores the editor pawn possessions that were saved before entering play mode.
    /// Called after exiting play mode.
    /// </summary>
    private static void RestoreEditorPawnSnapshot()
    {
        foreach (var (playerIndex, snapshot) in _editorPossessionSnapshot)
        {
            var localPlayer = Engine.State.GetOrCreateLocalPlayer(playerIndex, snapshot.ControllerType);

            PawnComponent? resolvedPawn = null;

            // Best: find by stable object ID (snapshot restore should preserve IDs).
            if (snapshot.PawnId is Guid pawnId
                && XREngine.Data.Core.XRObjectBase.ObjectsCache.TryGetValue(pawnId, out var obj)
                && obj is PawnComponent pawnById
                && pawnById.SceneNode is not null
                && !pawnById.IsDestroyed)
            {
                resolvedPawn = pawnById;
            }

            // Fallback: locate by owning scene node ID + pawn type.
            if (resolvedPawn is null && snapshot.PawnNodeId is Guid nodeId
                && XREngine.Data.Core.XRObjectBase.ObjectsCache.TryGetValue(nodeId, out var nodeObj)
                && nodeObj is SceneNode node
                && !node.IsDestroyed)
            {
                if (snapshot.PawnType is Type pawnType)
                    resolvedPawn = node.Components.OfType<PawnComponent>().FirstOrDefault(p => pawnType.IsInstanceOfType(p));
                else
                    resolvedPawn = node.Components.OfType<PawnComponent>().FirstOrDefault();
            }

            // Last resort: attempt to match by name + type across the current world.
            if (resolvedPawn is null && snapshot.PawnType is Type pawnType2)
            {
                resolvedPawn = FindPawnInCurrentWorldByNameAndType(snapshot.PawnName, pawnType2);
            }

            if (resolvedPawn is null)
            {
                Debug.Out($"Failed to restore editor pawn for player {playerIndex}: PawnId={snapshot.PawnId?.ToString() ?? "null"} Name={snapshot.PawnName ?? "<null>"}");
                continue;
            }

            // CRITICAL FIX: After snapshot restore, the pawn's CameraComponent field can point to a stale
            // reference (old deserialized object) instead of the actual sibling CameraComponent that was
            // restored with the scene node. Force rebind to the sibling camera to ensure consistency.
            var siblingCamera = resolvedPawn.GetSiblingComponent<CameraComponent>();
            if (siblingCamera is not null && !ReferenceEquals(resolvedPawn.CameraComponent, siblingCamera))
            {
                Debug.Out($"[EditorPlayModeController] Rebinding pawn CameraComponent: Old={resolvedPawn.CameraComponent?.GetHashCode().ToString() ?? "NULL"} New={siblingCamera.GetHashCode()}");
                resolvedPawn.CameraComponent = siblingCamera;
            }

            // Reset any lingering play-mode possession state. This forces all the same notifications
            // that happen on startup (pawn possession happens before/after viewport registration).
            localPlayer.UnlinkControlledPawn();

            // Ensure the player is registered with a valid viewport on a live window.
            // NOTE: localPlayer.Viewport can be non-null but stale across snapshot restore; use the
            // defensive XRWindow helper to repair the player ↔ viewport linkage.
            var window = Engine.Windows.FirstOrDefault(w => w.Viewports.Any(vp => vp.AssociatedPlayer?.LocalPlayerIndex == playerIndex))
                ?? Engine.Windows.FirstOrDefault();
            if (window is null)
            {
                Debug.Out($"[EditorPlayModeController] Cannot restore editor pawn: no windows exist (player {playerIndex}).");
                continue;
            }

            var ensuredViewport = window.EnsureControllerRegistered(localPlayer, autoSizeAllViewports: false);
            if (ensuredViewport is null)
            {
                Debug.Out($"[EditorPlayModeController] Failed to ensure viewport registration for player {playerIndex}.");
                continue;
            }

            // IMPORTANT: Ensure input devices are connected BEFORE setting ControlledPawn.
            // When ControlledPawn is set, RegisterController() calls Input.TryRegisterInput() which
            // will fail if no devices are connected. UpdateDevices re-acquires devices from the window.
            localPlayer.Input.UpdateDevices(ensuredViewport.Window?.Input, Engine.VRState.Actions);
            Debug.Out($"[EditorPlayModeController] Updated input devices for player {playerIndex} before pawn possession");

            localPlayer.ControlledPawn = resolvedPawn;
            Debug.Out($"[EditorPlayModeController] Set ControlledPawn to {resolvedPawn.Name}, now calling RefreshViewportCamera");
            localPlayer.RefreshViewportCamera();
            Debug.Out($"[EditorPlayModeController] RefreshViewportCamera completed");

            // Enhanced debug logging for viewport/camera binding diagnosis
            var actualViewport = localPlayer.Viewport;
            var pawnCamera = resolvedPawn.GetCamera();
            var xrCam = pawnCamera?.Camera;
            
            // CRITICAL FIX: After snapshot restore, the XRCamera's Transform reference can become stale.
            // The camera was created with a reference to the original scene node transform, but 
            // deserialization may have replaced the scene node's transform with a new instance.
            // We must rebind the camera's transform to the CameraComponent's current transform.
            if (xrCam is not null && pawnCamera is not null)
            {
                var cameraTransform = xrCam.Transform;
                var componentTransform = pawnCamera.Transform;
                if (!ReferenceEquals(cameraTransform, componentTransform))
                {
                    Debug.Out($"[EditorPlayModeController] Camera transform mismatch detected! " +
                        $"CamTfm={cameraTransform.GetHashCode()} at {cameraTransform.WorldTranslation} vs " +
                        $"CompTfm={componentTransform.GetHashCode()} at {componentTransform.WorldTranslation}. Rebinding...");
#pragma warning disable CS8601 // Possible null reference assignment
                    xrCam.Transform = componentTransform;
#pragma warning restore CS8601 // Possible null reference assignment
                }

                // Now recalculate matrices with the correct transform
                var camTfm = xrCam.Transform;
                if (camTfm is not null)
                {
                    camTfm.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
                    Debug.Out($"[EditorPlayModeController] Forced matrix recalc for camera transform. WorldPos={camTfm.WorldTranslation} RenderPos={camTfm.RenderTranslation}");
                }
                
                Debug.Out($"Restored editor pawn for player {playerIndex}: {resolvedPawn.Name} " +
                    $"| Viewport={(actualViewport is null ? "NULL" : actualViewport.GetHashCode().ToString())} " +
                    $"| PawnCamera={(pawnCamera is null ? "NULL" : pawnCamera.Name ?? pawnCamera.GetHashCode().ToString())} " +
                    $"| VP.CameraComponent={(actualViewport?.CameraComponent is null ? "NULL" : actualViewport?.CameraComponent?.Name ?? actualViewport?.CameraComponent?.GetHashCode().ToString())} " +
                    $"| XRCam={xrCam.GetHashCode()} " +
                    $"| CamTfmHash={camTfm?.GetHashCode().ToString() ?? "NULL"}" +
                    $"| Camera.Viewports={xrCam.Viewports.Count}");
            }
            else
            {
                Debug.Out($"Restored editor pawn for player {playerIndex}: {resolvedPawn.Name} " +
                    $"| Viewport={(actualViewport is null ? "NULL" : actualViewport.GetHashCode().ToString())} " +
                    $"| PawnCamera={(pawnCamera is null ? "NULL" : pawnCamera.Name ?? pawnCamera.GetHashCode().ToString())} " +
                    $"| VP.CameraComponent={(actualViewport?.CameraComponent is null ? "NULL" : actualViewport?.CameraComponent?.Name ?? actualViewport?.CameraComponent?.GetHashCode().ToString())} " +
                    $"| XRCam=NULL | CamTfmHash=NULL");
            }
            
            // Final safety check: ensure the viewport is bound to the camera
            // This catches edge cases where SetField didn't detect changes
            actualViewport?.EnsureViewportBoundToCamera();
        }

        _editorPossessionSnapshot.Clear();
    }

    private static PawnComponent? FindPawnInCurrentWorldByNameAndType(string? pawnName, Type pawnType)
    {
        if (pawnType is null)
            return null;

        var world = GetCurrentEditorWorld();
        if (world is null)
            return null;

        // Search all scenes in the current editor world.
        foreach (var scene in world.Scenes)
        {
            if (scene.RootNodes is null)
                continue;

            foreach (var root in scene.RootNodes)
            {
                var found = FindPawnRecursive(root, pawnName, pawnType);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private static PawnComponent? FindPawnRecursive(SceneNode node, string? pawnName, Type pawnType)
    {
        if (node is null)
            return null;

        foreach (var pawn in node.Components.OfType<PawnComponent>())
        {
            if (!pawnType.IsInstanceOfType(pawn))
                continue;

            if (string.IsNullOrWhiteSpace(pawnName) || string.Equals(pawn.Name, pawnName, StringComparison.Ordinal))
                return pawn;
        }

        foreach (var child in node.Transform.Children)
        {
            var childNode = child.SceneNode;
            if (childNode is null)
                continue;

            var found = FindPawnRecursive(childNode, pawnName, pawnType);
            if (found is not null)
                return found;
        }

        return null;
    }

    #endregion

    #region Events

    /// <summary>
    /// Fired when play mode UI state should change.
    /// Parameter is true when entering play, false when exiting.
    /// </summary>
    public static event Action<bool>? PlayModeUIChanged;

    /// <summary>
    /// Fired when pause state changes during play mode.
    /// </summary>
    public static event Action<bool>? PlayModePausedChanged;

    #endregion
}
