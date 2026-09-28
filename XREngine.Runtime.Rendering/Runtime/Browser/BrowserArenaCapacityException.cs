namespace XREngine.Rendering;

/// <summary>A bounded browser arena cannot accept another record until it grows at an idle boundary.</summary>
public sealed class BrowserArenaCapacityException : InvalidOperationException
{
    public BrowserArenaCapacityException(string arena, int requiredCapacity, int capacity, int maximumCapacity)
        : base($"The {arena} arena requires capacity {requiredCapacity}, has capacity {capacity}, and is limited to {maximumCapacity}; abort the batch before growing it while idle.")
    {
        Arena = arena;
        RequiredCapacity = requiredCapacity;
        Capacity = capacity;
        MaximumCapacity = maximumCapacity;
    }

    public string Arena { get; }
    public int RequiredCapacity { get; }
    public int Capacity { get; }
    public int MaximumCapacity { get; }
}
