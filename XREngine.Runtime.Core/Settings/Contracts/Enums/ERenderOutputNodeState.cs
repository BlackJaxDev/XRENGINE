namespace XREngine;

public enum ERenderOutputNodeState : byte
{
    Pending,
    Ready,
    Running,
    Complete,
    Reused,
    Deferred,
    Skipped,
}
