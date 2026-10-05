namespace XREngine
{
    public static partial class Engine
    {
        public static partial class PlayMode
        {
            /// <summary>
            /// Auxiliary log that records failed play-mode transitions. Unlike the engine log,
            /// auxiliary logs are written by Release builds too.
            /// </summary>
            private const string TransitionFailureLogName = "playmode-transitions.log";

            /// <summary>
            /// Records a failed transition step in the engine log and in the play-mode transition log.
            /// </summary>
            private static void ReportTransitionFailure(string transition, string step, string detail)
            {
                string message = $"Play mode {transition} failed at '{step}' (state {State}): {detail}";
                Debug.LogWarning(message);
                Debug.WriteAuxiliaryLog(TransitionFailureLogName, $"{DateTimeOffset.Now:O} {message}");
            }

            private static void ReportTransitionFailure(string transition, string step, Exception exception)
                => ReportTransitionFailure(transition, step, exception.ToString());

            /// <summary>
            /// Returns every world to a live edit-mode state after a transition threw: releases
            /// the game mode, restores the edit snapshot when the failed transition had not,
            /// restarts each world in edit mode (which finishes an interrupted play session and
            /// relinks the world's timer callbacks) and publishes the edit state. Each step is
            /// guarded and reported, so one failure cannot skip the steps after it.
            /// </summary>
            private static void RecoverToEditMode(string transition, bool restoreSnapshot)
            {
                State = EPlayModeState.ExitingPlay;

                RunRecoveryStep(transition, "recovery: game mode release", static () =>
                {
                    Time.Timer.Paused = false;
                    GameMode? activeGameMode = Controller.ActiveGameMode;
                    Controller.SetActiveGameMode(null);
                    if (activeGameMode is not null)
                        activeGameMode.WorldInstance = null;

                    foreach (RuntimeWorld worldInstance in Engine.WorldInstances.ToArray())
                    {
                        worldInstance.PhysicsEnabled = false;
                        worldInstance.GameMode = null;
                    }
                });

                WorldStateSnapshot? snapshot = _editModeSnapshot;
                _editModeSnapshot = null;
                if (restoreSnapshot && snapshot is not null)
                {
                    RunRecoveryStep(transition, "recovery: snapshot restore", () =>
                    {
                        if (!snapshot.Restore())
                            ReportTransitionFailure(transition, "recovery: snapshot restore", "The edit snapshot was only partly restored; see playmode-snapshot-diagnostics.log.");
                        Controller.RaisePostSnapshotRestore(snapshot.SourceWorld);
                    });
                }

                foreach (RuntimeWorld worldInstance in Engine.WorldInstances.ToArray())
                {
                    RunRecoveryStep(transition, "recovery: world edit mode start", () =>
                        (RuntimeWorldHostServices.Current?.BeginEditModeAsync(worldInstance)
                            ?? worldInstance.BeginPlayAsync()).GetAwaiter().GetResult());
                }

                State = EPlayModeState.Edit;
                RunRecoveryStep(transition, "recovery: post-exit-play handlers", static () => Controller.RaisePostExitPlay());
            }

            private static void RunRecoveryStep(string transition, string step, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    ReportTransitionFailure(transition, step, ex);
                }
            }
        }
    }
}
