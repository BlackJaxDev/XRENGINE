namespace XREngine;

internal sealed class NullPawnController(ELocalPlayerIndex localPlayerIndex) : IPawnController
{
    public bool IsLocal => true;
    public Players.PlayerInfo? PlayerInfo => null;
    public object? InputDevice => null;
    public object? Viewport { get; set; }
    public object? FocusedInteractable { get; set; }
    public XRComponent? ControlledPawnComponent { get; set; }
    public ELocalPlayerIndex? LocalPlayerIndex => localPlayerIndex;
    public void TickPawnInput(float delta, bool isUIInputCaptured) { }
    public void OnPawnCameraChanged() { }
    public void EnqueuePossession(XRComponent pawn) => ControlledPawnComponent = pawn;
    public void ApplyNetworkTransform(Networking.PlayerTransformUpdate update) { }
}
