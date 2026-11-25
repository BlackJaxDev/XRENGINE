using XREngine.Scene.Transforms;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    /// <summary>Admits one old transform World-to-null write during its owner's replacement.</summary>
    internal sealed class CanonicalTransformUnlinkPermit(
        WorldMutationLease lease, TransformBase target) : IDisposable
    {
        private bool _consumed;
        private bool _disposed;

        internal bool TryConsume(XRWorldObjectBase candidate, IRuntimeWorldContext? newWorld)
        {
            if (_disposed || _consumed || newWorld is not null || !ReferenceEquals(candidate, target))
                return false;
            _consumed = true;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            lease.EndCanonicalUnlink(this);
        }
    }
}
