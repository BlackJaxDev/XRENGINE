using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine
{
    /// <summary>
    /// Game mode that spawns a flying camera pawn by default.
    /// Also supports spawning a debug noclip pawn and switching possession to it.
    /// </summary>
    public class FlyingCameraGameMode : GameMode
    {
        private readonly Dictionary<ELocalPlayerIndex, PawnComponent> _primaryPawns = [];
        private readonly Dictionary<ELocalPlayerIndex, FlyingCameraPawnComponent> _noClipPawns = [];

        public FlyingCameraGameMode()
        {
            _defaultPlayerPawnClass = typeof(FlyingCameraPawnComponent);
        }

        protected override PawnComponent? SpawnDefaultPlayerPawn(ELocalPlayerIndex playerIndex)
        {
            var pawn = base.SpawnDefaultPlayerPawn(playerIndex);
            if (pawn is not null)
                _primaryPawns[playerIndex] = pawn;
            return pawn;
        }

        public override void OnEndPlay()
        {
            base.OnEndPlay();
            _primaryPawns.Clear();
            _noClipPawns.Clear();
        }

        public bool IsNoClipEnabled(ELocalPlayerIndex playerIndex)
            => _noClipPawns.ContainsKey(playerIndex);

        public virtual FlyingCameraPawnComponent? EnableNoClip(ELocalPlayerIndex playerIndex)
        {
            if (WorldInstance is null)
                return null;

            if (_noClipPawns.TryGetValue(playerIndex, out var existing) && existing is not null && !existing.IsDestroyed)
            {
                ForcePossession(existing, playerIndex);
                return existing;
            }

            var spawnTransform = GetNoClipSpawnTransform(playerIndex);

            var nodeName = $"Player{(int)playerIndex + 1}_NoClipPawn";
            var pawnNode = new SceneNode(WorldInstance, nodeName);

            if (pawnNode.AddComponent(typeof(FlyingCameraPawnComponent)) is not FlyingCameraPawnComponent pawn)
            {
                pawnNode.Destroy();
                return null;
            }

            WorldInstance.RootNodes.Add(pawnNode);

            if (pawnNode.GetTransformAs<Transform>(false) is Transform tfm)
            {
                tfm.SetWorldTranslation(spawnTransform.Position);
                tfm.SetWorldRotation(spawnTransform.Rotation);
            }

            TrackAutoSpawnedPawn(pawn);
            _noClipPawns[playerIndex] = pawn;

            ForcePossession(pawn, playerIndex);
            return pawn;
        }

        public virtual bool DisableNoClip(ELocalPlayerIndex playerIndex)
        {
            if (_primaryPawns.TryGetValue(playerIndex, out var primary) && primary is not null && !primary.IsDestroyed)
            {
                ForcePossession(primary, playerIndex);
                return true;
            }

            return false;
        }

        protected virtual (Vector3 Position, Quaternion Rotation) GetNoClipSpawnTransform(ELocalPlayerIndex playerIndex)
            => _primaryPawns.TryGetValue(playerIndex, out var primary) && primary?.SceneNode?.GetTransformAs<Transform>(false) is Transform t
                ? ((Vector3 Position, Quaternion Rotation))(t.WorldTranslation, t.WorldRotation)
                : GetSpawnPoint(playerIndex);
    }
}
