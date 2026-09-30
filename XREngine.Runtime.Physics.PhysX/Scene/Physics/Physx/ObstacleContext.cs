using MagicPhysX;
using XREngine.Data.Core;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe class ObstacleContext(PxObstacleContext* contextPtr) : XRBase
    {
        public PxObstacleContext* ContextPtr { get; } = contextPtr;

        public Dictionary<nint, uint> Obstacles { get; } = [];

        public void AddObstacle(PxObstacle* obstacle)
        {
            uint handle = ContextPtr->AddObstacleMut(obstacle);
            Obstacles.Add((nint)obstacle, handle);
        }
        public void RemoveObstacle(PxObstacle* obstacle)
        {
            nint ptr = (nint)obstacle;
            if (!Obstacles.TryGetValue(ptr, out var handle))
                return;

            ContextPtr->RemoveObstacleMut(handle);
            Obstacles.Remove(ptr);
        }
        public void UpdateObstacle(PxObstacle* obstacle)
        {
            nint ptr = (nint)obstacle;
            if (!Obstacles.TryGetValue(ptr, out var handle))
                return;

            ContextPtr->UpdateObstacleMut(handle, obstacle);
        }
        public uint ObstacleCount => ContextPtr->GetNbObstacles();
        public void Release()
        {
            Obstacles.Clear();
            ContextPtr->ReleaseMut();
        }

        public PxObstacle* GetObstacle(uint index)
            => ContextPtr->GetObstacle(index);
        public PxObstacle* GetObstacleByHandle(uint handle)
            => ContextPtr->GetObstacleByHandle(handle);
    }
}