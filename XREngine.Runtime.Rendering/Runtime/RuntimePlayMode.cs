namespace XREngine;

internal sealed class RuntimePlayMode
{
    public event Action? PostEnterPlay;
    public event Action? PreExitPlay;
    public bool IsTransitioning { get; set; }
    public RuntimePlayModeState State { get; set; } = RuntimePlayModeState.Edit;

    public void RaisePostEnterPlay() => PostEnterPlay?.Invoke();
    public void RaisePreExitPlay() => PreExitPlay?.Invoke();
}
