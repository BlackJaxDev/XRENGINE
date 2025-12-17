using MagicPhysX;
using XREngine.Data.Core;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class Obstacle(PxObstacle* obstaclePtr) : XRBase
    {
        public PxObstacle* ObstaclePtr { get; } = obstaclePtr;
    }
}