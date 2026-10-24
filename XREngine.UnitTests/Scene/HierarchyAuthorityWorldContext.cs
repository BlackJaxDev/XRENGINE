using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

/// <summary>Provides the world boundary used by root ownership tests.</summary>
internal sealed class HierarchyAuthorityWorldContext : IRuntimeWorldContext
{
    public bool IsPlaySessionActive => false;
    public void RegisterTick(ETickGroup group, int order, WorldTick tick) { }
    public void UnregisterTick(ETickGroup group, int order, WorldTick tick) { }
    public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
    public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
}
