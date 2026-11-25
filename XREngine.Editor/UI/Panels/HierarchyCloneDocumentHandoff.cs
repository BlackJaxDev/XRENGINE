using XREngine.Data.Core;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor;

/// <summary>Reserves live adopted structure while private constructor storage is retired.</summary>
internal sealed class HierarchyCloneDocumentHandoff(
    ObjectConstructionOwnership privateOwnership,
    HierarchyStructuralReservation reservation,
    Action requireCurrent) : IDisposable
{
    internal ObjectConstructionOwnership PrivateOwnership { get; } = privateOwnership;
    internal void RequireCurrent() => requireCurrent();
    public void Dispose() => reservation.Dispose();
}
