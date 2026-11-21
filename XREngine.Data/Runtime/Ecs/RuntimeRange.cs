namespace XREngine.Data.Runtime.Ecs;

public readonly record struct RuntimeRange(int Start, int End)
{
    public int Count => Math.Max(0, End - Start);
}
