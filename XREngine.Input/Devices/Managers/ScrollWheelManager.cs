using XREngine.Extensions;
using System.Diagnostics;

namespace XREngine.Input.Devices
{
    [Serializable]
    public class ScrollWheelManager : InputManagerBase
    {
        private readonly List<DelMouseScroll> _onUpdate = [];

        internal void Tick(float diff)
            => Tick(diff, null, 0);

        internal void Tick(float diff, InputDevice? device, ulong revision)
        {
            if (diff.EqualTo(0.0f))
                return;
            //Debug.WriteLine($"ScrollWheelManager::Tick({diff})");
            OnUpdate(diff, device, revision);
        }
        public void Register(DelMouseScroll func, bool unregister)
        {
            lock (_onUpdate)
            {
                if (unregister)
                {
                    int index = _onUpdate.FindIndex(x => x == func);
                    if (index >= 0 && index < _onUpdate.Count)
                        _onUpdate.RemoveAt(index);
                }
                else
                    _onUpdate.Add(func);
            }
        }
        private void OnUpdate(float diff, InputDevice? device, ulong revision)
        {
            lock (_onUpdate)
            {
                for (int x = 0; x < _onUpdate.Count && (device is null || revision == device.InputDispatchRevision); ++x)
                    _onUpdate[x](diff);
            }
        }
    }
}
