using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

/// <summary>Minimal reusable runtime-world context for physics-chain unit tests.</summary>
internal sealed class TestWorldContext : IRuntimeWorldContext
{
    private readonly List<(ETickGroup Group, WorldTick Tick)> _ticks = [];

    public bool IsPlaySessionActive => false;

    public void RegisterTick(ETickGroup group, int order, WorldTick tick)
        => _ticks.Add((group, tick));

    public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
        => _ticks.Remove((group, tick));

    public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
    {
    }

    public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
    {
    }

    public void Run(ETickGroup group)
    {
        for (int i = 0; i < _ticks.Count; ++i)
            if (_ticks[i].Group == group)
                _ticks[i].Tick();
    }
}
