using System.Numerics;

namespace XREngine.Scene.Transforms;

/// <summary>A world-local slot identity. Recycling a slot invalidates every previous handle.</summary>
public readonly record struct TransformHandle(int Index, uint Generation)
{
    public bool IsValid => Generation != 0;
}
