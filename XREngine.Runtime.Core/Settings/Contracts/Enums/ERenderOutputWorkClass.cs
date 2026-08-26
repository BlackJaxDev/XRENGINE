namespace XREngine;

/// <summary>Identifies the urgency class used when admitting output work.</summary>
public enum ERenderOutputWorkClass : byte
{
    /// <summary>Work that must be completed for the current presentation transaction.</summary>
    PresentNow,

    /// <summary>Work performed ahead of presentation to make a later frame ready.</summary>
    Prewarm,

    /// <summary>Opportunistic work that may consume leftover frame budget.</summary>
    Background,
}
