using MagicPhysX;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    /// <summary>
    /// Read-only wrapper around a PxController that exposes position for RigidBodyTransform.
    ///
    /// IMPORTANT: CCT's internal actor pose is NOT updated by MoveMut. Only PxController::getPosition()
    /// reflects the actual post-move position. This proxy reads from the controller, not the actor.
    /// </summary>
    public unsafe sealed class PhysxControllerActorProxy : IAbstractRigidPhysicsActor
    {
        private readonly PxController* _controller;
        private Vector3 _lastPosition;
        private Vector3 _cachedLinearVelocity;
        private float _lastRefreshTime;

        private (Vector3 position, Quaternion rotation) _cachedTransform;

        public PhysxControllerActorProxy(PxController* controller)
        {
            _controller = controller;
            _lastRefreshTime = 0;
            RefreshFromNative();
            _lastPosition = _cachedTransform.position;
        }

        private int _refreshLogCount = 0;
        /// <summary>
        /// Refresh cached state from the controller.
        /// Call this only when PhysX simulation is NOT running (i.e. after FetchResults).
        /// </summary>
        public void RefreshFromNative()
        {
            if (_controller is null)
            {
                _cachedTransform = (Vector3.Zero, Quaternion.Identity);
                _cachedLinearVelocity = Vector3.Zero;
                return;
            }

            // CCT position is the authoritative source after MoveMut
            var pos = _controller->GetPosition();
            var newPosition = new Vector3((float)pos->x, (float)pos->y, (float)pos->z);

            // Log first 30 refreshes to diagnose transform sync
            if (_refreshLogCount < 30)
            {
                _refreshLogCount++;
                Debug.Log(ELogCategory.Physics,
                    "[Proxy.Refresh] #{0} pos=({1:F2},{2:F2},{3:F2}) cached=({4:F2},{5:F2},{6:F2})",
                    _refreshLogCount,
                    newPosition.X, newPosition.Y, newPosition.Z,
                    _cachedTransform.position.X, _cachedTransform.position.Y, _cachedTransform.position.Z);
            }

            // CCT doesn't have rotation - use identity or up direction
            var up = _controller->GetUpDirection();
            // Build rotation from up direction (controllers are always upright)
            Quaternion rotation = Quaternion.Identity;
            if (MathF.Abs(up.y - 1.0f) > 0.001f)
            {
                // Non-standard up - compute rotation
                rotation = XRMath.RotationBetweenVectors(Globals.Up, new Vector3(up.x, up.y, up.z));
            }

            _cachedTransform = (newPosition, rotation);

            // Estimate velocity from position delta
            float currentTime = Engine.ElapsedTime;
            float dt = currentTime - _lastRefreshTime;
            if (dt > 0.0001f)
            {
                _cachedLinearVelocity = (newPosition - _lastPosition) / dt;
            }
            _lastPosition = newPosition;
            _lastRefreshTime = currentTime;
        }

        public (Vector3 position, Quaternion rotation) Transform
            => _cachedTransform;

        public Vector3 LinearVelocity
            => _cachedLinearVelocity;

        public Vector3 AngularVelocity
            => Vector3.Zero; // CCT doesn't rotate

        public bool IsSleeping
            => false; // CCT is always "awake"

        public void Destroy(bool wakeOnLostTouch = false)
        {
            // No-op: PxController/PxControllerManager own and release the controller.
        }
    }
}
