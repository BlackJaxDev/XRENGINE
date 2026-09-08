namespace XREngine.Rendering;

/// <summary>Lifetime state of one bounded backend output bank.</summary>
public enum EAdvancedOutputReservationBankState
{
    Free,
    Active,
    Retiring,
}
