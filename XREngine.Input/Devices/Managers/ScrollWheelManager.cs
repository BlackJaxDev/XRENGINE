using XREngine.Extensions;
using System.Diagnostics;

namespace XREngine.Input.Devices
{
    public delegate void DelMouseScroll(float diff);
    [Serializable]
    public class ScrollWheelManager : InputManagerBase
    {
        private readonly List<DelMouseScroll> _onUpdate = [];

        internal void Tick(float diff)
        {
            if (diff.EqualTo(0.0f))
                return;
            //Debug.WriteLine($"ScrollWheelManager::Tick({diff})");
            OnUpdate(diff);
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
        private void OnUpdate(float diff)
        {
            lock (_onUpdate)
            {
                for (int x = 0; x < _onUpdate.Count; ++x)
                    _onUpdate[x](diff);
            }
        }
    }
}
