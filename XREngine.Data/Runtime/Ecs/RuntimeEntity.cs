namespace XREngine.Data.Runtime.Ecs;

public readonly record struct RuntimeEntity(int Index, int Generation)
{
    public bool IsValid => Index >= 0 && Generation > 0;
}
