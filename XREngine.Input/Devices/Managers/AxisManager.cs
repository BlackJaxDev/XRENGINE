namespace XREngine.Input.Devices
{
    public delegate void DelSendAxisValue(int axisIndex, bool continuous, float value);
    public delegate void DelAxisValue(float value);
    [Serializable]
    public class AxisManager(int index, string name) : ButtonManager(index, name)
    {
        public event DelSendAxisValue? ListExecuted;

        private readonly object _axisSubscriptionsSync = new();
        private DelAxisValue[] _continuousUpdate = [];
        private DelAxisValue[] _deltaUpdate = [];
        
        private float _value = 0.0f;
        public float Value => Math.Abs(_value) > DeadZoneThreshold ? _value : 0.0f;

        public float PressedThreshold { get; set; } = 0.9f;
        public float DeadZoneThreshold { get; set; } = 0.1f;
        public float UpdateThreshold { get; set; } = 0.0001f;

        #region Registration
        public override bool IsEmpty() => base.IsEmpty()
            && Volatile.Read(ref _continuousUpdate).Length == 0
            && Volatile.Read(ref _deltaUpdate).Length == 0;
        public void RegisterAxis(DelAxisValue func, bool continuousUpdate, bool unregister)
        {
            lock (_axisSubscriptionsSync)
            {
                DelAxisValue[] current = continuousUpdate ? _continuousUpdate : _deltaUpdate;
                DelAxisValue[] next;
                if (unregister)
                {
                    int index = Array.IndexOf(current, func);
                    if (index < 0)
                        return;
                    next = new DelAxisValue[current.Length - 1];
                    Array.Copy(current, 0, next, 0, index);
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                }
                else
                {
                    next = new DelAxisValue[current.Length + 1];
                    Array.Copy(current, next, current.Length);
                    next[current.Length] = func;
                }
                if (continuousUpdate)
                    Volatile.Write(ref _continuousUpdate, next);
                else
                    Volatile.Write(ref _deltaUpdate, next);
            }
        }
        public override void UnregisterAll()
        {
            base.UnregisterAll();
            lock (_axisSubscriptionsSync)
            {
                Volatile.Write(ref _continuousUpdate, []);
                Volatile.Write(ref _deltaUpdate, []);
            }
        }
        #endregion

        #region Actions
        internal void Tick(float value, float delta)
        {
            float prev = Value;
            _value = value;
            float realValue = Value;

            OnContinuousAxisUpdate(realValue);
            if (Math.Abs(realValue - prev) > UpdateThreshold)
                OnAxisChanged(realValue);

            //Tick button events using a pressed threshold so the axis can also be used as a button
            Tick(Math.Abs(realValue) > PressedThreshold, delta);
        }

        private void OnAxisChanged(float value)
            => ExecuteList(false, value);

        private void OnContinuousAxisUpdate(float value)
            => ExecuteList(true, value);

        private void ExecuteList(bool continuous, float value)
        {
            DelAxisValue[] list = continuous
                ? Volatile.Read(ref _continuousUpdate)
                : Volatile.Read(ref _deltaUpdate);
            ListExecuted?.Invoke(Index, continuous, value);
            foreach (DelAxisValue v in list)
                v.Invoke(value);
        }
        #endregion

        public override string ToString() => Name;
    }
}
