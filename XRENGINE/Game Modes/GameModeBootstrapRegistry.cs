using System;
using System.Collections.Generic;

namespace XREngine
{
    /// <summary>
    /// AOT-safe game-mode bootstrap registry for realtime world assignment.
    /// Game projects can register additional stable ids during startup.
    /// </summary>
    public static class GameModeBootstrapRegistry
    {
        public const string CustomBootstrapId = "xre.custom";
        public const string FlyingCameraBootstrapId = "xre.flying-camera";
        public const string LocomotionBootstrapId = "xre.locomotion";
        public const string VrBootstrapId = "xre.vr";

        private static readonly object Sync = new();
        private static readonly Dictionary<string, GameModeBootstrapEntry> EntriesById = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Type, string> IdsByType = [];

        static GameModeBootstrapRegistry()
        {
            Register(CustomBootstrapId, static () => new CustomGameMode(), typeof(CustomGameMode), nameof(CustomGameMode), typeof(CustomGameMode).FullName!);
            Register(FlyingCameraBootstrapId, static () => new FlyingCameraGameMode(), typeof(FlyingCameraGameMode), nameof(FlyingCameraGameMode), typeof(FlyingCameraGameMode).FullName!);
            Register(LocomotionBootstrapId, static () => new LocomotionGameMode(), typeof(LocomotionGameMode), nameof(LocomotionGameMode), typeof(LocomotionGameMode).FullName!);
            Register(VrBootstrapId, static () => new VRGameMode(), typeof(VRGameMode), nameof(VRGameMode), typeof(VRGameMode).FullName!);
        }

        public static void Register(
            string bootstrapId,
            Func<GameMode> factory,
            Type? gameModeType = null,
            params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(bootstrapId))
                throw new ArgumentException("Bootstrap id cannot be empty.", nameof(bootstrapId));

            ArgumentNullException.ThrowIfNull(factory);

            string normalizedId = bootstrapId.Trim();
            GameModeBootstrapEntry entry = new(normalizedId, factory);

            lock (Sync)
            {
                EntriesById[normalizedId] = entry;

                if (gameModeType is not null)
                    IdsByType[gameModeType] = normalizedId;

                foreach (string alias in aliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    EntriesById[alias.Trim()] = entry;
                }
            }
        }

        public static bool TryResolveBootstrapId(string? bootstrapIdOrAlias, out string? bootstrapId)
        {
            bootstrapId = null;
            if (!TryGetEntry(bootstrapIdOrAlias, out GameModeBootstrapEntry? entry) || entry is null)
                return false;

            bootstrapId = entry.BootstrapId;
            return true;
        }

        public static bool TryCreate(string? bootstrapIdOrAlias, out GameMode? gameMode)
        {
            gameMode = null;
            if (!TryGetEntry(bootstrapIdOrAlias, out GameModeBootstrapEntry? entry) || entry is null)
                return false;

            gameMode = entry.Factory();
            return gameMode is not null;
        }

        public static bool TryGetBootstrapId(GameMode? gameMode, out string? bootstrapId)
        {
            bootstrapId = null;
            if (gameMode is null)
                return false;

            return TryGetBootstrapId(gameMode.GetType(), out bootstrapId);
        }

        public static bool TryGetBootstrapId(Type? gameModeType, out string? bootstrapId)
        {
            bootstrapId = null;
            if (gameModeType is null)
                return false;

            lock (Sync)
                return IdsByType.TryGetValue(gameModeType, out bootstrapId);
        }

        private static bool TryGetEntry(string? bootstrapIdOrAlias, out GameModeBootstrapEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(bootstrapIdOrAlias))
                return false;

            lock (Sync)
                return EntriesById.TryGetValue(bootstrapIdOrAlias.Trim(), out entry);
        }

        private sealed class GameModeBootstrapEntry(string bootstrapId, Func<GameMode> factory)
        {
            public string BootstrapId { get; } = bootstrapId;
            public Func<GameMode> Factory { get; } = factory;
        }
    }
}
