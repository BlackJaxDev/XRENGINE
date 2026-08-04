namespace XREngine.Components
{
    public interface IAudio2Face3DLiveClientAdapter
    {
        bool TryConnect(Audio2Face3DComponent component, out string? error);
        void Disconnect(Audio2Face3DComponent component);
    }
}
