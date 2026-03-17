using XREngine.Players;

namespace XREngine.Input
{
    public abstract class PlayerControllerBase : PawnController, IPawnController
    {
        public PlayerControllerBase() : base() { }

        private PlayerInfo _playerInfo = new();
        public new PlayerInfo PlayerInfo
        {
            get => _playerInfo;
            set => SetField(ref _playerInfo, value);
        }

        /// <inheritdoc />
        PlayerInfo? IPawnController.PlayerInfo => _playerInfo;
    }
}
