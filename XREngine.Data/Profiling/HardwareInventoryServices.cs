namespace XREngine.Data.Profiling;

/// <summary>Holds the hardware inventory capability installed by the application.</summary>
public static class HardwareInventoryServices
{
    public static IHardwareInventory? Current { get; set; }
}
