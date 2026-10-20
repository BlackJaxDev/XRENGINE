using System.Runtime.ExceptionServices;

namespace XREngine;

public static partial class Engine
{
    public static partial class PlayMode
    {
        private static StandalonePlaySession? _standaloneSession;

        /// <summary>Enters standalone play on the caller's thread, without editor transitions.</summary>
        internal static void BeginStandalonePlay()
        {
            StandalonePlaySession? session = CreateStandaloneSession();
            if (session is null)
                return;

            try
            {
                PrepareStandaloneSession(session);
                foreach (RuntimeWorld world in Engine.WorldInstances)
                    BeginStandaloneWorld(session, world).GetAwaiter().GetResult();
                CompleteStandaloneSession(session);
            }
            catch (Exception startupError)
            {
                RollBackStandaloneSession(session, startupError);
                throw;
            }
        }

        /// <summary>Begins standalone play without blocking the host's event thread.</summary>
        public static async Task BeginStandalonePlayAsync()
        {
            StandalonePlaySession? session = CreateStandaloneSession();
            if (session is null)
                return;

            try
            {
                PrepareStandaloneSession(session);
                foreach (RuntimeWorld world in Engine.WorldInstances)
                    await BeginStandaloneWorld(session, world);
                CompleteStandaloneSession(session);
            }
            catch (Exception startupError)
            {
                RollBackStandaloneSession(session, startupError);
                throw;
            }
        }

        private static StandalonePlaySession? CreateStandaloneSession()
        {
            if (_standaloneSession is { StopRequested: true })
                throw new InvalidOperationException("The previous standalone play session must finish stopping before restarting.");
            if (_standaloneSession is { Starting: true })
                throw new InvalidOperationException("A standalone play session is already starting.");
            if (_editModeSimulationActive || _standaloneSession is not null)
                return null;
            if (!IsEditing)
                throw new InvalidOperationException($"Cannot begin standalone play from state: {State}");

            // Preserve the engine's single-mode selection and world-binding policy. Only the
            // synthetic editor fallback is omitted; explicitly authored CustomGameMode is valid.
            StandalonePlaySession session = new(ResolveGameMode(ResolveStartupWorld(), createFallback: false));
            _standaloneSession = session;
            return session;
        }

        private static void PrepareStandaloneSession(StandalonePlaySession session)
        {
            Configuration.SimulatePhysics = true;
            State = EPlayModeState.EnteringPlay;
            RequireStandaloneSession(session);
            Controller.SetActiveGameMode(session.GameMode);
        }

        private static Task BeginStandaloneWorld(StandalonePlaySession session, RuntimeWorld world)
        {
            RequireStandaloneSession(session);
            // Include partial startup in rollback, without stopping worlds we never reached.
            session.Worlds.Add(world);
            world.PhysicsEnabled = true;
            world.GameMode = session.GameMode;
            return RuntimeWorldHostServices.Current?.BeginPlayAsync(world) ?? world.BeginPlayAsync();
        }

        private static void CompleteStandaloneSession(StandalonePlaySession session)
        {
            RequireStandaloneSession(session);
            if (session.GameMode is { } gameMode)
            {
                // Begin may acquire possessions and then throw, so End owns that attempt too.
                session.GameModeBeginAttempted = true;
                gameMode.OnBeginPlay();
            }
            RequireStandaloneSession(session);
            _editModeSimulationActive = true;
            State = EPlayModeState.Play;
            RequireStandaloneSession(session);
            session.Starting = false;
        }

        private static void RequireStandaloneSession(StandalonePlaySession session)
        {
            if (!ReferenceEquals(_standaloneSession, session) || session.Ending || session.StopRequested)
                throw new InvalidOperationException("The standalone play session ended during startup.");
        }

        private static void RollBackStandaloneSession(StandalonePlaySession session, Exception startupError)
        {
            session.Starting = false;
            try
            {
                EndStandaloneSession(session);
            }
            catch (Exception cleanupError)
            {
                // The caller rethrows the startup exception, including its original stack.
                try { startupError.Data["StandaloneCleanupException"] = cleanupError; }
                catch { /* Custom exception data may be read-only. Preserve the original error. */ }
                try { Debug.LogException(cleanupError, "Failed to clean up standalone play startup"); }
                catch { /* Diagnostics must not replace the original startup failure. */ }
            }
        }

        /// <summary>Ends the owned standalone session without entering the editor lifecycle.</summary>
        public static void EndStandalonePlay()
        {
            if (_standaloneSession is { } session)
                EndStandaloneSession(session);
        }

        private static void EndStandaloneSession(StandalonePlaySession session)
        {
            if (!ReferenceEquals(_standaloneSession, session) || session.Ending)
                return;
            session.StopRequested = true;
            // A world begin may still be awaiting backend activation. Its caller will unwind
            // after it settles; releasing the world now would race the remaining activation.
            if (session.Starting)
                return;
            session.Ending = true;

            List<Exception>? failures = null;
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }

            try
            {
                Cleanup(() => State = EPlayModeState.ExitingPlay);
                if (session.GameModeBeginAttempted)
                {
                    session.GameModeBeginAttempted = false;
                    // Keep the mode and world attached until gameplay has released its resources.
                    Cleanup(session.GameMode!.OnEndPlay);
                }
                for (int index = 0; index < session.Worlds.Count;)
                {
                    RuntimeWorld world = session.Worlds[index];
                    Cleanup(() =>
                    {
                        if (world.PlayState == RuntimeWorldPlayState.Stopped)
                            return;
                        if (RuntimeWorldHostServices.Current is { } host)
                            host.EndPlay(world);
                        else
                            world.EndPlay();
                    });
                    Cleanup(() => world.PhysicsEnabled = false);
                    if (world.PlayState == RuntimeWorldPlayState.Stopped)
                        Cleanup(() => world.GameMode = null);
                    if (world.PlayState == RuntimeWorldPlayState.Stopped && !world.PhysicsEnabled && world.GameMode is null)
                        session.Worlds.RemoveAt(index);
                    else
                        index++;
                }
                if (session.Worlds.Count == 0)
                {
                    if (session.GameMode is { } gameMode)
                        gameMode.WorldInstance = null;
                    Controller.SetActiveGameMode(null);
                    _editModeSimulationActive = false;
                    Cleanup(() => State = EPlayModeState.Edit);
                    _standaloneSession = null;
                }
                else if (failures is null)
                {
                    failures = [new InvalidOperationException("Standalone world cleanup has not finished; end play must be retried before restarting.")];
                }
            }
            finally
            {
                session.Ending = false;
            }

            if (failures is [Exception failure])
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException("One or more standalone play cleanup operations failed.", failures);
        }

        /// <summary>Tracks exactly which lifecycle calls this standalone entry owns.</summary>
        private sealed class StandalonePlaySession(GameMode? gameMode)
        {
            public GameMode? GameMode { get; } = gameMode;
            public List<RuntimeWorld> Worlds { get; } = [];
            public bool GameModeBeginAttempted { get; set; }
            public bool Ending { get; set; }
            public bool Starting { get; set; } = true;
            public bool StopRequested { get; set; }
        }
    }
}
